using System;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimePrioritiesApi
    {
        private readonly RuntimeApiBackend backend = new RuntimeApiBackend();
        private readonly RuntimeMainThreadExecutor executor = new RuntimeMainThreadExecutor();

        public bool Handle(OniAiController controller, System.Net.HttpListenerContext context, string method, string path)
        {
            if (!string.Equals(path, "/priorities", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return executor.Execute(controller, context, () => RuntimeApiResult.Json(200, backend.BuildPriorities()));
            }

            if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                JObject body = RuntimeJson.ReadJsonBody(context.Request);
                if (body == null)
                {
                    RuntimeJson.WriteJson(context.Response, 400, RuntimeApiRouter.InvalidJson());
                    return true;
                }

                return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.ApplyPriorities(body), "action_unavailable"));
            }

            return false;
        }
    }
}
