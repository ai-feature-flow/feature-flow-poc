using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// Live agent activity with the real CLI in a Docker sandbox and a scripted model (no model quota): while the session runs,
/// the agent worker records the CLI's completed events, so its message and tool calls can be read before the session ends.
/// Run for both runtimes.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentActivityTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    /// <summary>
    /// The agent says it is looking, runs <c>echo one</c>, then <c>sleep 8</c>, then reports. While it sleeps, the step's
    /// activity already holds the message and the first call, started and finished, and the second call started.
    /// </summary>
    [Theory]
    [InlineData("claude_code")]
    [InlineData("copilot_cli")]
    public async Task The_agents_message_and_tool_calls_are_recorded_before_its_session_ends(string runtime)
    {
        var claude = runtime == "claude_code";
        JsonObject Shell(string command) => ScriptedModel.ToolUse(claude ? "Bash" : "bash", new JsonObject { ["command"] = command, ["description"] = "scripted" });
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        model.ThenSay("Looking at the task.", _ => Shell("echo one"))
            .Then(_ => Shell("sleep 8"))
            .Then(_ => claude
                ? ScriptedModel.ToolUse("Write", AgentScripts.Write("/ff/out/notes.md", "Said one.\n"))
                : ScriptedModel.ToolUse("create", new JsonObject { ["path"] = "/ff/out/notes.md", ["file_text"] = "Said one.\n" }))
            .Then(_ => ScriptedModel.ToolUse(claude ? "mcp__engine__report" : "engine-report", AgentScripts.Report()))
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("activity-" + runtime[..6], model, new AgentHarnessOptions { Runtime = runtime });
        var runId = h.Start("T-71", "Say one.");
        var store = h.Host.Providers.Required<IRecordStoreProvider>("record_store");

        IReadOnlyList<ActivityRecord> seen = [];
        DateTimeOffset? seenAt = null;
        var deadline = DateTime.UtcNow + RunTimeout;
        while (seenAt is null && DateTime.UtcNow < deadline)
        {
            seen = (await store.QueryAsync<ActivityRecord>(Queries.Activity, new Dictionary<string, string> { ["run_id"] = runId, ["step_id"] = "implement" },
                TestContext.Current.CancellationToken)).Unwrap();
            var echo = seen.FirstOrDefault(a => a.Kind == ActivityKinds.ToolStarted && a.Detail?.Contains("echo one", StringComparison.Ordinal) == true);
            if (seen.Any(a => a is { Kind: ActivityKinds.Message, Text: "Looking at the task." })
                && echo is not null && seen.Any(a => a.Kind == ActivityKinds.ToolFinished && a.CallId == echo.CallId)
                && seen.Any(a => a.Kind == ActivityKinds.ToolStarted && a.Detail?.Contains("sleep 8", StringComparison.Ordinal) == true))
                seenAt = DateTimeOffset.UtcNow;
            else
                await Task.Delay(250, TestContext.Current.CancellationToken);
        }

        var status = await h.WaitForEndAsync(runId, RunTimeout);
        var timeline = await h.TimelineAsync(runId);
        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        Assert.True(seenAt is not null, $"the activity never held the message and both calls: {string.Join("; ", seen.Select(a => $"{a.Sequence} {a.Kind} {a.Text ?? a.Detail}"))}");
        var ended = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, AgentScripts.Key(runId))).First(e => e is { Kind: "session", Detail: "1 ended completed" }).At;
        Assert.True(seenAt < ended, $"the activity was first complete at {seenAt:O}, after the session ended at {ended:O}");
        // Seen while the agent slept: the sleep had started and not yet finished.
        var sleep = seen.Single(a => a.Kind == ActivityKinds.ToolStarted && a.Detail?.Contains("sleep 8", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(seen, a => a.Kind == ActivityKinds.ToolFinished && a.CallId == sleep.CallId);
        Assert.All(seen, a => Assert.Equal((1, 1), (a.Execution, a.Session)));

        // Once the session has ended, the route reads it from its stored transcript: the real CLI's transcript, read as turns.
        var activity = await h.GetAsync($"/runs/{runId}/steps/implement/activity");
        var session = activity["sessions"]!.AsArray().Single(s => (int)s!["session"]! == 1)!;
        Assert.Equal("transcript", (string?)session["source"]);
        Assert.Contains(session["turns"]!.AsArray(), t => (string?)t!["kind"] == "tool"
            && ((string?)t["tool"]!["arguments"])?.Contains("echo one", StringComparison.Ordinal) == true);
    }
}
