using System.Net;
using System.Text;
using System.Text.Json.Nodes;

var port = 18766;
for (var index = 0; index < args.Length; index++)
{
    if (args[index] == "--port" && index + 1 < args.Length && int.TryParse(args[index + 1], out var parsedPort))
    {
        port = parsedPort;
        index++;
    }
}

var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/");
listener.Start();

var queuedActions = new JsonArray();
var requestCounters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
{
    ["/health"] = 0,
    ["/state"] = 0,
    ["/actions_get"] = 0,
    ["/actions_post"] = 0,
    ["/priorities_get"] = 0,
    ["/priorities_post"] = 0,
};

var priorityUpdates = new JsonArray();

var statePayload = new JsonObject
{
    ["request_id"] = "example_idle_001",
    ["request_dir"] = ".",
    ["api_base_url"] = "http://127.0.0.1:8766",
    ["screenshot_path"] = "screenshot.png",
    ["requested_at_utc"] = "2026-02-13T23:15:00Z",
    ["context"] = new JsonObject
    {
        ["cycle"] = 42,
        ["time_since_cycle_start"] = 312.6,
        ["time_in_cycles"] = 42.52,
        ["paused"] = true,
        ["current_speed"] = 1,
        ["previous_speed"] = 2,
        ["real_time_since_startup_seconds"] = 5123.4,
        ["unscaled_time_seconds"] = 5098.1,
    },
    ["duplicants"] = new JsonArray
    {
        new JsonObject
        {
            ["id"] = "1001",
            ["name"] = "Ada",
            ["status"] = new JsonObject
            {
                ["active_self"] = true,
                ["active_in_hierarchy"] = true,
                ["current_chore"] = "Idle",
            },
            ["priority"] = new JsonObject
            {
                ["dig"] = 6,
                ["build"] = 5,
                ["life_support"] = 8,
            },
            ["skills"] = new JsonArray("ImprovedDigging1", "HardDigging"),
        },
    },
    ["pending_actions"] = new JsonArray
    {
        new JsonObject
        {
            ["id"] = "act_001",
            ["type"] = "dig",
            ["priority"] = 6,
            ["status"] = "queued",
            ["duplicant_id"] = "1001",
            ["duplicant_name"] = "Ada",
        },
    },
    ["priorities"] = new JsonArray
    {
        new JsonObject
        {
            ["duplicant_id"] = "1001",
            ["duplicant_name"] = "Ada",
            ["values"] = new JsonObject
            {
                ["dig"] = 6,
                ["build"] = 5,
                ["life_support"] = 8,
            },
        },
    },
    ["runtime_config"] = new JsonObject
    {
        ["unity_version"] = "2021.3.39f1",
        ["platform"] = "OSXPlayer",
        ["target_frame_rate"] = -1,
        ["product_name"] = "Oxygen Not Included",
        ["version"] = "U55-678078",
        ["scene_count"] = 2,
    },
    ["assemblies"] = new JsonArray
    {
        new JsonObject { ["name"] = "Assembly-CSharp", ["version"] = "0.0.0.0" },
        new JsonObject { ["name"] = "UnityEngine.CoreModule", ["version"] = "0.0.0.0" },
        new JsonObject { ["name"] = "Newtonsoft.Json", ["version"] = "13.0.0.0" },
    },
    ["scenes"] = new JsonArray
    {
        new JsonObject
        {
            ["index"] = 0,
            ["name"] = "Main",
            ["is_loaded"] = true,
            ["root_count"] = 2,
            ["roots"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "Game",
                    ["active"] = true,
                    ["child_count"] = 5,
                    ["components"] = new JsonArray("Game", "KMonoBehaviour"),
                },
                new JsonObject
                {
                    ["name"] = "OverlayCanvas",
                    ["active"] = true,
                    ["child_count"] = 22,
                    ["components"] = new JsonArray("Canvas", "GraphicRaycaster"),
                },
            },
        },
        new JsonObject
        {
            ["index"] = 1,
            ["name"] = "ClusterManager",
            ["is_loaded"] = true,
            ["root_count"] = 1,
            ["roots"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "WorldContainer",
                    ["active"] = true,
                    ["child_count"] = 3,
                    ["components"] = new JsonArray("Transform", "ClusterManager"),
                },
            },
        },
    },
    ["singletons"] = new JsonArray
    {
        new JsonObject
        {
            ["type"] = "SpeedControlScreen",
            ["summary"] = new JsonObject
            {
                ["object_type"] = "SpeedControlScreen",
                ["values"] = new JsonObject
                {
                    ["IsPaused"] = true,
                    ["CurrentSpeed"] = 1,
                },
            },
        },
        new JsonObject
        {
            ["type"] = "GameClock",
            ["summary"] = new JsonObject
            {
                ["object_type"] = "GameClock",
                ["values"] = new JsonObject
                {
                    ["Cycle"] = 42,
                    ["TimeInCycle"] = 312.6,
                },
            },
        },
    },
    ["world"] = new JsonObject
    {
        ["world_id"] = 0,
        ["biome"] = "Temperate",
        ["surrounding_blocks"] = new JsonArray
        {
            new JsonObject { ["x"] = 22, ["y"] = 16, ["element"] = "Oxygen", ["temperature_c"] = 24.1 },
            new JsonObject { ["x"] = 23, ["y"] = 16, ["element"] = "Sandstone", ["temperature_c"] = 26.3 },
        },
    },
};

Console.WriteLine($"mock_oni_api_listening=http://127.0.0.1:{port}");

while (true)
{
    HttpListenerContext context;
    try
    {
        context = await listener.GetContextAsync();
    }
    catch (HttpListenerException)
    {
        return;
    }

    var path = context.Request.Url?.AbsolutePath ?? "/";
    var method = context.Request.HttpMethod ?? "GET";

    if (path == "/health" && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
    {
        requestCounters["/health"]++;
        await WriteJson(context.Response, 200, new JsonObject
        {
            ["ok"] = true,
            ["service"] = "oni_api_mock",
            ["port"] = port,
        });
        continue;
    }

    if (path == "/state" && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
    {
        requestCounters["/state"]++;
        await WriteJson(context.Response, 200, new JsonObject
        {
            ["state"] = statePayload,
            ["last_execution"] = null,
            ["pending_action_count"] = queuedActions.Count,
        });
        continue;
    }

    if (path == "/actions" && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
    {
        requestCounters["/actions_get"]++;
        await WriteJson(context.Response, 200, new JsonObject
        {
            ["actions"] = queuedActions,
            ["source"] = "game_pending",
        });
        continue;
    }

    if (path == "/priorities" && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
    {
        requestCounters["/priorities_get"]++;
        await WriteJson(context.Response, 200, new JsonObject
        {
            ["priorities"] = statePayload["priorities"]?.DeepClone(),
            ["updates"] = priorityUpdates,
            ["source"] = "game_live",
        });
        continue;
    }

    if (path == "/actions" && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
    {
        requestCounters["/actions_post"]++;
        JsonNode? rootNode;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            rootNode = JsonNode.Parse(body);
        }
        catch
        {
            await WriteJson(context.Response, 400, new JsonObject { ["error"] = "invalid_json" });
            continue;
        }

        if (rootNode is not JsonObject bodyObject || bodyObject["actions"] is not JsonArray incomingActions)
        {
            await WriteJson(context.Response, 400, new JsonObject { ["error"] = "actions_must_be_array" });
            continue;
        }

        var accepted = 0;
        foreach (var item in incomingActions)
        {
            if (item is JsonObject action)
            {
                queuedActions.Add(action.DeepClone());
                accepted++;
            }
        }

        await WriteJson(context.Response, 200, new JsonObject { ["accepted"] = accepted });
        continue;
    }

    if (path == "/priorities" && method.Equals("POST", StringComparison.OrdinalIgnoreCase))
    {
        requestCounters["/priorities_post"]++;
        JsonNode? rootNode;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            rootNode = JsonNode.Parse(body);
        }
        catch
        {
            await WriteJson(context.Response, 400, new JsonObject { ["error"] = "invalid_json" });
            continue;
        }

        JsonArray? incomingPriorities = null;
        if (rootNode is JsonObject bodyObject)
        {
            incomingPriorities = bodyObject["priorities"] as JsonArray;
            if (incomingPriorities is null)
            {
                incomingPriorities = bodyObject["updates"] as JsonArray;
            }
        }

        if (incomingPriorities is null)
        {
            await WriteJson(context.Response, 400, new JsonObject { ["error"] = "priorities_must_be_array" });
            continue;
        }

        var accepted = 0;
        foreach (var item in incomingPriorities)
        {
            if (item is JsonObject update)
            {
                priorityUpdates.Add(update.DeepClone());
                accepted++;
            }
        }

        await WriteJson(context.Response, 200, new JsonObject
        {
            ["accepted"] = accepted,
            ["status"] = "scheduled",
        });
        continue;
    }

    if (path == "/stats" && method.Equals("GET", StringComparison.OrdinalIgnoreCase))
    {
        await WriteJson(context.Response, 200, new JsonObject
        {
            ["counters"] = new JsonObject
            {
                ["health"] = requestCounters["/health"],
                ["state"] = requestCounters["/state"],
                ["actions_get"] = requestCounters["/actions_get"],
                ["actions_post"] = requestCounters["/actions_post"],
                ["priorities_get"] = requestCounters["/priorities_get"],
                ["priorities_post"] = requestCounters["/priorities_post"],
            },
            ["pending_action_count"] = queuedActions.Count,
        });
        continue;
    }

    await WriteJson(context.Response, 404, new JsonObject { ["error"] = "not_found" });
}

static async Task WriteJson(HttpListenerResponse response, int statusCode, JsonObject payload)
{
    var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());
    response.StatusCode = statusCode;
    response.ContentType = "application/json; charset=utf-8";
    response.ContentLength64 = bytes.Length;
    await response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
    response.OutputStream.Close();
}
