using System;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeControlApi
    {
        private readonly RuntimeApiBackend backend = new RuntimeApiBackend();
        private readonly RuntimeMainThreadExecutor executor = new RuntimeMainThreadExecutor();

        public bool Handle(OniAiController controller, System.Net.HttpListenerContext context, string method, string path)
        {
            if (string.Equals(path, "/runtime", StringComparison.Ordinal)
                && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.BuildRuntimeInfo(), "runtime_info_unavailable"));
            }

            if (string.Equals(path, "/health", StringComparison.Ordinal)
                && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.BuildHealth(controller), "speed_control_unavailable"));
            }

            if (string.Equals(path, "/speed", StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.BuildSpeed(), "speed_control_unavailable"));
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

                    return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.ApplySpeed(speed), "speed_control_unavailable"));
                }

                return false;
            }

            if (string.Equals(path, "/pause", StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.BuildPause(), "speed_control_unavailable"));
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
                    return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.ApplyPause(paused), "speed_control_unavailable"));
                }

                return false;
            }

            return false;
        }
    }
}
