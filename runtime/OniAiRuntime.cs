using UnityEngine;
using OniAiAssistant;

namespace OniAiAssistantRuntime
{
    public sealed class EntryPoint : IOniAiRuntime
    {
        public string RuntimeId => "default-runtime-v1";

        public void OnAttach(OniAiController controller)
        {
            controller.PublishInfo("ONI AI runtime attached");
        }

        public void OnDetach()
        {
        }

        public void OnTick(OniAiController controller)
        {
        }

        public bool HandleTrigger(OniAiController controller)
        {
            return false;
        }
    }
}
