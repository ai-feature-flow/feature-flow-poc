using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Engine.Tests;

/// <summary><c>ci.checks</c> and <c>wait.condition</c> (docs/04 section 8): a wait is a retried read, never a held worker.</summary>
public sealed class WaitingActionTests
{
    static string Outcome(ActionResult r) => r.Report?.Outcome ?? $"{r.Kind}: {r.Reason}";

    static async Task<ActionWorld> WithPullRequestAsync()
    {
        var w = new ActionWorld();
        w.CommitWork("a.txt", "work");
        await w.RunAsync("scm.pull_request", "{ title: T-1 }", "open_pr");
        return w;
    }

    [Fact]
    public async Task With_no_CI_and_no_checks_the_checks_pass_at_once()
    {
        await using var w = await WithPullRequestAsync();

        var result = await w.RunAsync("ci.checks", null, "checks", waits: 1);

        Assert.Equal("passed", Outcome(result));
    }

    [Fact]
    public async Task An_early_third_party_check_does_not_pass_while_a_check_the_base_requires_has_not_appeared()
    {
        await using var w = await WithPullRequestAsync();
        var head = w.Work!;
        w.Forge.Protect(w.Repo, new BranchProtection("main", true, true, 0, ["build"], false));
        w.Forge.ReportCheck(w.Repo, head, new CheckRun("licence", CheckStates.Completed, Conclusions.Success, null));

        var early = await w.RunAsync("ci.checks", null, "checks", waits: 3);
        Assert.Equal(ActionResult.Wait, early.Kind);
        Assert.Contains("check build, which main requires, has not appeared", early.Reason, StringComparison.Ordinal);
        // wait.condition on the pull request's checks waits for it too.
        Assert.Equal(ActionResult.Wait, (await w.RunAsync("wait.condition", "{ checks: passed }", "condition", waits: 2)).Kind);

        // The CI run reports late, and passes: now the step passes.
        w.Forge.ReportCheck(w.Repo, head, new CheckRun("build", CheckStates.Completed, Conclusions.Success, "build.yml"));
        Assert.Equal("passed", Outcome(await w.RunAsync("ci.checks", null, "checks", waits: 1)));
    }

    [Fact]
    public async Task A_failing_or_still_running_check_holds_or_fails_the_step_and_skipped_ones_are_counted()
    {
        await using var w = await WithPullRequestAsync();
        var head = w.Work!;
        w.Forge.ReportCheck(w.Repo, head, new CheckRun("unit", CheckStates.InProgress, null, "ci.yml"));
        w.Forge.ReportCheck(w.Repo, head, new CheckRun("docs", CheckStates.Completed, Conclusions.Skipped, "ci.yml"));
        Assert.Equal(ActionResult.Wait, (await w.RunAsync("ci.checks", null, "checks", waits: 2)).Kind);

        w.Forge.ReportCheck(w.Repo, head, new CheckRun("unit", CheckStates.Completed, Conclusions.Failure, "ci.yml"));
        var failed = await w.RunAsync("ci.checks", null, "checks", waits: 1);

        Assert.Equal("failed", Outcome(failed));
        Assert.Equal(1L, YamlWriter.ReadPlain(failed.Report!.Outputs["skipped"]));
    }

    [Theory]
    [InlineData("cancelled", "failed")]
    [InlineData("timed_out", "failed")]
    [InlineData("action_required", "failed")]
    [InlineData("startup_failure", "failed")]
    [InlineData("stale", "failed")]
    [InlineData("neutral", "passed")]
    [InlineData("skipped", "passed")]
    [InlineData("success", "passed")]
    public async Task Only_success_neutral_and_skipped_pass(string conclusion, string outcome)
    {
        await using var w = await WithPullRequestAsync();
        w.Forge.ReportCheck(w.Repo, w.Work!, new CheckRun("ci", CheckStates.Completed, conclusion, null));

        Assert.Equal(outcome, Outcome(await w.RunAsync("ci.checks", null, "checks", waits: 1)));
        Assert.Equal(outcome == "passed" ? "met" : "unmet", Outcome(await w.RunAsync("wait.condition", "{ checks: passed }", "condition", waits: 1)));
    }

    [Fact]
    public async Task A_conclusion_the_engine_does_not_know_raises_an_intervention()
    {
        await using var w = await WithPullRequestAsync();
        w.Forge.ReportCheck(w.Repo, w.Work!, new CheckRun("ci", CheckStates.Completed, "exploded", null));

        var result = await w.RunAsync("ci.checks", null, "checks", waits: 1);

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("'exploded'", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_expected_check_name_is_waited_for_and_with_CI_configured_the_whole_grace_is_waited()
    {
        await using var w = await WithPullRequestAsync();
        w.Forge.ConfigureCi(w.Repo);

        Assert.Equal(ActionResult.Wait, (await w.RunAsync("ci.checks", "{ expected: [sonar], grace: 0s }", "checks", waits: 2)).Kind);
        var waiting = await w.RunAsync("ci.checks", "{ grace: 1h }", "checks2", waits: 2);
        Assert.Equal(ActionResult.Wait, waiting.Kind);
        Assert.Contains("CI is configured, so the step waits", waiting.Reason, StringComparison.Ordinal);
        // After the grace with no check, it passes and says CI produced none.
        var none = await w.RunAsync("ci.checks", "{ grace: 0s }", "checks3", waits: 1);
        Assert.Equal("passed", Outcome(none));
        Assert.Contains(none.Events!, e => e.Contains("has CI configured, but no check appeared", StringComparison.Ordinal));
        // An early check that is not CI does not end the grace (review F1): a later CI check may still appear.
        w.Forge.ReportCheck(w.Repo, w.Work!, new CheckRun("licence", CheckStates.Completed, Conclusions.Success, null));
        Assert.Equal(ActionResult.Wait, (await w.RunAsync("ci.checks", "{ grace: 1h }", "checks4", waits: 2)).Kind);
        Assert.Equal("passed", Outcome(await w.RunAsync("ci.checks", "{ grace: 0s }", "checks5", waits: 1)));
    }

    /// <summary>Review F2: wait.condition on checks waits out the same default grace when CI is configured.</summary>
    [Fact]
    public async Task Wait_condition_on_checks_waits_for_CI_to_report_when_it_is_configured()
    {
        await using var w = await WithPullRequestAsync();
        w.Forge.ConfigureCi(w.Repo);

        Assert.Equal(ActionResult.Wait, (await w.RunAsync("wait.condition", "{ checks: passed }", "condition", waits: 2)).Kind);
    }

    [Fact]
    public async Task A_head_that_changes_while_checks_are_awaited_raises_an_intervention()
    {
        await using var w = await WithPullRequestAsync();
        w.Forge.ReportCheck(w.Repo, w.Work!, new CheckRun("unit", CheckStates.InProgress, null, null));

        var result = await w.RunAsync("ci.checks", null, "checks", waits: 1);
        Assert.Equal(ActionResult.Wait, result.Kind);
        w.CommitWork("b.txt", "pushed while waiting");

        // The same execution reads again (Temporal's retry): its recorded head no longer matches.
        var call = w.Call("ci.checks", "checks", 1, null);
        var again = await w.Actions.RunAsync(call, w.Scope, () => { }, TestContext.Current.CancellationToken);
        Assert.Equal(StepOutcome.CannotAnswer, again.Kind);
        Assert.Contains("changed", again.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Checks_that_do_not_finish_within_the_timeout_raise_an_intervention()
    {
        await using var w = await WithPullRequestAsync();
        w.Forge.ReportCheck(w.Repo, w.Work!, new CheckRun("unit", CheckStates.InProgress, null, null));

        var result = await w.RunAsync("ci.checks", "{ timeout: 0s }", "checks", waits: 3);

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
    }

    [Fact]
    public async Task Wait_condition_meets_on_approval_and_is_unmet_on_a_change_request()
    {
        await using var w = await WithPullRequestAsync();
        var pr = ActionRunAccess.Pr(w.PullRequests[0]);
        Assert.Equal(ActionResult.Wait, (await w.RunAsync("wait.condition", "{ pull_request: approved }", "approval", waits: 2)).Kind);

        w.Forge.Review(pr, "bob", approve: true);
        Assert.Equal("met", Outcome(await w.RunAsync("wait.condition", "{ pull_request: approved }", "approval")));

        w.Forge.Review(pr, "carol", approve: false);
        Assert.Equal("unmet", Outcome(await w.RunAsync("wait.condition", "{ pull_request: approved }", "approval")));
    }

    [Fact]
    public async Task Wait_condition_on_a_merge_and_on_checks()
    {
        await using var w = await WithPullRequestAsync();
        var pr = ActionRunAccess.Pr(w.PullRequests[0]);
        w.Forge.ReportCheck(w.Repo, w.Work!, new CheckRun("unit", CheckStates.Completed, Conclusions.Failure, null));

        Assert.Equal("unmet", Outcome(await w.RunAsync("wait.condition", "{ checks: passed }", "checks")));
        Assert.Equal(ActionResult.Wait, (await w.RunAsync("wait.condition", "{ pull_request: merged }", "merged", waits: 2)).Kind);
        w.Forge.MergePullRequest(pr);
        Assert.Equal("met", Outcome(await w.RunAsync("wait.condition", "{ pull_request: merged }", "merged")));
    }

    [Fact]
    public async Task Wait_condition_times_out_into_an_intervention_and_takes_one_condition_only()
    {
        await using var w = await WithPullRequestAsync();

        Assert.Equal(StepOutcome.CannotAnswer, (await w.RunAsync("wait.condition", "{ pull_request: approved, timeout: 0s }", "a", waits: 3)).Kind);
        var two = await w.RunAsync("wait.condition", "{ pull_request: approved, checks: passed }", "b");
        Assert.Equal(StepOutcome.CannotAnswer, two.Kind);
        Assert.Contains("check 31", two.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ci.checks", "{ poll_every: 0s }")]
    [InlineData("ci.checks", "{ poll_every: 4900ms }")]
    [InlineData("wait.condition", "{ pull_request: approved, poll_every: 1ms }")]
    public async Task Check_31_refuses_a_poll_interval_under_five_seconds(string action, string inputs)
    {
        await using var w = await WithPullRequestAsync();

        var result = await w.RunAsync(action, inputs, "wait");

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("'poll_every' must be at least 5s", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_poll_interval_of_five_seconds_is_the_next_reads_delay()
    {
        await using var w = await WithPullRequestAsync();
        w.Forge.ReportCheck(w.Repo, w.Work!, new CheckRun("unit", CheckStates.InProgress, null, null));

        var result = await w.RunAsync("ci.checks", "{ poll_every: 5s }", "checks", waits: 1);

        Assert.Equal((ActionResult.Wait, TimeSpan.FromSeconds(5)), (result.Kind, result.Delay));
    }

    [Fact]
    public async Task Check_31_refuses_an_unknown_input_and_a_wrong_type_before_the_action_runs()
    {
        await using var w = new ActionWorld();

        var unknown = await w.RunAsync("scm.merge", "{ force: true }");
        var wrong = await w.RunAsync("test.run", "{ tier_tags: unit }");
        var badEnum = await w.RunAsync("analysis.run", "{ fail_on: sometimes }");

        Assert.All([unknown, wrong, badEnum], r => Assert.Equal(StepOutcome.CannotAnswer, r.Kind));
        Assert.Contains("has no input 'force'", unknown.Reason, StringComparison.Ordinal);
        Assert.Contains("must be a list", wrong.Reason, StringComparison.Ordinal);
        Assert.Contains("must be one of new_issues", badEnum.Reason, StringComparison.Ordinal);
        Assert.Null(w.Work);                          // nothing ran: no branch was created
    }
}
