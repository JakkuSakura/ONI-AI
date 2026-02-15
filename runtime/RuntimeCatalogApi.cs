using System;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeCatalogApi
    {
        private readonly RuntimeApiBackend backend = new RuntimeApiBackend();

        public bool Handle(OniAiController controller, System.Net.HttpListenerContext context, string method, string path)
        {
            if (string.Equals(path, "/buildings", StringComparison.Ordinal)
                && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                JObject payload = backend.BuildBuildings();
                RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "building_catalog_unavailable" });
                return true;
            }

            if (string.Equals(path, "/research", StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    JObject payload = backend.BuildResearch();
                    RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "research_catalog_unavailable" });
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

                    try
                    {
                        JObject payload = backend.ApplyResearch(controller, body);
                        RuntimeJson.WriteJson(context.Response, payload != null ? 200 : 503, payload ?? new JObject { ["error"] = "research_catalog_unavailable" });
                    }
                    catch (Exception exception)
                    {
                        RuntimeJson.WriteJson(context.Response, 400, new JObject { ["error"] = exception.Message });
                    }

                    return true;
                }

                return false;
            }

            return false;
        }
    }
}
