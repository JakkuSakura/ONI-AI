using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json.Linq;
using OniAiAssistant;
using UnityEngine;

namespace OniAiAssistantRuntime
{
    internal sealed class RuntimeApiBackend
    {
        public JObject BuildHealth(OniAiController controller)
        {
            if (!TryReadLiveSpeedControlState(out int speed, out bool paused))
            {
                return null;
            }

            bool busy = false;
            FieldInfo busyField = typeof(OniAiController).GetField("isBusy", BindingFlags.Instance | BindingFlags.NonPublic);
            if (busyField != null && controller != null)
            {
                object raw = busyField.GetValue(controller);
                if (raw is bool value)
                {
                    busy = value;
                }
            }

            return new JObject
            {
                ["ok"] = true,
                ["busy"] = busy,
                ["current_speed"] = speed,
                ["paused"] = paused
            };
        }

        public JObject BuildSpeed()
        {
            if (!TryReadLiveSpeedControlState(out int speed, out bool paused))
            {
                return null;
            }

            return new JObject
            {
                ["speed"] = speed,
                ["paused"] = paused
            };
        }

        public JObject ApplySpeed(int speed)
        {
            if (!TryGetSpeedControl(out object speedControl))
            {
                return null;
            }

            int requested = Mathf.Clamp(speed, 1, 3);
            InvokeByName(speedControl, "SetSpeed", requested);
            int currentSpeed = Mathf.Clamp(ReadIntByName(speedControl, "GetSpeed", 1), 1, 3);
            bool paused = ReadBoolByMemberName(speedControl, "IsPaused", true);

            return new JObject
            {
                ["status"] = "applied",
                ["speed"] = currentSpeed,
                ["paused"] = paused
            };
        }

        public JObject BuildPause()
        {
            if (!TryReadLiveSpeedControlState(out int speed, out bool paused))
            {
                return null;
            }

            return new JObject
            {
                ["paused"] = paused,
                ["speed"] = speed
            };
        }

        public JObject ApplyPause(bool paused)
        {
            if (!TryGetSpeedControl(out object speedControl))
            {
                return null;
            }

            if (paused)
            {
                InvokeByName(speedControl, "Pause", false, false);
            }
            else
            {
                InvokeByName(speedControl, "Unpause", false);
                int currentSpeed = Mathf.Clamp(ReadIntByName(speedControl, "GetSpeed", 1), 1, 3);
                InvokeByName(speedControl, "SetSpeed", currentSpeed);
            }

            return new JObject
            {
                ["status"] = "applied",
                ["paused"] = ReadBoolByMemberName(speedControl, "IsPaused", true),
                ["speed"] = Mathf.Clamp(ReadIntByName(speedControl, "GetSpeed", 1), 1, 3)
            };
        }

        public JObject BuildBuildings()
        {
            if (!TryInvokeStatic("BuildBuildingCatalog", out object result, null, typeof(JObject).MakeByRefType()))
            {
                return null;
            }

            return result as JObject;
        }

        public JObject BuildResearch()
        {
            if (!TryInvokeStatic("BuildResearchCatalog", out object result, null, typeof(JObject).MakeByRefType()))
            {
                return null;
            }

            return result as JObject;
        }

        public JObject ApplyResearch(OniAiController controller, JObject payload)
        {
            if (payload == null)
            {
                return null;
            }

            string message = InvokeInstance<string>(controller, "ApplyResearchAction", payload);
            return new JObject
            {
                ["status"] = "applied",
                ["message"] = message
            };
        }

        public JObject BuildPriorities()
        {
            JArray priorities = InvokeStatic<JArray>("BuildPrioritiesSnapshot");
            return new JObject
            {
                ["priorities"] = priorities ?? new JArray(),
                ["source"] = "game_live"
            };
        }

        public JObject ApplyPriorities(JObject payload)
        {
            if (payload == null)
            {
                return null;
            }

            JArray updates = payload["priorities"] as JArray;
            if (updates == null)
            {
                throw new InvalidOperationException("priorities_must_be_array");
            }

            int accepted = 0;
            foreach (JToken token in updates)
            {
                if (!(token is JObject update))
                {
                    continue;
                }

                JObject values = update["values"] as JObject;
                if (values == null || values.Count == 0)
                {
                    continue;
                }

                var parameters = new JObject
                {
                    ["priorities"] = values.DeepClone()
                };

                if (update["duplicant_id"]?.Type == JTokenType.String)
                {
                    string duplicantId = update["duplicant_id"].Value<string>()?.Trim();
                    if (!string.IsNullOrEmpty(duplicantId))
                    {
                        parameters["duplicant_id"] = duplicantId;
                    }
                }

                InvokeStatic<string>("ApplyDuplicantPriorityUpdate", parameters);
                accepted++;
            }

            return new JObject
            {
                ["accepted"] = accepted,
                ["status"] = "applied"
            };
        }

        public JObject BuildState(OniAiController controller)
        {
            try
            {
                int previousSpeed = 1;
                if (TryReadLiveSpeedControlState(out int liveSpeed, out bool _))
                {
                    previousSpeed = Mathf.Clamp(liveSpeed, 1, 3);
                }

                string requestId = "state_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture);
                JObject state = InvokeStatic<JObject>("BuildStatePayload", requestId, previousSpeed, string.Empty);
                if (state == null)
                {
                    return null;
                }

                string apiBaseUrl = InvokeInstance<string>(controller, "BuildApiBaseUrl");
                if (!string.IsNullOrWhiteSpace(apiBaseUrl))
                {
                    state["api_base_url"] = apiBaseUrl.TrimEnd('/');
                }

                int pendingCount = (state["pending_actions"] as JArray)?.Count ?? 0;
                return new JObject
                {
                    ["state"] = state,
                    ["pending_action_count"] = pendingCount
                };
            }
            catch (Exception exception)
            {
                controller?.PublishError("Failed to build /state snapshot: " + exception.Message);
                return null;
            }
        }

        public JObject BuildCamera()
        {
            return InvokeStatic<JObject>("BuildCameraStatePayload");
        }

        public JObject ApplyCamera(OniAiController controller, JObject payload)
        {
            if (payload == null)
            {
                return null;
            }

            return InvokeInstance<JObject>(controller, "ApplyCameraRequestForApi", payload);
        }

        public JObject ApplyBuild(OniAiController controller, JObject payload)
        {
            if (payload == null)
            {
                return null;
            }

            string message = InvokeInstance<string>(controller, "ApplyBuildAction", payload);
            return new JObject
            {
                ["status"] = "applied",
                ["message"] = message
            };
        }

        public JObject ApplyDig(OniAiController controller, JObject payload)
        {
            if (payload == null)
            {
                return null;
            }

            string message = InvokeInstance<string>(controller, "ApplyDigAction", payload);
            return new JObject
            {
                ["status"] = "applied",
                ["message"] = message
            };
        }

        public JObject ApplyDeconstruct(OniAiController controller, JObject payload)
        {
            if (payload == null)
            {
                return null;
            }

            string message = InvokeInstance<string>(controller, "ApplyDeconstructAction", payload);
            return new JObject
            {
                ["status"] = "applied",
                ["message"] = message
            };
        }

        private static bool TryGetSpeedControl(out object speedControl)
        {
            speedControl = null;
            Type speedControlType = AppDomain.CurrentDomain
                .GetAssemblies()
                .SelectMany(assembly =>
                {
                    try
                    {
                        return assembly.GetTypes();
                    }
                    catch (ReflectionTypeLoadException exception)
                    {
                        return exception.Types.Where(type => type != null);
                    }
                    catch
                    {
                        return Array.Empty<Type>();
                    }
                })
                .FirstOrDefault(type => string.Equals(type.Name, "SpeedControlScreen", StringComparison.Ordinal));

            if (speedControlType == null)
            {
                return false;
            }

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            PropertyInfo instanceProperty = speedControlType.GetProperty("Instance", flags);
            if (instanceProperty != null)
            {
                speedControl = instanceProperty.GetValue(null, null);
                return speedControl != null;
            }

            FieldInfo instanceField = speedControlType.GetField("Instance", flags);
            if (instanceField != null)
            {
                speedControl = instanceField.GetValue(null);
                return speedControl != null;
            }

            return false;
        }

        private static int ReadIntByName(object instance, string methodName, int fallback)
        {
            try
            {
                MethodInfo method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null && method.Invoke(instance, null) is int value)
                {
                    return value;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static bool ReadBoolByMemberName(object instance, string memberName, bool fallback)
        {
            try
            {
                PropertyInfo property = instance.GetType().GetProperty(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.GetValue(instance, null) is bool propValue)
                {
                    return propValue;
                }

                FieldInfo field = instance.GetType().GetField(memberName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (field != null && field.GetValue(instance) is bool fieldValue)
                {
                    return fieldValue;
                }
            }
            catch
            {
            }

            return fallback;
        }

        private static bool InvokeByName(object instance, string methodName, params object[] args)
        {
            MethodInfo method = ResolveMethod(instance.GetType(), methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, args);
            if (method == null)
            {
                return false;
            }

            try
            {
                method.Invoke(instance, args);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadLiveSpeedControlState(out int speed, out bool paused)
        {
            speed = 1;
            paused = true;
            MethodInfo method = typeof(OniAiController).GetMethod("ReadLiveSpeedControlState", BindingFlags.Static | BindingFlags.NonPublic);
            if (method == null)
            {
                return false;
            }

            object[] args = { 1, true };
            bool ok = false;
            try
            {
                object result = method.Invoke(null, args);
                ok = result is bool b && b;
            }
            catch
            {
                return false;
            }

            if (args[0] is int resolvedSpeed)
            {
                speed = resolvedSpeed;
            }

            if (args[1] is bool resolvedPaused)
            {
                paused = resolvedPaused;
            }

            return ok;
        }

        private static bool TryInvokeStatic(string methodName, out object outValue, params Type[] paramTypes)
        {
            outValue = null;
            MethodInfo method = typeof(OniAiController).GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic, null, paramTypes, null);
            if (method == null)
            {
                return false;
            }

            object[] args = new object[paramTypes.Length];
            for (int i = 0; i < args.Length; i++)
            {
                args[i] = null;
            }

            object result = method.Invoke(null, args);
            if (!(result is bool ok) || !ok)
            {
                return false;
            }

            outValue = args.LastOrDefault();
            return outValue != null;
        }

        private static T InvokeStatic<T>(string methodName, params object[] args)
        {
            MethodInfo method = ResolveMethod(typeof(OniAiController), methodName, BindingFlags.Static | BindingFlags.NonPublic, args);
            if (method == null)
            {
                throw new MissingMethodException("OniAiController", methodName);
            }

            object value = method.Invoke(null, args);
            return value is T typed ? typed : default;
        }

        private static T InvokeInstance<T>(OniAiController controller, string methodName, params object[] args)
        {
            MethodInfo method = ResolveMethod(typeof(OniAiController), methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, args);
            if (method == null)
            {
                throw new MissingMethodException("OniAiController", methodName);
            }

            object value = method.Invoke(controller, args);
            return value is T typed ? typed : default;
        }

        private static MethodInfo ResolveMethod(Type type, string methodName, BindingFlags flags, object[] args)
        {
            MethodInfo[] methods = type.GetMethods(flags).Where(method => string.Equals(method.Name, methodName, StringComparison.Ordinal)).ToArray();
            foreach (MethodInfo method in methods)
            {
                ParameterInfo[] parameters = method.GetParameters();
                if ((args == null ? 0 : args.Length) != parameters.Length)
                {
                    continue;
                }

                bool match = true;
                for (int index = 0; index < parameters.Length; index++)
                {
                    object arg = args[index];
                    Type parameterType = parameters[index].ParameterType;
                    if (arg == null)
                    {
                        if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) == null)
                        {
                            match = false;
                            break;
                        }

                        continue;
                    }

                    if (!parameterType.IsAssignableFrom(arg.GetType()))
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                {
                    return method;
                }
            }

            return null;
        }
    }
}
