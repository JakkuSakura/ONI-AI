using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
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
    public interface IOniAiRuntime
    {
        string RuntimeId { get; }

        void OnAttach(OniAiController controller);

        void OnDetach();

        void OnTick(OniAiController controller);

        bool HandleTrigger(OniAiController controller);
    }

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
        private GameObject messageCanvasRoot;
        private Text messageText;
        private float messageHideAt;
        private float nextUiAttachAttemptAt;
        private bool loggedButtonAttachFailure;
        private IOniAiRuntime runtimeHook;
        private string runtimeDllPath;
        private long runtimeDllLastWriteTicks;
        private float nextRuntimeReloadCheckAt;
        private bool runtimeMissingLogged;
        private long configLastWriteTicks;
        private float nextConfigReloadCheckAt;

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
            configLastWriteTicks = GetConfigLastWriteTicks();
            TryCreateNativeUiButton();
            TryReloadRuntime(true);
            StartHttpServer();
            Debug.Log("[ONI-AI] Controller initialized");
        }

        private void Update()
        {
            if (buttonRoot == null && Time.unscaledTime >= nextUiAttachAttemptAt)
            {
                TryCreateNativeUiButton();
            }

            TryReloadConfig(false);

            TryReloadRuntime(false);


            if (runtimeHook != null)
            {
                try
                {
                    runtimeHook.OnTick(this);
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[ONI-AI] Runtime OnTick failed: " + exception.Message);
                }
            }

            if (messageCanvasRoot != null && messageCanvasRoot.activeSelf && Time.unscaledTime >= messageHideAt)
            {
                messageCanvasRoot.SetActive(false);
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

            if (runtimeHook != null)
            {
                try
                {
                    if (runtimeHook.HandleTrigger(this))
                    {
                        return;
                    }
                }
                catch (Exception exception)
                {
                    Debug.LogWarning("[ONI-AI] Runtime HandleTrigger failed: " + exception.Message);
                }
            }

            TriggerDefaultAiRequest();
        }

        private void OnDestroy()
        {
            StopHttpServer();

            if (runtimeHook == null)
            {
                return;
            }

            try
            {
                runtimeHook.OnDetach();
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ONI-AI] Runtime OnDetach failed during destroy: " + exception.Message);
            }
        }

        public void TriggerDefaultAiRequest()
        {
            if (isBusy)
            {
                return;
            }

            ShowInGameMessage("ONI AI: analyzing colony...");
            StartCoroutine(RunAiCycle());
        }

        public void PublishInfo(string text)
        {
            ShowInGameMessage(text, new Color(0.86f, 0.94f, 1.00f, 1.00f), 2.0f);
        }

        public void PublishSuccess(string text)
        {
            ShowInGameMessage(text, new Color(0.70f, 1.00f, 0.75f, 1.00f), 2.5f);
        }

        public void PublishError(string text)
        {
            ShowInGameMessage(text, new Color(1.00f, 0.70f, 0.70f, 1.00f), 4.0f);
        }

        private bool TryInvokeRuntimeMethod(string methodName, object[] args, out object result)
        {
            result = null;
            if (runtimeHook == null)
            {
                return false;
            }

            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                MethodInfo method = runtimeHook
                    .GetType()
                    .GetMethods(flags)
                    .FirstOrDefault(candidate =>
                        string.Equals(candidate.Name, methodName, StringComparison.Ordinal)
                        && candidate.GetParameters().Length == args.Length);

                if (method == null)
                {
                    return false;
                }

                result = method.Invoke(runtimeHook, args);
                return true;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ONI-AI] Runtime method failed: " + methodName + " error=" + exception.Message);
                return false;
            }
        }

        private void NotifyRuntimeConfigReloaded()
        {
            TryInvokeRuntimeMethod("OnConfigReload", new object[] { this }, out _);
        }

        private void TryCreateNativeUiButton()
        {
            if (buttonRoot != null)
            {
                return;
            }

            nextUiAttachAttemptAt = Time.unscaledTime + 2f;

            KButton templateButton = null;
            Transform parentTransform = null;

            var topLeftControl = FindObjectOfType<TopLeftControlScreen>();
            if (topLeftControl != null)
            {
                templateButton = FindTemplateButton(topLeftControl.transform);
                if (templateButton != null)
                {
                    parentTransform = templateButton.transform.parent;
                }
            }

            if (templateButton == null)
            {
                templateButton = FindFallbackTemplateButton();
                if (templateButton != null)
                {
                    parentTransform = templateButton.transform.parent;
                    Debug.Log("[ONI-AI] Using fallback KButton template for ONI AI button attachment");
                }
            }

            if (templateButton == null)
            {
                if (!loggedButtonAttachFailure)
                {
                    Debug.LogWarning("[ONI-AI] Could not find a UI template button yet; retrying");
                    loggedButtonAttachFailure = true;
                }
                return;
            }

            GameObject templateObject = templateButton.gameObject;
            GameObject root = Instantiate(templateObject, parentTransform);
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
            loggedButtonAttachFailure = false;
            Debug.Log("[ONI-AI] Native button attached");
        }

        private static KButton FindTemplateButton(Transform root)
        {
            foreach (var button in root.GetComponentsInChildren<KButton>(true))
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

        private static KButton FindFallbackTemplateButton()
        {
            foreach (var button in Resources.FindObjectsOfTypeAll<KButton>())
            {
                if (button == null || button.transform == null || button.transform.parent == null)
                {
                    continue;
                }

                if (!button.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var text = button.GetComponentInChildren<LocText>(true);
                if (text == null)
                {
                    continue;
                }

                return button;
            }

            return null;
        }

        private void TryReloadRuntime(bool force)
        {
            float interval = Mathf.Clamp(config != null ? config.RuntimeReloadIntervalSeconds : 1.0f, 0.2f, 30.0f);
            if (!force && Time.unscaledTime < nextRuntimeReloadCheckAt)
            {
                return;
            }

            nextRuntimeReloadCheckAt = Time.unscaledTime + interval;

            string candidatePath = ResolveRuntimeDllPath();
            if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
            {
                if (!runtimeMissingLogged)
                {
                    Debug.LogWarning("[ONI-AI] Runtime DLL not found: " + candidatePath);
                    runtimeMissingLogged = true;
                }

                return;
            }

            runtimeMissingLogged = false;

            System.DateTime lastWriteUtc = File.GetLastWriteTimeUtc(candidatePath);
            long lastWriteTicks = lastWriteUtc.Ticks;
            if (!force && candidatePath == runtimeDllPath && lastWriteTicks == runtimeDllLastWriteTicks)
            {
                return;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(candidatePath);
                var assembly = Assembly.Load(bytes);
                Type runtimeType = assembly
                    .GetTypes()
                    .FirstOrDefault(type => typeof(IOniAiRuntime).IsAssignableFrom(type) && !type.IsAbstract && type.IsClass);

                if (runtimeType == null)
                {
                    Debug.LogWarning("[ONI-AI] Runtime DLL has no IOniAiRuntime implementation: " + candidatePath);
                    return;
                }

                var nextRuntime = (IOniAiRuntime)Activator.CreateInstance(runtimeType);

                if (runtimeHook != null)
                {
                    try
                    {
                        runtimeHook.OnDetach();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning("[ONI-AI] Previous runtime OnDetach failed: " + exception.Message);
                    }
                }

                runtimeHook = nextRuntime;
                runtimeDllPath = candidatePath;
                runtimeDllLastWriteTicks = lastWriteTicks;

                runtimeHook.OnAttach(this);
                string runtimeId = string.IsNullOrWhiteSpace(runtimeHook.RuntimeId) ? runtimeType.FullName : runtimeHook.RuntimeId;
                Debug.Log("[ONI-AI] Runtime reloaded: " + runtimeId + " from " + runtimeDllPath);
                PublishSuccess("ONI AI runtime reloaded");
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ONI-AI] Runtime reload failed: " + exception);
                PublishError("ONI AI runtime reload failed");
            }
        }

        private void TryReloadConfig(bool force)
        {
            const float intervalSeconds = 0.5f;
            if (!force && Time.unscaledTime < nextConfigReloadCheckAt)
            {
                return;
            }

            nextConfigReloadCheckAt = Time.unscaledTime + intervalSeconds;
            long currentTicks = GetConfigLastWriteTicks();
            if (currentTicks <= 0)
            {
                return;
            }

            if (!force && currentTicks == configLastWriteTicks)
            {
                return;
            }

            config = OniAiConfig.Load();
            configLastWriteTicks = currentTicks;
            Debug.Log("[ONI-AI] Config reloaded");
            PublishInfo("ONI AI config reloaded");
            NotifyRuntimeConfigReloaded();
        }

        private long GetConfigLastWriteTicks()
        {
            string path = Path.Combine(GetModDirectory(), "oni_ai_config.ini");
            if (!File.Exists(path))
            {
                return 0;
            }

            return File.GetLastWriteTimeUtc(path).Ticks;
        }

        private string ResolveRuntimeDllPath()
        {
            string configured = config != null ? config.RuntimeDllPath : string.Empty;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(GetModDirectory(), "runtime", "OniAiRuntime.dll");
            }

            if (Path.IsPathRooted(configured))
            {
                return configured;
            }

            return Path.GetFullPath(Path.Combine(GetModDirectory(), configured));
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

            try
            {
                var speedControl = SpeedControlScreen.Instance;
                int previousSpeed = speedControl != null ? Mathf.Clamp(speedControl.GetSpeed(), 1, 3) : 1;
                bool wasPaused = speedControl != null && speedControl.IsPaused;

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

                WriteTextSafe(Path.Combine(requestContext.RequestDir, "logs", "bridge_response_raw.txt"), aiResponse ?? string.Empty);

                if (!httpOk)
                {
                    WriteTextSafe(Path.Combine(requestContext.RequestDir, "bridge_error.txt"), "Request failed or timed out");
                    throw new InvalidOperationException("Bridge request failed or timed out");
                }

                if (wasPaused)
                {
                    PauseGame(speedControl);
                }
                else
                {
                    ResumeGame(speedControl, previousSpeed);
                }

                ShowInGameMessage("ONI AI: request sent", new Color(0.70f, 1.00f, 0.75f, 1.00f), 2.5f);
            }
            finally
            {
                SetBusyUiState(false);
            }
        }

        private void ShowInGameMessage(string text)
        {
            ShowInGameMessage(text, new Color(0.86f, 0.94f, 1.00f, 1.00f), 2.0f);
        }

        private void ShowInGameMessage(string text, Color color, float durationSeconds)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return;
            }

            EnsureMessageOverlay();
            if (messageCanvasRoot == null || messageText == null)
            {
                return;
            }

            messageText.text = text;
            messageText.color = color;
            messageCanvasRoot.SetActive(true);
            messageHideAt = Time.unscaledTime + Mathf.Max(0.5f, durationSeconds);
        }

        private void EnsureMessageOverlay()
        {
            if (messageCanvasRoot != null && messageText != null)
            {
                return;
            }

            var root = new GameObject("OniAiOverlay");
            DontDestroyOnLoad(root);

            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = short.MaxValue;

            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.screenMatchMode = CanvasScaler.ScreenMatchMode.MatchWidthOrHeight;
            scaler.matchWidthOrHeight = 0.5f;

            var raycaster = root.AddComponent<GraphicRaycaster>();
            raycaster.enabled = false;

            var textObject = new GameObject("StatusText");
            textObject.transform.SetParent(root.transform, false);

            var rect = textObject.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 1f);
            rect.anchorMax = new Vector2(0.5f, 1f);
            rect.pivot = new Vector2(0.5f, 1f);
            rect.anchoredPosition = new Vector2(0f, -150f);
            rect.sizeDelta = new Vector2(1200f, 120f);

            var uiText = textObject.AddComponent<Text>();
            uiText.alignment = TextAnchor.MiddleCenter;
            uiText.resizeTextForBestFit = true;
            uiText.resizeTextMinSize = 16;
            uiText.resizeTextMaxSize = 34;
            uiText.horizontalOverflow = HorizontalWrapMode.Wrap;
            uiText.verticalOverflow = VerticalWrapMode.Truncate;
            uiText.color = new Color(0.86f, 0.94f, 1.00f, 1.00f);
            uiText.raycastTarget = false;

            var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            if (font != null)
            {
                uiText.font = font;
            }

            var outline = textObject.AddComponent<Outline>();
            outline.effectColor = new Color(0f, 0f, 0f, 0.85f);
            outline.effectDistance = new Vector2(1f, -1f);

            root.SetActive(false);

            messageCanvasRoot = root;
            messageText = uiText;
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
            string requestRoot = GetRequestRootDirectory();
            string requestDir = Path.Combine(requestRoot, requestId);
            string logsDir = Path.Combine(requestDir, "logs");

            Directory.CreateDirectory(requestDir);
            Directory.CreateDirectory(logsDir);

            string screenshotPath = Path.Combine(requestDir, "screenshot.png");
            ScreenCapture.CaptureScreenshot(screenshotPath);

            string screenshotRelativePath = "screenshot.png";

            JObject state = BuildStatePayload(requestId, previousSpeed, screenshotRelativePath);
            string apiBaseUrl = BuildApiBaseUrl();
            JObject bridgePayload = BuildBridgePayload(state, requestDir, apiBaseUrl);
            UpdateLastStateSnapshot(state);

            return new RequestContext
            {
                RequestId = requestId,
                RequestDir = requestDir,
                PayloadJson = bridgePayload.ToString(Formatting.None)
            };
        }

        private string BuildApiBaseUrl()
        {
            if (config == null)
            {
                return string.Empty;
            }

            string host = string.IsNullOrWhiteSpace(config.HttpServerHost) ? "127.0.0.1" : config.HttpServerHost.Trim();
            return "http://" + host + ":" + config.HttpServerPort.ToString(CultureInfo.InvariantCulture);
        }

        private string GetRequestRootDirectory()
        {
            string configured = config != null ? config.RequestRootDir : string.Empty;
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (Path.IsPathRooted(configured))
                {
                    return configured;
                }

                return Path.GetFullPath(Path.Combine(GetModDirectory(), configured));
            }

            return Path.Combine(Path.GetTempPath(), "oni_ai_assistant", "requests");
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

        private static JArray BuildDuplicantsSnapshot()
        {
            var snapshot = new JArray();
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>();
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || behaviour.gameObject == null)
                {
                    continue;
                }

                Type identityType = behaviour.GetType();
                if (!string.Equals(identityType.Name, "MinionIdentity", StringComparison.Ordinal))
                {
                    continue;
                }

                string duplicateId = behaviour.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                string duplicateName = ResolveDuplicantName(behaviour);

                var item = new JObject
                {
                    ["id"] = duplicateId,
                    ["name"] = duplicateName,
                    ["status"] = BuildDuplicantStatusObject(behaviour),
                    ["priority"] = BuildDuplicantPriorityObject(behaviour),
                    ["skills"] = BuildDuplicantSkillsArray(behaviour)
                };

                snapshot.Add(item);
            }

            return snapshot;
        }

        private static JArray BuildPendingActionsSnapshot()
        {
            var pending = new JArray();
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>();
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || behaviour.gameObject == null)
                {
                    continue;
                }

                if (!string.Equals(behaviour.GetType().Name, "MinionIdentity", StringComparison.Ordinal))
                {
                    continue;
                }

                string duplicantId = behaviour.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                string duplicantName = ResolveDuplicantName(behaviour);

                var item = new JObject
                {
                    ["duplicant_id"] = duplicantId,
                    ["duplicant_name"] = duplicantName
                };

                JObject status = BuildDuplicantStatusObject(behaviour);
                if (status.TryGetValue("current_chore", StringComparison.OrdinalIgnoreCase, out JToken currentChore))
                {
                    item["current_action"] = currentChore;
                }

                Component choreConsumer = FindComponentByTypeName(behaviour.gameObject, "ChoreConsumer");
                if (choreConsumer != null)
                {
                    object choreList = TryGetMemberValue(choreConsumer, "chores")
                        ?? TryGetMemberValue(choreConsumer, "availableChores");
                    JToken choreToken = ConvertToJToken(choreList, 2);
                    if (choreToken != null)
                    {
                        item["chores"] = choreToken;
                    }
                }

                pending.Add(item);
            }

            return pending;
        }

        private static JArray BuildPrioritiesSnapshot()
        {
            var priorities = new JArray();
            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>();
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || behaviour.gameObject == null)
                {
                    continue;
                }

                if (!string.Equals(behaviour.GetType().Name, "MinionIdentity", StringComparison.Ordinal))
                {
                    continue;
                }

                var item = new JObject
                {
                    ["duplicant_id"] = behaviour.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture),
                    ["duplicant_name"] = ResolveDuplicantName(behaviour),
                    ["values"] = BuildDuplicantPriorityObject(behaviour)
                };

                priorities.Add(item);
            }

            return priorities;
        }

        private static string ResolveDuplicantName(MonoBehaviour identity)
        {
            object nameFromMethod = TryInvokeNoArg(identity, "GetProperName");
            if (nameFromMethod is string properName && !string.IsNullOrWhiteSpace(properName))
            {
                return properName.Trim();
            }

            object nameFromProperty = TryGetMemberValue(identity, "name");
            if (nameFromProperty is string directName && !string.IsNullOrWhiteSpace(directName))
            {
                return directName.Trim();
            }

            return identity.gameObject.name;
        }

        private static JObject BuildDuplicantStatusObject(MonoBehaviour identity)
        {
            var status = new JObject
            {
                ["active_self"] = identity.gameObject.activeSelf,
                ["active_in_hierarchy"] = identity.gameObject.activeInHierarchy
            };

            Component choreConsumer = FindComponentByTypeName(identity.gameObject, "ChoreConsumer");
            if (choreConsumer != null)
            {
                object chore = TryGetMemberValue(choreConsumer, "chore");
                if (chore != null)
                {
                    status["current_chore"] = chore.ToString();
                }
            }

            return status;
        }

        private static JObject BuildDuplicantPriorityObject(MonoBehaviour identity)
        {
            var priority = new JObject();
            Component minionResume = FindComponentByTypeName(identity.gameObject, "MinionResume");
            if (minionResume == null)
            {
                return priority;
            }

            object priorities = TryGetMemberValue(minionResume, "personalPriorities")
                ?? TryGetMemberValue(minionResume, "priorityTable")
                ?? TryGetMemberValue(minionResume, "chorePriorities");

            JToken token = ConvertToJToken(priorities, 2);
            if (token != null && token.Type == JTokenType.Object)
            {
                return (JObject)token;
            }

            return priority;
        }

        private static JArray BuildDuplicantSkillsArray(MonoBehaviour identity)
        {
            Component minionResume = FindComponentByTypeName(identity.gameObject, "MinionResume");
            if (minionResume == null)
            {
                return new JArray();
            }

            object skills = TryGetMemberValue(minionResume, "MasteredSkillIDs")
                ?? TryGetMemberValue(minionResume, "masteredSkills")
                ?? TryGetMemberValue(minionResume, "skillAptitudes")
                ?? TryGetMemberValue(minionResume, "Skills");

            JToken token = ConvertToJToken(skills, 2);
            if (token != null && token.Type == JTokenType.Array)
            {
                return (JArray)token;
            }

            return new JArray();
        }

        private static Component FindComponentByTypeName(GameObject gameObject, string typeName)
        {
            foreach (Component component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    continue;
                }

                if (string.Equals(component.GetType().Name, typeName, StringComparison.Ordinal))
                {
                    return component;
                }
            }

            return null;
        }

        private static object TryInvokeNoArg(object instance, string methodName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(methodName))
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                MethodInfo method = instance.GetType().GetMethod(methodName, flags, null, Type.EmptyTypes, null);
                if (method == null)
                {
                    return null;
                }

                return method.Invoke(instance, null);
            }
            catch
            {
                return null;
            }
        }

        private static object TryGetMemberValue(object instance, string memberName)
        {
            if (instance == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            Type type = instance.GetType();

            try
            {
                PropertyInfo property = type.GetProperty(memberName, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(instance, null);
                }
            }
            catch
            {
            }

            try
            {
                FieldInfo field = type.GetField(memberName, flags);
                if (field != null)
                {
                    return field.GetValue(instance);
                }
            }
            catch
            {
            }

            return null;
        }

        private static JToken ConvertToJToken(object value, int depth)
        {
            if (value == null)
            {
                return JValue.CreateNull();
            }

            if (depth <= 0)
            {
                return value.ToString();
            }

            if (value is string || value is bool || value is int || value is long || value is float || value is double || value is decimal)
            {
                return JToken.FromObject(value);
            }

            if (value is IEnumerable enumerable && !(value is IDictionary))
            {
                var array = new JArray();
                int count = 0;
                foreach (object item in enumerable)
                {
                    if (count >= 32)
                    {
                        break;
                    }

                    array.Add(ConvertToJToken(item, depth - 1));
                    count++;
                }

                return array;
            }

            if (value is IDictionary dictionary)
            {
                var obj = new JObject();
                int count = 0;
                foreach (DictionaryEntry entry in dictionary)
                {
                    if (count >= 32)
                    {
                        break;
                    }

                    string key = entry.Key != null ? entry.Key.ToString() : "null";
                    obj[key] = ConvertToJToken(entry.Value, depth - 1);
                    count++;
                }

                return obj;
            }

            return value.ToString();
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

        private static bool ApplyDuplicantStatusUpdate(JObject parameters, out string message)
        {
            message = "set_duplicant_status requires target duplicant and currently supports params.active boolean";
            if (!TryResolveTargetDuplicant(parameters, out MonoBehaviour identity))
            {
                return false;
            }

            if (!parameters.TryGetValue("active", StringComparison.OrdinalIgnoreCase, out JToken activeToken) || activeToken.Type != JTokenType.Boolean)
            {
                message = "Target duplicant found, but params.active boolean is missing";
                return false;
            }

            bool active = activeToken.Value<bool>();
            identity.gameObject.SetActive(active);
            message = "Duplicant active state updated";
            return true;
        }

        private static bool ApplyDuplicantPriorityUpdate(JObject parameters, out string message)
        {
            message = "set_duplicant_priority requires target duplicant and valid priorities map";
            if (!TryResolveTargetDuplicant(parameters, out MonoBehaviour identity))
            {
                message = "Target duplicant not found for set_duplicant_priority";
                return false;
            }

            Component resume = FindComponentByTypeName(identity.gameObject, "MinionResume");
            if (resume == null)
            {
                message = "Target duplicant has no MinionResume component";
                return false;
            }

            JToken prioritiesToken = parameters["priorities"];
            if (!(prioritiesToken is JObject prioritiesObject) || !prioritiesObject.Properties().Any())
            {
                message = "params.priorities object is required for set_duplicant_priority";
                return false;
            }

            int applied = 0;
            foreach (JProperty property in prioritiesObject.Properties())
            {
                if (property.Value.Type != JTokenType.Integer)
                {
                    continue;
                }

                string key = property.Name;
                int value = property.Value.Value<int>();
                bool ok = TryInvokePriorityMethod(resume, key, value);
                if (ok)
                {
                    applied++;
                }
            }

            if (applied > 0)
            {
                message = "Applied " + applied.ToString(CultureInfo.InvariantCulture) + " priority entries";
                return true;
            }

            message = "Priority update failed: unresolved chore group id(s) or runtime call rejected";
            return false;
        }

        private static bool ApplyDuplicantSkillsUpdate(JObject parameters, out string message)
        {
            message = "set_duplicant_skills accepted but ONI runtime mapping is not fully implemented";
            if (!TryResolveTargetDuplicant(parameters, out MonoBehaviour identity))
            {
                message = "Target duplicant not found for set_duplicant_skills";
                return false;
            }

            Component resume = FindComponentByTypeName(identity.gameObject, "MinionResume");
            if (resume == null)
            {
                message = "Target duplicant has no MinionResume component";
                return false;
            }

            JToken skillsToken = parameters["skills"];
            if (!(skillsToken is JArray skillsArray) || skillsArray.Count == 0)
            {
                message = "params.skills array is required for set_duplicant_skills";
                return false;
            }

            int applied = 0;
            foreach (JToken token in skillsArray)
            {
                if (token.Type != JTokenType.String)
                {
                    continue;
                }

                string skillId = token.Value<string>();
                if (TryInvokeSkillMethod(resume, skillId))
                {
                    applied++;
                }
            }

            if (applied > 0)
            {
                message = "Applied " + applied.ToString(CultureInfo.InvariantCulture) + " skill entries";
                return true;
            }

            message = "Skill update failed: no compatible runtime skill method found";
            return false;
        }

        private static bool TryResolveTargetDuplicant(JObject parameters, out MonoBehaviour identity)
        {
            identity = null;
            if (parameters == null)
            {
                return false;
            }

            string targetId = (parameters.Value<string>("duplicant_id") ?? string.Empty).Trim();
            string targetName = (parameters.Value<string>("duplicant_name") ?? string.Empty).Trim();

            MonoBehaviour[] behaviours = FindObjectsOfType<MonoBehaviour>();
            foreach (MonoBehaviour behaviour in behaviours)
            {
                if (behaviour == null || behaviour.gameObject == null)
                {
                    continue;
                }

                if (!string.Equals(behaviour.GetType().Name, "MinionIdentity", StringComparison.Ordinal))
                {
                    continue;
                }

                string candidateId = behaviour.gameObject.GetInstanceID().ToString(CultureInfo.InvariantCulture);
                string candidateName = ResolveDuplicantName(behaviour);

                bool idMatch = !string.IsNullOrWhiteSpace(targetId) && string.Equals(candidateId, targetId, StringComparison.OrdinalIgnoreCase);
                bool nameMatch = !string.IsNullOrWhiteSpace(targetName) && string.Equals(candidateName, targetName, StringComparison.OrdinalIgnoreCase);

                if (idMatch || nameMatch)
                {
                    identity = behaviour;
                    return true;
                }
            }

            return false;
        }

        private bool TryApplyDigAction(JObject parameters, out string message)
        {
            message = "dig requires params.cells or params.cell";
            List<int> cells = ResolveCellsFromParameters(parameters);
            if (cells.Count == 0)
            {
                return false;
            }

            if (!TryGetRuntimeToolInstance("DigTool", out object digTool))
            {
                message = "DigTool instance unavailable";
                return false;
            }

            int applied = 0;
            foreach (int cell in cells)
            {
                bool ok = TryInvokeMethodByName(digTool, "OnDragTool", new object[] { cell, 0 });
                if (!ok)
                {
                    MethodInfo placeDig = digTool.GetType().GetMethod("PlaceDig", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(int), typeof(int) }, null);
                    if (placeDig != null)
                    {
                        try
                        {
                            ok = placeDig.Invoke(null, new object[] { cell, 0 }) != null;
                        }
                        catch
                        {
                        }
                    }
                }

                if (ok)
                {
                    applied++;
                }
            }

            if (applied <= 0)
            {
                message = "Unable to invoke DigTool mark methods for requested cells";
                return false;
            }

            message = "Applied dig to " + applied.ToString(CultureInfo.InvariantCulture) + " cells";
            return true;
        }

        private bool TryApplyDeconstructAction(JObject parameters, out string message)
        {
            message = "deconstruct requires params.cells or params.cell";
            List<int> cells = ResolveCellsFromParameters(parameters);
            if (cells.Count == 0)
            {
                return false;
            }

            if (!TryGetRuntimeToolInstance("DeconstructTool", out object deconstructTool))
            {
                message = "DeconstructTool instance unavailable";
                return false;
            }

            int applied = 0;
            foreach (int cell in cells)
            {
                if (TryInvokeMethodByName(deconstructTool, "DeconstructCell", new object[] { cell })
                    || TryInvokeMethodByName(deconstructTool, "OnDragTool", new object[] { cell, 0 }))
                {
                    applied++;
                }
            }

            if (applied <= 0)
            {
                message = "Unable to invoke DeconstructTool mark methods for requested cells";
                return false;
            }

            message = "Applied deconstruct to " + applied.ToString(CultureInfo.InvariantCulture) + " cells";
            return true;
        }

        private bool TryApplyBuildAction(JObject parameters, out string message)
        {
            message = "build requires building id and target cells";
            string buildingId = (parameters.Value<string>("building_id")
                ?? parameters.Value<string>("building")
                ?? parameters.Value<string>("prefab_id")
                ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(buildingId))
            {
                return false;
            }

            List<int> cells = ResolveCellsFromParameters(parameters);
            if (cells.Count == 0)
            {
                message = "build requires params.cells or params.cell";
                return false;
            }

            if (!TryResolveBuildingDef(buildingId, out object buildingDef))
            {
                message = "Building definition not found for id=" + buildingId;
                return false;
            }

            if (!TryGetRuntimeToolInstance("BuildTool", out object buildTool))
            {
                message = "BuildTool instance unavailable";
                return false;
            }

            object selectedElements = null;
            MethodInfo defaultElementsMethod = buildingDef.GetType().GetMethod("DefaultElements", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (defaultElementsMethod != null)
            {
                try
                {
                    selectedElements = defaultElementsMethod.Invoke(buildingDef, null);
                }
                catch
                {
                }
            }

            if (selectedElements == null)
            {
                message = "Building definition could not provide default construction elements for id=" + buildingId;
                return false;
            }

            bool selected = TryInvokeMethodByName(buildTool, "Activate", new[] { buildingDef, selectedElements })
                || TryInvokeMethodByName(buildTool, "SetSelectedBuildingDef", new[] { buildingDef })
                || TryInvokeMethodByName(buildTool, "SetToolParameter", new[] { buildingDef })
                || TryInvokeMethodByName(buildTool, "SetBuildingDef", new[] { buildingDef })
                || TryInvokeMethodByName(buildTool, "SetDef", new[] { buildingDef });

            if (!selected)
            {
                message = "BuildTool could not accept selected building def for id=" + buildingId;
                return false;
            }

            int applied = 0;
            foreach (int cell in cells)
            {
                if (TryInvokeMethodByName(buildTool, "TryBuild", new object[] { cell })
                    || TryInvokeMethodByName(buildTool, "OnDragTool", new object[] { cell, 0 }))
                {
                    applied++;
                }
            }

            if (applied <= 0)
            {
                message = "BuildTool did not accept placement cells for id=" + buildingId;
                return false;
            }

            message = "Applied build id=" + buildingId + " to " + applied.ToString(CultureInfo.InvariantCulture) + " cells";
            return true;
        }

        private bool TryApplyPriorityAction(JObject parameters, out string message)
        {
            if (ApplyDuplicantPriorityUpdate(parameters, out message))
            {
                return true;
            }

            return false;
        }

        private bool TryApplyArrangementAction(JObject parameters, out string message)
        {
            JArray actionItems = parameters["actions"] as JArray;
            if (actionItems == null || actionItems.Count == 0)
            {
                message = "arrangement requires params.actions array";
                return false;
            }

            if (!TryOptimizeArrangementActions(actionItems, out List<JObject> optimizedActions, out string optimizeMessage))
            {
                message = optimizeMessage;
                return false;
            }

            var speedControl = SpeedControlScreen.Instance;
            int resultingSpeed = speedControl != null ? Mathf.Clamp(speedControl.GetSpeed(), 1, 3) : 1;
            bool keepPaused = false;

            foreach (JObject action in optimizedActions)
            {
                string actionType = (action.Value<string>("type") ?? string.Empty).Trim().ToLowerInvariant();
                JObject actionParams = action["params"] as JObject ?? new JObject();

                if (string.IsNullOrWhiteSpace(actionType))
                {
                    message = "arrangement action is missing type";
                    return false;
                }

                if (string.Equals(actionType, "arrangement", StringComparison.Ordinal))
                {
                    message = "arrangement cannot contain nested arrangement action";
                    return false;
                }

                switch (actionType)
                {
                    case "set_speed":
                    {
                        int speed = actionParams.Value<int?>("speed") ?? 0;
                        if (speed < 1 || speed > 3)
                        {
                            message = "arrangement set_speed requires speed in [1,3]";
                            return false;
                        }

                        resultingSpeed = speed;
                        break;
                    }
                    case "pause":
                    {
                        keepPaused = true;
                        break;
                    }
                    case "resume":
                    {
                        keepPaused = false;
                        break;
                    }
                    case "no_op":
                    {
                        break;
                    }
                    case "build":
                    {
                        if (!TryApplyBuildAction(actionParams, out string applyMessage))
                        {
                            message = applyMessage;
                            return false;
                        }

                        break;
                    }
                    case "dig":
                    {
                        if (!TryApplyDigAction(actionParams, out string applyMessage))
                        {
                            message = applyMessage;
                            return false;
                        }

                        break;
                    }
                    case "deconstruct":
                    {
                        if (!TryApplyDeconstructAction(actionParams, out string applyMessage))
                        {
                            message = applyMessage;
                            return false;
                        }

                        break;
                    }
                    case "priority":
                    {
                        if (!TryApplyPriorityAction(actionParams, out string applyMessage))
                        {
                            message = applyMessage;
                            return false;
                        }

                        break;
                    }
                    case "research":
                    {
                        if (!TryApplyResearchAction(actionParams, out string applyMessage))
                        {
                            message = applyMessage;
                            return false;
                        }

                        break;
                    }
                    case "set_duplicant_status":
                    {
                        if (!ApplyDuplicantStatusUpdate(actionParams, out string applyMessage))
                        {
                            message = applyMessage;
                            return false;
                        }

                        break;
                    }
                    case "set_duplicant_priority":
                    {
                        if (!ApplyDuplicantPriorityUpdate(actionParams, out string applyMessage))
                        {
                            message = applyMessage;
                            return false;
                        }

                        break;
                    }
                    case "set_duplicant_skills":
                    {
                        if (!ApplyDuplicantSkillsUpdate(actionParams, out string applyMessage))
                        {
                            message = applyMessage;
                            return false;
                        }

                        break;
                    }
                    default:
                    {
                        message = "arrangement action type not supported: " + actionType;
                        return false;
                    }
                }
            }

            if (keepPaused)
            {
                PauseGame(speedControl);
            }
            else
            {
                ResumeGame(speedControl, resultingSpeed);
            }

            UpdateLastPlanSnapshot(JArray.FromObject(optimizedActions));
            message = "Applied " + optimizedActions.Count.ToString(CultureInfo.InvariantCulture) + " arranged actions; " + optimizeMessage;
            return true;
        }

        private bool TryOptimizeArrangementActions(JArray actionItems, out List<JObject> optimizedActions, out string message)
        {
            optimizedActions = new List<JObject>();
            message = "arrangement optimization completed";

            var nodes = new List<ArrangementNode>();
            var indexById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            for (int index = 0; index < actionItems.Count; index++)
            {
                if (!(actionItems[index] is JObject actionObject))
                {
                    message = "arrangement action item at index=" + index.ToString(CultureInfo.InvariantCulture) + " is not an object";
                    return false;
                }

                string actionType = (actionObject.Value<string>("type") ?? string.Empty).Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(actionType))
                {
                    message = "arrangement action item at index=" + index.ToString(CultureInfo.InvariantCulture) + " is missing type";
                    return false;
                }

                string actionId = (actionObject.Value<string>("id") ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(actionId))
                {
                    actionId = "arranged-" + index.ToString(CultureInfo.InvariantCulture);
                }
                else if (indexById.ContainsKey(actionId))
                {
                    message = "arrangement contains duplicate action id=" + actionId;
                    return false;
                }

                JObject paramsObject = actionObject["params"] as JObject ?? new JObject();
                var normalized = (JObject)actionObject.DeepClone();
                normalized["id"] = actionId;
                normalized["type"] = actionType;
                normalized["params"] = paramsObject;

                var node = new ArrangementNode
                {
                    Index = index,
                    Id = actionId,
                    Type = actionType,
                    Action = normalized,
                    Params = paramsObject,
                    Rank = GetArrangementActionRank(actionType),
                    Cells = new HashSet<int>(ResolveCellsFromParameters(paramsObject))
                };

                nodes.Add(node);
                indexById[actionId] = index;
            }

            for (int index = 0; index < nodes.Count; index++)
            {
                ArrangementNode node = nodes[index];
                foreach (string dependencyId in EnumerateArrangementDependencyIds(node.Action))
                {
                    if (!indexById.TryGetValue(dependencyId, out int dependencyIndex))
                    {
                        message = "arrangement action id=" + node.Id + " depends on unknown id=" + dependencyId;
                        return false;
                    }

                    if (dependencyIndex == index)
                    {
                        message = "arrangement action id=" + node.Id + " cannot depend on itself";
                        return false;
                    }

                    node.Dependencies.Add(dependencyIndex);
                }

                foreach (string beforeId in EnumerateArrangementBeforeIds(node.Action))
                {
                    if (!indexById.TryGetValue(beforeId, out int beforeIndex))
                    {
                        message = "arrangement action id=" + node.Id + " references unknown before id=" + beforeId;
                        return false;
                    }

                    if (beforeIndex == index)
                    {
                        message = "arrangement action id=" + node.Id + " cannot be before itself";
                        return false;
                    }

                    nodes[beforeIndex].Dependencies.Add(index);
                }
            }

            for (int index = 0; index < nodes.Count; index++)
            {
                ArrangementNode node = nodes[index];
                if (node.Cells.Count == 0)
                {
                    continue;
                }

                if (!string.Equals(node.Type, "build", StringComparison.Ordinal))
                {
                    continue;
                }

                for (int candidateIndex = 0; candidateIndex < nodes.Count; candidateIndex++)
                {
                    if (candidateIndex == index)
                    {
                        continue;
                    }

                    ArrangementNode candidate = nodes[candidateIndex];
                    bool isPrerequisiteType = string.Equals(candidate.Type, "dig", StringComparison.Ordinal)
                        || string.Equals(candidate.Type, "deconstruct", StringComparison.Ordinal);
                    if (!isPrerequisiteType || candidate.Cells.Count == 0)
                    {
                        continue;
                    }

                    if (candidate.Cells.Overlaps(node.Cells))
                    {
                        node.Dependencies.Add(candidateIndex);
                    }
                }
            }

            int nodeCount = nodes.Count;
            int[] inDegree = new int[nodeCount];
            var reverseEdges = new List<int>[nodeCount];
            for (int index = 0; index < nodeCount; index++)
            {
                reverseEdges[index] = new List<int>();
            }

            for (int index = 0; index < nodeCount; index++)
            {
                foreach (int dependency in nodes[index].Dependencies)
                {
                    inDegree[index]++;
                    reverseEdges[dependency].Add(index);
                }
            }

            var ready = new List<int>();
            for (int index = 0; index < nodeCount; index++)
            {
                if (inDegree[index] == 0)
                {
                    ready.Add(index);
                }
            }

            var orderedIndexes = new List<int>(nodeCount);
            while (ready.Count > 0)
            {
                int bestPosition = 0;
                int bestIndex = ready[0];
                for (int position = 1; position < ready.Count; position++)
                {
                    int candidateIndex = ready[position];
                    ArrangementNode candidate = nodes[candidateIndex];
                    ArrangementNode best = nodes[bestIndex];

                    if (candidate.Rank < best.Rank || (candidate.Rank == best.Rank && candidate.Index < best.Index))
                    {
                        bestPosition = position;
                        bestIndex = candidateIndex;
                    }
                }

                ready.RemoveAt(bestPosition);
                orderedIndexes.Add(bestIndex);

                foreach (int dependentIndex in reverseEdges[bestIndex])
                {
                    inDegree[dependentIndex]--;
                    if (inDegree[dependentIndex] == 0)
                    {
                        ready.Add(dependentIndex);
                    }
                }
            }

            if (orderedIndexes.Count != nodeCount)
            {
                var blocked = new List<string>();
                for (int index = 0; index < nodeCount; index++)
                {
                    if (inDegree[index] > 0)
                    {
                        blocked.Add(nodes[index].Id);
                    }
                }

                message = "arrangement dependencies contain cycle; blocked ids=" + string.Join(",", blocked.ToArray());
                return false;
            }

            bool changed = false;
            for (int position = 0; position < orderedIndexes.Count; position++)
            {
                int nodeIndex = orderedIndexes[position];
                optimizedActions.Add((JObject)nodes[nodeIndex].Action.DeepClone());
                if (nodeIndex != position)
                {
                    changed = true;
                }
            }

            message = changed ? "arrangement reordered actions using dependencies" : "arrangement kept provided order (already optimal)";
            return true;
        }

        private static IEnumerable<string> EnumerateArrangementDependencyIds(JObject action)
        {
            foreach (string value in EnumerateStringValues(action["after"]))
            {
                yield return value;
            }

            foreach (string value in EnumerateStringValues(action["depends_on"]))
            {
                yield return value;
            }

            foreach (string value in EnumerateStringValues(action["dependsOn"]))
            {
                yield return value;
            }
        }

        private static IEnumerable<string> EnumerateArrangementBeforeIds(JObject action)
        {
            foreach (string value in EnumerateStringValues(action["before"]))
            {
                yield return value;
            }
        }

        private static IEnumerable<string> EnumerateStringValues(JToken token)
        {
            if (token == null)
            {
                yield break;
            }

            if (token.Type == JTokenType.String)
            {
                string single = token.Value<string>();
                if (!string.IsNullOrWhiteSpace(single))
                {
                    yield return single.Trim();
                }

                yield break;
            }

            if (!(token is JArray array))
            {
                yield break;
            }

            foreach (JToken item in array)
            {
                if (item?.Type != JTokenType.String)
                {
                    continue;
                }

                string value = item.Value<string>();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    yield return value.Trim();
                }
            }
        }

        private static int GetArrangementActionRank(string actionType)
        {
            if (string.IsNullOrWhiteSpace(actionType))
            {
                return 50;
            }

            switch (actionType.Trim().ToLowerInvariant())
            {
                case "cancel":
                    return 0;
                case "deconstruct":
                    return 10;
                case "dig":
                    return 20;
                case "build":
                    return 30;
                case "priority":
                case "set_duplicant_priority":
                case "set_duplicant_skills":
                case "set_duplicant_status":
                    return 40;
                case "research":
                    return 50;
                case "set_speed":
                case "pause":
                case "resume":
                    return 90;
                default:
                    return 60;
            }
        }

        private sealed class ArrangementNode
        {
            public int Index;
            public string Id;
            public string Type;
            public int Rank;
            public JObject Action;
            public JObject Params;
            public HashSet<int> Dependencies = new HashSet<int>();
            public HashSet<int> Cells = new HashSet<int>();
        }

        private bool TryApplyResearchAction(JObject parameters, out string message)
        {
            message = "research requires tech id";
            string techId = (parameters.Value<string>("tech_id")
                ?? parameters.Value<string>("research_id")
                ?? parameters.Value<string>("id")
                ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(techId))
            {
                return false;
            }

            if (!TryResolveTech(techId, out object tech))
            {
                message = "Tech not found for id=" + techId;
                return false;
            }

            if (!TryGetSingletonByTypeName("Research", out object researchSingleton))
            {
                message = "Research singleton unavailable";
                return false;
            }

            bool invoked = TryInvokeMethodByName(researchSingleton, "SetActiveResearch", new[] { tech, false })
                || TryInvokeMethodByName(researchSingleton, "SetActiveResearch", new[] { tech, true })
                || TryInvokeMethodByName(researchSingleton, "SetActiveResearch", new[] { tech })
                || TryInvokeMethodByName(researchSingleton, "SetResearch", new[] { tech });

            if (!invoked)
            {
                message = "Research singleton rejected tech id=" + techId;
                return false;
            }

            message = "Set active research to id=" + techId;
            return true;
        }

        private static int ApplyCellMarkAction(object toolInstance, List<int> cells, string[] methodNames)
        {
            int applied = 0;
            foreach (int cell in cells)
            {
                bool ok = false;
                foreach (string methodName in methodNames)
                {
                    if (TryInvokeMethodByName(toolInstance, methodName, new object[] { cell }))
                    {
                        ok = true;
                        break;
                    }

                    if (TryInvokeMethodByName(toolInstance, methodName, new object[] { cell, true }))
                    {
                        ok = true;
                        break;
                    }
                }

                if (ok)
                {
                    applied++;
                }
            }

            return applied;
        }

        private static List<int> ResolveCellsFromParameters(JObject parameters)
        {
            var result = new List<int>();

            if (parameters == null)
            {
                return result;
            }

            int? directCell = parameters.Value<int?>("cell");
            if (directCell.HasValue)
            {
                result.Add(directCell.Value);
            }

            JArray cells = parameters["cells"] as JArray;
            if (cells != null)
            {
                foreach (JToken token in cells)
                {
                    if (token.Type == JTokenType.Integer)
                    {
                        result.Add(token.Value<int>());
                        continue;
                    }

                    if (token is JObject cellObject)
                    {
                        int? x = cellObject.Value<int?>("x");
                        int? y = cellObject.Value<int?>("y");
                        int? cell = cellObject.Value<int?>("cell");

                        if (cell.HasValue)
                        {
                            result.Add(cell.Value);
                            continue;
                        }

                        if (x.HasValue && y.HasValue && TryResolveCellFromXY(x.Value, y.Value, out int resolvedCell))
                        {
                            result.Add(resolvedCell);
                        }
                    }
                }
            }

            int? xSingle = parameters.Value<int?>("x");
            int? ySingle = parameters.Value<int?>("y");
            if (xSingle.HasValue && ySingle.HasValue && TryResolveCellFromXY(xSingle.Value, ySingle.Value, out int singleCell))
            {
                result.Add(singleCell);
            }

            return result.Distinct().ToList();
        }

        private static bool TryResolveCellFromXY(int x, int y, out int cell)
        {
            cell = -1;
            Type gridType = FindRuntimeType("Grid");
            if (gridType == null)
            {
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            MethodInfo method = gridType.GetMethod("XYToCell", flags, null, new[] { typeof(int), typeof(int) }, null);
            if (method != null)
            {
                try
                {
                    object value = method.Invoke(null, new object[] { x, y });
                    if (value is int cellValue)
                    {
                        cell = cellValue;
                        return true;
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        private static bool TryResolveBuildingDef(string id, out object buildingDef)
        {
            buildingDef = null;
            Type assetsType = FindRuntimeType("Assets");
            if (assetsType == null)
            {
                return false;
            }

            MethodInfo getBuildingDef = assetsType.GetMethod("GetBuildingDef", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (getBuildingDef != null)
            {
                try
                {
                    object resolved = getBuildingDef.Invoke(null, new object[] { id });
                    if (resolved != null)
                    {
                        buildingDef = resolved;
                        return true;
                    }
                }
                catch
                {
                }
            }

            if (!(TryGetStaticMemberValue(assetsType, "BuildingDefs") is IEnumerable collection))
            {
                return false;
            }

            foreach (object item in collection)
            {
                if (item == null)
                {
                    continue;
                }

                string itemId = (TryGetMemberValue(item, "PrefabID") ?? TryGetMemberValue(item, "ID") ?? string.Empty).ToString();
                if (string.Equals(itemId, id, StringComparison.OrdinalIgnoreCase))
                {
                    buildingDef = item;
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveTech(string id, out object tech)
        {
            tech = null;
            if (!TryGetDbCollection("Techs", out IEnumerable collection))
            {
                return false;
            }

            foreach (object item in collection)
            {
                if (item == null)
                {
                    continue;
                }

                string itemId = (TryGetMemberValue(item, "Id") ?? TryGetMemberValue(item, "ID") ?? string.Empty).ToString();
                if (string.Equals(itemId, id, StringComparison.OrdinalIgnoreCase))
                {
                    tech = item;
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetDbCollection(string memberName, out IEnumerable collection)
        {
            collection = null;
            Type dbType = FindRuntimeType("Db");
            if (dbType == null)
            {
                return false;
            }

            object db = null;
            MethodInfo getMethod = dbType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (getMethod != null)
            {
                try
                {
                    db = getMethod.Invoke(null, null);
                }
                catch
                {
                }
            }

            if (db == null)
            {
                db = TryGetStaticMemberValue(dbType, "Instance") ?? TryGetStaticMemberValue(dbType, "instance");
            }

            if (db == null)
            {
                return false;
            }

            object raw = TryGetMemberValue(db, memberName);
            if (raw == null)
            {
                return false;
            }

            if (raw is IEnumerable enumerable)
            {
                collection = enumerable;
                return true;
            }

            object resources = TryGetMemberValue(raw, "resources") ?? TryGetMemberValue(raw, "Resources") ?? TryGetMemberValue(raw, "items");
            if (resources is IEnumerable nested)
            {
                collection = nested;
                return true;
            }

            return false;
        }

        private static bool TryGetRuntimeToolInstance(string typeName, out object tool)
        {
            return TryGetSingletonByTypeName(typeName, out tool);
        }

        private static bool TryGetSingletonByTypeName(string typeName, out object instance)
        {
            instance = null;
            Type runtimeType = FindRuntimeType(typeName);
            if (runtimeType == null)
            {
                return false;
            }

            instance = TryGetStaticMemberValue(runtimeType, "Instance")
                ?? TryGetStaticMemberValue(runtimeType, "instance")
                ?? TryGetStaticMemberValue(runtimeType, "Inst");
            if (instance != null)
            {
                return true;
            }

            foreach (MonoBehaviour behaviour in Resources.FindObjectsOfTypeAll<MonoBehaviour>())
            {
                if (behaviour == null)
                {
                    continue;
                }

                if (behaviour.GetType() == runtimeType || string.Equals(behaviour.GetType().Name, typeName, StringComparison.Ordinal))
                {
                    instance = behaviour;
                    return true;
                }
            }

            return false;
        }

        private static Type FindRuntimeType(string typeName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type match = assembly.GetTypes().FirstOrDefault(type => string.Equals(type.Name, typeName, StringComparison.Ordinal));
                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static object TryGetStaticMemberValue(Type type, string memberName)
        {
            if (type == null || string.IsNullOrWhiteSpace(memberName))
            {
                return null;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                PropertyInfo property = type.GetProperty(memberName, flags);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(null, null);
                }
            }
            catch
            {
            }

            try
            {
                FieldInfo field = type.GetField(memberName, flags);
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

        private static bool TryInvokePriorityMethod(Component resume, string priorityKey, int value)
        {
            if (resume == null || resume.gameObject == null || string.IsNullOrWhiteSpace(priorityKey))
            {
                return false;
            }

            Component choreConsumer = FindComponentByTypeName(resume.gameObject, "ChoreConsumer");
            if (choreConsumer == null)
            {
                return false;
            }

            if (!TryResolveChoreGroup(priorityKey, out object choreGroup))
            {
                return false;
            }

            int clamped = Mathf.Clamp(value, 0, 5);
            if (TryInvokeMethodByName(choreConsumer, "SetPersonalPriority", new[] { choreGroup, clamped }))
            {
                return true;
            }

            return false;
        }

        private static bool TryResolveChoreGroup(string priorityKey, out object choreGroup)
        {
            choreGroup = null;
            Type dbType = FindRuntimeType("Db");
            if (dbType == null)
            {
                return false;
            }

            MethodInfo getMethod = dbType.GetMethod("Get", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            if (getMethod == null)
            {
                return false;
            }

            object db;
            try
            {
                db = getMethod.Invoke(null, null);
            }
            catch
            {
                return false;
            }

            if (db == null)
            {
                return false;
            }

            object groups = TryGetMemberValue(db, "ChoreGroups");
            if (groups == null)
            {
                return false;
            }

            string requested = priorityKey.Trim();
            MethodInfo tryGet = groups.GetType().GetMethod("TryGet", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(string) }, null);
            if (tryGet != null)
            {
                try
                {
                    object direct = tryGet.Invoke(groups, new object[] { requested });
                    if (direct != null)
                    {
                        choreGroup = direct;
                        return true;
                    }
                }
                catch
                {
                }
            }

            if (!(TryGetMemberValue(groups, "resources") is IEnumerable resources))
            {
                return false;
            }

            string normalizedRequested = requested.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
            foreach (object item in resources)
            {
                if (item == null)
                {
                    continue;
                }

                string id = (TryGetMemberValue(item, "Id") ?? string.Empty).ToString();
                string normalizedId = id.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);
                if (string.Equals(id, requested, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(normalizedId, normalizedRequested, StringComparison.OrdinalIgnoreCase))
                {
                    choreGroup = item;
                    return true;
                }
            }

            return false;
        }

        private static bool TryInvokeSkillMethod(Component resume, string skillId)
        {
            object[] args = { skillId };
            if (TryInvokeMethodByName(resume, "AssignSkill", args)
                || TryInvokeMethodByName(resume, "MasterSkill", args)
                || TryInvokeMethodByName(resume, "LearnSkill", args))
            {
                return true;
            }

            return false;
        }

        private static bool TryInvokeMethodByName(object instance, string methodName, object[] args)
        {
            if (instance == null)
            {
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            MethodInfo[] methods = instance.GetType().GetMethods(flags)
                .Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal))
                .ToArray();

            foreach (MethodInfo method in methods)
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length != args.Length)
                {
                    continue;
                }

                try
                {
                    object[] converted = new object[args.Length];
                    bool convertible = true;
                    for (int index = 0; index < args.Length; index++)
                    {
                        object value = args[index];
                        Type targetType = parameters[index].ParameterType;

                        if (value == null)
                        {
                            converted[index] = null;
                            continue;
                        }

                        if (targetType.IsInstanceOfType(value))
                        {
                            converted[index] = value;
                            continue;
                        }

                        if (targetType.IsEnum && value is string enumText)
                        {
                            converted[index] = Enum.Parse(targetType, enumText, true);
                            continue;
                        }

                        converted[index] = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
                    }

                    if (!convertible)
                    {
                        continue;
                    }

                    method.Invoke(instance, converted);
                    return true;
                }
                catch
                {
                }
            }

            return false;
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





        private static JObject BuildStatePayload(string requestId, int previousSpeed, string screenshotRelativePath)
        {
            var context = BuildContextObject(previousSpeed);
            return new JObject
            {
                ["request_id"] = requestId,
                ["request_dir"] = ".",
                ["screenshot_path"] = screenshotRelativePath,
                ["requested_at_utc"] = System.DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["context"] = context,
                ["duplicants"] = BuildDuplicantsSnapshot(),
                ["pending_actions"] = BuildPendingActionsSnapshot(),
                ["priorities"] = BuildPrioritiesSnapshot(),
                ["runtime_config"] = BuildRuntimeConfigObject(),
                ["assemblies"] = BuildAssembliesObject(),
                ["scenes"] = BuildScenesObject(),
                ["singletons"] = BuildSingletonSnapshot()
            };
        }

        private static JObject BuildBridgePayload(JObject state, string requestDir, string apiBaseUrl)
        {
            JObject bridgePayload = (JObject)state.DeepClone();
            bridgePayload["request_dir"] = requestDir;

            if (!string.IsNullOrWhiteSpace(apiBaseUrl))
            {
                string normalizedApiBaseUrl = apiBaseUrl.TrimEnd('/');
                bridgePayload["api_base_url"] = normalizedApiBaseUrl;
            }

            return bridgePayload;
        }
        private readonly object httpSync = new object();
        private HttpListener httpListener;
        private Thread httpThread;
        private bool httpRunning;
        private JObject lastStateSnapshot;
        private JObject lastExecutionSnapshot;
        private JArray lastPlannedActions;

        private void StartHttpServer()
        {
            if (config == null || !config.HttpServerEnabled)
            {
                return;
            }

            lock (httpSync)
            {
                if (httpRunning)
                {
                    return;
                }

                string prefix = "http://" + config.HttpServerHost + ":" + config.HttpServerPort + "/";
                try
                {
                    httpListener = new HttpListener();
                    httpListener.Prefixes.Add(prefix);
                    httpListener.Start();
                    httpRunning = true;
                    httpThread = new Thread(HttpListenLoop) { IsBackground = true };
                    httpThread.Start();
                    Debug.Log("[ONI-AI] Local HTTP server started: " + prefix);
                }
                catch (Exception exception)
                {
                    httpRunning = false;
                    Debug.LogWarning("[ONI-AI] Failed to start local HTTP server: " + exception.Message);
                }
            }
        }

        private void StopHttpServer()
        {
            lock (httpSync)
            {
                if (!httpRunning)
                {
                    return;
                }

                httpRunning = false;
                try
                {
                    httpListener?.Stop();
                    httpListener?.Close();
                }
                catch
                {
                }

                httpListener = null;
            }

            try
            {
                httpThread?.Join(200);
            }
            catch
            {
            }

            httpThread = null;
        }

        private void HttpListenLoop()
        {
            while (true)
            {
                HttpListener listener;
                lock (httpSync)
                {
                    if (!httpRunning || httpListener == null)
                    {
                        return;
                    }

                    listener = httpListener;
                }

                HttpListenerContext context;
                try
                {
                    context = listener.GetContext();
                }
                catch
                {
                    return;
                }

                try
                {
                    HandleHttpRequest(context);
                }
                catch (Exception exception)
                {
                    TryWriteJson(context.Response, 500, new JObject
                    {
                        ["error"] = "internal_error",
                        ["message"] = exception.Message
                    });
                }
            }
        }

        private void HandleHttpRequest(HttpListenerContext context)
        {
            string path = context.Request.Url != null ? context.Request.Url.AbsolutePath : "/";
            string method = context.Request.HttpMethod ?? "GET";

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/health", StringComparison.Ordinal))
            {
                if (!TryReadLiveSpeedControlState(out int speed, out bool paused))
                {
                    TryWriteJson(context.Response, 503, new JObject
                    {
                        ["error"] = "speed_control_unavailable"
                    });
                    return;
                }

                TryWriteJson(context.Response, 200, new JObject
                {
                    ["ok"] = true,
                    ["busy"] = isBusy,
                    ["current_speed"] = speed,
                    ["paused"] = paused
                });
                return;
            }

            if (path.Equals("/speed", StringComparison.Ordinal))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadLiveSpeedControlState(out int speed, out bool paused))
                    {
                        TryWriteJson(context.Response, 503, new JObject
                        {
                            ["error"] = "speed_control_unavailable"
                        });
                        return;
                    }

                    TryWriteJson(context.Response, 200, new JObject
                    {
                        ["speed"] = speed,
                        ["paused"] = paused
                    });
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    SpeedRequest payload = ReadRequestBody<SpeedRequest>(context.Request);
                    if (payload == null)
                    {
                        TryWriteJson(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                        return;
                    }

                    int speed = payload.Speed;
                    if (speed < 1 || speed > 3)
                    {
                        TryWriteJson(context.Response, 400, new JObject { ["error"] = "speed_must_be_1_2_3" });
                        return;
                    }

                    var speedControl = SpeedControlScreen.Instance;
                    if (speedControl == null)
                    {
                        throw new InvalidOperationException("SpeedControlScreen unavailable");
                    }

                    speedControl.SetSpeed(Mathf.Clamp(speed, 1, 3));
                    TryWriteJson(context.Response, 200, new JObject
                    {
                        ["status"] = "applied",
                        ["speed"] = Mathf.Clamp(speedControl.GetSpeed(), 1, 3),
                        ["paused"] = speedControl.IsPaused
                    });
                    return;
                }
            }

            if (path.Equals("/pause", StringComparison.Ordinal))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    if (!TryReadLiveSpeedControlState(out int speed, out bool paused))
                    {
                        TryWriteJson(context.Response, 503, new JObject
                        {
                            ["error"] = "speed_control_unavailable"
                        });
                        return;
                    }

                    TryWriteJson(context.Response, 200, new JObject
                    {
                        ["paused"] = paused,
                        ["speed"] = speed
                    });
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    PauseRequest payload = ReadRequestBody<PauseRequest>(context.Request);
                    if (payload == null)
                    {
                        TryWriteJson(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                        return;
                    }

                    bool? paused = payload.Paused;
                    if (!paused.HasValue)
                    {
                        TryWriteJson(context.Response, 400, new JObject { ["error"] = "paused_must_be_boolean" });
                        return;
                    }

                    var speedControl = SpeedControlScreen.Instance;
                    if (speedControl == null)
                    {
                        throw new InvalidOperationException("SpeedControlScreen unavailable");
                    }

                    if (paused.Value)
                    {
                        PauseGame(speedControl);
                    }
                    else
                    {
                        ResumeGame(speedControl, Mathf.Clamp(speedControl.GetSpeed(), 1, 3));
                    }

                    TryWriteJson(context.Response, 200, new JObject
                    {
                        ["status"] = "applied",
                        ["paused"] = speedControl.IsPaused,
                        ["speed"] = Mathf.Clamp(speedControl.GetSpeed(), 1, 3)
                    });
                    return;
                }
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/build", StringComparison.Ordinal))
            {
                JObject payload = ReadRequestBody<JObject>(context.Request);
                if (payload == null)
                {
                    TryWriteJson(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                    return;
                }

                if (!TryApplyBuildAction(payload, out string message))
                {
                    throw new InvalidOperationException("build failed: " + message);
                }

                TryWriteJson(context.Response, 200, new JObject
                {
                    ["status"] = "applied",
                    ["message"] = message
                });
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/dig", StringComparison.Ordinal))
            {
                JObject payload = ReadRequestBody<JObject>(context.Request);
                if (payload == null)
                {
                    TryWriteJson(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                    return;
                }

                if (!TryApplyDigAction(payload, out string message))
                {
                    throw new InvalidOperationException("dig failed: " + message);
                }

                TryWriteJson(context.Response, 200, new JObject
                {
                    ["status"] = "applied",
                    ["message"] = message
                });
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/deconstruct", StringComparison.Ordinal))
            {
                JObject payload = ReadRequestBody<JObject>(context.Request);
                if (payload == null)
                {
                    TryWriteJson(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                    return;
                }

                if (!TryApplyDeconstructAction(payload, out string message))
                {
                    throw new InvalidOperationException("deconstruct failed: " + message);
                }

                TryWriteJson(context.Response, 200, new JObject
                {
                    ["status"] = "applied",
                    ["message"] = message
                });
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/research", StringComparison.Ordinal))
            {
                JObject payload = ReadRequestBody<JObject>(context.Request);
                if (payload == null)
                {
                    TryWriteJson(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                    return;
                }

                if (!TryApplyResearchAction(payload, out string message))
                {
                    throw new InvalidOperationException("research failed: " + message);
                }

                TryWriteJson(context.Response, 200, new JObject
                {
                    ["status"] = "applied",
                    ["message"] = message
                });
                return;
            }

            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && path.Equals("/arrangement", StringComparison.Ordinal))
            {
                JObject payload = ReadRequestBody<JObject>(context.Request);
                if (payload == null)
                {
                    TryWriteJson(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                    return;
                }

                if (!TryApplyArrangementAction(payload, out string message))
                {
                    throw new InvalidOperationException("arrangement failed: " + message);
                }

                TryWriteJson(context.Response, 200, new JObject
                {
                    ["status"] = "applied",
                    ["message"] = message
                });
                return;
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/state", StringComparison.Ordinal))
            {
                JObject state;
                JObject execution;
                lock (httpSync)
                {
                    state = lastStateSnapshot != null ? (JObject)lastStateSnapshot.DeepClone() : null;
                    execution = lastExecutionSnapshot != null ? (JObject)lastExecutionSnapshot.DeepClone() : null;
                }

                if (state == null)
                {
                    TryWriteJson(context.Response, 404, new JObject { ["error"] = "no_state" });
                    return;
                }

                int pendingCount = 0;
                JArray pendingFromState = state["pending_actions"] as JArray;
                if (pendingFromState != null)
                {
                    pendingCount = pendingFromState.Count;
                }

                TryWriteJson(context.Response, 200, new JObject
                {
                    ["state"] = state,
                    ["last_execution"] = execution,
                    ["pending_action_count"] = pendingCount
                });
                return;
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/plan", StringComparison.Ordinal))
            {
                JArray planned;
                JObject execution;
                lock (httpSync)
                {
                    planned = lastPlannedActions != null ? (JArray)lastPlannedActions.DeepClone() : new JArray();
                    execution = lastExecutionSnapshot != null ? (JObject)lastExecutionSnapshot.DeepClone() : null;
                }

                TryWriteJson(context.Response, 200, new JObject
                {
                    ["planned_actions"] = planned,
                    ["last_execution"] = execution,
                    ["pending_actions"] = BuildPendingActionsSnapshot()
                });
                return;
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/buildings", StringComparison.Ordinal))
            {
                if (!TryBuildBuildingCatalog(out JObject catalog))
                {
                    TryWriteJson(context.Response, 503, new JObject { ["error"] = "building_catalog_unavailable" });
                    return;
                }

                TryWriteJson(context.Response, 200, catalog);
                return;
            }

            if (method.Equals("GET", StringComparison.OrdinalIgnoreCase) && path.Equals("/research", StringComparison.Ordinal))
            {
                if (!TryBuildResearchCatalog(out JObject catalog))
                {
                    TryWriteJson(context.Response, 503, new JObject { ["error"] = "research_catalog_unavailable" });
                    return;
                }

                TryWriteJson(context.Response, 200, catalog);
                return;
            }

            if (path.Equals("/priorities", StringComparison.Ordinal))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    TryWriteJson(context.Response, 200, new JObject
                    {
                        ["priorities"] = BuildPrioritiesSnapshot(),
                        ["source"] = "game_live"
                    });
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    PrioritiesRequest payload = ReadRequestBody<PrioritiesRequest>(context.Request);
                    if (payload == null)
                    {
                        TryWriteJson(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                        return;
                    }

                    List<PriorityUpdateRequest> updates = payload.Priorities ?? payload.Updates;
                    if (updates == null)
                    {
                        TryWriteJson(context.Response, 400, new JObject { ["error"] = "priorities_must_be_array" });
                        return;
                    }

                    int accepted = 0;
                    foreach (PriorityUpdateRequest update in updates)
                    {
                        if (update == null || update.Values == null || update.Values.Count == 0)
                        {
                            continue;
                        }

                        var parameters = new JObject
                        {
                            ["priorities"] = update.Values.DeepClone()
                        };

                        string duplicantId = (update.DuplicantId ?? string.Empty).Trim();
                        if (!string.IsNullOrEmpty(duplicantId))
                        {
                            parameters["duplicant_id"] = duplicantId;
                        }

                        string duplicantName = (update.DuplicantName ?? string.Empty).Trim();
                        if (!string.IsNullOrEmpty(duplicantName))
                        {
                            parameters["duplicant_name"] = duplicantName;
                        }

                        if (!ApplyDuplicantPriorityUpdate(parameters, out string message))
                        {
                            throw new InvalidOperationException("priority update failed: " + message);
                        }

                        accepted++;
                    }

                    TryWriteJson(context.Response, 200, new JObject
                    {
                        ["accepted"] = accepted,
                        ["status"] = "applied"
                    });
                    return;
                }
            }

            TryWriteJson(context.Response, 404, new JObject { ["error"] = "not_found" });
        }

        private static T ReadRequestBody<T>(HttpListenerRequest request) where T : class
        {
            try
            {
                using (var reader = new StreamReader(request.InputStream, request.ContentEncoding ?? Encoding.UTF8))
                {
                    string body = reader.ReadToEnd();
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        return null;
                    }

                    return JsonConvert.DeserializeObject<T>(body);
                }
            }
            catch
            {
                return null;
            }
        }

        private static void TryWriteJson(HttpListenerResponse response, int statusCode, JObject payload)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(payload.ToString(Newtonsoft.Json.Formatting.None));
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = bytes.Length;
                response.OutputStream.Write(bytes, 0, bytes.Length);
                response.OutputStream.Flush();
            }
            catch
            {
            }
            finally
            {
                try
                {
                    response.OutputStream.Close();
                }
                catch
                {
                }
            }
        }

        private void UpdateLastStateSnapshot(JObject state)
        {
            if (state == null)
            {
                return;
            }

            lock (httpSync)
            {
                lastStateSnapshot = (JObject)state.DeepClone();
            }
        }

        private void UpdateLastExecutionSnapshot(JObject execution)
        {
            if (execution == null)
            {
                return;
            }

            lock (httpSync)
            {
                lastExecutionSnapshot = (JObject)execution.DeepClone();
            }
        }

        private void UpdateLastPlanSnapshot(JArray actions)
        {
            if (actions == null)
            {
                return;
            }

            lock (httpSync)
            {
                lastPlannedActions = (JArray)actions.DeepClone();
            }
        }

        private static bool TryBuildBuildingCatalog(out JObject catalog)
        {
            catalog = null;
            Type assetsType = FindRuntimeType("Assets");
            if (assetsType == null)
            {
                return false;
            }

            if (!(TryGetStaticMemberValue(assetsType, "BuildingDefs") is IEnumerable collection))
            {
                return false;
            }

            var lockedByTech = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (TryGetDbCollection("Techs", out IEnumerable techCollection))
            {
                foreach (object tech in techCollection)
                {
                    if (tech == null)
                    {
                        continue;
                    }

                    bool isComplete = false;
                    MethodInfo isCompleteMethod = tech.GetType().GetMethod("IsComplete", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (isCompleteMethod != null)
                    {
                        try
                        {
                            if (isCompleteMethod.Invoke(tech, null) is bool complete)
                            {
                                isComplete = complete;
                            }
                        }
                        catch
                        {
                        }
                    }

                    if (isComplete)
                    {
                        continue;
                    }

                    if (!(TryGetMemberValue(tech, "unlockedItemIDs") is IEnumerable unlockedItems))
                    {
                        continue;
                    }

                    foreach (object item in unlockedItems)
                    {
                        string unlockedId = item?.ToString();
                        if (!string.IsNullOrWhiteSpace(unlockedId))
                        {
                            lockedByTech.Add(unlockedId);
                        }
                    }
                }
            }

            var available = new JArray();
            var potential = new JArray();
            foreach (object item in collection)
            {
                if (item == null)
                {
                    continue;
                }

                string id = (TryGetMemberValue(item, "PrefabID") ?? TryGetMemberValue(item, "ID") ?? string.Empty).ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string name = (TryGetMemberValue(item, "Name") ?? TryGetMemberValue(item, "name") ?? id).ToString();
                bool showInBuildMenu = (TryGetMemberValue(item, "ShowInBuildMenu") as bool?) ?? true;
                bool deprecated = (TryGetMemberValue(item, "Deprecated") as bool?) ?? false;
                bool unlocked = showInBuildMenu && !deprecated && !lockedByTech.Contains(id);

                var row = new JObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["category"] = (TryGetMemberValue(item, "Category") ?? string.Empty).ToString()
                };

                potential.Add(row);
                if (unlocked)
                {
                    available.Add((JObject)row.DeepClone());
                }
            }

            catalog = new JObject
            {
                ["available"] = available,
                ["potential"] = potential,
                ["counts"] = new JObject
                {
                    ["available"] = available.Count,
                    ["potential"] = potential.Count
                }
            };
            return true;
        }

        private static bool TryBuildResearchCatalog(out JObject catalog)
        {
            catalog = null;
            if (!TryGetDbCollection("Techs", out IEnumerable collection))
            {
                return false;
            }

            var available = new JArray();
            var potential = new JArray();
            foreach (object item in collection)
            {
                if (item == null)
                {
                    continue;
                }

                string id = (TryGetMemberValue(item, "Id") ?? TryGetMemberValue(item, "ID") ?? string.Empty).ToString();
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                string name = (TryGetMemberValue(item, "Name") ?? TryGetMemberValue(item, "name") ?? id).ToString();
                bool unlocked = false;
                MethodInfo isCompleteMethod = item.GetType().GetMethod("IsComplete", BindingFlags.Public | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (isCompleteMethod != null)
                {
                    try
                    {
                        if (isCompleteMethod.Invoke(item, null) is bool complete)
                        {
                            unlocked = complete;
                        }
                    }
                    catch
                    {
                    }
                }

                var row = new JObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["tier"] = (TryGetMemberValue(item, "tier") ?? TryGetMemberValue(item, "Tier") ?? string.Empty).ToString()
                };

                potential.Add(row);
                if (!unlocked)
                {
                    available.Add((JObject)row.DeepClone());
                }
            }

            string activeResearchId = string.Empty;
            if (TryGetSingletonByTypeName("Research", out object researchSingleton))
            {
                object current = TryGetMemberValue(researchSingleton, "currentResearch")
                    ?? TryGetMemberValue(researchSingleton, "CurrentResearch")
                    ?? TryGetMemberValue(researchSingleton, "activeResearch")
                    ?? TryGetMemberValue(researchSingleton, "ActiveResearch");

                if (current != null)
                {
                    object activeTech = TryGetMemberValue(current, "tech") ?? current;
                    activeResearchId = (TryGetMemberValue(activeTech, "Id") ?? TryGetMemberValue(activeTech, "ID") ?? activeTech.ToString()).ToString();
                }
            }

            catalog = new JObject
            {
                ["current"] = activeResearchId,
                ["available"] = available,
                ["potential"] = potential,
                ["counts"] = new JObject
                {
                    ["available"] = available.Count,
                    ["potential"] = potential.Count
                }
            };
            return true;
        }
        private static bool TryReadLiveSpeedControlState(out int speed, out bool paused)
        {
            speed = 1;
            paused = true;

            var speedControl = SpeedControlScreen.Instance;
            if (speedControl == null)
            {
                return false;
            }

            paused = speedControl.IsPaused;
            speed = Mathf.Clamp(speedControl.GetSpeed(), 1, 3);
            return true;
        }
        private RequestContext CreateHttpActionContext()
        {
            string timestamp = System.DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
            string requestId = "http_" + timestamp;
            string requestDir = Path.Combine(GetRequestRootDirectory(), requestId);
            string logsDir = Path.Combine(requestDir, "logs");
            Directory.CreateDirectory(logsDir);

            return new RequestContext
            {
                RequestId = requestId,
                RequestDir = requestDir,
                PayloadJson = string.Empty
            };
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

                        private sealed class SpeedRequest
        {
            [JsonProperty("speed")]
            public int Speed { get; set; }
        }

        private sealed class PauseRequest
        {
            [JsonProperty("paused")]
            public bool? Paused { get; set; }
        }

        private sealed class PriorityUpdateRequest
        {
            [JsonProperty("duplicant_id")]
            public string DuplicantId { get; set; }

            [JsonProperty("duplicant_name")]
            public string DuplicantName { get; set; }

            [JsonProperty("values")]
            public JObject Values { get; set; }
        }

        private sealed class PrioritiesRequest
        {
            [JsonProperty("priorities")]
            public List<PriorityUpdateRequest> Priorities { get; set; }

            [JsonProperty("updates")]
            public List<PriorityUpdateRequest> Updates { get; set; }
        }

        private sealed class RequestContext
        {
            public string RequestId { get; set; }

            public string RequestDir { get; set; }

            public string PayloadJson { get; set; }
        }
    }

    public sealed class OniAiConfig
    {
        public string BridgeUrl { get; private set; } = "http://127.0.0.1:8765/analyze";

        public KeyCode Hotkey { get; private set; } = KeyCode.F8;

        public bool EnableHotkey { get; private set; } = false;

        public int RequestTimeoutSeconds { get; private set; } = 120;

        public string RequestRootDir { get; private set; } = string.Empty;

        public string RuntimeDllPath { get; private set; } = string.Empty;

        public float RuntimeReloadIntervalSeconds { get; private set; } = 1.0f;

        public bool HttpServerEnabled { get; private set; } = true;

        public string HttpServerHost { get; private set; } = "127.0.0.1";

        public int HttpServerPort { get; private set; } = 8766;

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

                    continue;
                }

                if (key.Equals("request_root_dir", StringComparison.OrdinalIgnoreCase))
                {
                    config.RequestRootDir = value;
                    continue;
                }

                if (key.Equals("runtime_dll_path", StringComparison.OrdinalIgnoreCase))
                {
                    config.RuntimeDllPath = value;
                    continue;
                }

                if (key.Equals("runtime_reload_interval_seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float intervalSeconds))
                    {
                        config.RuntimeReloadIntervalSeconds = Mathf.Clamp(intervalSeconds, 0.2f, 30.0f);
                    }

                    continue;
                }

                if (key.Equals("http_server_enabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (bool.TryParse(value, out bool enabled))
                    {
                        config.HttpServerEnabled = enabled;
                    }

                    continue;
                }

                if (key.Equals("http_server_host", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        config.HttpServerHost = value.Trim();
                    }

                    continue;
                }

                if (key.Equals("http_server_port", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out int port) && port >= 1 && port <= 65535)
                    {
                        config.HttpServerPort = port;
                    }
                }
            }

            return config;
        }
    }
}
