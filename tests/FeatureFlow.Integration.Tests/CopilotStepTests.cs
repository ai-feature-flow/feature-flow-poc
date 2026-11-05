using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M8's Copilot CLI runtime in a Docker sandbox: the real Copilot CLI, the real sandbox, model proxy and engine tools,
/// with a scripted model behind the proxy, as M4's Claude Code tests run. Copilot reaches the model as a custom
/// provider of the Messages API with only the step's token (LOG 293).
/// </summary>
[Trait("Category", "Integration")]
public sealed class CopilotStepTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    static string StepKey(string runId) => $"{runId}/implement/1";

    /// <summary>
    /// An implementer step on Copilot CLI commits, is denied a push by the hook (its settings are the engine's), reports
    /// through the engine's tool, and its commit reaches the work branch. Copilot's MCP tools are named <c>engine-*</c>.
    /// </summary>
    [Fact]
    public async Task A_copilot_step_commits_is_denied_a_push_and_reports_through_the_engine()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        string? pushResult = null;
        model.Then(_ => Bash("cd /ff/work && echo a > a.txt && git add -A && git commit -q -m 'Add a'"))
            .Then(_ => Bash("cd /ff/work && git push origin HEAD"))
            .Then(c =>
            {
                pushResult = c.LastToolResult;
                return ScriptedModel.ToolUse("create", new JsonObject { ["path"] = "/ff/out/notes.md", ["file_text"] = "Added a\n" });
            })
            .Then(_ => ScriptedModel.ToolUse("engine-report", AgentScripts.Report()))
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("copilot", model, new AgentHarnessOptions { Runtime = "copilot_cli" });
        var runId = h.Start("T-51", "Add a.txt.");
        var status = await h.WaitForEndAsync(runId, RunTimeout);
        var timeline = await h.TimelineAsync(runId);

        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        Assert.Contains("never push", pushResult);
        var events = await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, StepKey(runId));
        Assert.Contains(events, e => e.Kind == "hook_pre_tool_use" && e.Detail.StartsWith("Bash deny", StringComparison.Ordinal));
        Assert.Contains(events, e => e.Kind == "hook_pre_tool_use" && e.Detail.StartsWith("engine-report allow", StringComparison.Ordinal));
        Assert.Contains("session 1 started on runtime copilot_cli", timeline);
        Assert.Equal("Add a", await h.RemoteMessageAsync((await h.RemoteRevisionAsync("refs/heads/ff/ticket/T-51"))!));
        Assert.True(model.Accepted > 0 && model.Refused == 0, $"the model saw {model.Accepted} requests with the proxy's credential and {model.Refused} without");
        Assert.All(model.Requests, r => Assert.Equal("claude-haiku-4-5", r["model"]?.ToString()));
    }

    static JsonObject Bash(string command) => ScriptedModel.ToolUse("bash", new JsonObject { ["command"] = command, ["description"] = "scripted" });
}
