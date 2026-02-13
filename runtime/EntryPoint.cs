using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    public sealed class EntryPoint : IOniAiRuntime
    {
        public string RuntimeId => "entrypoint-runtime-v1";

        public void OnAttach(OniAiController controller)
        {
            RuntimeHooks.OnAttach(controller);
        }

        public void OnConfigReload(OniAiController controller)
        {
            RuntimeHooks.OnConfigReload(controller);
        }

        public void OnDetach()
        {
            RuntimeHooks.OnDetach();
        }

        public void OnTick(OniAiController controller)
        {
            RuntimeHooks.OnTick(controller);
        }

        public bool HandleTrigger(OniAiController controller)
        {
            return RuntimeHooks.HandleTrigger(controller);
        }
    }
}
