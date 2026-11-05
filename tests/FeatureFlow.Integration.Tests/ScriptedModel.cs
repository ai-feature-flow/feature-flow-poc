using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// A stand-in for the model's API, so the integration tests run the real Claude Code, the real sandbox, the
/// real proxy and the real engine tools without spending model tokens, as Spike 2's mock router did
/// (<c>spikes/02-real-run/tools/mock-model-router.py</c>). It answers the Messages API, streaming or not. Each
/// main-loop request gets the next action of a script: a tool call or text. It accepts only requests carrying
/// <see cref="Credential"/>, so a request that reached it with any other credential (the step's token, say) is
/// refused, as the real API would.
/// </summary>
public sealed class ScriptedModel : IAsyncDisposable
{
    readonly List<Func<Conversation, JsonObject>> script = [];
    WebApplication? app;

    public ScriptedModel(string credential, int? port = null)
    {
        Credential = credential;
        Port = port ?? FreePort();
    }

    public string Credential { get; }
    public int Port { get; }
    public string Url => $"http://127.0.0.1:{Port}";

    /// <summary>Requests answered with the right credential, and requests refused.</summary>
    public int Accepted;

    public int Refused;

    /// <summary>Every main-loop request's body, in order.</summary>
    public ConcurrentQueue<JsonNode> Requests { get; } = new();

    public static int FreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }

    /// <summary>Adds the next action: a content block built from what the conversation holds so far.</summary>
    public ScriptedModel Then(Func<Conversation, JsonObject> action)
    {
        script.Add(action);
        return this;
    }

    public ScriptedModel ThenTool(string name, JsonObject input) => Then(_ => ToolUse(name, input));

    public ScriptedModel ThenBash(string command) => ThenTool("Bash", new JsonObject { ["command"] = command, ["description"] = "scripted" });

    public ScriptedModel ThenText(string text) => Then(_ => new JsonObject { ["type"] = "text", ["text"] = text });

    /// <summary>The action once the script is used up, chosen from the conversation; by default the text "Done."</summary>
    public Func<Conversation, JsonObject>? Otherwise { get; set; }

    public static JsonObject ToolUse(string name, JsonObject input) => new() { ["type"] = "tool_use", ["name"] = name, ["input"] = input };

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(Url);
        app = builder.Build();
        app.MapPost("/v1/messages/count_tokens", () => Results.Json(new { input_tokens = 100 }));
        app.MapPost("/v1/messages", HandleAsync);
        await app.StartAsync();
    }

    public async Task StopAsync()
    {
        if (app is not null)
            await app.DisposeAsync();
        app = null;
    }

    async Task HandleAsync(HttpContext http)
    {
        var key = http.Request.Headers["x-api-key"].FirstOrDefault()
                  ?? http.Request.Headers.Authorization.FirstOrDefault()?.Replace("Bearer ", "", StringComparison.Ordinal);
        if (key != Credential)
        {
            Interlocked.Increment(ref Refused);
            http.Response.StatusCode = 401;
            await http.Response.WriteAsync("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
            return;
        }

        Interlocked.Increment(ref Accepted);
        var body = (await JsonNode.ParseAsync(http.Request.Body))!;
        JsonObject block;
        var tools = body["tools"]?.AsArray().Select(t => t?["name"]?.ToString()).ToList() ?? [];
        // An agent's request offers its tools: Claude Code's (Bash, Read, mcp__engine__*) or Copilot CLI's (bash, view, engine-*).
        if (!tools.Any(t => t is "Bash" or "Read" or "bash" or "view" || t?.StartsWith("mcp__", StringComparison.Ordinal) == true
                            || t?.StartsWith("engine-", StringComparison.Ordinal) == true))
            block = new JsonObject { ["type"] = "text", ["text"] = "ok" };
        else
        {
            Requests.Enqueue(body.DeepClone());
            var conversation = new Conversation(body);
            var turn = conversation.AssistantTurns;
            block = turn < script.Count ? script[turn](conversation) : Otherwise?.Invoke(conversation) ?? new JsonObject { ["type"] = "text", ["text"] = "Done." };
            if (block["type"]?.ToString() == "tool_use")
                block["id"] = $"toolu_{turn:D3}_{Guid.NewGuid():N}"[..24];
        }

        var stop = block["type"]?.ToString() == "tool_use" ? "tool_use" : "end_turn";
        var model = body["model"]?.ToString() ?? "scripted";
        if (body["stream"]?.GetValue<bool>() != true)
        {
            await http.Response.WriteAsJsonAsync(new JsonObject
            {
                ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model,
                ["content"] = new JsonArray(block), ["stop_reason"] = stop, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 20 },
            });
            return;
        }

        http.Response.ContentType = "text/event-stream";
        var sse = new StringBuilder();
        void Event(string name, JsonObject data) => sse.Append($"event: {name}\ndata: {data.ToJsonString()}\n\n");
        Event("message_start", new JsonObject
        {
            ["type"] = "message_start",
            ["message"] = new JsonObject
            {
                ["id"] = "msg_" + Guid.NewGuid().ToString("N"), ["type"] = "message", ["role"] = "assistant", ["model"] = model,
                ["content"] = new JsonArray(), ["stop_reason"] = null, ["stop_sequence"] = null,
                ["usage"] = new JsonObject { ["input_tokens"] = 100, ["output_tokens"] = 1 },
            },
        });
        if (block["type"]?.ToString() == "tool_use")
        {
            Event("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = 0,
                ["content_block"] = new JsonObject { ["type"] = "tool_use", ["id"] = block["id"]!.DeepClone(), ["name"] = block["name"]!.DeepClone(), ["input"] = new JsonObject() },
            });
            Event("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = 0,
                ["delta"] = new JsonObject { ["type"] = "input_json_delta", ["partial_json"] = block["input"]!.ToJsonString() },
            });
        }
        else
        {
            Event("content_block_start", new JsonObject
            {
                ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" },
            });
            Event("content_block_delta", new JsonObject
            {
                ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = block["text"]!.DeepClone() },
            });
        }

        Event("content_block_stop", new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 });
        Event("message_delta", new JsonObject
        {
            ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = stop, ["stop_sequence"] = null },
            ["usage"] = new JsonObject { ["output_tokens"] = 20 },
        });
        Event("message_stop", new JsonObject { ["type"] = "message_stop" });
        await http.Response.WriteAsync(sse.ToString());
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>A main-loop request: how many assistant turns it holds, and the latest tool result's text.</summary>
    public sealed class Conversation(JsonNode body)
    {
        public JsonNode Body { get; } = body;

        public int AssistantTurns => Body["messages"]!.AsArray().Count(m => m?["role"]?.ToString() == "assistant");

        /// <summary>The text of the conversation's first user message: the prompt the session started with.</summary>
        public string Prompt => Body["messages"]!.AsArray().FirstOrDefault()?["content"] switch
        {
            JsonValue v => v.ToString(),
            JsonArray a => string.Join("\n", a.Select(x => x?["text"]?.ToString() ?? "")),
            _ => "",
        };

        /// <summary>The text of the last tool result the runtime sent back, or "".</summary>
        public string LastToolResult => ToolResults.LastOrDefault() ?? "";

        /// <summary>The text of every tool result in the conversation, in order.</summary>
        public List<string> ToolResults =>
            Body["messages"]!.AsArray()
                .Where(m => m?["role"]?.ToString() == "user" && m["content"] is JsonArray)
                .SelectMany(m => m!["content"]!.AsArray())
                .Where(b => b?["type"]?.ToString() == "tool_result")
                .Select(b => b!["content"] switch
                {
                    JsonValue v => v.ToString(),
                    JsonArray a => string.Join("\n", a.Select(x => x?["text"]?.ToString() ?? "")),
                    _ => "",
                })
                .ToList();
    }
}
