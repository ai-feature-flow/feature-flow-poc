using System.Text.Json.Nodes;

namespace FeatureFlow.SandboxClient;

/// <summary>
/// The hook command (docs/02 section 4.1): sends the runtime's hook input to a replica and applies its decision.
/// Unreachable is not denied (risk R19): it holds and retries across replicas for the hold time, then stops the
/// session in a resumable state. Its exit codes follow Claude Code's hooks: 0 allows, 2 blocks with the reason
/// on standard error. Copilot CLI shows the agent only the exit code of a hook that exits 2, so for it
/// (<paramref name="copilot"/>) a denial is its JSON decision with the reason, and exit 0.
/// </summary>
public static class HookCommand
{
    public static async Task<int> RunAsync(string hook, EngineClient engine, TextReader input, TextWriter output, TextWriter error, ClientLog log,
        CancellationToken cancellationToken, bool copilot = false)
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
        // Fail closed: a denial, or an answer with no decision (an expired or revoked token, a refused call), blocks it.
        var reason = reply["reason"]?.ToString() ?? "the engine did not allow this call: " + (reply["error"]?.ToString() ?? "no decision");
        if (copilot)
        {
            await output.WriteLineAsync(new JsonObject { ["permissionDecision"] = "deny", ["permissionDecisionReason"] = reason }.ToJsonString());
            return 0;
        }

        await error.WriteLineAsync(reason);
        return 2;
    }
}
