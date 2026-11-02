using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// SP2 as a test (docs/spikes/sp2-results.md), and a worker killed mid-step: the agent worker runs in a host of
/// its own, which the test stops and starts again while the agent waits in its sandbox.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentResumeTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(5);

    static string Key(string runId) => $"{runId}/implement/1";

    /// <summary>
    /// SP2's S2 with a restart: once the question is open, the agent worker stops; the person answers while no
    /// worker runs; a new worker starts. The agent gets the answer once, from the one question, and the new
    /// worker resumes the step from the same sandbox (docs/02 section 4.3).
    /// </summary>
    [Fact]
    public async Task A_question_is_answered_after_the_agent_worker_is_restarted()
    {
        var nonce = "teal-" + Random.Shared.Next(10000, 99999);
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        model.ThenTool("mcp__engine__ask_human", new JsonObject { ["question"] = "Which colour?" })
            .Then(c => ScriptedModel.ToolUse("Write", AgentScripts.Write("/ff/out/notes.md", c.LastToolResult + "\n")))
            .Then(c => ScriptedModel.ToolUse("mcp__engine__report", AgentScripts.Report(answer: AgentScripts.Answer(c))))
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("restart", model, new AgentHarnessOptions { SeparateAgentWorker = true });
        var runId = h.Start("T-6", "Ask which colour, then report the answer.");
        var wait = await h.WaitForWaitAsync(runId, "question", RunTimeout);

        await h.StopAgentWorkerAsync();
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        var (exit, _, error) = h.Cli("answer", runId, wait, nonce);
        Assert.True(exit == 0, error);
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await h.StartAgentWorkerAsync();
        var status = await h.WaitForEndAsync(runId, RunTimeout);

        var timeline = await h.TimelineAsync(runId);
        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        var question = Assert.Single(await h.RecordsAsync<QuestionRecord>(Queries.Questions, Key(runId)));
        Assert.Equal(nonce, question.Answer);
        var report = (await h.RecordsAsync<ReportRecord>(Queries.Reports, Key(runId))).Last(r => r.Accepted).Report!;
        Assert.Contains(nonce, report.Outputs["answer"]);
        // E1: the client lost its replica while waiting, and reconnected.
        var log = await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "sandbox client log: "));
        Assert.Contains("\"connection_failed\"", log);
        Assert.Contains("\"answer_received\"", log);
        // The new worker re-attached to the sandbox the first one created; no second sandbox was made.
        await AgentScripts.AssertOneSandboxAsync(h, Key(runId));
        // C2: one ask_human call reached the model's conversation.
        Assert.Single(model.Requests.Last()["messages"]!.AsArray().SelectMany(m => m!["content"] as JsonArray ?? []),
            b => b?["type"]?.ToString() == "tool_use" && b["name"]?.ToString() == "mcp__engine__ask_human");
    }

    /// <summary>
    /// A worker killed mid-step resumes from the same sandbox: the agent is in a long command when the worker stops;
    /// a new worker re-attaches to the sandbox and to the running session, and the step completes with the commit
    /// the agent made before the kill.
    /// </summary>
    [Fact]
    public async Task A_worker_killed_mid_step_resumes_from_the_same_sandbox()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        model.ThenBash("cd /ff/work && echo one > one.txt && git add one.txt && git commit -q -m 'Add one' && sleep 25")
            .ThenTool("Write", AgentScripts.Write("/ff/out/notes.md", "Added one.txt\n"))
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("kill", model, new AgentHarnessOptions { SeparateAgentWorker = true });
        var runId = h.Start("T-7", "Add one.txt and commit it.");
        await h.WaitForEventAsync(Key(runId), e => e is { Kind: "session", Detail: "1 started" }, RunTimeout);
        await Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken);
        await h.StopAgentWorkerAsync();
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await h.StartAgentWorkerAsync();
        var status = await h.WaitForEndAsync(runId, RunTimeout);

        var timeline = await h.TimelineAsync(runId);
        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        await AgentScripts.AssertOneSandboxAsync(h, Key(runId));
        Assert.Equal("Add one",await h.RemoteMessageAsync((await h.RemoteRevisionAsync("refs/heads/ff/ticket/T-7"))!));
        // One session: the new worker re-attached to it rather than starting another.
        var sessions = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, Key(runId))).Where(e => e.Kind == "session").Select(e => e.Detail).ToList();
        Assert.Equal(["1 started", "1 ended completed"], sessions);
    }
}
