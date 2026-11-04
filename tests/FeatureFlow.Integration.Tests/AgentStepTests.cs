using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M4's agent step in a Docker sandbox, with the real Claude Code and a scripted model (docs/11, M4). Each test
/// runs one step of <c>m4:templates/agent_step</c> on a local bare repository standing for the code host.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AgentStepTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(4);

    static string StepKey(string runId) => $"{runId}/implement/1";

    /// <summary>
    /// The done-when, against the stand-in code host: an implementer step commits; the commit appears on the
    /// hidden reference while the step runs, and on the work branch only when the step finishes (docs/04
    /// section 3.1). The agent sleeps after committing, so the commit is pushed by detection, not at the end.
    /// </summary>
    [Fact]
    public async Task An_implementer_step_commits_to_the_hidden_reference_then_the_work_branch()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        model.ThenBash("cd /ff/work && printf 'hello\\n' > hello.txt && git add hello.txt && git commit -q -m 'Add hello' && git rev-parse HEAD")
            .ThenBash("sleep 10")
            .ThenTool("Write", AgentScripts.Write("/ff/out/notes.md", "Added hello.txt\n"))
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("commit", model);
        var runId = h.Start("T-1", "Add a file hello.txt holding the word hello, and commit it.");
        var hidden = AgentHarness.HiddenReference(runId, "implement");
        const string branch = "refs/heads/ff/ticket/T-1";

        string? hiddenWhileRunning = null;
        string? branchWhileHiddenSeen = null;
        var hiddenSeenAt = DateTimeOffset.MaxValue;
        var deadline = DateTime.UtcNow + RunTimeout;
        string status;
        while (true)
        {
            var view = await h.ShowAsync(runId);
            status = view["run"]!["status"]!.ToString();
            if (status is not ("starting" or "running") || view["waits"]!.AsArray().Count > 0 || DateTime.UtcNow > deadline)
                break;
            if (hiddenWhileRunning is null && await h.RemoteRevisionAsync(hidden) is { } seen)
            {
                hiddenWhileRunning = seen;
                hiddenSeenAt = DateTimeOffset.UtcNow;
                branchWhileHiddenSeen = await h.RemoteRevisionAsync(branch);
            }

            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        var timeline = await h.TimelineAsync(runId);
        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        Assert.NotNull(hiddenWhileRunning);
        Assert.Equal("Add hello", await h.RemoteMessageAsync(hiddenWhileRunning!));
        // While the commit was only on the hidden reference, the work branch was still at main.
        Assert.Equal(h.MainRevision, branchWhileHiddenSeen);
        // The commit reached the hidden reference while the agent's session still ran: detection pushed it, not the finish.
        var ended = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, StepKey(runId))).First(e => e is { Kind: "session", Detail: "1 ended completed" }).At;
        Assert.True(hiddenSeenAt < ended, $"the hidden reference was first seen at {hiddenSeenAt:O}, after the session ended at {ended:O}");
        var final = await h.RemoteRevisionAsync(branch);
        Assert.Equal(hiddenWhileRunning, final);
        Assert.Equal(final, await h.RemoteRevisionAsync(hidden));
        Assert.Contains($"pushed {final![..10]} to {hidden}", timeline);
        Assert.Contains($"moved ff/ticket/T-1 to {final[..10]}", timeline);
        Assert.True(model.Accepted > 0 && model.Refused == 0, $"the model saw {model.Accepted} requests with the proxy's credential and {model.Refused} without");
        Assert.DoesNotContain(FeatureFlow.Providers.Docker.DockerSandbox.NameOf(StepKey(runId)), await AgentHarness.SandboxesAsync());
    }

    /// <summary>
    /// LOG 193, review F3 of PR 14: the agent's checkout holds its parent branch at the parent's head as a local branch of
    /// its name, so a conflict resolver, with no remote (F5), can merge it. Here the parent is the default branch.
    /// </summary>
    [Fact]
    public async Task The_checkout_holds_the_parent_branch_at_its_head()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        model.ThenBash("cd /ff/work && (git rev-parse --verify -q refs/heads/main || echo none) > /ff/out/notes.md")
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("parent-ref", model);
        var runId = h.Start("T-31", "Note the parent branch's head.");
        var status = await h.WaitForEndAsync(runId, RunTimeout);
        var timeline = await h.TimelineAsync(runId);

        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        Assert.Equal(h.MainRevision, (await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "artifact notes: "))).Trim());
    }

    /// <summary>
    /// Hooks (docs/02 section 4.1): a <c>git push</c> is denied with the reason, and the files the agent read are
    /// recorded, so the engine publishes <c>coverage_known</c> and <c>changed_files_unread</c> (docs/04 section 3.6).
    /// The agent commits two files and reads one of them.
    /// </summary>
    [Fact]
    public async Task Hooks_deny_a_push_and_record_the_files_the_agent_read()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        string? pushResult = null;
        model.ThenBash("cd /ff/work && echo a > a.txt && echo b > b.txt && git add -A && git commit -q -m 'Add a and b'")
            .ThenBash("cd /ff/work && git push origin HEAD")
            .Then(c =>
            {
                pushResult = c.LastToolResult;
                return ScriptedModel.ToolUse("Read", new JsonObject { ["file_path"] = "/ff/work/a.txt" });
            })
            .ThenTool("Write", AgentScripts.Write("/ff/out/notes.md", "Added a and b\n"))
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("hooks", model);
        var runId = h.Start("T-10", "Add a.txt and b.txt.");
        var status = await h.WaitForEndAsync(runId, RunTimeout);
        var timeline = await h.TimelineAsync(runId);

        Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
        Assert.Contains("never push", pushResult);
        var hooks = await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, StepKey(runId));
        Assert.Contains(hooks, e => e.Kind == "hook_pre_tool_use" && e.Detail.StartsWith("Bash deny", StringComparison.Ordinal));
        Assert.Contains("engine outputs: commits=1 changed_files_unread=1 coverage_known=true", timeline);
    }

    /// <summary>
    /// Write access is enforced twice (docs/04 section 3.7): a reviewer's runtime has no editing tools, its hooks
    /// deny them, and its checkout is read-only, so a shell write fails too. The step still reports.
    /// </summary>
    [Fact]
    public async Task A_role_with_no_write_access_cannot_change_its_checkout()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        string? shellResult = null;
        model.ThenBash("touch /ff/work/x.txt; echo exit=$?")
            .Then(c =>
            {
                shellResult = c.LastToolResult;
                return ScriptedModel.ToolUse("Bash", new JsonObject { ["command"] = "echo reviewed > /ff/out/notes.md" });
            })
            .ThenTool("mcp__engine__report", AgentScripts.Report())
            .ThenText("Done.");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("readonly", model);
        var runId = h.Start("T-11", "Review the repository.", "m4:templates/review_step");
        var status = await h.WaitForEndAsync(runId, RunTimeout);

        Assert.True(status == "completed", $"the run ended {status}:\n{await h.TimelineAsync(runId)}");
        Assert.Contains("Permission denied", shellResult);
        Assert.All(model.Requests, r => Assert.DoesNotContain("\"name\":\"Write\"", r["tools"]!.ToJsonString()));
        Assert.Null(await h.RemoteRevisionAsync(AgentHarness.HiddenReference(runId, "implement")));
    }

    /// <summary>
    /// M3 follow-up 9: a cancelled step's activity is waited for. The run is aborted while its agent runs a long
    /// command; by the time the run's workflow has ended, the agent was stopped and its sandbox removed.
    /// </summary>
    [Fact]
    public async Task An_aborted_run_waits_for_its_agent_step_to_stop()
    {
        await using var model = new ScriptedModel("model-key-" + Guid.NewGuid().ToString("N"));
        // Claude Code refuses a long plain sleep, so the long command is a node timer.
        model.ThenBash("node -e \"setTimeout(() => {}, 300000)\"");
        await model.StartAsync();
        await using var h = await AgentHarness.StartAsync("cancel", model);
        var runId = h.Start("T-12", "Wait.");
        await h.WaitForEventAsync(StepKey(runId), e => e.Kind == "hook_pre_tool_use" && e.Detail.StartsWith("Bash allow", StringComparison.Ordinal), RunTimeout);
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var (exit, _, error) = h.Cli("abort", runId, "--reason", "test");
        Assert.True(exit == 0, error);
        await h.Host.Client.GetWorkflowHandle(runId).GetResultAsync<string>();

        // Read at once when the workflow has ended: with the default cancellation type it ends first and the
        // activity learns of the cancellation later, from a heartbeat.
        Assert.DoesNotContain(FeatureFlow.Providers.Docker.DockerSandbox.NameOf(StepKey(runId)), await AgentHarness.SandboxesAsync());
        Assert.Contains(await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, StepKey(runId)), e => e.Kind == "cancelled");
    }
}
