using System;
using System.Threading.Tasks;
using McpUnity.Unity;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;

namespace McpUnity.Tools
{
    /// <summary>
    /// Simulates a keyboard key entirely inside Unity's own Input System (InputSystem.QueueStateEvent)
    /// -- never touches the operating system, never needs the Unity window to have focus. Only has an
    /// effect in Play Mode, since the game's own InputActions (see InputHandler.cs) only exist while
    /// running. Works regardless of whether those InputActions come from a .inputactions asset or are
    /// built in code (both read from the same underlying Keyboard device).
    ///
    /// By default Unity's Input System only routes keyboard/pointer input to the game while a Game View
    /// has OS focus. Two SEPARATE settings gate this, and BOTH have to change together or neither has any
    /// effect (confirmed by reading Unity's own InputManager.cs, not just the public docs):
    /// -- InputSettings.editorInputBehaviorInPlayMode (default PointersAndKeyboardsRespectGameViewFocus)
    ///    decides, per incoming event, whether a Keyboard/Pointer event gets diverted to the Editor
    ///    instead of the game while unfocused.
    /// -- InputSettings.backgroundBehavior (default ResetAndDisableNonBackgroundDevices) independently
    ///    disables the Keyboard device itself while unfocused, regardless of the setting above --
    ///    InputManager just drops incoming events for a disabled device outright.
    /// Only the combination editorInputBehaviorInPlayMode=AllDeviceInputAlwaysGoesToGameView AND
    /// backgroundBehavior=IgnoreFocus makes Unity's internal gameHasFocus flag itself evaluate true
    /// (see InputManager.gameShouldGetInputRegardlessOfFocus), which is what actually bypasses every
    /// focus-gated check in the pipeline at once. Since this tool never touches OS-level window focus,
    /// it flips both settings for the exact duration of the press (queue-down .. release) and restores
    /// whatever it found -- same released-in-every-exit-path guarantee as the key itself (own timer,
    /// Play Mode exit, domain reload).
    ///
    /// Movement (continuous ReadValue/IsPressed) worked from the very first version of this tool.
    /// Edge-triggered actions (Dash, menu toggles -- anything gated on WasPressedThisFrame()) did not,
    /// even after the focus-settings fix above. Root cause, found by reading the installed Input System
    /// package source (ButtonControl.cs), not guessed: WasPressedThisFrame() doesn't compare live state
    /// against last frame -- past the first-ever poll it compares InputUpdate.s_UpdateStepCount (a
    /// global per-frame counter) against a per-control m_UpdateCountLastPressed value that gets stamped
    /// with device.m_CurrentUpdateStepCount at the moment the press is processed. Queuing the event from
    /// EditorApplication.delayCall/update (not synchronized with the Input System's own per-frame
    /// pipeline) stamps it with whatever step count happens to be current at that arbitrary moment --
    /// almost always one step behind the value the game checks against on the very next real frame, so
    /// the edge is always "already in the past" by the time WasPressedThisFrame() reads it.
    /// InputSystem.onBeforeUpdate is Unity's own documented fix for exactly this: "events queued from a
    /// callback will be fed right into the upcoming update" -- queuing from inside it lands the state
    /// change on the SAME step the game's Update() checks moments later, instead of a stale one.
    /// </summary>
    public class SimulateKeyPressTool : McpToolBase
    {
        // Estado de una sola tecla "prestada" a la vez -- soltarla es el recurso que hay que
        // garantizar en TODOS los caminos de salida (CLAUDE.md II.5), no solo el feliz:
        // recarga de dominio (borra las suscripciones de delegate, así que se suelta ANTES de
        // que eso pase), salida de Play Mode (el juego que la escucha deja de existir), y una
        // llamada nueva mientras la anterior seguía sin resolver.
        Key? _pendingReleaseKey;
        Keyboard _keyboard;
        bool _downQueued;
        double _releaseAt;
        float _holdSeconds;
        TaskCompletionSource<JObject> _pendingTcs;

        // Mismo principio para el segundo recurso prestado: mientras dura la simulación, las DOS
        // settings de foco quedan forzadas -- tienen que volver a su valor original en los mismos
        // caminos de salida que la tecla, nunca quedar prendidas más de lo que dura la simulación.
        bool _inputModeOverrideActive;
        InputSettings.EditorInputBehaviorInPlayMode _savedInputMode;
        InputSettings.BackgroundBehavior _savedBackgroundBehavior;

        public SimulateKeyPressTool()
        {
            Name = "simulate_key_press";
            Description = "Simulates a keyboard key press-hold-release entirely inside Unity's own Input System (never touches the OS, never needs window focus). Only has an effect in Play Mode. Correctly registers as an edge for WasPressedThisFrame()-gated actions (Dash, menu toggles), not just continuous polling. Use to drive gameplay input for self-verification via capture_game_view or by reading component state.";
            IsAsync = true;
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            string keyName = parameters["key"]?.ToObject<string>();
            if (string.IsNullOrEmpty(keyName))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'key' not provided", "validation_error"));
                return;
            }

            if (!Enum.TryParse(keyName, true, out Key key))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Unknown key '{keyName}'. Use a UnityEngine.InputSystem.Key enum name (e.g. 'V', 'C', 'Space', 'Escape').",
                    "validation_error"));
                return;
            }

            if (!EditorApplication.isPlaying)
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "Not in Play Mode -- simulated input only reaches the running game, which doesn't exist outside Play. Enter Play Mode first (execute_menu_item 'Edit/Play').",
                    "invalid_state_error"));
                return;
            }

            Keyboard keyboard = Keyboard.current;
            if (keyboard == null)
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "No Keyboard device found (Keyboard.current is null).", "not_found_error"));
                return;
            }

            // Default ~0.15s, no 0: un press+release en el MISMO cuadro corre el riesgo de que
            // WasPressedThisFrame() del juego nunca llegue a verlo. Un hold corto pero real
            // garantiza al menos un cuadro procesado con la tecla abajo, igual que un toque
            // humano real (nunca es literalmente instantáneo).
            float holdSeconds = parameters["holdSeconds"]?.ToObject<float?>() ?? 0.15f;
            if (holdSeconds < 0.05f) holdSeconds = 0.05f;

            ForceReleasePending(); // por si quedó algo sin resolver de una llamada anterior

            BeginInputModeOverride();

            _keyboard = keyboard;
            _pendingReleaseKey = key;
            _downQueued = false;
            _holdSeconds = holdSeconds;
            _pendingTcs = tcs;

            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            InputSystem.onBeforeUpdate -= OnBeforeInputUpdate;
            InputSystem.onBeforeUpdate += OnBeforeInputUpdate;
        }

        /// <summary>
        /// Corre justo antes de que el Input System procese su propio cuadro (documentado: los
        /// eventos encolados acá "se sirven directo en el próximo update"). Encolar el flanco de
        /// bajada desde acá -- en vez de desde delayCall/EditorApplication.update -- es lo que
        /// sincroniza el "paso" grabado por UpdateWasPressed() con el que WasPressedThisFrame()
        /// va a comparar momentos después, en el mismo cuadro.
        /// </summary>
        void OnBeforeInputUpdate()
        {
            if (!_pendingReleaseKey.HasValue)
            {
                InputSystem.onBeforeUpdate -= OnBeforeInputUpdate;
                return;
            }

            if (!_downQueued)
            {
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState(_pendingReleaseKey.Value));
                _downQueued = true;
                _releaseAt = EditorApplication.timeSinceStartup + _holdSeconds;
                McpLogger.LogInfo($"[MCP Unity] Simulated key down: {_pendingReleaseKey.Value}");
                return; // el flanco de bajada y el de subida no pueden ir en el mismo pase
            }

            if (EditorApplication.timeSinceStartup < _releaseAt) return;

            FinishRelease(TryReleaseNow(Keyboard.current));
        }

        void FinishRelease(bool releasedCleanly)
        {
            InputSystem.onBeforeUpdate -= OnBeforeInputUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            Key key = _pendingReleaseKey.Value;
            float holdSeconds = _holdSeconds;
            TaskCompletionSource<JObject> tcs = _pendingTcs;

            _pendingReleaseKey = null;
            _pendingTcs = null;
            EndInputModeOverride();

            McpLogger.LogInfo($"[MCP Unity] Simulated key up: {key}");

            tcs?.SetResult(new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Simulated key '{key}' ({holdSeconds:0.00}s hold) via Input System (in-process, no OS input).",
                ["key"] = key.ToString(),
                ["holdSeconds"] = holdSeconds,
                ["releasedCleanly"] = releasedCleanly
            });
        }

        void OnBeforeAssemblyReload()
        {
            if (_pendingReleaseKey.HasValue) FinishRelease(TryReleaseNow(Keyboard.current));
        }

        void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.ExitingPlayMode) return;
            if (_pendingReleaseKey.HasValue) FinishRelease(TryReleaseNow(Keyboard.current));
        }

        void ForceReleasePending()
        {
            InputSystem.onBeforeUpdate -= OnBeforeInputUpdate;
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            if (_pendingReleaseKey.HasValue)
            {
                // No se resuelve el tcs viejo con SetResult -- si quedó uno pendiente de una
                // llamada anterior sin terminar, no hay forma segura de completarlo bien acá;
                // se descarta en silencio.
                TryReleaseNow(Keyboard.current);
                _pendingReleaseKey = null;
                _pendingTcs = null;
            }
            EndInputModeOverride();
        }

        void BeginInputModeOverride()
        {
            if (_inputModeOverrideActive) return;
            _savedInputMode = InputSystem.settings.editorInputBehaviorInPlayMode;
            _savedBackgroundBehavior = InputSystem.settings.backgroundBehavior;
            _inputModeOverrideActive = true;
            InputSystem.settings.editorInputBehaviorInPlayMode = InputSettings.EditorInputBehaviorInPlayMode.AllDeviceInputAlwaysGoesToGameView;
            InputSystem.settings.backgroundBehavior = InputSettings.BackgroundBehavior.IgnoreFocus;
        }

        void EndInputModeOverride()
        {
            if (!_inputModeOverrideActive) return;
            InputSystem.settings.editorInputBehaviorInPlayMode = _savedInputMode;
            InputSystem.settings.backgroundBehavior = _savedBackgroundBehavior;
            _inputModeOverrideActive = false;
        }

        static bool TryReleaseNow(Keyboard keyboard)
        {
            if (keyboard == null) return false;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            return true;
        }
    }
}
