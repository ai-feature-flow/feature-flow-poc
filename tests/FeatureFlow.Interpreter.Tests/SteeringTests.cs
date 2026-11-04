using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// A person's run-time overrides in the core (docs/05 section 5): breakpoints (docs/04 section 4.5), skips, attempt grants,
/// role changes and amends, and re-running a branch of a decided join (m6-results follow-up 3).
/// </summary>
public class SteeringTests
{
    const string Line = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: line, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
          limits: { attempts: 2 }
        steps:
          - id: design
            agent: { role: std:roles/planner, task: std:tasks/repair_failures }
            outcomes: { done: success, stuck: blocked }
          - id: review
            protected: true
            agent: { role: std:roles/reviewer, task: std:tasks/repair_failures }
            outcomes: { accepted: success, rejected: failure }
          - id: gate
            decision: { type: delivery.partial_recovery, prompt: std:prompts/partial_delivery }
            outcomes: { go: success, stop: failure }
          - id: ship
            action: notify.send
            routes: { sent: { end: completed } }
        """;

    const string Fan = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: fan, level: epic }
        steps:
          - id: fan
            parallel:
              branches:
                - { id: a, action: test.run }
                - { id: b, action: test.run }
                - { id: c, action: test.run }
            routes: { completed: ship, any_failed: { escalate: true } }
          - id: ship
            action: notify.send
            routes: { sent: { end: completed } }
        """;

    const string Chain = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: chain, level: epic }
        steps:
          - id: each
            for_each:
              items: [unit, component, e2e]
              mode: chain
              step: { action: test.run, with: { tier_tags: [{ expr: "item" }] } }
            routes: { completed: { end: completed }, any_failed: { escalate: true } }
        """;

    /// <summary>A planner with authority over its own plan, ahead of a protected review it might route around.</summary>
    const string Planned = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: planned, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: plan
            agent:
              role: std:roles/planner
              task: std:tasks/repair_failures
              planning: { targets: [self] }
            outcomes: { planned: success, blocked: blocked }
          - id: build
            action: test.run
          - id: review
            protected: true
            action: artifact.validate
          - id: ship
            action: notify.send
            routes: { sent: { end: completed } }
        """;

    static readonly Probe Lib = new("steering", ("line", Line), ("fan", Fan), ("chain", Chain), ("planned", Planned));

    static ApplyResult PlannerPatch(RunCore core, string changes, string resumeAt = "build") =>
        core.Apply(new StepCompleted("plan", "planned", Patch: new PatchDocument("run-1", new Proposer("agent", "plan"), "test", 1, resumeAt,
            changes.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Op).ToList(), new Model.SourceLocation("t", 1, 1))));

    [Fact]
    public void An_agent_patch_routing_around_a_protected_step_awaits_a_person()
    {
        // review stays reachable through build's failure, but build's passed now goes round it.
        var core = Lib.Start("planned");
        core.Advance();
        var result = PlannerPatch(core, """
            { op: set, target: build, field: routes.passed, value: ship }
            { op: set, target: build, field: routes.failed, value: review }
            """);
        Assert.True(result.Patch!.Status == PatchStatus.Pending, string.Join("; ", result.Patch.Diagnostics));
        Assert.Contains(result.Patch.Diagnostics, d => d.Message.Contains("leaves protected step review unrun", StringComparison.Ordinal));

        // The same patch, older run (no Steering): applied, as before M7.
        var old = Lib.Start("planned", new RunOptions { Steering = false });
        old.Advance();
        Assert.Equal(PatchStatus.Applied, PlannerPatch(old, """
            { op: set, target: build, field: routes.passed, value: ship }
            { op: set, target: build, field: routes.failed, value: review }
            """).Patch!.Status);
    }

    /// <summary>R1 F1: four ways round a protected step that a check on direct routes missed; each leaves review unrun.</summary>
    [Theory]
    [InlineData("{ op: set, target: build, field: limits, value: { attempts: 2 } }", "ship")]
    [InlineData("{ op: set, target: plan, field: routes.planned, value: ship }\n{ op: set, target: plan, field: routes.blocked, value: build }", "build")]
    [InlineData("{ op: set, target: build, field: routes.passed, value: [{ when: \"false\", to: review }, { to: ship }] }", "build")]
    [InlineData("{ op: insert, before: review, step: { id: lint, action: test.run, routes: { passed: [{ when: \"false\", to: review }, { to: { end: completed } }], failed: review, baseline_failed: review } } }", "build")]
    public void An_agent_patch_that_leaves_a_protected_step_unrun_awaits_a_person(string changes, string resumeAt)
    {
        var core = Lib.Start("planned");
        core.Advance();
        var result = PlannerPatch(core, changes, resumeAt);
        Assert.True(result.Patch!.Status == PatchStatus.Pending, string.Join("; ", result.Patch.Diagnostics));
        Assert.Contains(result.Patch.Diagnostics, d => d.Message.Contains("leaves protected step review unrun", StringComparison.Ordinal));
    }

    [Fact]
    public void A_go_to_that_leaves_a_protected_step_unrun_is_refused_until_a_person_skips_it()
    {
        // R1 F2: a breakpoint on the protected review, then go to ship past it.
        var (core, driver) = Line_core();
        Ok(core.Apply(new BreakpointSet("review", true, "ann")));
        driver.Run();
        var paused = Assert.Single(core.State.Waits);
        Assert.Contains("leave protected step review unrun", core.Check(new InterventionAnswered(paused.Id, new Recovery(RecoveryKind.GoTo, "ship"))));
        Assert.Null(core.Check(new InterventionAnswered(paused.Id, new Recovery(RecoveryKind.GoTo, "design"))));

        Ok(core.Apply(new StepSkipped("review", "accepted", "root", "reviewed by hand", MayTouchProtected: true)));
        driver.Run();
        Assert.True(core.State.Steps["review"].Current is { Skipped: true });
        Assert.DoesNotContain(core.State.Waits, w => w.Kind == WaitKind.Breakpoint);
    }

    [Fact]
    public void A_go_to_past_a_protected_step_a_person_skipped_is_allowed()
    {
        // R1 re-check R2: the path the refusal names. The protected review is skipped while the run is elsewhere.
        var (core, driver) = Line_core();
        Ok(core.Apply(new BreakpointSet("design", true, "ann")));
        driver.Run();
        var paused = Assert.Single(core.State.Waits);
        var past = new InterventionAnswered(paused.Id, new Recovery(RecoveryKind.GoTo, "gate"));
        Assert.Contains("leave protected step review unrun", core.Check(past));
        Ok(core.Apply(new StepSkipped("review", "accepted", "root", "reviewed by hand", MayTouchProtected: true)));
        Assert.Null(core.Check(past));
    }

    [Fact]
    public void A_skip_is_refused_on_a_run_started_before_m7()
    {
        // R1 F8: such a run would accept the skip, then run the step anyway.
        var (core, _) = Line_core(new RunOptions { Steering = false });
        Assert.Contains("started before skips existed", core.Check(new StepSkipped("ship", "sent", "ann", "r", true)));
    }

    [Fact]
    public void A_skip_is_refused_while_the_steps_patch_awaits_approval()
    {
        var core = Lib.Start("planned", new RunOptions { RequireReview = true });
        core.Advance();
        Assert.Equal(PatchStatus.Pending, PlannerPatch(core, "{ op: set, target: build, field: limits, value: { attempts: 2 } }").Patch!.Status);
        Assert.Contains("patch awaiting approval", core.Check(new StepSkipped("plan", "planned", "ann", "r", true)));
    }

    [Fact]
    public void The_organisations_require_review_makes_every_agent_patch_await_a_person()
    {
        const string harmless = "{ op: set, target: build, field: limits, value: { attempts: 2 } }";
        var core = Lib.Start("planned", new RunOptions { RequireReview = true });
        core.Advance();
        var result = PlannerPatch(core, harmless);
        Assert.Equal(PatchStatus.Pending, result.Patch!.Status);
        Assert.Contains("planning.require_review", Assert.Single(result.Patch.Diagnostics).Message);

        var free = Lib.Start("planned");
        free.Advance();
        Assert.Equal(PatchStatus.Applied, PlannerPatch(free, harmless).Patch!.Status);
    }

    [Fact]
    public void An_agents_library_override_opens_a_patch_approval_and_approved_asks_the_shell_to_apply_it()
    {
        var core = Lib.Start("planned");
        core.Advance();
        Assert.Contains("not templates", core.Check(new LibraryOverrideProposed("plan", "std:templates/ticket_delivery", "x")) ?? "");
        Ok(core.Apply(new LibraryOverrideProposed("plan", "std:tasks/repair_failures", "new text")));
        var wait = Assert.Single(core.State.Waits);
        Assert.Equal((WaitKind.PatchApproval, "std:tasks/repair_failures"), (wait.Kind, wait.LibraryItem));
        Ok(core.Apply(new PatchAnswered(wait.Id, true, "ann")));
        var command = Assert.Single(core.Advance().OfType<OverrideLibrary>());
        Assert.Equal(("std:tasks/repair_failures", "new text", "ann"), (command.Item, command.Content, command.Actor));
        Assert.Empty(core.State.Waits);
    }

    /// <summary>Each step's success; the gate, a person's decision, is left open.</summary>
    static RunInput? Succeed(string id) => id switch
    {
        "gate" => null,
        "design" => new StepCompleted(id, "done"),
        "review" => new StepCompleted(id, "accepted"),
        "ship" => new StepCompleted(id, "sent"),
        _ => new StepCompleted(id, "passed"),
    };

    static (RunCore Core, Driver Driver) Line_core(RunOptions? options = null)
    {
        var core = Lib.Start("line", options);
        var driver = new Driver(core) { Respond = (id, _) => Succeed(id) };
        return (core, driver);
    }

    static void Ok(ApplyResult result) => Assert.True(result.Accepted, string.Join("; ", result.Errors));

    // ---- Breakpoints --------------------------------------------------------------------------------

    [Fact]
    public void A_breakpoint_pauses_before_the_step_and_continue_starts_it_once()
    {
        var (core, driver) = Line_core();
        Ok(core.Apply(new BreakpointSet("review", true, "ann")));
        driver.Run();
        var wait = Assert.Single(core.State.Waits);
        Assert.Equal((WaitKind.Breakpoint, "review"), (wait.Kind, wait.StepId));
        Assert.Equal(["design"], Driver.Path(core));
        Assert.Equal(1, core.State.RunAttempts);

        Ok(core.Apply(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.Continue))));
        driver.Run();
        // The gate is a person's decision: the run waits there, past the breakpoint.
        Assert.Equal(["design", "review", "gate"], Driver.Path(core));
        Assert.Equal(WaitKind.Decision, Assert.Single(core.State.Waits).Kind);
    }

    [Fact]
    public void A_breakpoint_pauses_every_entry_until_cleared_and_continue_is_only_for_breakpoints()
    {
        var (core, driver) = Line_core();
        Ok(core.Apply(new BreakpointSet("design", true, "ann")));
        driver.Run();
        var first = Assert.Single(core.State.Waits);
        Ok(core.Apply(new InterventionAnswered(first.Id, new Recovery(RecoveryKind.Continue))));
        driver.Respond = (id, _) => id == "design" ? new StepCompleted(id, "stuck") : Succeed(id);
        driver.Run();
        // stuck escalates; a retry re-enters design, which pauses again at its breakpoint.
        var escalation = Assert.Single(core.State.Waits);
        Assert.Equal(WaitKind.Intervention, escalation.Kind);
        Assert.Contains("continue answers a breakpoint", core.Check(new InterventionAnswered(escalation.Id, new Recovery(RecoveryKind.Continue))));
        Ok(core.Apply(new InterventionAnswered(escalation.Id, new Recovery(RecoveryKind.Retry))));
        driver.Run();
        Assert.Equal(WaitKind.Breakpoint, Assert.Single(core.State.Waits).Kind);

        Ok(core.Apply(new BreakpointSet("design", false, "ann")));
        Assert.Empty(core.State.Breakpoints);
        Assert.Contains(core.State.Trace, t => t is { Kind: "breakpoint_cleared", StepId: "design" });
    }

    [Fact]
    public void A_breakpoint_is_refused_on_a_step_that_is_running_or_waiting()
    {
        var (core, driver) = Line_core();
        driver.Advance();
        Assert.Contains("running or waiting", core.Check(new BreakpointSet("design", true, "ann")));
        Assert.Contains("not a step", core.Check(new BreakpointSet("nope", true, "ann")));
        Assert.Null(core.Check(new BreakpointSet("review", true, "ann")));
    }

    [Fact]
    public void A_breakpoint_pauses_each_retry_but_not_an_exhausted_step_and_a_grant_there_keeps_the_clean_up()
    {
        var (core, driver) = Line_core();
        Ok(core.Apply(new BreakpointSet("design", true, "ann")));
        driver.Respond = (id, _) => id == "design" ? new StepFailed(id, ErrorClass.Work, "bad") : Succeed(id);
        driver.Run();
        for (var i = 0; i < 2; i++)
        {
            var pause = Assert.Single(core.State.Waits);
            Assert.Equal(WaitKind.Breakpoint, pause.Kind);
            Ok(core.Apply(new InterventionAnswered(pause.Id, new Recovery(RecoveryKind.Continue))));
            driver.Run();
        }

        // Two work failures spend design's allowance of 2: the next entry is exhausted and escalates without pausing.
        Assert.Equal(WaitKind.Intervention, Assert.Single(core.State.Waits).Kind);
        Assert.Equal(2, core.State.Trace.Count(t => t.Kind == "paused"));

        // Granting at the breakpoint: the grant runs the paused step with its clean_up.
        var (core2, driver2) = Line_core();
        Ok(core2.Apply(new BreakpointSet("review", true, "ann")));
        driver2.Respond = (id, _) => id == "review" ? new StepCompleted(id, "rejected") : Succeed(id);
        driver2.Run();
        var rejected = Assert.Single(core2.State.Waits);
        Ok(core2.Apply(new InterventionAnswered(rejected.Id, new Recovery(RecoveryKind.Continue))));
        driver2.Run();
        var stop = Assert.Single(core2.State.Waits);
        Assert.Equal(WaitKind.Intervention, stop.Kind);
        Ok(core2.Apply(new InterventionAnswered(stop.Id, new Recovery(RecoveryKind.GoTo, "review", CleanUp: true))));
        driver2.Advance();
        var paused = Assert.Single(core2.State.Waits);
        Assert.True(paused is { Kind: WaitKind.Breakpoint, CleanUp: true });
        Ok(core2.Apply(new InterventionAnswered(paused.Id, new Recovery(RecoveryKind.GrantAttempts, Attempts: 1))));
        var commands = driver2.Advance();
        Assert.Contains(commands, c => c is CleanUp { StepId: "review" });
        Assert.Contains(commands, c => c is StartStep { StepId: "review" });
    }

    // ---- Skips --------------------------------------------------------------------------------------

    [Fact]
    public void A_skipped_step_ends_with_the_chosen_outcome_without_running_or_using_an_attempt()
    {
        var (core, driver) = Line_core();
        Ok(core.Apply(new StepSkipped("design", "done", "ann", "already designed", MayTouchProtected: false)));
        driver.Respond = (id, _) => Succeed(id);
        driver.Run();
        Assert.DoesNotContain("design", Driver.Path(core));
        var design = core.State.Steps["design"];
        Assert.True(design.Current is { Skipped: true, Outcome: "done" });
        Assert.Equal(0, design.AttemptsUsed);
        Assert.Contains(core.State.Trace, t => t is { Kind: "skip_set", StepId: "design", Detail: "done by ann: already designed" });
        Assert.Contains(core.State.Trace, t => t is { Kind: "skipped", StepId: "design" });
        Assert.Equal(["review", "gate"], Driver.Path(core));
    }

    [Fact]
    public void A_protected_step_is_skipped_only_with_skip_protected_and_only_with_one_of_its_outcomes()
    {
        var (core, _) = Line_core();
        Assert.Contains("skip_protected", core.Check(new StepSkipped("review", "accepted", "ann", "r", MayTouchProtected: false)));
        Assert.Contains("not an outcome", core.Check(new StepSkipped("review", "nope", "ann", "r", MayTouchProtected: true)));
        Assert.Contains("not an outcome", core.Check(new StepSkipped("review", "attempts_exhausted", "ann", "r", MayTouchProtected: true)));
        Assert.Null(core.Check(new StepSkipped("review", "accepted", "ann", "r", MayTouchProtected: true)));
    }

    [Fact]
    public void A_step_at_a_decision_or_intervention_is_skipped_at_once_and_a_running_one_is_refused()
    {
        var (core, driver) = Line_core();
        driver.Advance();
        Assert.Contains("is running", core.Check(new StepSkipped("design", "done", "ann", "r", false)));
        driver.Run();
        var gate = Assert.Single(core.State.Waits);
        Assert.Equal((WaitKind.Decision, "gate"), (gate.Kind, gate.StepId));
        Ok(core.Apply(new StepSkipped("gate", "go", "ann", "approved in the meeting", false)));
        var commands = driver.Advance();
        Assert.Contains(commands, c => c is CloseWait { StepId: "gate" });
        Assert.True(core.State.Steps["gate"].Current is { Skipped: true, Outcome: "go" });
        driver.Run();
        Assert.Equal(RunStatus.Completed, core.State.Status);
    }

    // ---- Grants ------------------------------------------------------------------------------------

    [Fact]
    public void A_grant_adds_to_the_allowance_and_the_run_ceiling_within_the_hard_ceiling_unless_beyond()
    {
        var options = new RunOptions { HardStepCeiling = 4, HardRunCeiling = 205 };
        var (core, _) = Line_core(options);
        Ok(core.Apply(new AttemptsGranted("design", 2, false, "ann")));
        Assert.Equal(2, core.State.Steps["design"].AttemptsGranted);
        Assert.Equal(2, core.State.RunCeilingGranted);
        Assert.Contains("above the hard ceiling of 4", core.Check(new AttemptsGranted("design", 1, false, "ann")));
        Assert.Contains("above the hard ceiling of 205", core.Check(new AttemptsGranted(null, 4, false, "ann")));
        Ok(core.Apply(new AttemptsGranted("design", 5, true, "root")));
        Assert.Equal(7, core.State.Steps["design"].AttemptsGranted);
        Assert.Contains("uses no attempts", core.Check(new AttemptsGranted("gate", 1, false, "ann")));
        Assert.Contains("positive", core.Check(new AttemptsGranted("design", 0, false, "ann")));
    }

    [Fact]
    public void The_recovery_grant_and_the_override_grant_share_the_hard_ceiling_check()
    {
        var options = new RunOptions { HardStepCeiling = 3 };
        var (core, driver) = Line_core(options);
        driver.Respond = (id, _) => id == "design" ? new StepFailed(id, ErrorClass.Work, "bad") : Succeed(id);
        driver.Run();
        var wait = Assert.Single(core.State.Waits);
        Assert.Contains("above the hard ceiling of 3", core.Check(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.GrantAttempts, Attempts: 2))));
        Ok(core.Apply(new AttemptsGranted("design", 1, false, "ann")));
        Assert.Contains("above the hard ceiling of 3", core.Check(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.GrantAttempts, Attempts: 1))));
    }

    // ---- Role changes and amends --------------------------------------------------------------------

    [Fact]
    public void A_role_change_is_a_persons_patch_and_an_amend_cannot_change_a_role()
    {
        var (core, _) = Line_core();
        Assert.Contains("running or waiting", core.Check(new RoleChanged("design", "std:roles/designer", "root", "r")) ?? core.Apply(new RoleChanged("design", "std:roles/designer", "root", "r")).Errors[0]);
        Ok(core.Apply(new RoleChanged("review", "std:roles/designer", "root", "needs a designer")));
        Assert.Equal("std:roles/designer", core.StepById("review")!.Agent!.Role!.Text);
        Assert.True(core.StepById("review")!.Protected);
        Assert.Equal(2, core.State.Revision);
        Assert.Contains(core.State.Trace, t => t is { Kind: "role_changed", StepId: "review" });
        Assert.Contains("not an agent step", core.Check(new RoleChanged("ship", "std:roles/designer", "root", "r")));
        Assert.Contains("not a role reference", core.Check(new RoleChanged("design", "std:tasks/plan_ticket", "root", "r")));

        var amend = Amend(core, """{ op: replace, target: review, step: { id: review, protected: true, agent: { role: std:roles/implementer, task: std:tasks/repair_failures }, outcomes: { accepted: success, rejected: failure } } }""");
        var result = core.Apply(new PatchProposed(amend, new Proposer("human", null)));
        Assert.Equal(PatchStatus.Rejected, result.Patch!.Status);
        Assert.Contains(Model.Checks.PatchProposer, result.Patch.BlockingChecks);
    }

    [Fact]
    public void A_persons_amend_of_protected_content_needs_amend_protected_and_may_omit_resume_at()
    {
        var (core, _) = Line_core();
        var amend = Amend(core, """{ op: set, target: review, field: limits, value: { attempts: 1 } }""");
        var refused = core.Apply(new PatchProposed(amend, new Proposer("human", null), MayTouchProtected: false));
        Assert.Equal(PatchStatus.Rejected, refused.Patch!.Status);
        Assert.Equal([Model.Checks.PatchProtected], refused.Patch.BlockingChecks);

        var applied = core.Apply(new PatchProposed(amend, new Proposer("human", null), MayTouchProtected: true));
        Assert.Equal(PatchStatus.Applied, applied.Patch!.Status);
        Assert.Equal(1, core.StepById("review")!.Limits!.Attempts);
        // No resume_at: nothing new is entered; the run continues where it was.
        Assert.Equal(["design"], core.State.Pending.Select(e => e.StepId));
    }

    [Fact]
    public void Routing_around_a_protected_step_needs_amend_protected_and_an_agent_patch_doing_it_awaits_a_person()
    {
        var (core, _) = Line_core();
        var around = Amend(core, """{ op: set, target: design, field: routes.done, value: gate }""", resumeAt: null);
        // review would be unreachable: no one may do that (check 9), protected or not.
        Assert.Contains(Model.Checks.PatchLoadChecks, core.Apply(new PatchProposed(around, new Proposer("human", null))).Patch!.BlockingChecks);

        // Reachable through gate's stop, which now goes back to review: a way round it, not a removal.
        var bypass = new PatchDocument("run-1", new Proposer("human", null), "bypass", 1, null,
        [
            Op("""{ op: set, target: design, field: routes.done, value: gate }"""),
            Op("""{ op: set, target: gate, field: routes.stop, value: review }"""),
        ], new Model.SourceLocation("t", 1, 1));
        var refused = core.Apply(new PatchProposed(bypass, new Proposer("human", null), MayTouchProtected: false));
        Assert.Equal([Model.Checks.PatchProtected], refused.Patch!.BlockingChecks);
        Assert.Contains(refused.Patch.Diagnostics, d => d.Message.Contains("leaves protected step review unrun", StringComparison.Ordinal));
    }

    [Fact]
    public void A_guard_inserted_before_a_protected_step_does_not_route_around_it()
    {
        var (core, _) = Line_core();
        var guard = Amend(core, """{ op: insert, before: review, step: { id: lint, action: test.run, routes: { passed: review, failed: { escalate: true }, baseline_failed: { end: failed } } } }""");
        Assert.Equal(PatchStatus.Applied, core.Apply(new PatchProposed(guard, new Proposer("human", null), MayTouchProtected: false)).Patch!.Status);

        var (core2, _) = Line_core();
        var around = Amend(core2, """{ op: insert, before: review, step: { id: lint, action: test.run, routes: { passed: gate, failed: review, baseline_failed: review } } }""");
        Assert.Equal(PatchStatus.Rejected, core2.Apply(new PatchProposed(around, new Proposer("human", null), MayTouchProtected: false)).Patch!.Status);
    }

    // ---- Re-running a branch of a decided join -------------------------------------------------------

    [Fact]
    public void Going_to_a_failed_branch_of_a_decided_join_runs_the_join_again_and_the_run_completes()
    {
        var core = Lib.Start("fan");
        var driver = new Driver(core) { Respond = (id, e) => id == "b" && e == 1 ? new StepCompleted(id, "failed") : Succeed(id) };
        driver.Run();
        Assert.True(core.State.Waits.Count == 1, string.Join("\n", core.State.Trace));
        var wait = core.State.Waits[0];
        Assert.Equal("fan", wait.StepId);

        Ok(core.Apply(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.GoTo, "b"))));
        driver.Advance();
        // While b runs again its branch is running, so a join counting open branches waits for it.
        Assert.Equal([BranchStatus.Succeeded, BranchStatus.Running, BranchStatus.Succeeded], core.State.Containers["fan"].Branches);
        driver.Run();
        Assert.Equal(RunStatus.Completed, core.State.Status);
        Assert.Equal(1, core.State.Steps["a"].Executions);
        Assert.Equal(2, core.State.Steps["b"].Executions);
        Assert.Contains(core.State.Trace, t => t is { Kind: "reopened", StepId: "fan" });
        Assert.Equal(1, core.State.Steps["fan"].Executions);
    }

    [Fact]
    public void Without_steering_the_old_behaviour_ends_incomplete()
    {
        var core = Lib.Start("fan", new RunOptions { Steering = false });
        var driver = new Driver(core) { Respond = (id, e) => id == "b" && e == 1 ? new StepCompleted(id, "failed") : Succeed(id) };
        driver.Run();
        Ok(core.Apply(new InterventionAnswered(core.State.Waits[0].Id, new Recovery(RecoveryKind.GoTo, "b"))));
        driver.Run();
        Assert.Equal(RunStatus.Incomplete, core.State.Status);
    }

    [Fact]
    public void Going_to_a_failed_item_of_a_chain_runs_the_items_after_it()
    {
        var core = Lib.Start("chain");
        var driver = new Driver(core) { Respond = (id, e) => id == "each_1" && e == 1 ? new StepCompleted(id, "failed") : Succeed(id) };
        driver.Run();
        var wait = Assert.Single(core.State.Waits);
        Assert.False(core.State.Steps.ContainsKey("each_2"));
        Ok(core.Apply(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.GoTo, "each_1"))));
        driver.Run();
        Assert.Equal(RunStatus.Completed, core.State.Status);
        Assert.Equal(1, core.State.Steps["each_2"].Executions);
    }

    [Fact]
    public void Skipping_a_failed_branch_at_the_joins_intervention_lets_the_join_decide_again()
    {
        var core = Lib.Start("fan");
        var driver = new Driver(core) { Respond = (id, _) => id == "c" ? new StepCompleted(id, "failed") : Succeed(id) };
        driver.Run();
        var wait = Assert.Single(core.State.Waits);
        Ok(core.Apply(new StepSkipped("c", "passed", "ann", "flaky test, checked by hand", false)));
        Ok(core.Apply(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.GoTo, "c"))));
        driver.Run();
        Assert.Equal(RunStatus.Completed, core.State.Status);
        Assert.True(core.State.Steps["c"].Current is { Skipped: true });
    }

    // ---- Carried across a hand-over ------------------------------------------------------------------

    [Fact]
    public void A_breakpoint_waits_clean_up_survives_a_snapshot()
    {
        var (core, driver) = Line_core();
        Ok(core.Apply(new BreakpointSet("review", true, "ann")));
        driver.Respond = (id, _) => id == "review" ? new StepCompleted(id, "rejected") : Succeed(id);
        driver.Run();
        Ok(core.Apply(new InterventionAnswered(core.State.Waits[0].Id, new Recovery(RecoveryKind.Continue))));
        driver.Run();
        Ok(core.Apply(new InterventionAnswered(core.State.Waits[0].Id, new Recovery(RecoveryKind.GoTo, "review", CleanUp: true))));
        driver.Advance();
        var resumed = RunCore.Resume(Lib.Libraries, Probe.Ref("line"),
            System.Text.Json.JsonSerializer.Deserialize<CarriedCore>(System.Text.Json.JsonSerializer.Serialize(core.Snapshot()))!);
        Assert.True(Assert.Single(resumed.State.Waits) is { Kind: WaitKind.Breakpoint, CleanUp: true });
    }

    [Fact]
    public void Breakpoints_skips_and_overrides_survive_a_snapshot()
    {
        var (core, driver) = Line_core();
        Ok(core.Apply(new BreakpointSet("review", true, "ann")));
        Ok(core.Apply(new StepSkipped("ship", "sent", "ann", "nothing to say", false)));
        core.State.Overrides.Add(new AppliedOverride("o-1", "breakpoint", "applied"));
        driver.Run();
        var paused = Assert.Single(core.State.Waits);
        var json = System.Text.Json.JsonSerializer.Serialize(core.Snapshot());
        var carried = System.Text.Json.JsonSerializer.Deserialize<CarriedCore>(json)!;
        var resumed = RunCore.Resume(Lib.Libraries, Probe.Ref("line"), carried);
        Assert.Equal(["review"], resumed.State.Breakpoints);
        Assert.Equal("sent", resumed.State.Skips["ship"]);
        Assert.Equal("o-1", Assert.Single(resumed.State.Overrides).Id);
        Assert.Equal(WaitKind.Breakpoint, Assert.Single(resumed.State.Waits).Kind);
        Ok(resumed.Apply(new InterventionAnswered(paused.Id, new Recovery(RecoveryKind.Continue))));
        // The entry let through the breakpoint is carried as such: resumed again, it starts rather than pausing.
        var again = RunCore.Resume(Lib.Libraries, Probe.Ref("line"),
            System.Text.Json.JsonSerializer.Deserialize<CarriedCore>(System.Text.Json.JsonSerializer.Serialize(resumed.Snapshot()))!);
        Assert.Contains(again.State.Pending, e => e is { StepId: "review", PastBreakpoint: true });
        var d2 = new Driver(resumed) { Respond = (id, _) => Succeed(id) };
        d2.Run();
        Assert.Equal(WaitKind.Decision, Assert.Single(resumed.State.Waits).Kind);
        Ok(resumed.Apply(new DecisionAnswered("gate", "go")));
        d2.Run();
        Assert.Equal(RunStatus.Completed, resumed.State.Status);
        Assert.True(resumed.State.Steps["ship"].Current is { Skipped: true });
    }

    static PatchOp Op(string yaml)
    {
        var d = new Model.DiagnosticBag();
        var doc = $"{{ api_version: feature-flow/v1, kind: patch, base_revision: 1, changes: [{yaml}] }}";
        return PatchReader.Read(YamlText.Parse(doc, "patch", d)!, d)!.Changes[0];
    }

    static PatchDocument Amend(RunCore core, string op, string? resumeAt = null) =>
        new(core.Run.Id, new Proposer("human", null), "amend", core.State.Revision, resumeAt, [Op(op)], new Model.SourceLocation("t", 1, 1));
}
