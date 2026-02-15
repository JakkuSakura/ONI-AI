using System;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeControlApi
    {
        private readonly RuntimeApiBackend backend = new RuntimeApiBackend();

        public bool Handle(OniAiController controller, System.Net.HttpListenerContext context, string method, string path)
        {
            if (string.Equals(path, "/health", StringComparison.Ordinal)
                && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                JObject payload = backend.BuildHealth(controller);
                RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
                return true;
            }

            if (string.Equals(path, "/speed", StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload = backend.BuildSpeed();
                    RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
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

                    if (body["speed"] == null || body["speed"].Type != JTokenType.Integer)
                    {
                        RuntimeJson.WriteJson(context.Response, 400, new JObject { ["error"] = "speed_must_be_1_2_3" });
                        return true;
                    }

                    int speed = body["speed"].Value<int>();
                    if (speed < 1 || speed > 3)
                    {
                        RuntimeJson.WriteJson(context.Response, 400, new JObject { ["error"] = "speed_must_be_1_2_3" });
                        return true;
                    }

                    JObject payload = backend.ApplySpeed(speed);
                    RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
                    return true;
                }

                return false;
            }

            if (string.Equals(path, "/pause", StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload = backend.BuildPause();
                    RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
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

                    if (body["paused"] == null || body["paused"].Type != JTokenType.Boolean)
                    {
                        RuntimeJson.WriteJson(context.Response, 400, new JObject { ["error"] = "paused_must_be_boolean" });
                        return true;
                    }

                    bool paused = body["paused"].Value<bool>();
                    JObject payload = backend.ApplyPause(paused);
                    RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
                    return true;
                }

                return false;
            }

            return false;
        }
    }
}
