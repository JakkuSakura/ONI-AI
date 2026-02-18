using System;
using System.Collections.Generic;
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
        private static readonly DateTime runtimeStartedAtUtc = DateTime.UtcNow;
        private static bool speedThreeMappedToRuntimeTwo;

        public JObject BuildRuntimeInfo()
        {
            DateTime observedAtUtc = DateTime.UtcNow;
            TimeSpan uptime = observedAtUtc - runtimeStartedAtUtc;

            return new JObject
            {
                ["runtime_started_at_utc"] = runtimeStartedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                ["observed_at_utc"] = observedAtUtc.ToString("o", CultureInfo.InvariantCulture),
                ["uptime_seconds"] = Math.Max(0, (int)Math.Floor(uptime.TotalSeconds))
            };
        }

        public JObject BuildHealth(OniAiController controller)
        {
            if (!TryReadLiveSpeedControlState(out int speed, out bool paused))
            {
                return null;
            }

            speed = NormalizeApiSpeedForCompatibility(speed);

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

            speed = NormalizeApiSpeedForCompatibility(speed);

            return new JObject
            {
                ["speed"] = speed,
                ["paused"] = paused
            };
        }

        public JObject ApplySpeed(int speed)
        {
            int requested = Mathf.Clamp(speed, 1, 3);

            JObject directPayload = null;
            try
            {
                directPayload = InvokeStatic<JObject>("ApplyLiveSpeedPayload", requested);
            }
            catch
            {
            }

            if (directPayload != null)
            {
                return directPayload;
            }

            if (!TryGetSpeedControl(out object speedControl))
            {
                return null;
            }

            InvokeByName(speedControl, "SetSpeed", requested);
            int currentSpeed = Mathf.Clamp(ReadIntByName(speedControl, "GetSpeed", 1), 1, 3);
            bool paused = ReadBoolByMemberName(speedControl, "IsPaused", true);

            int runtimeSpeed = currentSpeed;
            bool mapped = false;
            if (requested == 3 && currentSpeed != 3)
            {
                InvokeByName(speedControl, "SetSpeed", 2);
                int probe = Mathf.Clamp(ReadIntByName(speedControl, "GetSpeed", 1), 1, 3);
                if (probe == 2)
                {
                    currentSpeed = 3;
                    runtimeSpeed = probe;
                    mapped = true;
                    speedThreeMappedToRuntimeTwo = true;
                }
                else
                {
                    currentSpeed = probe;
                    runtimeSpeed = probe;
                }
            }

            string status = currentSpeed == requested ? "applied" : "rejected";

            var result = new JObject
            {
                ["status"] = status,
                ["requested_speed"] = requested,
                ["speed"] = currentSpeed,
                ["paused"] = paused,
                ["runtime_speed"] = runtimeSpeed,
                ["mapped_compatibility_mode"] = mapped
            };

            return result;
        }

        public JObject BuildPause()
        {
            if (!TryReadLiveSpeedControlState(out int speed, out bool paused))
            {
                return null;
            }

            speed = NormalizeApiSpeedForCompatibility(speed);

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

            var result = new JObject
            {
                ["status"] = "applied",
                ["paused"] = ReadBoolByMemberName(speedControl, "IsPaused", true),
                ["speed"] = NormalizeApiSpeedForCompatibility(Mathf.Clamp(ReadIntByName(speedControl, "GetSpeed", 1), 1, 3))
            };

            return result;
        }

        private static int NormalizeApiSpeedForCompatibility(int speed)
        {
            if (speedThreeMappedToRuntimeTwo && speed == 2)
            {
                return 3;
            }

            return speed;
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
            var result = new JObject
            {
                ["status"] = "applied",
                ["message"] = message
            };

            return result;
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
            int failed = 0;
            var results = new JArray();
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

                var result = new JObject();
                if (update["duplicant_id"]?.Type == JTokenType.String)
                {
                    result["duplicant_id"] = update["duplicant_id"].Value<string>();
                }

                if (update["duplicant_name"]?.Type == JTokenType.String)
                {
                    result["duplicant_name"] = update["duplicant_name"].Value<string>();
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

                if (update["duplicant_name"]?.Type == JTokenType.String)
                {
                    string duplicantName = update["duplicant_name"].Value<string>()?.Trim();
                    if (!string.IsNullOrEmpty(duplicantName))
                    {
                        parameters["duplicant_name"] = duplicantName;
                    }
                }

                try
                {
                    InvokeStatic<string>("ApplyDuplicantPriorityUpdate", parameters);
                }
                catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException inner)
                {
                    failed++;
                    result["status"] = "failed";
                    result["error"] = inner.Message;
                    results.Add(result);
                    continue;
                }
                catch (InvalidOperationException exception)
                {
                    failed++;
                    result["status"] = "failed";
                    result["error"] = exception.Message;
                    results.Add(result);
                    continue;
                }

                JArray snapshot = InvokeStatic<JArray>("BuildPrioritiesSnapshot") ?? new JArray();
                if (!TryFindPriorityEntry(snapshot, parameters, out JObject currentValues))
                {
                    failed++;
                    result["status"] = "failed";
                    result["error"] = "priority_snapshot_missing";
                    results.Add(result);
                    continue;
                }

                if (!AreRequestedPriorityValuesApplied(values, currentValues))
                {
                    failed++;
                    result["status"] = "failed";
                    result["error"] = "priority_update_not_observed";
                    result["requested"] = values.DeepClone();
                    result["observed"] = currentValues.DeepClone();
                    results.Add(result);
                    continue;
                }

                accepted++;
                result["status"] = "applied";
                results.Add(result);
            }

            var resultPayload = new JObject
            {
                ["accepted"] = accepted,
                ["failed"] = failed,
                ["status"] = failed == 0 ? "applied" : (accepted > 0 ? "partial" : "failed"),
                ["results"] = results
            };

            return resultPayload;
        }

        public JObject BuildPendingActionsProof()
        {
            JArray pending = InvokeStatic<JArray>("BuildPendingActionsSnapshot") ?? new JArray();
            int withCurrent = 0;
            int withQueues = 0;

            foreach (JToken token in pending)
            {
                if (!(token is JObject item))
                {
                    continue;
                }

                if (item["current_action"] != null)
                {
                    withCurrent++;
                }

                if (item["chores"] is JArray chores && chores.Count > 0)
                {
                    withQueues++;
                }
            }

            return new JObject
            {
                ["source"] = "game_live",
                ["observed_at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                ["counts"] = new JObject
                {
                    ["duplicants"] = pending.Count,
                    ["with_current_action"] = withCurrent,
                    ["with_chore_queue"] = withQueues
                },
                ["pending_actions"] = pending
            };
        }

        public JObject BuildCellProof(JObject payload)
        {
            if (payload == null)
            {
                return null;
            }

            return InvokeStatic<JObject>("BuildCellProofPayload", payload);
        }

        private static bool TryFindPriorityEntry(JArray snapshot, JObject parameters, out JObject currentValues)
        {
            currentValues = null;
            if (snapshot == null)
            {
                return false;
            }

            string targetId = parameters["duplicant_id"]?.Value<string>();
            string targetName = parameters["duplicant_name"]?.Value<string>();

            foreach (JToken token in snapshot)
            {
                if (!(token is JObject item))
                {
                    continue;
                }

                string snapshotId = item["duplicant_id"]?.Value<string>();
                string snapshotName = item["duplicant_name"]?.Value<string>();

                bool idMatch = !string.IsNullOrWhiteSpace(targetId) && string.Equals(snapshotId, targetId, StringComparison.Ordinal);
                bool nameMatch = !string.IsNullOrWhiteSpace(targetName) && string.Equals(snapshotName, targetName, StringComparison.Ordinal);
                if (!idMatch && !nameMatch)
                {
                    continue;
                }

                currentValues = item["values"] as JObject ?? new JObject();
                return true;
            }

            return false;
        }

        private static bool AreRequestedPriorityValuesApplied(JObject requested, JObject observed)
        {
            if (requested == null)
            {
                return false;
            }

            var observedMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (observed != null)
            {
                foreach (JProperty property in observed.Properties())
                {
                    if (property.Value.Type == JTokenType.Integer)
                    {
                        observedMap[property.Name] = Mathf.Clamp(property.Value.Value<int>(), 0, 5);
                    }
                }
            }

            foreach (JProperty property in requested.Properties())
            {
                if (property.Value.Type != JTokenType.Integer)
                {
                    continue;
                }

                int expected = Mathf.Clamp(property.Value.Value<int>(), 0, 5);
                if (!observedMap.TryGetValue(property.Name, out int actual) || actual != expected)
                {
                    return false;
                }
            }

            return true;
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
            var result = new JObject
            {
                ["status"] = "applied",
                ["message"] = message
            };

            return result;
        }

        public JObject ApplyDig(OniAiController controller, JObject payload)
        {
            if (payload == null)
            {
                return null;
            }

            string message = InvokeInstance<string>(controller, "ApplyDigAction", payload);
            var result = new JObject
            {
                ["status"] = "applied",
                ["message"] = message
            };

            return result;
        }

        public JObject ApplyDeconstruct(OniAiController controller, JObject payload)
        {
            if (payload == null)
            {
                return null;
            }

            string message = InvokeInstance<string>(controller, "ApplyDeconstructAction", payload);
            var result = new JObject
            {
                ["status"] = "applied",
                ["message"] = message
            };

            return result;
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
