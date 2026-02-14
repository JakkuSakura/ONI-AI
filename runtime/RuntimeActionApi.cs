using System.Net;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeActionApi
    {
        public bool Handle(OniAiController controller, HttpListenerContext context, string method, string path)
        {
            if (!string.Equals(method, "POST", System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.Equals(path, "/build", System.StringComparison.Ordinal)
                && !string.Equals(path, "/dig", System.StringComparison.Ordinal)
                && !string.Equals(path, "/deconstruct", System.StringComparison.Ordinal))
            {
                return false;
            }

            JObject body = OniAiController.ReadJsonBodyForApi(context.Request);
            if (body == null)
            {
                OniAiController.WriteJsonForApi(context.Response, 400, RuntimeApiRouter.InvalidJson());
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
    }
}
