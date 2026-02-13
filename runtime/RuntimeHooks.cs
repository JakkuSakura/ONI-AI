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
    }
}
