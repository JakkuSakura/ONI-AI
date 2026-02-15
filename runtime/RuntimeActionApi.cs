using System;
using System.Net;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeActionApi
    {
        private readonly RuntimeApiBackend backend = new RuntimeApiBackend();

        public bool Handle(OniAiController controller, HttpListenerContext context, string method, string path)
        {
            if (!string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(path, "/build", StringComparison.Ordinal)
                && !string.Equals(path, "/dig", StringComparison.Ordinal)
                && !string.Equals(path, "/deconstruct", StringComparison.Ordinal))
            {
                return false;
            }

            JObject body = RuntimeJson.ReadJsonBody(context.Request);
            if (body == null)
            {
                RuntimeJson.WriteJson(context.Response, 400, RuntimeApiRouter.InvalidJson());
                return true;
            }

            JObject payload;
            if (string.Equals(path, "/build", StringComparison.Ordinal))
            {
                payload = backend.ApplyBuild(controller, body);
            }
            else if (string.Equals(path, "/dig", StringComparison.Ordinal))
            {
                payload = backend.ApplyDig(controller, body);
            }
            else
            {
                payload = backend.ApplyDeconstruct(controller, body);
            }

            RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "action_unavailable" });
            return true;
        }
    }
}
