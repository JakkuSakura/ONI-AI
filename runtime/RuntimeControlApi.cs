using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeControlApi
    {
        public bool Handle(OniAiController controller, System.Net.HttpListenerContext context, string method, string path)
        {
            if (string.Equals(path, "/health", System.StringComparison.Ordinal)
                && string.Equals(method, "GET", System.StringComparison.OrdinalIgnoreCase))
            {
                JObject payload = controller.BuildHealthResponseForApi();
                OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
                return true;
            }

            if (string.Equals(path, "/speed", System.StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", System.StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload = controller.BuildSpeedResponseForApi();
                    OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
                    return true;
                }

                if (string.Equals(method, "POST", System.StringComparison.OrdinalIgnoreCase))
                {
                    JObject body = OniAiController.ReadJsonBodyForApi(context.Request);
                    if (body == null)
                    {
                        OniAiController.WriteJsonForApi(context.Response, 400, RuntimeApiRouter.InvalidJson());
                        return true;
                    }

                    if (body["speed"] == null || body["speed"].Type != JTokenType.Integer)
                    {
                        OniAiController.WriteJsonForApi(context.Response, 400, new JObject { ["error"] = "speed_must_be_1_2_3" });
                        return true;
                    }

                    int speed = body["speed"].Value<int>();
                    if (speed < 1 || speed > 3)
                    {
                        OniAiController.WriteJsonForApi(context.Response, 400, new JObject { ["error"] = "speed_must_be_1_2_3" });
                        return true;
                    }

                    JObject payload = controller.ApplySpeedForApi(speed);
                    OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
                    return true;
                }

                return false;
            }

            if (string.Equals(path, "/pause", System.StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", System.StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload = controller.BuildPauseResponseForApi();
                    OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
                    return true;
                }

                if (string.Equals(method, "POST", System.StringComparison.OrdinalIgnoreCase))
                {
                    JObject body = OniAiController.ReadJsonBodyForApi(context.Request);
                    if (body == null)
                    {
                        OniAiController.WriteJsonForApi(context.Response, 400, RuntimeApiRouter.InvalidJson());
                        return true;
                    }

                    if (body["paused"] == null || body["paused"].Type != JTokenType.Boolean)
                    {
                        OniAiController.WriteJsonForApi(context.Response, 400, new JObject { ["error"] = "paused_must_be_boolean" });
                        return true;
                    }

                    bool paused = body["paused"].Value<bool>();
                    JObject payload = controller.ApplyPauseForApi(paused);
                    OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "speed_control_unavailable" });
                    return true;
                }

                return false;
            }

            return false;
        }
    }
}
