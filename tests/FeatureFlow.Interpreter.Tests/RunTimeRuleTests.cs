using FeatureFlow.Model;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// The run-time rules of docs/09 section 3 that the core owns: attempt ceilings on every execution (F4),
/// re-entry runs a step again, no success without an end route (F9), and the core's part of output contracts.
/// The rules it does not own are listed in docs/build/m2-results.md.
/// </summary>
public class RunTimeRuleTests
{
    /// <summary>A test step and a validation step that sends it back while invalid.</summary>
    const string Loop = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: loop, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: work
            action: test.run
            routes: { failed: check }
          - id: check
            action: artifact.validate
            routes: { invalid: work, valid: { end: completed } }
        """;

    /// <summary>An agent step that loops on its own number output.</summary>
    const string Countdown = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: countdown, level: ticket }
        artifact_types: [std:artifact_types/evidence_claims]
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
          limits: { attempts: 5 }
        steps:
          - id: fix
            agent: { role: std:roles/fixer, task: std:tasks/repair_failures }
            outputs: { left: { type: number } }
            produces:
              fixed: [{ name: fix_claims, type: std:artifact_types/evidence_claims }]
            outcomes: { fixed: success, blocked: blocked }
            routes:
              fixed:
                - when: "steps.fix.outputs.left - 1 >= 1"
                  to: fix
                - to: { end: completed }
        """;

    /// <summary>A merge whose failures are not routed: no defaults, no routes (check 7 refuses it at load).</summary>
    const string Unrouted = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: unrouted, level: ticket }
        steps:
          - id: merge
            action: scm.merge
            routes: { merged: { end: completed }, base_moved: merge }
        """;

    static readonly Probe Lib = new("rules", ("loop", Loop), ("countdown", Countdown), ("unrouted", Unrouted));

    static StepCompleted Fixed(long left) => new("fix", "fixed", new Dictionary<string, object?> { ["left"] = left },
        new Dictionary<string, string> { ["fix_claims"] = "artifact:fix" });

    [Fact]
    public void Every_entry_counts_against_the_allowance_and_the_entry_after_the_last_is_refused()
    {
        var core = Lib.Start("loop");
        var driver = new Driver(core)
        {
            Respond = (id, _) => new StepCompleted(id, id == "work" ? "failed" : "invalid"),
        };

        driver.Run();

        Assert.Equal(RunStatus.Running, core.State.Status);
        Assert.Equal(3, Driver.Path(core).Count(p => p == "work"));
        Assert.Contains(core.State.Trace, t => t is { Kind: "exhausted", StepId: "work" });
        var intervention = Assert.Single(driver.Commands.OfType<RaiseIntervention>());
        Assert.Equal(("work", Actions.AttemptsExhausted), (intervention.StepId, intervention.Outcome));
    }

    [Fact]
    public void The_documents_default_limit_sets_the_allowance()
    {
        var core = Lib.Start("countdown");
        new Driver(core) { Respond = (_, _) => Fixed(9) }.Run();

        Assert.Equal(5, Driver.Path(core).Count);
        Assert.Contains(core.State.Trace, t => t is { Kind: "exhausted", StepId: "fix", Detail: "5 of 5 attempts used" });
    }

    [Fact]
    public void The_run_ceiling_stops_a_loop_across_steps()
    {
        var core = Lib.Start("loop", new RunOptions { RunCeiling = 4, DefaultAttempts = 10 });
        new Driver(core) { Respond = (id, _) => new StepCompleted(id, id == "work" ? "failed" : "invalid") }.Run();

        Assert.Equal(["work", "check", "work", "check"], Driver.Path(core));
        Assert.Contains(core.State.Trace, t => t.Kind == "exhausted" && t.StepId == "work" && t.Detail!.Contains("run ceiling"));
    }

    [Fact]
    public void Reentry_clears_the_current_execution_and_keeps_the_previous_readable()
    {
        var core = Lib.Start("loop");
        var driver = new Driver(core);
        driver.Advance();
        Assert.True(core.Apply(new StepCompleted("work", "failed", new Dictionary<string, object?> { ["failed_tests"] = 2L })).Accepted);
        driver.Advance();
        Assert.True(core.Apply(new StepCompleted("check", "invalid")).Accepted);
        driver.Advance();

        var work = core.State.Steps["work"];
        Assert.Contains("work", core.State.InFlight);
        Assert.Equal(2, work.Executions);
        Assert.Null(work.Current!.Outcome);
        Assert.Empty(work.Current.Outputs);
        Assert.Equal("failed", work.Previous!.Outcome);
        Assert.Equal(2L, work.Previous.Outputs["failed_tests"]);
    }

    [Fact]
    public void A_loop_guard_reads_the_new_execution_and_a_number_given_as_a_double_evaluates_as_checked()
    {
        var core = Lib.Start("countdown");
        var lefts = new Queue<object?>([3.0, 2L, 1]);
        new Driver(core)
        {
            Respond = (_, _) => new StepCompleted("fix", "fixed", new Dictionary<string, object?> { ["left"] = lefts.Dequeue() },
                new Dictionary<string, string> { ["fix_claims"] = "artifact:fix" }),
        }.Run();

        Assert.Equal(RunStatus.Completed, core.State.Status);
        Assert.Equal(3, Driver.Path(core).Count);
        Assert.Equal(2L, Assert.IsType<long>(core.State.Steps["fix"].Previous!.Outputs["left"]));
        Assert.Equal(1L, Assert.IsType<long>(core.State.Steps["fix"].Current!.Outputs["left"]));
    }

    [Fact]
    public void An_unrouted_failure_ends_the_run_incomplete_not_completed()
    {
        var (plan, diagnostics) = Lib.LoadUnchecked("unrouted");
        Assert.Contains(diagnostics, d => d.Check == Checks.OutcomeRouted);
        var core = RunCore.FromPlan(plan, Probe.Run());

        var driver = new Driver(core) { Respond = (id, _) => new StepCompleted(id, "checks_failed") };
        driver.Run();

        Assert.Equal(RunStatus.Incomplete, core.State.Status);
        Assert.Contains(core.State.Trace, t => t is { Kind: "unrouted", StepId: "merge", Detail: "checks_failed" });
        Assert.Equal(RunStatus.Incomplete, Assert.Single(driver.Commands.OfType<EndRun>()).Status);
    }

    [Fact]
    public void Only_an_end_route_completes_a_run()
    {
        var (plan, _) = Lib.LoadUnchecked("unrouted");
        var core = RunCore.FromPlan(plan, Probe.Run());
        new Driver(core) { Respond = (id, _) => new StepCompleted(id, "merged") }.Run();

        Assert.Equal(RunStatus.Completed, core.State.Status);
    }

    [Fact]
    public void An_outcome_the_step_does_not_have_is_refused()
    {
        var core = Lib.Start("countdown");
        new Driver(core).Advance();

        var result = core.Apply(new StepCompleted("fix", "done"));

        Assert.False(result.Accepted);
        Assert.Contains("'done' is not an outcome", result.Errors[0]);
        Assert.Contains("fix", core.State.InFlight);
    }

    [Fact]
    public void An_undeclared_output_and_a_fractional_number_are_refused()
    {
        var core = Lib.Start("countdown");
        new Driver(core).Advance();
        var claims = new Dictionary<string, string> { ["fix_claims"] = "artifact:fix" };

        var undeclared = core.Apply(new StepCompleted("fix", "fixed", new Dictionary<string, object?> { ["left"] = 1L, ["extra"] = 1L }, claims));
        var fractional = core.Apply(new StepCompleted("fix", "fixed", new Dictionary<string, object?> { ["left"] = 1.5 }, claims));

        Assert.False(undeclared.Accepted);
        Assert.Contains("output 'extra' is not declared", undeclared.Errors.Single());
        Assert.False(fractional.Accepted);
        Assert.Contains("1.5 is not a whole number", fractional.Errors.Single());
    }

    [Fact]
    public void A_promised_artifact_must_be_produced_and_no_other()
    {
        var core = Lib.Start("countdown");
        new Driver(core).Advance();
        var left = new Dictionary<string, object?> { ["left"] = 1L };

        var missing = core.Apply(new StepCompleted("fix", "fixed", left));
        var extra = core.Apply(new StepCompleted("fix", "blocked", left, new Dictionary<string, string> { ["fix_claims"] = "artifact:fix" }));

        Assert.Contains("promises artifact 'fix_claims'", missing.Errors.Single());
        Assert.Contains("artifact 'fix_claims' is not declared for outcome 'blocked'", extra.Errors.Single());
    }

    [Fact]
    public void Infrastructure_failures_are_retried_free_and_work_failures_count()
    {
        var core = Lib.Start("loop");
        core.Advance();

        core.Apply(new StepFailed("work", ErrorClass.Infrastructure, "sandbox lost"));
        core.Apply(new StepFailed("work", ErrorClass.Infrastructure, "sandbox lost"));
        Assert.Equal([new StartStep("work", 1), new StartStep("work", 1)], core.Advance().OfType<StartStep>());
        Assert.Equal((1, 1, 1L), (core.State.Steps["work"].Executions, core.State.Steps["work"].AttemptsUsed, core.State.RunAttempts));

        core.Apply(new StepFailed("work", ErrorClass.Work, "output still invalid after correction turns"));
        Assert.Equal([new StartStep("work", 2)], core.Advance().OfType<StartStep>());
        Assert.Equal((2, 2, 2L), (core.State.Steps["work"].Executions, core.State.Steps["work"].AttemptsUsed, core.State.RunAttempts));
    }

    [Fact]
    public void A_guard_that_cannot_be_evaluated_raises_an_intervention()
    {
        var core = Lib.Start("countdown");
        new Driver(core).Advance();

        // The step reports no 'left', so the guard has nothing to read.
        core.Apply(new StepCompleted("fix", "fixed", null, new Dictionary<string, string> { ["fix_claims"] = "artifact:fix" }));
        var commands = core.Advance();

        var intervention = Assert.Single(commands.OfType<RaiseIntervention>());
        Assert.Contains("has not reported output 'left'", intervention.Reason);
        Assert.Equal(RunStatus.Running, core.State.Status);
    }
}
