using System;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Utility class for screenshot/capture tool operations
    /// </summary>
    public static class ScreenshotToolUtils
    {
        public const int DefaultWidth = 1024;
        public const int DefaultHeight = 768;
        public const int MaxDimension = 2048;

        /// <summary>
        /// Clamp a requested resolution to a sane range
        /// </summary>
        public static (int width, int height) ClampResolution(int? requestedWidth, int? requestedHeight)
        {
            int width = requestedWidth.GetValueOrDefault(DefaultWidth);
            int height = requestedHeight.GetValueOrDefault(DefaultHeight);

            width = Mathf.Clamp(width, 64, MaxDimension);
            height = Mathf.Clamp(height, 64, MaxDimension);

            return (width, height);
        }

        /// <summary>
        /// Render a Camera to an offscreen RenderTexture and read it back into a Texture2D.
        /// Restores the camera's original targetTexture afterward.
        /// </summary>
        public static Texture2D RenderCameraToTexture2D(Camera camera, int width, int height)
        {
            RenderTexture originalTarget = camera.targetTexture;
            RenderTexture originalActive = RenderTexture.active;
            RenderTexture renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);

            try
            {
                camera.targetTexture = renderTexture;
                camera.Render();

                RenderTexture.active = renderTexture;
                Texture2D texture = new Texture2D(width, height, TextureFormat.RGB24, false);
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply();

                return texture;
            }
            finally
            {
                camera.targetTexture = originalTarget;
                RenderTexture.active = originalActive;
                RenderTexture.ReleaseTemporary(renderTexture);
            }
        }

        /// <summary>
        /// Encode a Texture2D to a base64 PNG string
        /// </summary>
        public static string EncodePngBase64(Texture2D texture)
        {
            byte[] pngBytes = texture.EncodeToPNG();
            return Convert.ToBase64String(pngBytes);
        }

        /// <summary>
        /// Find a GameObject by instance ID or hierarchy path
        /// </summary>
        public static GameObject FindGameObject(int? instanceId, string objectPath)
        {
            if (instanceId.HasValue)
            {
                return EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
            }

            if (!string.IsNullOrEmpty(objectPath))
            {
                GameObject found = GameObject.Find(objectPath);
                if (found != null)
                {
                    return found;
                }

                string[] pathParts = objectPath.Split('/');
                GameObject[] rootGameObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

                foreach (GameObject rootObj in rootGameObjects)
                {
                    if (rootObj.name != pathParts[0])
                    {
                        continue;
                    }

                    GameObject current = rootObj;
                    for (int i = 1; i < pathParts.Length; i++)
                    {
                        Transform child = current.transform.Find(pathParts[i]);
                        if (child == null)
                        {
                            current = null;
                            break;
                        }
                        current = child.gameObject;
                    }

                    if (current != null)
                    {
                        return current;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Find the best-guess "main" camera in the active scene: Camera.main first,
        /// then the first enabled Camera found in the scene as a fallback.
        /// </summary>
        public static Camera FindMainCamera()
        {
            if (Camera.main != null)
            {
                return Camera.main;
            }

            Camera[] allCameras = UnityEngine.Object.FindObjectsByType<Camera>(FindObjectsSortMode.None);
            foreach (Camera candidate in allCameras)
            {
                if (candidate.isActiveAndEnabled)
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Tool for capturing the Unity Editor's Scene View camera
    /// </summary>
    public class CaptureSceneViewTool : McpToolBase
    {
        public CaptureSceneViewTool()
        {
            Name = "capture_scene_view";
            Description = "Captures a screenshot of the Unity Editor's Scene View (the editing viewport, including gizmos/overlays) and returns it as a base64-encoded PNG image";
        }

        public override JObject Execute(JObject parameters)
        {
            SceneView sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null || sceneView.camera == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "No active Scene View found. Open a Scene View window in the Unity Editor first.",
                    "not_found_error"
                );
            }

            int? requestedWidth = parameters["width"]?.ToObject<int?>();
            int? requestedHeight = parameters["height"]?.ToObject<int?>();
            (int width, int height) = ScreenshotToolUtils.ClampResolution(requestedWidth, requestedHeight);

            Texture2D texture = ScreenshotToolUtils.RenderCameraToTexture2D(sceneView.camera, width, height);
            string base64 = ScreenshotToolUtils.EncodePngBase64(texture);
            UnityEngine.Object.DestroyImmediate(texture);

            McpLogger.LogInfo($"[MCP Unity] Captured Scene View screenshot ({width}x{height})");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "image",
                ["message"] = $"Captured Scene View screenshot ({width}x{height})",
                ["data"] = base64,
                ["mimeType"] = "image/png",
                ["width"] = width,
                ["height"] = height
            };
        }
    }

    /// <summary>
    /// Tool for capturing what the main/active game Camera currently sees
    /// </summary>
    public class CaptureGameViewTool : McpToolBase
    {
        public CaptureGameViewTool()
        {
            Name = "capture_game_view";
            Description = "Captures what the main game Camera currently sees (the player's view, without Editor gizmos/overlays) and returns it as a base64-encoded PNG image";
        }

        public override JObject Execute(JObject parameters)
        {
            Camera camera = ScreenshotToolUtils.FindMainCamera();
            if (camera == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "No active Camera found in the currently loaded scene(s).",
                    "not_found_error"
                );
            }

            int? requestedWidth = parameters["width"]?.ToObject<int?>();
            int? requestedHeight = parameters["height"]?.ToObject<int?>();
            (int width, int height) = ScreenshotToolUtils.ClampResolution(requestedWidth, requestedHeight);

            Texture2D texture = ScreenshotToolUtils.RenderCameraToTexture2D(camera, width, height);
            string base64 = ScreenshotToolUtils.EncodePngBase64(texture);
            UnityEngine.Object.DestroyImmediate(texture);

            McpLogger.LogInfo($"[MCP Unity] Captured Game View screenshot from camera '{camera.name}' ({width}x{height})");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "image",
                ["message"] = $"Captured Game View screenshot from camera '{camera.name}' ({width}x{height})",
                ["data"] = base64,
                ["mimeType"] = "image/png",
                ["width"] = width,
                ["height"] = height,
                ["cameraName"] = camera.name
            };
        }
    }

    /// <summary>
    /// Tool for capturing from a specific Camera in the scene
    /// </summary>
    public class CaptureCameraTool : McpToolBase
    {
        public CaptureCameraTool()
        {
            Name = "capture_camera";
            Description = "Captures a screenshot from a specific Camera in the scene, found by GameObject instanceId or hierarchy path";
        }

        public override JObject Execute(JObject parameters)
        {
            int? instanceId = parameters["instanceId"]?.ToObject<int?>();
            string objectPath = parameters["objectPath"]?.ToObject<string>();

            if (!instanceId.HasValue && string.IsNullOrEmpty(objectPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'instanceId' or 'objectPath' must be provided",
                    "validation_error"
                );
            }

            GameObject gameObject = ScreenshotToolUtils.FindGameObject(instanceId, objectPath);
            if (gameObject == null)
            {
                string identifier = instanceId.HasValue ? $"ID {instanceId.Value}" : $"path '{objectPath}'";
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject with {identifier} not found",
                    "not_found_error"
                );
            }

            Camera camera = gameObject.GetComponent<Camera>();
            if (camera == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject '{gameObject.name}' does not have a Camera component",
                    "component_error"
                );
            }

            int? requestedWidth = parameters["width"]?.ToObject<int?>();
            int? requestedHeight = parameters["height"]?.ToObject<int?>();
            (int width, int height) = ScreenshotToolUtils.ClampResolution(requestedWidth, requestedHeight);

            Texture2D texture = ScreenshotToolUtils.RenderCameraToTexture2D(camera, width, height);
            string base64 = ScreenshotToolUtils.EncodePngBase64(texture);
            UnityEngine.Object.DestroyImmediate(texture);

            McpLogger.LogInfo($"[MCP Unity] Captured screenshot from camera '{camera.name}' ({width}x{height})");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "image",
                ["message"] = $"Captured screenshot from camera '{camera.name}' ({width}x{height})",
                ["data"] = base64,
                ["mimeType"] = "image/png",
                ["width"] = width,
                ["height"] = height,
                ["cameraName"] = camera.name
            };
        }
    }

    /// <summary>
    /// Tool for capturing an isolated GameObject, framed automatically from a chosen angle
    /// </summary>
    public class CaptureIsolatedObjectTool : McpToolBase
    {
        private static readonly string[] ValidAngles = { "front", "side", "top", "iso" };

        /// <summary>
        /// Computes a world-space camera direction and LookAt up-hint for the requested
        /// angle, relative to the TARGET OBJECT'S OWN current rotation — never fixed world
        /// axes. The real use case for this tool is an object that follows someone else's
        /// rotation (a tool in a hand, a character seated on a bench, a weapon in a socket);
        /// for those, a world-fixed "front" can end up looking straight down the object's
        /// length instead of at it. When the object's own rotation is identity, forward/
        /// right/up equal the world axes, so this reduces exactly to the old fixed-axis
        /// behavior — it is a strict generalization, not a special case to maintain.
        ///
        /// KNOWN LIMITATION: this trusts the object's own forward/right/up as meaningful
        /// axes. If whatever rotated the object (an attach socket, a hand-tuned offset)
        /// didn't leave a clean axis perpendicular to its actual shape, "side" or "top"
        /// can still end up near edge-on for that specific object/pose — verified on a
        /// hand-held Axe, where side/top came out edge-on while front/iso stayed usable.
        /// This is a property of that object's authored rotation, not a bug here, and it
        /// is NOT auto-corrected (would need measuring the object's actual longest visual
        /// axis from its geometry, not just trusting its transform — deliberately not
        /// built: not worth it against just requesting a different angle when one fails).
        /// "iso" blends all three axes, so it is never fully edge-on — safest default,
        /// and already is one.
        /// </summary>
        private static (Vector3 direction, Vector3 upHint) GetAngleFraming(string angleKey, Transform target)
        {
            switch (angleKey)
            {
                case "front":
                    // Camera stands where the object points to and looks back at it,
                    // so the face the object is oriented toward fills the frame.
                    return (target.forward, target.up);
                case "side":
                    return (target.right, target.up);
                case "top":
                    // Looking straight down the object's own up axis makes it nearly
                    // parallel to an up-hint of "up" (unstable/degenerate LookAt roll),
                    // so use the object's forward as the up-hint instead.
                    return (target.up, target.forward);
                case "iso":
                default:
                    return ((target.forward + target.right + target.up).normalized, target.up);
            }
        }

        public CaptureIsolatedObjectTool()
        {
            Name = "capture_isolated_object";
            Description = "Captures a GameObject framed automatically from a chosen angle (front, side, top, iso) against a solid background, useful for verifying scale/orientation/appearance without opening the Editor";
        }

        public override JObject Execute(JObject parameters)
        {
            int? instanceId = parameters["instanceId"]?.ToObject<int?>();
            string objectPath = parameters["objectPath"]?.ToObject<string>();

            if (!instanceId.HasValue && string.IsNullOrEmpty(objectPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'instanceId' or 'objectPath' must be provided",
                    "validation_error"
                );
            }

            GameObject gameObject = ScreenshotToolUtils.FindGameObject(instanceId, objectPath);
            if (gameObject == null)
            {
                string identifier = instanceId.HasValue ? $"ID {instanceId.Value}" : $"path '{objectPath}'";
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject with {identifier} not found",
                    "not_found_error"
                );
            }

            // Unity never renders an inactive GameObject regardless of camera/layers.
            // Many capturable objects (unequipped weapons, disabled variants) are inactive
            // by design, so temporarily activate the target for the render and always restore
            // its original state afterward, even if something below throws.
            bool wasActive = gameObject.activeSelf;
            if (!wasActive)
            {
                gameObject.SetActive(true);
            }

            try
            {
                return CaptureIsolated(gameObject, parameters);
            }
            finally
            {
                if (!wasActive)
                {
                    gameObject.SetActive(false);
                }
            }
        }

        private JObject CaptureIsolated(GameObject gameObject, JObject parameters)
        {
            Renderer[] renderers = gameObject.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject '{gameObject.name}' has no renderable geometry (no Renderer found in itself or its children)",
                    "validation_error"
                );
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                bounds.Encapsulate(renderers[i].bounds);
            }

            string angleKey = (parameters["angle"]?.ToObject<string>() ?? "iso").ToLowerInvariant();
            bool foundAngle = Array.IndexOf(ValidAngles, angleKey) >= 0;

            if (!foundAngle)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Unknown angle '{angleKey}'. Valid values: front, side, top, iso",
                    "validation_error"
                );
            }

            (Vector3 direction, Vector3 upHint) = GetAngleFraming(angleKey, gameObject.transform);

            int? requestedWidth = parameters["width"]?.ToObject<int?>();
            int? requestedHeight = parameters["height"]?.ToObject<int?>();
            (int width, int height) = ScreenshotToolUtils.ClampResolution(requestedWidth, requestedHeight);

            JObject bgParam = parameters["backgroundColor"] as JObject;
            Color backgroundColor = bgParam != null
                ? new Color(
                    bgParam["r"]?.ToObject<float>() ?? 0.2f,
                    bgParam["g"]?.ToObject<float>() ?? 0.2f,
                    bgParam["b"]?.ToObject<float>() ?? 0.2f,
                    1f)
                : new Color(0.2f, 0.2f, 0.2f, 1f);

            GameObject tempCameraObject = new GameObject("MCP_ScreenshotIsolationCamera")
            {
                hideFlags = HideFlags.HideAndDontSave
            };

            try
            {
                Camera tempCamera = tempCameraObject.AddComponent<Camera>();
                tempCamera.clearFlags = CameraClearFlags.SolidColor;
                tempCamera.backgroundColor = backgroundColor;
                tempCamera.orthographic = true;
                tempCamera.cullingMask = ~0;

                float radius = bounds.extents.magnitude;
                if (radius < 0.001f)
                {
                    radius = 0.5f;
                }

                float distance = radius * 3f;
                Vector3 normalizedDirection = direction.normalized;
                tempCameraObject.transform.position = bounds.center + normalizedDirection * distance;
                tempCameraObject.transform.LookAt(bounds.center, upHint);

                tempCamera.orthographicSize = radius * 1.2f;
                tempCamera.nearClipPlane = 0.01f;
                tempCamera.farClipPlane = distance + radius * 2f;

                Texture2D texture = ScreenshotToolUtils.RenderCameraToTexture2D(tempCamera, width, height);
                byte[] pngBytes = texture.EncodeToPNG();
                string base64 = System.Convert.ToBase64String(pngBytes);
                UnityEngine.Object.DestroyImmediate(texture);

                // Además de devolver la imagen inline (como siempre), la escribe SIEMPRE a un
                // path fijo en disco -- necesario para post-procesar (recorte/alfa) fuera de
                // Unity, ej. generar un ícono de ítem desde un asset sin Previews/ propio (Cris,
                // 2026-07-19). Path fijo (no un parámetro nuevo) para no depender de que el
                // servidor Node ya compilado conozca un campo que todavía no existe del lado TS
                // -- evita necesitar un reinicio de Claude Code solo para esto. Se pisa en cada
                // llamada, es un scratch de una sola imagen a la vez, no un archivo por captura.
                const string savePath = @"C:\Users\cris8\AppData\Local\Temp\claude\D--Velgrimor\746ca987-f692-4b38-a000-7cc380984845\scratchpad\last_isolated_capture.png";
                System.IO.File.WriteAllBytes(savePath, pngBytes);

                McpLogger.LogInfo($"[MCP Unity] Captured isolated screenshot of '{gameObject.name}' from '{angleKey}' angle ({width}x{height})");

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "image",
                    ["message"] = $"Captured isolated screenshot of '{gameObject.name}' from '{angleKey}' angle ({width}x{height})",
                    ["data"] = base64,
                    ["mimeType"] = "image/png",
                    ["width"] = width,
                    ["height"] = height,
                    ["gameObjectName"] = gameObject.name,
                    ["angle"] = angleKey,
                    ["savedTo"] = savePath
                };
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempCameraObject);
            }
        }
    }
}
