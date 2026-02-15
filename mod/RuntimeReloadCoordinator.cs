using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace OniAiAssistant
{
    internal sealed class RuntimeReloadCoordinator
    {
        private string runtimeDllPath;
        private long runtimeDllLastWriteTicks;
        private float nextRuntimeReloadCheckAt;
        private bool runtimeMissingLogged;

        public IOniAiRuntime Reload(OniAiController controller, IOniAiRuntime currentRuntime, OniAiConfig config, bool force, string modDirectory)
        {
            float interval = Mathf.Clamp(config != null ? config.RuntimeReloadIntervalSeconds : 1.0f, 0.2f, 30.0f);
            if (!force && Time.unscaledTime < nextRuntimeReloadCheckAt)
            {
                return currentRuntime;
            }

            nextRuntimeReloadCheckAt = Time.unscaledTime + interval;

            string candidatePath = ResolveRuntimeDllPath(config, modDirectory);
            if (string.IsNullOrWhiteSpace(candidatePath) || !File.Exists(candidatePath))
            {
                if (!runtimeMissingLogged)
                {
                    Debug.LogWarning("[ONI-AI] Runtime DLL not found: " + candidatePath);
                    runtimeMissingLogged = true;
                }

                return currentRuntime;
            }

            runtimeMissingLogged = false;

            long lastWriteTicks = File.GetLastWriteTimeUtc(candidatePath).Ticks;
            if (!force && candidatePath == runtimeDllPath && lastWriteTicks == runtimeDllLastWriteTicks)
            {
                return currentRuntime;
            }

            try
            {
                byte[] bytes = File.ReadAllBytes(candidatePath);
                Assembly assembly = Assembly.Load(bytes);
                Type runtimeType = assembly
                    .GetTypes()
                    .FirstOrDefault(type => typeof(IOniAiRuntime).IsAssignableFrom(type) && !type.IsAbstract && type.IsClass);

                if (runtimeType == null)
                {
                    Debug.LogWarning("[ONI-AI] Runtime DLL has no IOniAiRuntime implementation: " + candidatePath);
                    return currentRuntime;
                }

                var nextRuntime = (IOniAiRuntime)Activator.CreateInstance(runtimeType);

                if (currentRuntime != null)
                {
                    try
                    {
                        currentRuntime.OnDetach();
                    }
                    catch (Exception exception)
                    {
                        Debug.LogWarning("[ONI-AI] Previous runtime OnDetach failed: " + exception.Message);
                    }
                }

                runtimeDllPath = candidatePath;
                runtimeDllLastWriteTicks = lastWriteTicks;

                nextRuntime.OnAttach(controller);
                string runtimeId = string.IsNullOrWhiteSpace(nextRuntime.RuntimeId) ? runtimeType.FullName : nextRuntime.RuntimeId;
                Debug.Log("[ONI-AI] Runtime reloaded: " + runtimeId + " from " + runtimeDllPath);
                controller.PublishSuccess("ONI AI runtime reloaded");
                return nextRuntime;
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[ONI-AI] Runtime reload failed: " + exception);
                controller.PublishError("ONI AI runtime reload failed");
                return currentRuntime;
            }
        }

        private static string ResolveRuntimeDllPath(OniAiConfig config, string modDirectory)
        {
            string configured = config != null ? config.RuntimeDllPath : string.Empty;
            if (string.IsNullOrWhiteSpace(configured))
            {
                return Path.Combine(modDirectory, "runtime", "OniAiRuntime.dll");
            }

            if (Path.IsPathRooted(configured))
            {
                return configured;
            }

            return Path.GetFullPath(Path.Combine(modDirectory, configured));
        }
    }
}
