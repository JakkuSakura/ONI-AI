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
                if (!HasExplicitTarget(body))
                {
                    RuntimeJson.WriteJson(context.Response, 400, new JObject { ["error"] = "build_requires_explicit_target" });
                    return true;
                }

                payload = backend.ApplyBuild(controller, body);
            }
            else if (string.Equals(path, "/dig", StringComparison.Ordinal))
            {
                if (!HasExplicitTarget(body))
                {
                    RuntimeJson.WriteJson(context.Response, 400, new JObject { ["error"] = "dig_requires_explicit_target" });
                    return true;
                }

                payload = backend.ApplyDig(controller, body);
            }
            else
            {
                if (!HasExplicitTarget(body))
                {
                    RuntimeJson.WriteJson(context.Response, 400, new JObject { ["error"] = "deconstruct_requires_explicit_target" });
                    return true;
                }

                payload = backend.ApplyDeconstruct(controller, body);
            }

            RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "action_unavailable" });
            return true;
        }

        private static bool HasExplicitTarget(JObject body)
        {
            if (body == null)
            {
                return false;
            }

            if (body["x"] != null && body["y"] != null
                && body["x"].Type == JTokenType.Integer
                && body["y"].Type == JTokenType.Integer)
            {
                return true;
            }

            if (!(body["points"] is JArray points) || points.Count <= 0)
            {
                return false;
            }

            foreach (JToken token in points)
            {
                if (!(token is JObject point))
                {
                    continue;
                }

                if (point["x"] != null && point["y"] != null
                    && point["x"].Type == JTokenType.Integer
                    && point["y"].Type == JTokenType.Integer)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
