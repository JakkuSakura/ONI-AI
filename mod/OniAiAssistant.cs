using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using KMod;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace OniAiAssistant
{
    public interface IOniAiRuntime
    {
        string RuntimeId { get; }

        void OnAttach(OniAiController controller);

        void OnDetach();

        void OnTick(OniAiController controller);

        bool HandleTrigger(OniAiController controller);
    }

    public sealed class OniAiAssistantMod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            harmony.PatchAll();
            Debug.Log("[ONI-AI] Mod loaded");
        }
    }

    [HarmonyPatch(typeof(Game), "OnSpawn")]
    public static class OniAiAssistantGameOnSpawnPatch
    {
        public static void Postfix()
        {
            OniAiController.EnsureInstance();
        }
    }

    
}
