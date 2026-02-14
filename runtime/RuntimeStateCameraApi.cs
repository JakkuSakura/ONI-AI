using System.Net;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeStateCameraApi
    {
        public bool Handle(OniAiController controller, HttpListenerContext context, string method, string path)
        {
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
                        OniAiController.WriteJsonForApi(context.Response, 400, RuntimeApiRouter.InvalidJson());
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

            return false;
        }
    }
}
