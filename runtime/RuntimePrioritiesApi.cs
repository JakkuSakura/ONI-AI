using System;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimePrioritiesApi
    {
        private readonly RuntimeApiBackend backend = new RuntimeApiBackend();

        public bool Handle(OniAiController controller, System.Net.HttpListenerContext context, string method, string path)
        {
            if (!string.Equals(path, "/priorities", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                JObject payload = backend.BuildPriorities();
                RuntimeJson.WriteJson(context.Response, 200, payload);
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

                try
                {
                    JObject payload = backend.ApplyPriorities(body);
                    RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "action_unavailable" });
                }
                catch (InvalidOperationException exception)
                {
                    RuntimeJson.WriteJson(context.Response, 400, new JObject { ["error"] = exception.Message });
                }

                return true;
            }

            return false;
        }
    }
}
