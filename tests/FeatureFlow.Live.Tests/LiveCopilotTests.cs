using FeatureFlow.Integration.Tests;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Live.Tests;

/// <summary>
/// SP5's checks for Copilot CLI against the real world (docs/spikes/sp5-results.md; docs/11 M8, verified by): Copilot CLI in
/// the sandbox with the real model through the proxy, which alone holds the subscription token, on the fixture repository
/// through the GitHub App. C1: a real step completes and commits. C2 to C4: no credential in its environment, home or
/// transcript. N1: with the model unreachable it does not succeed. N2: with a key the model refuses it does not succeed,
/// having no other credential to fall back to. Each test removes the references it pushed.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveCopilotTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(8);
    static readonly AgentHarnessOptions Live = new() { Live = true, Runtime = "copilot_cli", CommitInterval = TimeSpan.FromSeconds(10) };

    static string Ticket(string name) => $"LIVE-copilot-{name}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

    static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("FF_LIVE_LOG") is { Length: > 0 } path)
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {line}\n");
    }

    static async Task LogRunAsync(AgentHarness h, string test, string runId)
    {
        var events = await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, AgentScripts.Key(runId));
        var reports = await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId));
        Log($"copilot {test}: run {runId}; reports {string.Join(",", reports.Select(r => r.Accepted ? "accepted" : "rejected"))}; " +
            $"sessions {string.Join(",", events.Where(e => e.Kind == "session").Select(e => e.Detail))}; model requests {events.Count(e => e.Kind == "model")} " +
            $"({string.Join(",", events.Where(e => e.Kind == "model").Select(e => e.Detail.Split(' ')[2]).Distinct())}); " +
            $"hook calls {events.Count(e => e.Kind.StartsWith("hook_", StringComparison.Ordinal))}");
    }

    async Task CleanUpAsync(AgentHarness h, string test, string runId, string ticket)
    {
        await LogRunAsync(h, test, runId);
        await h.StopRunAsync(runId);
        await h.DeleteRemoteReferencesAsync($"refs/heads/ff/ticket/{ticket}", AgentHarness.HiddenReference(runId, "implement"));
    }

    const string Finish = " Then write notes.md in your output folder saying what you did, and report the outcome implemented with outputs answer set to none" +
                          " and the artifact notes set to notes.md.";

    /// <summary>C1: a real implementer step on Copilot CLI commits, and the commit reaches the work branch.</summary>
    [Fact]
    public async Task A_copilot_implementer_step_on_the_fixture_repository_commits_to_the_work_branch()
    {
        await using var h = await AgentHarness.StartAsync("live-copilot", null, Live);
        var ticket = Ticket("done");
        var runId = h.Start(ticket, $"Create the file ff-live/{ticket}.md holding the single line 'Feature Flow live test {ticket}', and commit it " +
                                    $"with git, with the message 'Add {ticket} note'." + Finish);
        try
        {
            var status = await h.WaitForEndAsync(runId, RunTimeout);
            var timeline = await h.TimelineAsync(runId);
            Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
            var final = await h.RemoteRevisionAsync($"refs/heads/ff/ticket/{ticket}");
            Assert.NotEqual(h.MainRevision, final);
            Assert.Contains("session 1 started on runtime copilot_cli", timeline);
            Log($"copilot C1: run {runId}; branch at {final![..10]}");
        }
        finally
        {
            await CleanUpAsync(h, "C1", runId, ticket);
        }
    }

    /// <summary>C2 to C4: the real agent records its environment; no credential is in it, its home or its transcript.</summary>
    [Fact]
    public async Task The_real_copilot_agent_s_sandbox_holds_no_model_or_github_credential()
    {
        await using var h = await AgentHarness.StartAsync("live-copilot-sp5", null, Live);
        var ticket = Ticket("sp5");
        var runId = h.Start(ticket, "Change nothing in the repository. Run this command with your shell tool, exactly: " +
                                    "{ env | sort; echo '--- home'; ls -A \"$HOME\"; echo '--- startup bytes'; cat \"$HOME/.profile\" \"$HOME/.bashrc\" " +
                                    "\"$HOME/.bash_profile\" \"$HOME/.zshenv\" \"$HOME/.zshrc\" | wc -c; } > /ff/out/notes.md . Then report the outcome " +
                                    "implemented with outputs answer set to none and the artifact notes set to notes.md.");
        try
        {
            var status = await h.WaitForEndAsync(runId, RunTimeout);
            var timeline = await h.TimelineAsync(runId);
            Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
            var notes = await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "artifact notes: "));
            var transcript = await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "transcript of session 1: "));
            // The agent's own view is real; Copilot keeps even the step's token, its provider key, out of its tools' shells.
            Assert.Contains("HOME=/ff/home", notes);
            Assert.Contains("COPILOT_AGENT_SESSION_ID=", notes);
            Assert.DoesNotContain("COPILOT_PROVIDER_API_KEY=", notes);
            Assert.Contains("--- startup bytes\n0", notes);
            foreach (var name in new[] { "CLAUDE_CODE_OAUTH_TOKEN=", "ANTHROPIC_API_KEY=", "COPILOT_GITHUB_TOKEN=", "GH_TOKEN=", "GITHUB_TOKEN=" })
                Assert.DoesNotContain(name, notes);
            var credential = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN")!;
            Assert.False(notes.Contains(credential, StringComparison.Ordinal), "the credential is in the agent's environment");
            Assert.False(transcript.Contains(credential, StringComparison.Ordinal), "the credential is in the transcript");
            var model = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, AgentScripts.Key(runId))).Where(e => e.Kind == "model").ToList();
            Assert.NotEmpty(model);
            Assert.All(model, e => Assert.Contains(" 200 ", e.Detail));
        }
        finally
        {
            await CleanUpAsync(h, "C2-C4", runId, ticket);
        }
    }

    /// <summary>N1: the model is unreachable behind the proxy; the step stops resumably, uses no attempt and does not succeed.</summary>
    [Fact]
    public async Task With_the_model_unreachable_the_real_copilot_step_does_not_succeed()
    {
        await using var h = await AgentHarness.StartAsync("live-copilot-n1", null, Live with { ModelUpstream = "http://127.0.0.1:9" });
        var ticket = Ticket("n1");
        var runId = h.Start(ticket, "Change nothing in the repository. Write notes.md in your output folder saying hello." + Finish);
        try
        {
            var deadline = DateTime.UtcNow + RunTimeout;
            while (DateTime.UtcNow < deadline)
            {
                var sessions = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, AgentScripts.Key(runId))).Where(e => e.Kind == "session").Select(e => e.Detail).ToList();
                if (sessions.Count(s => s.EndsWith("ended model_unreachable", StringComparison.Ordinal)) >= 2)
                    break;
                await Task.Delay(2000, TestContext.Current.CancellationToken);
            }

            var view = await h.ShowAsync(runId);
            Assert.Equal("running", view["run"]!["status"]!.ToString());
            Assert.DoesNotContain("attempt_failed", await h.TimelineAsync(runId));
            Assert.Empty(await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId)));
            var ended = (await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, AgentScripts.Key(runId))).Where(e => e.Kind == "session" && e.Detail.Contains("ended", StringComparison.Ordinal)).ToList();
            Assert.True(ended.Count >= 2 && ended.All(e => e.Detail.EndsWith("ended model_unreachable", StringComparison.Ordinal)), string.Join(", ", ended.Select(e => e.Detail)));
        }
        finally
        {
            await CleanUpAsync(h, "N1", runId, ticket);
        }
    }

    /// <summary>N2: the proxy sends a key the model refuses; the step does not succeed but stops for a person, a configuration fault.</summary>
    [Fact]
    public async Task With_a_key_the_model_refuses_the_real_copilot_step_stops_for_a_person()
    {
        await using var h = await AgentHarness.StartAsync("live-copilot-n2", null, Live with { ModelKey = "not-a-valid-model-credential" });
        var ticket = Ticket("n2");
        var runId = h.Start(ticket, "Change nothing in the repository. Write notes.md in your output folder saying hello." + Finish);
        try
        {
            await h.WaitForWaitAsync(runId, "intervention", RunTimeout);
            Assert.Contains("the model refused the step's requests", await h.TimelineAsync(runId));
            Assert.Empty(await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId)));
        }
        finally
        {
            await CleanUpAsync(h, "N2", runId, ticket);
        }
    }
}
