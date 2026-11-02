namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M3's first "done when": a std <c>ticket_delivery</c> run completes on a Temporal dev server with fake
/// providers, driven through the CLI. Also the CLI's other commands: recovery at an intervention, abort, a dry
/// run, and a run refused at start by capability negotiation (check 21).
/// </summary>
[Trait("Category", "Integration")]
public class TicketRunTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task A_std_ticket_runs_to_completion_driven_through_the_cli()
    {
        // The implementer takes 3 seconds, so the repeated and conflicting answers reach a running run.
        await using var h = await Harness.StartAsync("ticket", script: """{ "ticket_delivery:implement": { delay: 3s } }""");
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-1042", "--repo", "acme/api");

        // The spec approval is a decision; the organisation allows no auto-resolution, so a person answers.
        var (waitExit, waitOutput, _) = h.Cli("wait", id, "--for", "wait", "--timeout", "60");
        Assert.Equal(0, waitExit);
        Assert.Contains("decision at approve_spec", waitOutput);
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
        await h.RecordHistoryAsync(id, "ticket_delivery");
    }

    [Fact]
    public async Task An_exhausted_step_escalates_and_granting_attempts_through_the_cli_completes_the_run()
    {
        await using var h = await Harness.StartAsync("recover", script: """
            { "ticket_delivery:implement": [{ error: work }, { error: work }, { error: work }, implemented] }
            """, autoApprove: true);
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-7");
        var waitId = await h.WaitForWaitAsync(id, Timeout);
        Assert.NotNull(waitId);
        var (_, show, _) = h.Cli("show", id);
        Assert.Contains($"wait {waitId} intervention at implement", show);
        Assert.Contains("attempts exhausted", show);

        var (refusedExit, _, refused) = h.Cli("recover", id, waitId!, "end", "completed");
        Assert.Equal(1, refusedExit);
        Assert.Contains("must be one of", refused);
        var (exit, _, error) = h.Cli("recover", id, waitId!, "grant_attempts", "1");
        Assert.True(exit == 0, error);
        var (endExit, end, _) = h.Cli("wait", id, "--timeout", "120");
        Assert.Equal((0, $"run {id} completed"), (endExit, end.Trim()));
        Assert.Equal(4, Harness.StartedSteps(await h.ShowAsync(id)).Count(s => s == "implement"));
    }

    [Fact]
    public async Task Abort_through_the_cli_ends_the_run_aborted()
    {
        await using var h = await Harness.StartAsync("abort");
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-8");
        Assert.NotNull(await h.WaitForWaitAsync(id, Timeout));
        var (exit, output, error) = h.Cli("abort", id, "--reason", "duplicate ticket");
        Assert.True(exit == 0, error);
        Assert.Equal("aborted", output.Trim());
        var (endExit, end, _) = h.Cli("wait", id, "--timeout", "60");
        Assert.Equal((1, $"run {id} aborted"), (endExit, end.Trim()));
        var view = await h.ShowAsync(id);
        Assert.Empty(view["waits"]!.AsArray());
    }

    [Fact]
    public async Task A_dry_run_records_each_call_instead_of_acting()
    {
        await using var h = await Harness.StartAsync("dry", autoApprove: true);
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-9", "--dry-run");
        var (exit, end, _) = h.Cli("wait", id, "--timeout", "120");
        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), (await h.ShowAsync(id)).ToJsonString());
        var recorded = await File.ReadAllLinesAsync(Path.Combine(h.Folder, "recorded", id + ".jsonl"), TestContext.Current.CancellationToken);
        Assert.Contains(recorded, l => l.Contains("\"step\":\"implement\"", StringComparison.Ordinal) && l.Contains("\"kind\":\"agent\"", StringComparison.Ordinal));
        Assert.Contains(recorded, l => l.Contains("\"what\":\"scm.merge\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_needing_a_capability_no_provider_offers_is_refused_at_start()
    {
        await using var h = await Harness.StartAsync("refused", omitTypes: ["code_host"]);
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-10");
        var (exit, end, _) = h.Cli("wait", id, "--timeout", "60");
        Assert.Equal((1, $"run {id} failed"), (exit, end.Trim()));
        var view = await h.ShowAsync(id);
        Assert.Contains("check 21", view["run"]!["detail"]!.ToString());
        Assert.Contains(view["timeline"]!.AsArray(), e => e!["detail"]!.ToString().Contains("no code_host provider is configured", StringComparison.Ordinal));
    }
}
