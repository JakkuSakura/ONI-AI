using System;
using System.Net;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeStateCameraApi
    {
        private readonly RuntimeApiBackend backend = new RuntimeApiBackend();
        private readonly RuntimeMainThreadExecutor executor = new RuntimeMainThreadExecutor();

        public bool Handle(OniAiController controller, HttpListenerContext context, string method, string path)
        {
            if (string.Equals(path, "/state", StringComparison.Ordinal))
            {
                if (!string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.BuildState(controller), "state_snapshot_unavailable"));
            }

            if (string.Equals(path, "/camera", StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.BuildCamera(), "camera_unavailable"));
                }

                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    JObject body = RuntimeJson.ReadJsonBody(context.Request);
                    if (body == null)
                    {
                        RuntimeJson.WriteJson(context.Response, 400, RuntimeApiRouter.InvalidJson());
                        return true;
                    }

                    return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.ApplyCamera(controller, body), "camera_unavailable"));
                }

                return false;
            }

            return false;
        }
    }
}
