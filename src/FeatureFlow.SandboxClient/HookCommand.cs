using System.Text.Json.Nodes;

namespace FeatureFlow.SandboxClient;

/// <summary>
/// The hook command (docs/02 section 4.1): sends the runtime's hook input to a replica and applies its decision.
/// Unreachable is not denied (risk R19): it holds and retries across replicas for the hold time, then stops the
/// session in a resumable state. It knows no runtime: a denial carries the step's runtime's reply (what to print and
/// the exit code), and a call with no decision is blocked with exit 2 and the reason on standard error, which every
/// runtime's CLI reads as blocked (LOG A5b-1).
/// </summary>
public static class HookCommand
{
    public static async Task<int> RunAsync(string hook, EngineClient engine, TextReader input, TextWriter output, TextWriter error, ClientLog log,
        CancellationToken cancellationToken)
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
            await output.WriteLineAsync(new JsonObject { ["continue"] = false, ["stopReason"] = "the engine is unreachable; the step can be resumed" }.ToJsonString());
            return 0;
        }

        log.Write("decision", ("hook", hook), ("tool", tool), ("decision", reply["decision"]?.ToString()));
        // Only the pre-tool-use hook gates anything. Another hook that gets no decision (a revoked or expired token)
        // must not block: exit 2 on Stop would keep the runtime from stopping.
        if (reply["decision"]?.ToString() == "allow" || hook != "pre_tool_use" && reply["decision"]?.ToString() != "deny")
            return 0;
        // The runtime's own way of refusing the call.
        if (reply["reply"] is JsonObject runtime && runtime["exit"] is JsonValue code && code.TryGetValue<int>(out var exit))
        {
            if (runtime["output"]?.ToString() is { Length: > 0 } printed)
                await output.WriteLineAsync(printed);
            if (runtime["error"]?.ToString() is { Length: > 0 } shown)
                await error.WriteLineAsync(shown);
            return exit;
        }

        // Fail closed: an answer with no reply (an expired or revoked token, a refused call) blocks it.
        await error.WriteLineAsync(reply["reason"]?.ToString() ?? "the engine did not allow this call: " + (reply["error"]?.ToString() ?? "no decision"));
        return 2;
    }
}
