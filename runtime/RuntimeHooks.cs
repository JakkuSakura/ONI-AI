using System.Net;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal static class RuntimeHooks
    {
        public static void OnAttach(OniAiController controller)
        {
            controller.PublishInfo("ONI AI runtime attached");
        }

        public static void OnConfigReload(OniAiController controller)
        {
            controller.PublishInfo("ONI AI runtime observed config reload");
        }

        public static void OnDetach()
        {
        }

        public static void OnTick(OniAiController controller)
        {
        }

        public static bool HandleTrigger(OniAiController controller)
        {
            return false;
        }


        public static bool HandleHttpRequest(OniAiController controller, HttpListenerContext context)
        {
            if (controller == null || context == null || context.Request == null)
            {
                return false;
            }

            string method = context.Request.HttpMethod ?? string.Empty;
            string path = context.Request.Url != null ? context.Request.Url.AbsolutePath : string.Empty;

            if (string.Equals(path, "/state", System.StringComparison.Ordinal))
            {
                if (!string.Equals(method, "GET", System.StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                JObject payload = controller.BuildStateResponseForApi();
                OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "state_snapshot_unavailable" });
                return true;
            }

            if (string.Equals(path, "/camera", System.StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", System.StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload = controller.BuildCameraStateForApi();
                    OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "camera_unavailable" });
                    return true;
                }

                if (string.Equals(method, "POST", System.StringComparison.OrdinalIgnoreCase))
                {
                    JObject body = OniAiController.ReadJsonBodyForApi(context.Request);
                    if (body == null)
                    {
                        OniAiController.WriteJsonForApi(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                        return true;
                    }

                    JObject payload = controller.ApplyCameraRequestForApi(body);
                    if (payload == null)
                    {
                        OniAiController.WriteJsonForApi(context.Response, 503, new JObject { ["error"] = "camera_unavailable" });
                        return true;
                    }

                    payload["status"] = "applied";
                    OniAiController.WriteJsonForApi(context.Response, 200, payload);
                    return true;
                }

                return false;
            }

            if (string.Equals(method, "POST", System.StringComparison.OrdinalIgnoreCase)
                && (string.Equals(path, "/build", System.StringComparison.Ordinal)
                    || string.Equals(path, "/dig", System.StringComparison.Ordinal)
                    || string.Equals(path, "/deconstruct", System.StringComparison.Ordinal)))
            {
                JObject body = OniAiController.ReadJsonBodyForApi(context.Request);
                if (body == null)
                {
                    OniAiController.WriteJsonForApi(context.Response, 400, new JObject { ["error"] = "invalid_json" });
                    return true;
                }

                JObject payload;
                if (string.Equals(path, "/build", System.StringComparison.Ordinal))
                {
                    payload = controller.ApplyBuildRequestForApi(body);
                }
                else if (string.Equals(path, "/dig", System.StringComparison.Ordinal))
                {
                    payload = controller.ApplyDigRequestForApi(body);
                }
                else
                {
                    payload = controller.ApplyDeconstructRequestForApi(body);
                }

                OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "action_unavailable" });
                return true;
            }

            return false;
        }

    }
}
