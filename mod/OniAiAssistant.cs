using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using KMod;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OniAiAssistant
{
    public sealed class OniAiAssistantMod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            harmony.PatchAll();
            Debug.Log("[ONI-AI] Mod loaded");
        }
    }

    [HarmonyPatch(typeof(Game), "OnSpawn")]
    public static class OniAiAssistantGameOnSpawnPatch
    {
        public static void Postfix()
        {
            OniAiController.EnsureInstance();
        }
    }

    public sealed class OniAiController : MonoBehaviour
    {
        private const string ButtonIdleText = "ONI AI Request";
        private const string ButtonBusyText = "AI Working...";

        private static OniAiController instance;

        private OniAiConfig config;
        private bool isBusy;
        private KButton triggerButton;
        private LocText triggerButtonText;
        private GameObject buttonRoot;
        private float nextUiAttachAttemptAt;

        public static void EnsureInstance()
        {
            if (instance != null)
            {
                return;
            }

            var gameObject = new GameObject("OniAiAssistantController");
            DontDestroyOnLoad(gameObject);
            instance = gameObject.AddComponent<OniAiController>();
        }

        private void Awake()
        {
            config = OniAiConfig.Load();
            TryCreateNativeUiButton();
            Debug.Log("[ONI-AI] Controller initialized");
        }

        private void Update()
        {
            if (buttonRoot == null && Time.unscaledTime >= nextUiAttachAttemptAt)
            {
                TryCreateNativeUiButton();
            }

            if (isBusy)
            {
                return;
            }

            if (config.EnableHotkey && Input.GetKeyDown(config.Hotkey))
            {
                TriggerAiRequest();
            }
        }

        private void TriggerAiRequest()
        {
            if (isBusy)
            {
                return;
            }

            StartCoroutine(RunAiCycle());
        }

        private void TryCreateNativeUiButton()
        {
            if (buttonRoot != null)
            {
                return;
            }

            nextUiAttachAttemptAt = Time.unscaledTime + 2f;

            var topLeftControl = FindObjectOfType<TopLeftControlScreen>();
            if (topLeftControl == null)
            {
                return;
            }

            var templateButton = FindTemplateButton(topLeftControl);
            if (templateButton == null)
            {
                return;
            }

            GameObject templateObject = templateButton.gameObject;
            GameObject root = Instantiate(templateObject, templateObject.transform.parent);
            root.name = "OniAiAssistantNativeButton";

            triggerButton = root.GetComponent<KButton>();
            if (triggerButton == null)
            {
                Destroy(root);
                return;
            }

            triggerButton.ClearOnClick();
            triggerButton.onClick += TriggerAiRequest;
            triggerButton.isInteractable = true;

            triggerButtonText = root.GetComponentInChildren<LocText>(true);
            if (triggerButtonText != null)
            {
                triggerButtonText.text = ButtonIdleText;
            }

            var rootRect = root.GetComponent<RectTransform>();
            if (rootRect != null)
            {
                rootRect.anchoredPosition += new Vector2(0f, -40f);
            }

            buttonRoot = root;
            Debug.Log("[ONI-AI] Native button attached to TopLeftControlScreen");
        }

        private static KButton FindTemplateButton(TopLeftControlScreen topLeftControl)
        {
            foreach (var button in topLeftControl.GetComponentsInChildren<KButton>(true))
            {
                if (button == null)
                {
                    continue;
                }

                var text = button.GetComponentInChildren<LocText>(true);
                if (text != null)
                {
                    return button;
                }
            }

            return null;
        }

        private void SetBusyUiState(bool busy)
        {
            isBusy = busy;

            if (triggerButton != null)
            {
                triggerButton.isInteractable = !busy;
            }

            if (triggerButtonText != null)
            {
                triggerButtonText.text = busy ? ButtonBusyText : ButtonIdleText;
            }
        }

        private IEnumerator RunAiCycle()
        {
            SetBusyUiState(true);

            var speedControl = SpeedControlScreen.Instance;
            int previousSpeed = speedControl != null ? Mathf.Clamp(speedControl.GetSpeed(), 1, 3) : 1;

            PauseGame(speedControl);
            yield return new WaitForEndOfFrame();

            var requestContext = PrepareRequestSnapshot(previousSpeed);

            string aiResponse = string.Empty;
            bool httpOk = false;
            yield return SendToBridge(requestContext.PayloadJson, result =>
            {
                aiResponse = result.Response;
                httpOk = result.Ok;
            });

            int finalSpeed = previousSpeed;
            if (httpOk)
            {
                finalSpeed = ExecutePlan(aiResponse, requestContext, previousSpeed);
            }
            else
            {
                Debug.LogWarning("[ONI-AI] Bridge request failed");
                WriteTextSafe(Path.Combine(requestContext.RequestDir, "bridge_error.txt"), "Request failed or timed out");
            }

            ResumeGame(speedControl, finalSpeed);
            SetBusyUiState(false);
        }

        private static void PauseGame(SpeedControlScreen speedControl)
        {
            if (speedControl == null)
            {
                return;
            }

            speedControl.Pause(false, false);
        }

        private static void ResumeGame(SpeedControlScreen speedControl, int speed)
        {
            if (speedControl == null)
            {
                return;
            }

            speedControl.Unpause(false);
            speedControl.SetSpeed(Mathf.Clamp(speed, 1, 3));
        }

        private RequestContext PrepareRequestSnapshot(int previousSpeed)
        {
            string timestamp = System.DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string requestId = timestamp + "_" + UnityEngine.Random.Range(1000, 9999).ToString(CultureInfo.InvariantCulture);
            string requestDir = Path.Combine(GetModDirectory(), "requests", requestId);
            string snapshotDir = Path.Combine(requestDir, "snapshot");
            string logsDir = Path.Combine(requestDir, "logs");

            Directory.CreateDirectory(snapshotDir);
            Directory.CreateDirectory(logsDir);

            string screenshotPath = Path.Combine(snapshotDir, "screenshot.png");
            ScreenCapture.CaptureScreenshot(screenshotPath);

            var context = BuildContextObject(previousSpeed);
            var requestEnvelope = new JObject
            {
                ["request_id"] = requestId,
                ["request_dir"] = requestDir,
                ["snapshot_dir"] = snapshotDir,
                ["screenshot_path"] = screenshotPath,
                ["requested_at_utc"] = System.DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["context"] = context
            };

            WriteJsonSafe(Path.Combine(requestDir, "request.json"), requestEnvelope);
            WriteJsonSafe(Path.Combine(snapshotDir, "context.json"), context);
            WriteJsonSafe(Path.Combine(snapshotDir, "runtime_config.json"), BuildRuntimeConfigObject());
            WriteJsonSafe(Path.Combine(snapshotDir, "assemblies.json"), BuildAssembliesObject());
            WriteJsonSafe(Path.Combine(snapshotDir, "scenes.json"), BuildScenesObject());
            WriteJsonSafe(Path.Combine(snapshotDir, "singletons.json"), BuildSingletonSnapshot());

            return new RequestContext
            {
                RequestId = requestId,
                RequestDir = requestDir,
                SnapshotDir = snapshotDir,
                PayloadJson = requestEnvelope.ToString(Formatting.None)
            };
        }

        private static JObject BuildRuntimeConfigObject()
        {
            return new JObject
            {
                ["unity_version"] = Application.unityVersion,
                ["platform"] = Application.platform.ToString(),
                ["target_frame_rate"] = Application.targetFrameRate,
                ["product_name"] = Application.productName,
                ["version"] = Application.version,
                ["scene_count"] = SceneManager.sceneCount
            };
        }

        private static JArray BuildAssembliesObject()
        {
            var assemblies = new JArray();
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal))
            {
                var name = assembly.GetName();
                assemblies.Add(new JObject
                {
                    ["name"] = name.Name,
                    ["version"] = name.Version != null ? name.Version.ToString() : string.Empty
                });
            }

            return assemblies;
        }

        private static JArray BuildScenesObject()
        {
            var scenes = new JArray();
            for (int index = 0; index < SceneManager.sceneCount; index++)
            {
                Scene scene = SceneManager.GetSceneAt(index);
                var roots = new JArray();
                foreach (var root in scene.GetRootGameObjects())
                {
                    var components = new JArray();
                    foreach (var component in root.GetComponents<Component>())
                    {
                        if (component == null)
                        {
                            continue;
                        }

                        components.Add(component.GetType().FullName);
                    }

                    roots.Add(new JObject
                    {
                        ["name"] = root.name,
                        ["active"] = root.activeSelf,
                        ["child_count"] = root.transform.childCount,
                        ["components"] = components
                    });
                }

                scenes.Add(new JObject
                {
                    ["index"] = index,
                    ["name"] = scene.name,
                    ["is_loaded"] = scene.isLoaded,
                    ["root_count"] = roots.Count,
                    ["roots"] = roots
                });
            }

            return scenes;
        }

        private static JArray BuildSingletonSnapshot()
        {
            var results = new JArray();
            int collected = 0;
            const int maxTypes = 500;

            var assembly = typeof(Game).Assembly;
            foreach (var type in assembly.GetTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
            {
                if (collected >= maxTypes)
                {
                    break;
                }

                object singleton = TryGetSingletonInstance(type);
                if (singleton == null)
                {
                    continue;
                }

                results.Add(new JObject
                {
                    ["type"] = type.FullName,
                    ["summary"] = BuildObjectSummary(singleton)
                });

                collected++;
            }

            return results;
        }

        private static object TryGetSingletonInstance(Type type)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

            try
            {
                PropertyInfo prop = type.GetProperty("Instance", flags);
                if (prop != null && prop.GetIndexParameters().Length == 0)
                {
                    return prop.GetValue(null, null);
                }
            }
            catch
            {
            }

            try
            {
                FieldInfo field = type.GetField("Instance", flags);
                if (field != null)
                {
                    return field.GetValue(null);
                }
            }
            catch
            {
            }

            return null;
        }

        private static JObject BuildObjectSummary(object instance)
        {
            var summary = new JObject
            {
                ["object_type"] = instance.GetType().FullName
            };

            var values = new JObject();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance;
            int written = 0;
            const int maxMembers = 60;

            foreach (var property in instance.GetType().GetProperties(flags))
            {
                if (written >= maxMembers)
                {
                    break;
                }

                if (!property.CanRead || property.GetIndexParameters().Length > 0)
                {
                    continue;
                }

                if (!IsSimpleType(property.PropertyType))
                {
                    continue;
                }

                try
                {
                    object value = property.GetValue(instance, null);
                    values[property.Name] = value != null ? JToken.FromObject(value) : JValue.CreateNull();
                    written++;
                }
                catch
                {
                }
            }

            summary["values"] = values;
            return summary;
        }

        private static bool IsSimpleType(Type type)
        {
            if (type.IsPrimitive || type.IsEnum)
            {
                return true;
            }

            return type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(System.DateTime)
                || type == typeof(System.TimeSpan)
                || type == typeof(Guid);
        }

        private static JObject BuildContextObject(int previousSpeed)
        {
            int cycle = -1;
            float cycleTime = -1f;
            float totalCycles = -1f;

            if (GameClock.Instance != null)
            {
                cycle = GameClock.Instance.GetCycle();
                cycleTime = GameClock.Instance.GetTimeSinceStartOfCycle();
                totalCycles = GameClock.Instance.GetTimeInCycles();
            }

            var speedControl = SpeedControlScreen.Instance;
            bool paused = speedControl != null && speedControl.IsPaused;
            int currentSpeed = speedControl != null ? speedControl.GetSpeed() : previousSpeed;

            return new JObject
            {
                ["cycle"] = cycle,
                ["time_since_cycle_start"] = cycleTime,
                ["time_in_cycles"] = totalCycles,
                ["paused"] = paused,
                ["current_speed"] = currentSpeed,
                ["previous_speed"] = previousSpeed,
                ["real_time_since_startup_seconds"] = Time.realtimeSinceStartup,
                ["unscaled_time_seconds"] = Time.unscaledTime
            };
        }

        private IEnumerator SendToBridge(string payload, Action<BridgeResult> onComplete)
        {
            var bytes = Encoding.UTF8.GetBytes(payload);
            using (var request = new UnityWebRequest(config.BridgeUrl, UnityWebRequest.kHttpVerbPOST))
            {
                request.uploadHandler = new UploadHandlerRaw(bytes);
                request.downloadHandler = new DownloadHandlerBuffer();
                request.SetRequestHeader("Content-Type", "application/json");
                request.timeout = Mathf.Clamp(config.RequestTimeoutSeconds, 5, 600);

                yield return request.SendWebRequest();

                if (request.result != UnityWebRequest.Result.Success)
                {
                    Debug.LogWarning("[ONI-AI] Request error: " + request.error);
                    onComplete(new BridgeResult(false, string.Empty));
                    yield break;
                }

                onComplete(new BridgeResult(true, request.downloadHandler.text));
            }
        }

        private int ExecutePlan(string aiResponse, RequestContext context, int previousSpeed)
        {
            WriteTextSafe(Path.Combine(context.RequestDir, "logs", "bridge_response_raw.txt"), aiResponse ?? string.Empty);

            if (string.IsNullOrWhiteSpace(aiResponse))
            {
                WriteTextSafe(Path.Combine(context.RequestDir, "logs", "execution_note.txt"), "Empty response; no actions executed");
                return previousSpeed;
            }

            JToken root;
            try
            {
                root = JToken.Parse(aiResponse);
            }
            catch (Exception exception)
            {
                WriteTextSafe(Path.Combine(context.RequestDir, "logs", "parse_error.txt"), exception.ToString());
                return previousSpeed;
            }

            JArray actionArray = root["actions"] as JArray;
            if (actionArray == null && root is JArray)
            {
                actionArray = (JArray)root;
            }

            if (actionArray == null)
            {
                WriteTextSafe(Path.Combine(context.RequestDir, "logs", "execution_note.txt"), "No actions array in response");
                return previousSpeed;
            }

            int resultingSpeed = previousSpeed;
            var executionLog = new JArray();

            foreach (var token in actionArray)
            {
                if (!(token is JObject actionObject))
                {
                    continue;
                }

                string actionType = (actionObject.Value<string>("type") ?? string.Empty).Trim().ToLowerInvariant();
                string actionId = (actionObject.Value<string>("id") ?? Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)).Trim();
                JObject parameters = actionObject["params"] as JObject ?? new JObject();

                var itemLog = new JObject
                {
                    ["id"] = actionId,
                    ["type"] = actionType,
                    ["status"] = "ignored",
                    ["message"] = ""
                };

                switch (actionType)
                {
                    case "set_speed":
                    {
                        int speed = parameters.Value<int?>("speed") ?? 0;
                        if (speed >= 1 && speed <= 3)
                        {
                            resultingSpeed = speed;
                            itemLog["status"] = "applied";
                            itemLog["message"] = "Speed accepted";
                        }
                        else
                        {
                            itemLog["status"] = "rejected";
                            itemLog["message"] = "Invalid speed; expected 1..3";
                        }

                        break;
                    }
                    case "no_op":
                    {
                        itemLog["status"] = "applied";
                        itemLog["message"] = "No operation";
                        break;
                    }
                    case "pause":
                    case "resume":
                    {
                        itemLog["status"] = "rejected";
                        itemLog["message"] = "Pause/resume is controlled by mod runtime";
                        break;
                    }
                    default:
                    {
                        itemLog["status"] = "unsupported";
                        itemLog["message"] = "Action type not yet implemented in executor";
                        break;
                    }
                }

                executionLog.Add(itemLog);
            }

            var executionResult = new JObject
            {
                ["resulting_speed"] = resultingSpeed,
                ["actions"] = executionLog
            };

            WriteJsonSafe(Path.Combine(context.RequestDir, "logs", "execution_result.json"), executionResult);
            Debug.Log("[ONI-AI] Executed plan with " + executionLog.Count + " actions");
            return resultingSpeed;
        }

        private static void WriteJsonSafe(string path, JToken token)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, token.ToString(Formatting.Indented));
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ONI-AI] Failed to write JSON: " + path + " error=" + exception.Message);
            }
        }

        private static void WriteTextSafe(string path, string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, text ?? string.Empty);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ONI-AI] Failed to write text: " + path + " error=" + exception.Message);
            }
        }

        private static string GetModDirectory()
        {
            return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        }

        private readonly struct BridgeResult
        {
            public BridgeResult(bool ok, string response)
            {
                Ok = ok;
                Response = response;
            }

            public bool Ok { get; }

            public string Response { get; }
        }

        private sealed class RequestContext
        {
            public string RequestId { get; set; }

            public string RequestDir { get; set; }

            public string SnapshotDir { get; set; }

            public string PayloadJson { get; set; }
        }
    }

    public sealed class OniAiConfig
    {
        public string BridgeUrl { get; private set; } = "http://127.0.0.1:8765/analyze";

        public KeyCode Hotkey { get; private set; } = KeyCode.F8;

        public bool EnableHotkey { get; private set; } = false;

        public int RequestTimeoutSeconds { get; private set; } = 120;

        public static OniAiConfig Load()
        {
            var config = new OniAiConfig();
            string filePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "oni_ai_config.ini");

            if (!File.Exists(filePath))
            {
                return config;
            }

            foreach (string line in File.ReadAllLines(filePath))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = trimmed.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                string key = trimmed.Substring(0, separator).Trim();
                string value = trimmed.Substring(separator + 1).Trim();

                if (key.Equals("bridge_url", StringComparison.OrdinalIgnoreCase))
                {
                    config.BridgeUrl = value;
                    continue;
                }

                if (key.Equals("hotkey", StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse(value, true, out KeyCode parsed))
                    {
                        config.Hotkey = parsed;
                    }

                    continue;
                }

                if (key.Equals("enable_hotkey", StringComparison.OrdinalIgnoreCase))
                {
                    if (bool.TryParse(value, out bool enableHotkey))
                    {
                        config.EnableHotkey = enableHotkey;
                    }

                    continue;
                }

                if (key.Equals("request_timeout_seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out int timeout))
                    {
                        config.RequestTimeoutSeconds = timeout;
                    }
                }
            }

            return config;
        }
    }
}
