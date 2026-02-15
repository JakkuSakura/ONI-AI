using System;
using System.Net;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeStateCameraApi
    {
        private readonly RuntimeApiBackend backend = new RuntimeApiBackend();

        public bool Handle(OniAiController controller, HttpListenerContext context, string method, string path)
        {
            if (string.Equals(path, "/state", StringComparison.Ordinal))
            {
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                JObject payload = backend.BuildState(controller);
                RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "state_snapshot_unavailable" });
                return true;
            }

            if (string.Equals(path, "/camera", StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload = backend.BuildCamera();
                    RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "camera_unavailable" });
                    return true;
                }

                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    JObject body = RuntimeJson.ReadJsonBody(context.Request);
                    if (body == null)
                    {
                        RuntimeJson.WriteJson(context.Response, 400, RuntimeApiRouter.InvalidJson());
                        return true;
                    }

                    JObject payload = backend.ApplyCamera(controller, body);
                    if (payload == null)
                    {
                        RuntimeJson.WriteJson(context.Response, 503, new JObject { ["error"] = "camera_unavailable" });
                        return true;
                    }

                    RuntimeJson.WriteJson(context.Response, 200, payload);
                    return true;
                }

                return false;
            }

            return false;
        }
    }
}
