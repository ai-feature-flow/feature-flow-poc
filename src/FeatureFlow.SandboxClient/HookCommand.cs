using System.Text.Json.Nodes;

namespace FeatureFlow.SandboxClient;

/// <summary>
/// The hook command (docs/02 section 4.1): sends the runtime's hook input to a replica and applies its decision.
/// Unreachable is not denied (risk R19): it holds and retries across replicas for the hold time, then marks the session
/// stopped in a resumable state and, on the pre-tool-use hook, applies the reply the step's runtime gave it in
/// <c>FF_HOOK_UNREACHABLE</c>, or blocks the call with exit 2 when it gave none. It knows no runtime: a denial carries the
/// step's runtime's reply (what to print and the exit code), and a call with no decision is blocked with exit 2 and the
/// reason on standard error, which every runtime's CLI reads as blocked (LOG A5b-1).
/// </summary>
public static class HookCommand
{
    public static async Task<int> RunAsync(string hook, EngineClient engine, TextReader input, TextWriter output, TextWriter error, ClientLog log,
        CancellationToken cancellationToken, string? unreachable = null)
    {
        var text = await input.ReadToEndAsync(cancellationToken);
        JsonNode body;
        try
        {
            body = JsonNode.Parse(text) ?? new JsonObject();
        }
        catch (System.Text.Json.JsonException)
        {
            body = new JsonObject();
        }

        var tool = body["tool_name"]?.ToString() ?? "";
        JsonNode reply;
        try
        {
            reply = await engine.SendAsync(HttpMethod.Post, $"/agent/v1/hooks/{hook}", body, TimeSpan.FromSeconds(10), cancellationToken);
        }
        catch (EngineUnreachableException e)
        {
            log.Write("hold_exhausted", ("hook", hook), ("tool", tool));
            SessionStop.Mark(e.Message);
            // Only the pre-tool-use hook gates anything; the marker ends the session as resumable whatever the runtime does next.
            if (hook != "pre_tool_use")
                return 0;
            if (Parse(unreachable) is { } runtime && await ApplyAsync(runtime, output, error) is { } code)
                return code;
            await error.WriteLineAsync("the step is paused; it will be resumed");
            return 2;
        }

        log.Write("decision", ("hook", hook), ("tool", tool), ("decision", reply["decision"]?.ToString()));
        // Only the pre-tool-use hook gates anything. Another hook that gets no decision (a revoked or expired token)
        // must not block: exit 2 on Stop would keep the runtime from stopping.
        if (reply["decision"]?.ToString() == "allow" || hook != "pre_tool_use" && reply["decision"]?.ToString() != "deny")
            return 0;
        // The runtime's own way of refusing the call.
        if (reply["reply"] is JsonObject denial && await ApplyAsync(denial, output, error) is { } exit)
            return exit;

        // Fail closed: an answer with no reply (an expired or revoked token, a refused call) blocks it.
        await error.WriteLineAsync(reply["reason"]?.ToString() ?? "the engine did not allow this call: " + (reply["error"]?.ToString() ?? "no decision"));
        return 2;
    }

    static JsonObject? Parse(string? json)
    {
        try
        {
            return string.IsNullOrWhiteSpace(json) ? null : JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Prints a runtime's reply (<c>exit</c>, <c>output</c>, <c>error</c>) and gives its exit code; null when it has none.</summary>
    static async Task<int?> ApplyAsync(JsonObject reply, TextWriter output, TextWriter error)
    {
        if (reply["exit"] is not JsonValue code || !code.TryGetValue<int>(out var exit))
            return null;
        if (reply["output"]?.ToString() is { Length: > 0 } printed)
            await output.WriteLineAsync(printed);
        if (reply["error"]?.ToString() is { Length: > 0 } shown)
            await error.WriteLineAsync(shown);
        return exit;
    }
}
