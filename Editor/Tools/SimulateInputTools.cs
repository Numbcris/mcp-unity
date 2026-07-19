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
    /// </summary>
    public class SimulateKeyPressTool : McpToolBase
    {
        // Estado de una sola tecla "prestada" a la vez -- soltarla es el recurso que hay que
        // garantizar en TODOS los caminos de salida (CLAUDE.md II.5), no solo el feliz:
        // recarga de dominio (borra las suscripciones de delegate, así que se suelta ANTES de
        // que eso pase) y salida de Play Mode (el juego que la escucha deja de existir).
        Key? _pendingReleaseKey;
        EditorApplication.CallbackFunction _pendingTick;

        public SimulateKeyPressTool()
        {
            Name = "simulate_key_press";
            Description = "Simulates a keyboard key press-hold-release entirely inside Unity's own Input System (never touches the OS, never needs window focus). Only has an effect in Play Mode. Use to drive gameplay input (e.g. open a UI panel with its bound key) for self-verification via capture_game_view.";
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

            // Default ~0.15s, no 0: un press+release en el MISMO frame corre el riesgo de que
            // WasPressedThisFrame() del juego nunca llegue a verlo. Un hold corto pero real
            // garantiza al menos un frame procesado con la tecla abajo, igual que un toque
            // humano real (nunca es literalmente instantáneo).
            float holdSeconds = parameters["holdSeconds"]?.ToObject<float?>() ?? 0.15f;
            if (holdSeconds < 0.05f) holdSeconds = 0.05f;

            ForceReleasePending(keyboard); // por si quedó algo sin resolver de una llamada anterior

            InputSystem.QueueStateEvent(keyboard, new KeyboardState(key));
            InputSystem.Update();
            McpLogger.LogInfo($"[MCP Unity] Simulated key down: {key}");

            _pendingReleaseKey = key;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;

            double releaseAt = EditorApplication.timeSinceStartup + holdSeconds;
            _pendingTick = () =>
            {
                if (EditorApplication.timeSinceStartup < releaseAt) return;

                EditorApplication.update -= _pendingTick;
                _pendingTick = null;
                AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
                EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

                bool releasedCleanly = TryReleaseNow(Keyboard.current);
                _pendingReleaseKey = null;

                McpLogger.LogInfo($"[MCP Unity] Simulated key up: {key}");

                tcs.SetResult(new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Simulated key '{key}' ({holdSeconds:0.00}s hold) via Input System (in-process, no OS input).",
                    ["key"] = key.ToString(),
                    ["holdSeconds"] = holdSeconds,
                    ["releasedCleanly"] = releasedCleanly
                });
            };
            EditorApplication.update += _pendingTick;
        }

        void OnBeforeAssemblyReload()
        {
            if (_pendingReleaseKey.HasValue) TryReleaseNow(Keyboard.current);
        }

        void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode && _pendingReleaseKey.HasValue)
                TryReleaseNow(Keyboard.current);
        }

        void ForceReleasePending(Keyboard keyboard)
        {
            if (_pendingTick != null)
            {
                EditorApplication.update -= _pendingTick;
                _pendingTick = null;
            }
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;

            if (_pendingReleaseKey.HasValue)
            {
                TryReleaseNow(keyboard);
                _pendingReleaseKey = null;
            }
        }

        static bool TryReleaseNow(Keyboard keyboard)
        {
            if (keyboard == null) return false;
            InputSystem.QueueStateEvent(keyboard, new KeyboardState());
            InputSystem.Update();
            return true;
        }
    }
}
