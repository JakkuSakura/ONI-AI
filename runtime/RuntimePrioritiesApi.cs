using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimePrioritiesApi
    {
        public bool Handle(OniAiController controller, System.Net.HttpListenerContext context, string method, string path)
        {
            if (!string.Equals(path, "/priorities", System.StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(method, "GET", System.StringComparison.OrdinalIgnoreCase))
            {
                JObject payload = controller.BuildPrioritiesResponseForApi();
                OniAiController.WriteJsonForApi(context.Response, 200, payload);
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

                try
                {
                    JObject payload = controller.ApplyPrioritiesRequestForApi(body);
                    OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "action_unavailable" });
                }
                catch (System.InvalidOperationException exception)
                {
                    OniAiController.WriteJsonForApi(context.Response, 400, new JObject { ["error"] = exception.Message });
                }

                return true;
            }

            return false;
        }
    }
}
