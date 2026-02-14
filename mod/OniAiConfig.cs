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
public sealed class OniAiConfig
    {
        public string BridgeUrl { get; private set; } = "http://127.0.0.1:8765/analyze";

        public KeyCode Hotkey { get; private set; } = KeyCode.F8;

        public bool EnableHotkey { get; private set; } = false;

        public int RequestTimeoutSeconds { get; private set; } = 120;

        public string RequestRootDir { get; private set; } = string.Empty;

        public string RuntimeDllPath { get; private set; } = string.Empty;

        public float RuntimeReloadIntervalSeconds { get; private set; } = 1.0f;

        public bool HttpServerEnabled { get; private set; } = true;

        public string HttpServerHost { get; private set; } = "127.0.0.1";

        public int HttpServerPort { get; private set; } = 8766;

        public bool EnableChatLogMirror { get; private set; } = true;

        public static OniAiConfig Load()
        {
            var config = new OniAiConfig();
            string filePath = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "oni_ai_config.ini");

            if (!File.Exists(filePath))
            {
                return config;
            }

            foreach (string line in File.ReadAllLines(filePath))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                {
                    continue;
                }

                int separator = trimmed.IndexOf('=');
                if (separator < 0)
                {
                    continue;
                }

                string key = trimmed.Substring(0, separator).Trim();
                string value = trimmed.Substring(separator + 1).Trim();

                if (key.Equals("bridge_url", StringComparison.OrdinalIgnoreCase))
                {
                    config.BridgeUrl = value;
                    continue;
                }

                if (key.Equals("hotkey", StringComparison.OrdinalIgnoreCase))
                {
                    if (Enum.TryParse(value, true, out KeyCode parsed))
                    {
                        config.Hotkey = parsed;
                    }

                    continue;
                }

                if (key.Equals("enable_hotkey", StringComparison.OrdinalIgnoreCase))
                {
                    if (bool.TryParse(value, out bool enableHotkey))
                    {
                        config.EnableHotkey = enableHotkey;
                    }

                    continue;
                }

                if (key.Equals("request_timeout_seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out int timeout))
                    {
                        config.RequestTimeoutSeconds = timeout;
                    }

                    continue;
                }

                if (key.Equals("request_root_dir", StringComparison.OrdinalIgnoreCase))
                {
                    config.RequestRootDir = value;
                    continue;
                }

                if (key.Equals("runtime_dll_path", StringComparison.OrdinalIgnoreCase))
                {
                    config.RuntimeDllPath = value;
                    continue;
                }

                if (key.Equals("runtime_reload_interval_seconds", StringComparison.OrdinalIgnoreCase))
                {
                    if (float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float intervalSeconds))
                    {
                        config.RuntimeReloadIntervalSeconds = Mathf.Clamp(intervalSeconds, 0.2f, 30.0f);
                    }

                    continue;
                }

                if (key.Equals("http_server_enabled", StringComparison.OrdinalIgnoreCase))
                {
                    if (bool.TryParse(value, out bool enabled))
                    {
                        config.HttpServerEnabled = enabled;
                    }

                    continue;
                }

                if (key.Equals("http_server_host", StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        config.HttpServerHost = value.Trim();
                    }

                    continue;
                }

                if (key.Equals("http_server_port", StringComparison.OrdinalIgnoreCase))
                {
                    if (int.TryParse(value, out int port) && port >= 1 && port <= 65535)
                    {
                        config.HttpServerPort = port;
                    }

                    continue;
                }

                if (key.Equals("enable_chat_log_mirror", StringComparison.OrdinalIgnoreCase))
                {
                    if (bool.TryParse(value, out bool enabled))
                    {
                        config.EnableChatLogMirror = enabled;
                    }
                }
            }

            return config;
        }
    }
}
