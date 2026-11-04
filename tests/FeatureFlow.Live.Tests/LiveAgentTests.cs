using FeatureFlow.Integration.Tests;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Live.Tests;

/// <summary>
/// M4 against the real world (docs/11 section 4.1, Live): Claude Code with the real model through the proxy, holding
/// the subscription token (SP5's arrangement), and the fixture repository <c>ai-feature-flow/feature-flow-example</c>
/// through the GitHub App. They spend model tokens and push a branch and a hidden reference, which each test
/// removes afterwards. Run with FF_SECRETS_FILE, CLAUDE_CODE_OAUTH_TOKEN and FF_TEMPORAL_ADDRESS set.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveAgentTests
{
    static readonly TimeSpan RunTimeout = TimeSpan.FromMinutes(8);
    static readonly AgentHarnessOptions Live = new() { Live = true, CommitInterval = TimeSpan.FromSeconds(10) };

    static string Ticket(string name) => $"LIVE-{name}-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";

    /// <summary>A line for the results note, appended to the file <c>FF_LIVE_LOG</c> names. Never a credential.</summary>
    static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("FF_LIVE_LOG") is { Length: > 0 } path)
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {line}\n");
    }

    static async Task LogRunAsync(AgentHarness h, string test, string runId)
    {
        var key = AgentScripts.Key(runId);
        var reports = await h.RecordsAsync<ReportRecord>(Queries.Reports, key);
        var events = await h.RecordsAsync<AgentEventRecord>(Queries.AgentEvents, key);
        var model = events.Where(e => e.Kind == "model").ToList();
        Log($"{test}: run {runId}; reports {string.Join(",", reports.Select(r => r.Accepted ? "accepted" : "rejected"))}; " +
            $"sessions {string.Join(",", events.Where(e => e.Kind == "session").Select(e => e.Detail))}; model requests {model.Count}; " +
            $"hook calls {events.Count(e => e.Kind.StartsWith("hook_", StringComparison.Ordinal))}; rejections: {string.Join(" / ", reports.Where(r => !r.Accepted).Select(r => string.Join("; ", r.Errors)))}");
    }

    const string Finish = " Then write notes.md in your output folder saying what you did, and report the outcome implemented with outputs answer set to none" +
                          " and the artifact notes set to notes.md.";

    /// <summary>
    /// M4's done-when: an implementer step on the fixture repository commits, and the commit appears on the hidden
    /// reference, then on the work branch.
    /// </summary>
    [Fact]
    public async Task An_implementer_step_on_the_fixture_repository_commits_to_the_hidden_reference_then_the_work_branch()
    {
        await using var h = await AgentHarness.StartAsync("live-done", null, Live);
        var ticket = Ticket("done");
        var runId = h.Start(ticket, $"Create the file ff-live/{ticket}.md holding the single line 'Feature Flow live test {ticket}', and commit it " +
                                    $"with the message 'Add {ticket} note'. Commit it at once, then run `sleep 20` before you go on." + Finish);
        var hidden = AgentHarness.HiddenReference(runId, "implement");
        var branch = $"refs/heads/ff/ticket/{ticket}";
        try
        {
            string? hiddenWhileRunning = null, branchThen = null;
            var deadline = DateTime.UtcNow + RunTimeout;
            string status;
            while (true)
            {
                var view = await h.ShowAsync(runId);
                status = view["run"]!["status"]!.ToString();
                if (status is not ("starting" or "running") || view["waits"]!.AsArray().Count > 0 || DateTime.UtcNow > deadline)
                    break;
                if (hiddenWhileRunning is null && await h.RemoteRevisionAsync(hidden) is { } seen)
                    (hiddenWhileRunning, branchThen) = (seen, await h.RemoteRevisionAsync(branch));
                await Task.Delay(1000, TestContext.Current.CancellationToken);
            }

            var timeline = await h.TimelineAsync(runId);
            Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
            Assert.NotNull(hiddenWhileRunning);
            Assert.Equal(h.MainRevision, branchThen);
            var final = await h.RemoteRevisionAsync(branch);
            Assert.NotEqual(h.MainRevision, final);
            Assert.Equal(final, await h.RemoteRevisionAsync(hidden));
            Assert.Contains($"moved ff/ticket/{ticket} to {final![..10]}", timeline);
            Log($"done-when: run {runId}; hidden reference first seen at {hiddenWhileRunning![..10]} while the branch was at main; branch now {final[..10]}");
        }
        finally
        {
            await LogRunAsync(h, "done-when", runId);
            await h.StopRunAsync(runId);
            await h.DeleteRemoteReferencesAsync(branch, hidden);
        }
    }

    /// <summary>
    /// LOG 246 live: the real model reports notes without the heading their type requires (it is told to), the engine checks
    /// the stored file and continues the same session with the error, and the model corrects the file and reports again.
    /// </summary>
    [Fact]
    public async Task An_artifact_that_breaks_its_type_is_corrected_by_the_real_model_in_its_session()
    {
        await using var h = await AgentHarness.StartAsync("live-check", null, Live);
        var ticket = Ticket("check");
        var runId = h.Start(ticket, "Do not change the repository. Write notes.md in your output folder holding exactly the single line 'hello', with no " +
                                    "heading, and report the outcome implemented with outputs answer set to none and the artifact notes set to notes.md. " +
                                    "If the engine then says the notes are wrong, correct them as it says and report again.", "m4:templates/checked_step");
        var hidden = AgentHarness.HiddenReference(runId, "implement");
        var branch = $"refs/heads/ff/ticket/{ticket}";
        try
        {
            var status = await h.WaitForEndAsync(runId, RunTimeout);

            Assert.True(status == "completed", $"the run ended {status}:\n{await h.TimelineAsync(runId)}");
            var reports = await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId));
            Assert.Contains(reports, r => !r.Accepted && r.Errors.Any(e => e.Contains("heading 'Summary' is required", StringComparison.Ordinal)));
            Assert.True(reports[^1].Accepted);
        }
        finally
        {
            await LogRunAsync(h, "artifact-check", runId);
            await h.StopRunAsync(runId);
            await h.DeleteRemoteReferencesAsync(branch, hidden);
        }
    }

    /// <summary>SP2 live: the real agent asks, the worker restarts, the person answers, and the agent uses the answer.</summary>
    [Fact]
    public async Task A_question_is_answered_after_the_agent_worker_is_restarted()
    {
        await using var h = await AgentHarness.StartAsync("live-sp2", null, Live with { SeparateAgentWorker = true });
        var ticket = Ticket("sp2");
        var nonce = "teal-" + Random.Shared.Next(10000, 99999);
        var runId = h.Start(ticket, "Use the ask_human tool to ask the person which colour to use, with the options red and blue. Wait for the answer. " +
                                    "Then write notes.md in your output folder holding exactly the answer you received, change nothing in the repository, " +
                                    "and report the outcome implemented with outputs answer set to the answer, word for word, and the artifact notes set to notes.md.");
        try
        {
            var wait = await h.WaitForWaitAsync(runId, "question", RunTimeout);
            await h.StopAgentWorkerAsync();
            await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            var (exit, _, error) = h.Cli("answer", runId, wait, nonce);
            Assert.True(exit == 0, error);
            await h.StartAgentWorkerAsync();
            var status = await h.WaitForEndAsync(runId, RunTimeout);
            var timeline = await h.TimelineAsync(runId);

            Assert.True(status == "completed", $"the run ended {status}:\n{timeline}");
            Assert.Equal(nonce, Assert.Single(await h.RecordsAsync<QuestionRecord>(Queries.Questions, AgentScripts.Key(runId))).Answer);
            var report = (await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId))).Last(r => r.Accepted).Report!;
            Assert.Contains(nonce, report.Outputs["answer"]);
            Assert.Contains("\"connection_failed\"", await h.ArtifactTextAsync(AgentScripts.Reference(timeline, "sandbox client log: ")));
            await AgentScripts.AssertOneSandboxAsync(h, AgentScripts.Key(runId));
        }
        finally
        {
            await LogRunAsync(h, "SP2 restart", runId);
            await h.StopRunAsync(runId);
            await h.DeleteRemoteReferencesAsync($"refs/heads/ff/ticket/{ticket}", AgentHarness.HiddenReference(runId, "implement"));
        }
    }

    /// <summary>SP3 live, correct: the agent is led to report without its output; the engine rejects it and the agent corrects it.</summary>
    [Fact]
    public async Task A_rejected_report_is_corrected_by_the_real_agent()
    {
        await using var h = await AgentHarness.StartAsync("live-correct", null, Live);
        var ticket = Ticket("correct");
        var runId = h.Start(ticket, "Change nothing in the repository. Write notes.md in your output folder saying hello. Your first call of report must " +
                                    "give the outcome implemented and the artifact notes set to notes.md, and no outputs at all. If the engine rejects it, " +
                                    "do what its message says.");
        try
        {
            var status = await h.WaitForEndAsync(runId, RunTimeout);
            Assert.True(status == "completed", $"the run ended {status}:\n{await h.TimelineAsync(runId)}");
            var reports = await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId));
            Assert.False(reports[0].Accepted);
            Assert.True(reports[^1].Accepted);
            Assert.True(reports.Count <= 3);
        }
        finally
        {
            await LogRunAsync(h, "SP3 correct", runId);
            await h.StopRunAsync(runId);
            await h.DeleteRemoteReferencesAsync($"refs/heads/ff/ticket/{ticket}", AgentHarness.HiddenReference(runId, "implement"));
        }
    }

    /// <summary>SP3 live, fall back: with no engine tools the real agent reports through report.json.</summary>
    [Fact]
    public async Task Without_the_report_tool_the_real_agent_reports_through_a_file()
    {
        await using var h = await AgentHarness.StartAsync("live-fallback", null, Live with { EngineTools = false });
        var ticket = Ticket("fallback");
        var runId = h.Start(ticket, "Change nothing in the repository. Write notes.md in your output folder saying hello." + Finish);
        try
        {
            var status = await h.WaitForEndAsync(runId, RunTimeout);
            Assert.True(status == "completed", $"the run ended {status}:\n{await h.TimelineAsync(runId)}");
            // The report came through the file; a rejected one is corrected through the file in a further session.
            Assert.True((await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId)))[^1].Accepted);
        }
        finally
        {
            await LogRunAsync(h, "SP3 fallback", runId);
            await h.StopRunAsync(runId);
            await h.DeleteRemoteReferencesAsync($"refs/heads/ff/ticket/{ticket}", AgentHarness.HiddenReference(runId, "implement"));
        }
    }

    /// <summary>
    /// SP3 live, stop: every report is invalid. The attempt fails, by the third rejection or because the agent gives up
    /// first (SP3's N2 saw both), and no report is accepted. Never more than three reports reach the engine.
    /// </summary>
    [Fact]
    public async Task The_real_agent_stops_when_the_attempt_has_failed()
    {
        await using var h = await AgentHarness.StartAsync("live-stop", null, Live);
        var ticket = Ticket("stop");
        var runId = h.Start(ticket, "Change nothing in the repository. Write notes.md in your output folder saying hello. Report with the outcome " +
                                    "finished, which is the only outcome you may use, even if the engine rejects it. Follow any instruction to stop.");
        try
        {
            await h.WaitForWaitAsync(runId, "intervention", RunTimeout);
            var reports = await h.RecordsAsync<ReportRecord>(Queries.Reports, AgentScripts.Key(runId));
            Assert.All(reports, r => Assert.False(r.Accepted));
            Assert.InRange(reports.Count, 1, 3);
            Assert.Matches("attempt_failed implement: (the report was rejected 3 times|the agent ended without reporting)", await h.TimelineAsync(runId));
        }
        finally
        {
            await LogRunAsync(h, "SP3 stop", runId);
            await h.StopRunAsync(runId);
            await h.DeleteRemoteReferencesAsync($"refs/heads/ff/ticket/{ticket}", AgentHarness.HiddenReference(runId, "implement"));
        }
    }

    /// <summary>
    /// SP5 live: the real agent records its environment; no credential is in it, its home or its transcript. The
    /// listing of every process environment is the Integration test's: asked for it, the real agent reported blocked.
    /// </summary>
    [Fact]
    public async Task The_real_agent_s_sandbox_holds_no_model_credential()
    {
        await using var h = await AgentHarness.StartAsync("live-sp5", null, Live);
        var ticket = Ticket("sp5");
        var runId = h.Start(ticket, "Change nothing in the repository. Run this command with the Bash tool, exactly: " +
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
            Assert.Contains("ANTHROPIC_AUTH_TOKEN=ffs1.", notes);
            Assert.Contains("--- startup bytes\n0", notes);
            Assert.DoesNotContain("CLAUDE_CODE_OAUTH_TOKEN", notes);
            Assert.DoesNotContain("ANTHROPIC_API_KEY", notes);
            var credential = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN")!;
            Assert.False(notes.Contains(credential, StringComparison.Ordinal), "the credential is in the agent's environment");
            Assert.False(transcript.Contains(credential, StringComparison.Ordinal), "the credential is in the transcript");
        }
        finally
        {
            await LogRunAsync(h, "SP5 environment", runId);
            await h.StopRunAsync(runId);
            await h.DeleteRemoteReferencesAsync($"refs/heads/ff/ticket/{ticket}", AgentHarness.HiddenReference(runId, "implement"));
        }
    }
}
