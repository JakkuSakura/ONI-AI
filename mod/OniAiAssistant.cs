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
            bool keepPaused = false;
            if (httpOk)
            {
                var executionOutcome = ExecutePlan(aiResponse, requestContext, previousSpeed);
                finalSpeed = executionOutcome.ResultingSpeed;
                keepPaused = executionOutcome.KeepPaused;
                ShowInGameMessage("ONI AI: completed", new Color(0.70f, 1.00f, 0.75f, 1.00f), 2.5f);
            }
            else
            {
                Debug.LogWarning("[ONI-AI] Bridge request failed");
                WriteTextSafe(Path.Combine(requestContext.RequestDir, "bridge_error.txt"), "Request failed or timed out");
                ShowInGameMessage("ONI AI: bridge unreachable", new Color(1.00f, 0.70f, 0.70f, 1.00f), 4.0f);
            }

            if (keepPaused)
            {
                PauseGame(speedControl);
            }
            else
            {
                ResumeGame(speedControl, finalSpeed);
            }

            SetBusyUiState(false);
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

            var context = BuildContextObject(previousSpeed);
            var state = new JObject
            {
                ["request_id"] = requestId,
                ["request_dir"] = ".",
                ["state_path"] = "state.json",
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

            JObject bridgePayload = (JObject)state.DeepClone();
            bridgePayload["request_dir"] = requestDir;
            bridgePayload["state_path"] = "state.json";

            WriteJsonSafe(Path.Combine(requestDir, "state.json"), state);

            return new RequestContext
            {
                RequestId = requestId,
                RequestDir = requestDir,
                PayloadJson = bridgePayload.ToString(Formatting.None)
            };
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
                    object queue = TryGetMemberValue(choreConsumer, "choreQueue")
                        ?? TryGetMemberValue(choreConsumer, "chores")
                        ?? TryGetMemberValue(choreConsumer, "availableChores");
                    JToken queueToken = ConvertToJToken(queue, 2);
                    if (queueToken != null)
                    {
                        item["queue"] = queueToken;
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

        private ExecutionOutcome ExecutePlan(string aiResponse, RequestContext context, int previousSpeed)
        {
            WriteTextSafe(Path.Combine(context.RequestDir, "logs", "bridge_response_raw.txt"), aiResponse ?? string.Empty);

            var outcome = new ExecutionOutcome
            {
                ResultingSpeed = previousSpeed,
                KeepPaused = false
            };

            if (string.IsNullOrWhiteSpace(aiResponse))
            {
                WriteTextSafe(Path.Combine(context.RequestDir, "logs", "execution_note.txt"), "Empty response; no actions executed");
                return outcome;
            }

            JToken root;
            try
            {
                root = JToken.Parse(aiResponse);
            }
            catch (Exception exception)
            {
                WriteTextSafe(Path.Combine(context.RequestDir, "logs", "parse_error.txt"), exception.ToString());
                return outcome;
            }

            JArray actionArray = root["actions"] as JArray;
            if (actionArray == null && root is JArray)
            {
                actionArray = (JArray)root;
            }

            if (actionArray == null)
            {
                WriteTextSafe(Path.Combine(context.RequestDir, "logs", "execution_note.txt"), "No actions array in response");
                return outcome;
            }

            var executionLog = new JArray();
            var canceledActionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var canceledActionTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                if (canceledActionIds.Contains(actionId) || canceledActionTypes.Contains(actionType))
                {
                    itemLog["status"] = "canceled";
                    itemLog["message"] = "Action canceled by a previous cancel instruction";
                    executionLog.Add(itemLog);
                    continue;
                }

                switch (actionType)
                {
                    case "set_speed":
                    {
                        int speed = parameters.Value<int?>("speed") ?? 0;
                        if (speed >= 1 && speed <= 3)
                        {
                            outcome.ResultingSpeed = speed;
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
                    {
                        outcome.KeepPaused = true;
                        itemLog["status"] = "applied";
                        itemLog["message"] = "Game will remain paused after execution";
                        break;
                    }
                    case "resume":
                    {
                        outcome.KeepPaused = false;
                        itemLog["status"] = "applied";
                        itemLog["message"] = "Game will resume after execution";
                        break;
                    }
                    case "cancel":
                    {
                        string targetActionId = (parameters.Value<string>("target_action_id") ?? string.Empty).Trim();
                        string targetActionType = (parameters.Value<string>("target_action_type") ?? string.Empty).Trim().ToLowerInvariant();

                        bool hasTargetId = !string.IsNullOrEmpty(targetActionId);
                        bool hasTargetType = !string.IsNullOrEmpty(targetActionType);

                        if (!hasTargetId && !hasTargetType)
                        {
                            itemLog["status"] = "rejected";
                            itemLog["message"] = "Cancel requires target_action_id or target_action_type";
                            break;
                        }

                        if (hasTargetId)
                        {
                            canceledActionIds.Add(targetActionId);
                        }

                        if (hasTargetType)
                        {
                            canceledActionTypes.Add(targetActionType);
                        }

                        itemLog["status"] = "applied";
                        itemLog["message"] = "Cancel criteria recorded; matching later actions will be skipped";
                        break;
                    }
                    case "build":
                    case "dig":
                    case "deconstruct":
                    case "priority":
                    case "arrangement":
                    case "research":
                    {
                        itemLog["status"] = "deferred";
                        itemLog["message"] = "Action acknowledged and logged; executor implementation pending";
                        break;
                    }
                    case "set_duplicant_status":
                    {
                        if (ApplyDuplicantStatusUpdate(parameters, out string statusMessage))
                        {
                            itemLog["status"] = "applied";
                            itemLog["message"] = statusMessage;
                        }
                        else
                        {
                            itemLog["status"] = "deferred";
                            itemLog["message"] = statusMessage;
                        }

                        break;
                    }
                    case "set_duplicant_priority":
                    {
                        if (ApplyDuplicantPriorityUpdate(parameters, out string priorityMessage))
                        {
                            itemLog["status"] = "applied";
                            itemLog["message"] = priorityMessage;
                        }
                        else
                        {
                            itemLog["status"] = "deferred";
                            itemLog["message"] = priorityMessage;
                        }

                        break;
                    }
                    case "set_duplicant_skills":
                    {
                        if (ApplyDuplicantSkillsUpdate(parameters, out string skillsMessage))
                        {
                            itemLog["status"] = "applied";
                            itemLog["message"] = skillsMessage;
                        }
                        else
                        {
                            itemLog["status"] = "deferred";
                            itemLog["message"] = skillsMessage;
                        }

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
                ["resulting_speed"] = outcome.ResultingSpeed,
                ["keep_paused"] = outcome.KeepPaused,
                ["actions"] = executionLog
            };

            WriteJsonSafe(Path.Combine(context.RequestDir, "logs", "execution_result.json"), executionResult);
            Debug.Log("[ONI-AI] Executed plan with " + executionLog.Count + " actions");
            return outcome;
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
            message = "set_duplicant_priority accepted but ONI runtime mapping is not fully implemented";
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

            message = "Priority update deferred: no compatible runtime priority method found";
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

            message = "Skill update deferred: no compatible runtime skill method found";
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

        private static bool TryInvokePriorityMethod(Component resume, string priorityKey, int value)
        {
            object[] args = { priorityKey, value };
            if (TryInvokeMethodByName(resume, "SetPriority", args)
                || TryInvokeMethodByName(resume, "SetPersonalPriority", args)
                || TryInvokeMethodByName(resume, "SetChoreGroupPriority", args))
            {
                return true;
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

        private sealed class ExecutionOutcome
        {
            public int ResultingSpeed { get; set; }

            public bool KeepPaused { get; set; }
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
                }
            }

            return config;
        }
    }
}
