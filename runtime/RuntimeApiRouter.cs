using System.Net;
using Newtonsoft.Json.Linq;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeApiRouter
    {
        private readonly RuntimeControlApi controlApi = new RuntimeControlApi();
        private readonly RuntimeStateCameraApi stateCameraApi = new RuntimeStateCameraApi();
        private readonly RuntimeActionApi actionApi = new RuntimeActionApi();
        private readonly RuntimeCatalogApi catalogApi = new RuntimeCatalogApi();
        private readonly RuntimePrioritiesApi prioritiesApi = new RuntimePrioritiesApi();

        public bool Handle(OniAiController controller, HttpListenerContext context)
        {
            if (controller == null || context == null || context.Request == null)
            {
                return false;
            }

            string method = context.Request.HttpMethod ?? string.Empty;
            string path = context.Request.Url != null ? context.Request.Url.AbsolutePath : string.Empty;

            if (controlApi.Handle(controller, context, method, path))
            {
                return true;
            }

            if (stateCameraApi.Handle(controller, context, method, path))
            {
                return true;
            }

            if (actionApi.Handle(controller, context, method, path))
            {
                return true;
            }

            if (catalogApi.Handle(controller, context, method, path))
            {
                return true;
            }

            if (prioritiesApi.Handle(controller, context, method, path))
            {
                return true;
            }

            return false;
        }

        internal static JObject InvalidJson()
        {
            return new JObject { ["error"] = "invalid_json" };
        }
    }
}
