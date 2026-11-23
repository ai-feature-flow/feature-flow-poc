using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M3's first "done when": a std <c>ticket_delivery</c> run completes on a Temporal dev server on the memory providers,
/// driven through the CLI. Also the CLI's other commands: recovery at an intervention, abort, a dry run, and a run
/// refused at start by capability negotiation (check 21).
/// </summary>
[Trait("Category", "Integration")]
public class TicketRunTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>The organisation lets the engine merge into the fixture repository's protected default branch (F1).</summary>
    const string EngineMerges = "  merge: { default_branch: { engine_may_merge: [acme/api] } }";

    static readonly string[] Ticket = ["--repo", Harness.RepoName, "--param", "engine_can_merge=true"];

    [Fact]
    public async Task A_std_ticket_runs_to_completion_driven_through_the_cli()
    {
        // The implementer takes 3 seconds, so the repeated and conflicting answers reach a running run.
        await using var h = await Harness.StartAsync("ticket", script: $$"""{ implement: { delay: 3s, artifacts: { {{Harness.ImplementationClaims}} } } }""", policy: EngineMerges);
        var id = h.Start("std:templates/ticket_delivery", ["--param", "ticket_id=FF-1042", .. Ticket]);

        // The spec approval is a decision; the organisation allows no auto-resolution, so a person answers.
        var (waitExit, waitOutput, _) = h.Cli("wait", id, "--for", "wait", "--timeout", "60");
        Assert.Equal(0, waitExit);
        Assert.Contains("gate at approve_spec", waitOutput);
        var waitId = waitOutput.Split(' ')[1];
        var (showExit, show, _) = h.Cli("show", id);
        Assert.Equal(0, showExit);
        Assert.Contains("(ticket.spec_approval)", show);
        Assert.Contains("answer approved | rejected", show);

        var (answerExit, answer, answerError) = h.Cli("answer", id, waitId, "approved");
        Assert.True(answerExit == 0, answerError);
        Assert.Equal("recorded", answer.Trim());
        var (again, repeat, repeatError) = h.Cli("answer", id, waitId, "approved");
        Assert.True((again, repeat.Trim()) == (0, "duplicate"), repeatError);
        var (conflictExit, _, conflict) = h.Cli("answer", id, waitId, "rejected");
        Assert.Equal(1, conflictExit);
        Assert.Contains("already answered", conflict);

        var (endExit, end, endError) = h.Cli("wait", id, "--timeout", "120");
        Assert.True(endExit == 0, end + endError + (await h.ShowAsync(id)).ToJsonString());
        Assert.Contains("completed", end);

        var path = Harness.StartedSteps(await h.ShowAsync(id));
        string[] expected = ["baseline.tests", "plan.plan", "approve_spec", "implement", "verify.tests", "verify.review.review_0.assess", "deliver.open_pr", "deliver.catch_up", "deliver.merge"];
        Assert.Equal(expected, path.Where(expected.Contains).Distinct().ToArray());
        var taskFailures = await h.WorkflowTaskFailuresAsync(id);
        Assert.True(taskFailures.Count == 0, "C5: workflow task failures: " + string.Join("; ", taskFailures));

        // A1, A2: the run's record carries its level, its issue with the issue's title, and when it started; each step's
        // counters are recorded, with no allowance for the decision.
        var run = Assert.Single(await RecordsAsync<RunRecord>(h, Queries.Run, id));
        Assert.Equal(("ticket", "FF-1042", "Ticket FF-1042", null), (run.Level, run.TrackerIssue, run.TrackerTitle, run.ParentStepId));
        Assert.NotNull(run.StartedAt);
        var steps = await RecordsAsync<StepStateRecord>(h, Queries.StepStates, id);
        Assert.Contains(steps, s => s is { StepId: "implement", Executions: 1, AttemptsUsed: 1, AttemptsGranted: 0, AttemptsAllowed: > 0 });
        Assert.Contains(steps, s => s is { StepId: "approve_spec", Executions: 1, AttemptsAllowed: null });
        await h.RecordHistoryAsync(id, "ticket_delivery");
    }

    static async Task<IReadOnlyList<T>> RecordsAsync<T>(Harness h, string query, string runId, bool openOnly = false) =>
        (await h.Host.Providers.Required<IRecordStoreProvider>("record_store").QueryAsync<T>(query,
            openOnly ? new Dictionary<string, string> { ["run_id"] = runId, ["open"] = "true" } : new Dictionary<string, string> { ["run_id"] = runId },
            TestContext.Current.CancellationToken)).Unwrap();

    [Fact]
    public async Task An_exhausted_step_escalates_and_granting_attempts_through_the_cli_completes_the_run()
    {
        await using var h = await Harness.StartAsync("recover", script: $$"""
            { implement: [{ error: work }, { error: work }, { error: work }, { outcome: implemented, artifacts: { {{Harness.ImplementationClaims}} } }] }
            """, autoApprove: true, policy: EngineMerges);
        var id = h.Start("std:templates/ticket_delivery", ["--param", "ticket_id=FF-7", .. Ticket]);
        var waitId = await h.WaitForWaitAsync(id, Timeout);
        Assert.NotNull(waitId);
        var (_, show, _) = h.Cli("show", id);
        Assert.Contains($"wait {waitId} intervention at implement", show);
        Assert.Contains("attempts exhausted", show);

        // A11: the intervention's cause has the step's attempts and each failed attempt that sent it back.
        var cause = Assert.Single(await RecordsAsync<WaitRecord>(h, Queries.Waits, id, openOnly: true)).Cause!;
        Assert.Equal(("implement", Actions.AttemptsExhausted), (cause.StepId, cause.Outcome));
        Assert.Equal(cause.AttemptsAllowed, cause.AttemptsUsed);
        Assert.Equal([1, 2, 3], cause.SentBack.Select(s => s.Execution));
        Assert.All(cause.SentBack, s => Assert.True(s.ByStep is null && s.Text.StartsWith("attempt ", StringComparison.Ordinal), s.Text));

        var (refusedExit, _, refused) = h.Cli("recover", id, waitId!, "end", "completed");
        Assert.Equal(1, refusedExit);
        Assert.Contains("must be one of", refused);
        var (exit, _, error) = h.Cli("recover", id, waitId!, "grant_attempts", "1");
        Assert.True(exit == 0, error);
        var (endExit, end, _) = h.Cli("wait", id, "--timeout", "120");
        Assert.Equal((0, $"run {id} completed"), (endExit, end.Trim()));
        Assert.Equal(4, Harness.StartedSteps(await h.ShowAsync(id)).Count(s => s == "implement"));
        Assert.Contains(await RecordsAsync<StepStateRecord>(h, Queries.StepStates, id),
            s => s.StepId == "implement" && s.AttemptsGranted == 1 && s.AttemptsAllowed == cause.AttemptsAllowed + 1);
    }

    [Fact]
    public async Task Abort_through_the_cli_ends_the_run_aborted()
    {
        await using var h = await Harness.StartAsync("abort", policy: EngineMerges);
        var id = h.Start("std:templates/ticket_delivery", ["--param", "ticket_id=FF-8", .. Ticket]);
        Assert.NotNull(await h.WaitForWaitAsync(id, Timeout));
        var (exit, output, error) = h.Cli("abort", id, "--reason", "duplicate ticket");
        Assert.True(exit == 0, error);
        Assert.Equal("aborted", output.Trim());
        var (endExit, end, _) = h.Cli("wait", id, "--timeout", "60");
        Assert.Equal((1, $"run {id} aborted"), (endExit, end.Trim()));
        var view = await h.ShowAsync(id);
        Assert.Empty(view["waits"]!.AsArray());
    }

    /// <summary>
    /// A dry run runs on the same paths as any run, on the dry-run instances: the dry world's memory providers and the
    /// recording runtime (docs/07 section 5). The work lands in the dry world; the run's own world is untouched.
    /// </summary>
    [Fact]
    public async Task A_dry_run_acts_on_its_dry_run_providers_and_records_each_agent_step()
    {
        await using var h = await Harness.StartAsync("dry", autoApprove: true, policy: EngineMerges);
        var before = h.Forge.Resolve(h.Repo, "main");
        var id = h.Start("std:templates/ticket_delivery", ["--param", "ticket_id=FF-9", "--dry-run", .. Ticket]);
        var (exit, end, _) = h.Cli("wait", id, "--timeout", "120");
        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), (await h.ShowAsync(id)).ToJsonString());
        var recorded = await File.ReadAllLinesAsync(Path.Combine(h.Folder, "recorded", id + ".jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains(recorded, l => l.Contains("\"step\":\"implement\"", StringComparison.Ordinal) && l.Contains("\"kind\":\"agent\"", StringComparison.Ordinal));

        Assert.StartsWith("implement execution 1", h.DryForge.ReadFile(h.Repo, h.DryForge.Resolve(h.Repo, "main")!, $"work/{id}/implement.txt"), StringComparison.Ordinal);
        Assert.Equal(before, h.Forge.Resolve(h.Repo, "main"));
    }

    [Fact]
    public async Task A_run_needing_a_capability_no_provider_offers_is_refused_at_start()
    {
        await using var h = await Harness.StartAsync("refused", without: ["code_host"]);
        var id = h.Start("std:templates/ticket_delivery", ["--param", "ticket_id=FF-10", .. Ticket]);
        var (exit, end, _) = h.Cli("wait", id, "--timeout", "60");
        Assert.Equal((1, $"run {id} failed"), (exit, end.Trim()));
        var view = await h.ShowAsync(id);
        Assert.Contains("check 21", view["run"]!["detail"]!.ToString());
        Assert.Contains(view["timeline"]!.AsArray(), e => e!["detail"]!.ToString().Contains("no code_host provider is configured", StringComparison.Ordinal));
    }

    /// <summary>
    /// LOG 501: a std ticket run started with <c>ticket_id</c> and no <c>tracker_issue</c> takes its tracker issue from
    /// <c>ticket_id</c>, so the ticket is read from the tracker and staged for each agent step, and declarative updates move it.
    /// </summary>
    [Fact]
    public async Task A_std_ticket_run_takes_its_tracker_issue_from_ticket_id()
    {
        await using var h = await Harness.StartAsync("ticket-input", autoApprove: true, policy: EngineMerges,
            script: $$"""{ implement: { delay: 5s, artifacts: { {{Harness.ImplementationClaims}} } } }""");
        h.Tracker.Add("FF-198", new IssueSpec("Add GET /widgets/{id}", "Return one widget by id."));
        var id = h.Start("std:templates/ticket_delivery", ["--param", "ticket_id=FF-198", .. Ticket]);

        // The implementer's sandbox, while it works, holds the ticket the engine read from the tracker.
        string? ticket = null;
        for (var deadline = DateTime.UtcNow + Timeout; ticket is null && DateTime.UtcNow < deadline; await Task.Delay(100, TestContext.Current.CancellationToken))
            ticket = h.Sandbox.Specs.Select(s => h.Sandbox.ReadFile(new SandboxHandle(s.Key, "memory"), "/ff/inputs/ticket.md")).FirstOrDefault(t => t is not null);
        Assert.StartsWith("# Add GET /widgets/{id}\n\nReturn one widget by id.", ticket);
        var (exit, end, _) = h.Cli("wait", id, "--timeout", "120");
        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), (await h.ShowAsync(id)).ToJsonString());
        Assert.Equal("done", (await h.Tracker.GetIssueAsync(new IssueRef("FF-198"), TestContext.Current.CancellationToken)).Unwrap().Status);
    }

    /// <summary>
    /// LOG 501: a std ticket run whose ticket the tracker does not have stops for a person at its planner, before the planner
    /// runs, rather than planning without a ticket. (An installation with no tracker refuses a std ticket run at start, check 21.)
    /// </summary>
    [Fact]
    public async Task A_std_ticket_run_without_its_ticket_stops_before_planning()
    {
        // The run's tracker is a world of its own, where the test files no ticket.
        var world = "no-ticket-" + Guid.NewGuid().ToString("N");
        await using var h = await Harness.StartAsync("no-ticket", autoApprove: true, policy: EngineMerges, without: ["tracker"],
            providers: $"  tracker: {{ instances: {{ memory: {{ world: {world} }} }} }}");
        var id = h.Start("std:templates/ticket_delivery", ["--param", "ticket_id=FF-404", .. Ticket]);

        var waitId = await h.WaitForWaitAsync(id, Timeout);
        Assert.True(waitId is not null, (await h.ShowAsync(id)).ToJsonString());
        var (_, show, _) = h.Cli("show", id);
        Assert.Contains($"wait {waitId} intervention at plan.plan", show);
        Assert.Contains("the run's tracker issue FF-404 does not exist", show);
        Assert.DoesNotContain(Harness.StartedSteps(await h.ShowAsync(id)), s => s == "approve_spec");
    }
}
