using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace OniAiAssistant
{
    public sealed partial class OniAiController
    {
        private readonly object httpSync = new object();
        private HttpListener httpListener;
        private Thread httpThread;
        private bool httpRunning;
        private JObject lastStateSnapshot;
        private JObject lastExecutionSnapshot;
        private JArray queuedHttpActions = new JArray();

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
                TryWriteJson(context.Response, 200, new JObject
                {
                    ["ok"] = true,
                    ["busy"] = isBusy
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

            if (path.Equals("/actions", StringComparison.Ordinal))
            {
                if (method.Equals("GET", StringComparison.OrdinalIgnoreCase))
                {
                    JArray pendingActions = BuildPendingActionsSnapshot();
                    TryWriteJson(context.Response, 200, new JObject
                    {
                        ["actions"] = pendingActions,
                        ["source"] = "game_pending"
                    });
                    return;
                }

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload = ReadRequestJsonBody(context.Request);
                    if (payload == null)
                    {
                        TryWriteJson(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                        return;
                    }

                    JArray incomingActions = payload["actions"] as JArray;
                    if (incomingActions == null)
                    {
                        TryWriteJson(context.Response, 400, new JObject { ["error"] = "actions_must_be_array" });
                        return;
                    }

                    int accepted = 0;
                    lock (httpSync)
                    {
                        if (queuedHttpActions == null)
                        {
                            queuedHttpActions = new JArray();
                        }

                        foreach (JToken token in incomingActions)
                        {
                            if (!(token is JObject actionObject))
                            {
                                continue;
                            }

                            queuedHttpActions.Add(actionObject.DeepClone());
                            accepted++;
                        }
                    }

                    TryWriteJson(context.Response, 200, new JObject
                    {
                        ["accepted"] = accepted,
                        ["status"] = "scheduled"
                    });
                    return;
                }
            }

            TryWriteJson(context.Response, 404, new JObject { ["error"] = "not_found" });
        }

        private static JObject ReadRequestJsonBody(HttpListenerRequest request)
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

                    return JObject.Parse(body);
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

        private void TryProcessQueuedHttpActions()
        {
            if (isBusy)
            {
                return;
            }

            JArray actionsToApply = null;
            lock (httpSync)
            {
                if (queuedHttpActions == null || queuedHttpActions.Count == 0)
                {
                    return;
                }

                actionsToApply = (JArray)queuedHttpActions.DeepClone();
                queuedHttpActions = new JArray();
            }

            try
            {
                var speedControl = SpeedControlScreen.Instance;
                int previousSpeed = speedControl != null ? Mathf.Clamp(speedControl.GetSpeed(), 1, 3) : 1;
                RequestContext context = CreateHttpActionContext();
                var wrapper = new JObject { ["actions"] = actionsToApply };
                ExecutionOutcome outcome = ExecutePlan(wrapper.ToString(Newtonsoft.Json.Formatting.None), context, previousSpeed);

                if (outcome.KeepPaused)
                {
                    PauseGame(speedControl);
                }
                else
                {
                    ResumeGame(speedControl, outcome.ResultingSpeed);
                }

                Debug.Log("[ONI-AI] Applied queued HTTP actions count=" + actionsToApply.Count);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ONI-AI] Failed to apply queued HTTP actions: " + exception.Message);
            }
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
    }
}
