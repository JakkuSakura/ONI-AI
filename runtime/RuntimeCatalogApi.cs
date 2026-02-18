using System;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeCatalogApi
    {
        private readonly RuntimeApiBackend backend = new RuntimeApiBackend();
        private readonly RuntimeMainThreadExecutor executor = new RuntimeMainThreadExecutor();

        public bool Handle(OniAiController controller, System.Net.HttpListenerContext context, string method, string path)
        {
            if (string.Equals(path, "/buildings", StringComparison.Ordinal)
                && string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
            {
                return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.BuildBuildings(), "building_catalog_unavailable"));
            }

            if (string.Equals(path, "/research", StringComparison.Ordinal))
            {
                if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase))
                {
                    return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.BuildResearch(), "research_catalog_unavailable"));
                }

                if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
                {
                    JObject body = RuntimeJson.ReadJsonBody(context.Request);
                    if (body == null)
                    {
                        RuntimeJson.WriteJson(context.Response, 400, RuntimeApiRouter.InvalidJson());
                        return true;
                    }

                    return executor.Execute(controller, context, () => RuntimeApiResult.Optional(backend.ApplyResearch(controller, body), "research_catalog_unavailable"));
                }

                return false;
            }

            return false;
        }
    }
}
