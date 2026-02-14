using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeCatalogApi
    {
        public bool Handle(OniAiController controller, System.Net.HttpListenerContext context, string method, string path)
        {
            if (string.Equals(path, "/buildings", System.StringComparison.Ordinal)
                && string.Equals(method, "GET", System.StringComparison.OrdinalIgnoreCase))
            {
                JObject payload = controller.BuildBuildingsResponseForApi();
                OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "building_catalog_unavailable" });
                return true;
            }

            if (string.Equals(path, "/research", System.StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", System.StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload = controller.BuildResearchResponseForApi();
                    OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "research_catalog_unavailable" });
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
                        JObject payload = controller.ApplyResearchRequestForApi(body);
                        OniAiController.WriteJsonForApi(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "research_catalog_unavailable" });
                    }
                    catch (System.Exception exception)
                    {
                        OniAiController.WriteJsonForApi(context.Response, 400, new JObject { ["error"] = exception.Message });
                    }

                    return true;
                }

                return false;
            }

            return false;
        }
    }
}
