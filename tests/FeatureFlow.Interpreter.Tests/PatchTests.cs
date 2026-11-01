using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// Patches (docs/03 section 10): checks 3, 17 and 25 to 30 of docs/09 section 2, the operations, and
/// <c>plan.expand</c> as a deterministic patch (docs/04 section 6.2).
/// </summary>
public class PatchTests
{
    /// <summary>A planner with authority over its own plan, then work, a protected review, and a notice.</summary>
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
            action: artifact.validate
            protected: true
          - id: ship
            action: notify.send
          - id: lanes
            parallel:
              branches: []
            routes: { completed: { end: completed }, any_failed: { escalate: true } }
        """;

    /// <summary>plan.expand chaining one test step per item after itself; a failed item goes to the report.</summary>
    const string Expanding = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: expanding, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: expand
            action: plan.expand
            with:
              from: tiers
              shape: chain
              target: expand
              on_other: report
              step: { action: test.run, with: { tier_tags: ["$.tier"] } }
          - id: done
            action: notify.send
          - id: report
            action: delivery.report
            routes: { complete: { end: completed }, partial: { end: partial } }
        """;

    /// <summary>Two branches that run at once, then a notice.</summary>
    const string Lanes = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: lanes, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: lanes
            parallel:
              branches:
                - { id: a, action: test.run }
                - { id: b, action: test.run }
            routes: { completed: done, any_failed: { escalate: true } }
          - id: done
            action: notify.send
        """;

    static readonly Probe Lib = new("patches", ("planned", Planned), ("expanding", Expanding), ("lanes", Lanes));

    static PatchDocument Patch(string yaml)
    {
        var d = new DiagnosticBag();
        var patch = PatchReader.Read(YamlText.Parse(yaml, "patch.yaml", d)!, d);
        Assert.True(!d.HasErrors && patch is not null, string.Join("\n", d));
        return patch!;
    }

    static string Doc(string by, string changes, long revision = 1, string resumeAt = "build") => $"""
        api_version: feature-flow/v1
        kind: patch
        metadata: {by}
        base_revision: {revision}
        resume_at: {resumeAt}
        changes:
        {changes}
        """;

    const string ByPlanner = "{ proposed_by: { type: agent, step_id: plan }, reason: test }";
    const string ByHuman = "{ proposed_by: { type: human }, reason: test }";

    /// <summary>Starts the planned template and completes the planner with the patch.</summary>
    static (RunCore Core, ApplyResult Result) Propose(string by, string changes, long revision = 1, string resumeAt = "build", RunOptions? options = null)
    {
        var core = Lib.Start("planned", options);
        core.Advance();
        var result = core.Apply(new StepCompleted("plan", "planned", Patch: Patch(Doc(by, changes, revision, resumeAt))));
        return (core, result);
    }

    /// <summary>A patch from a person through the API, which names the proposer (F8).</summary>
    static PatchProposed Human(PatchDocument patch) => new(patch, new Proposer("human", null));

    const string InsertLint = "  - { op: insert, after: build, step: { id: lint, action: analysis.run } }";

    [Fact]
    public void An_applied_insert_changes_the_route_and_the_run_continues_at_resume_at()
    {
        var (core, result) = Propose(ByPlanner, InsertLint);
        new Driver(core) { Respond = (id, _) => new StepCompleted(id, core.StepById(id)!.Outcomes()[0].Name) }.Run();

        Assert.Equal(PatchStatus.Applied, result.Patch!.Status);
        Assert.Equal(2, core.State.Revision);
        Assert.Equal(["plan", "build", "lint", "review", "ship"], Driver.Path(core));
        Assert.Equal(RunStatus.Completed, core.State.Status);
    }

    [Fact]
    public void Check_25_a_stale_base_revision_is_rejected_as_a_contract_error_without_using_an_attempt()
    {
        var (core, result) = Propose(ByPlanner, InsertLint, revision: 2);

        Assert.False(result.Accepted);
        Assert.Equal(PatchStatus.Rejected, result.Patch!.Status);
        Assert.Equal([25], result.Patch.BlockingChecks);
        Assert.Equal(1, core.State.Revision);
        Assert.Contains("plan", core.State.InFlight);
        Assert.Equal((1, 1), (core.State.Steps["plan"].Executions, core.State.Steps["plan"].AttemptsUsed));
        Assert.Empty(core.Advance().OfType<StartStep>());
    }

    [Fact]
    public void A_planner_claiming_to_be_a_person_is_still_an_agent()
    {
        var (core, result) = Propose("{ proposed_by: { type: human, step_id: plan } }", "  - { op: remove, target: review }");

        Assert.Equal(PatchStatus.Pending, result.Patch!.Status);
        Assert.Equal([29], result.Patch.BlockingChecks);
        Assert.NotNull(core.StepById("review"));
    }

    [Fact]
    public void Removing_a_parallel_whose_branches_are_running_is_rejected()
    {
        var core = Lib.Start("lanes");
        Assert.Equal(["a", "b"], core.Advance().OfType<StartStep>().Select(s => s.StepId));

        var result = core.Apply(Human(Patch(Doc(ByHuman, "  - { op: remove, target: lanes }", resumeAt: "done"))));

        Assert.Equal([28], result.Patch!.BlockingChecks);
        Assert.Contains("a is running or waiting inside lanes", result.Patch.Diagnostics.Single().Message);
        new Driver(core) { Respond = (id, _) => new StepCompleted(id, core.StepById(id)!.Outcomes()[0].Name) }.Run();
        core.Apply(new StepCompleted("a", "passed"));
        core.Apply(new StepCompleted("b", "passed"));
        new Driver(core) { Respond = (id, _) => new StepCompleted(id, "sent") }.Run();
        Assert.Equal(RunStatus.Completed, core.State.Status);
    }

    [Fact]
    public void An_approved_patch_that_has_gone_stale_lets_the_proposer_route_on()
    {
        var (core, result) = Propose(ByPlanner, "  - { op: set, target: review, field: limits, value: { attempts: 1 } }");
        var request = Assert.Single(core.Advance().OfType<RequestPatchApproval>());
        core.Apply(Human(Patch(Doc(ByHuman, "  - { op: set, target: ship, field: limits, value: { attempts: 2 } }", resumeAt: "ship"))));
        core.Advance();

        var answer = core.Apply(new PatchAnswered(request.WaitId, Approve: true));

        Assert.Equal([25], answer.Patch!.BlockingChecks);
        Assert.Contains(new StartStep("build", 1), core.Advance());
    }

    [Fact]
    public void Check_26_resume_at_must_exist_after_the_patch()
    {
        var (_, result) = Propose(ByPlanner, "  - { op: remove, target: ship }", resumeAt: "ship");

        Assert.Equal([26], result.Patch!.BlockingChecks);
    }

    [Fact]
    public void Check_27_an_agent_without_planning_authority_and_a_role_change_are_refused()
    {
        var core = Lib.Start("planned");
        core.Advance();
        var byBuild = core.Apply(new PatchProposed(Patch(Doc(ByPlanner, InsertLint)), new Proposer("agent", "build")));
        var (_, roleChange) = Propose(ByPlanner,
            "  - { op: replace, target: plan, step: { id: plan_again, agent: { role: std:roles/implementer, task: std:tasks/repair_failures }, outcomes: { done: success } } }",
            resumeAt: "build");

        Assert.Equal([27], byBuild.Patch!.BlockingChecks);
        Assert.Equal([27], roleChange.Patch!.BlockingChecks);
        Assert.Contains("an agent never changes a role", roleChange.Patch.Diagnostics.Single().Message);
    }

    [Fact]
    public void Check_28_a_patched_plan_that_fails_a_load_check_is_rejected()
    {
        var (_, result) = Propose(ByPlanner, "  - { op: set, target: build, field: routes.failed, value: nowhere }");

        Assert.Equal([28], result.Patch!.BlockingChecks);
        Assert.Contains("fails check 8 (route target)", result.Patch.Diagnostics.Single().Message);
    }

    [Fact]
    public void Check_29_an_agents_patch_touching_a_protected_step_waits_for_a_person()
    {
        var (core, result) = Propose(ByPlanner, "  - { op: set, target: review, field: limits, value: { attempts: 1 } }");

        Assert.Equal(PatchStatus.Pending, result.Patch!.Status);
        Assert.Equal([29], result.Patch.BlockingChecks);
        var request = Assert.Single(core.Advance().OfType<RequestPatchApproval>());
        Assert.Equal(1, core.State.Revision);

        Assert.True(core.Apply(new PatchAnswered(request.WaitId, Approve: true)).Accepted);
        Assert.Equal(2, core.State.Revision);
        Assert.Equal(1, core.StepById("review")!.Limits!.Attempts);
        Assert.Equal([new StartStep("build", 1)], core.Advance().OfType<StartStep>());
    }

    [Fact]
    public void A_persons_patch_to_a_protected_step_is_applied()
    {
        var core = Lib.Start("planned");
        core.Advance();
        var result = core.Apply(Human(Patch(Doc(ByHuman, "  - { op: remove, target: review }", resumeAt: "plan"))));

        Assert.Equal(PatchStatus.Applied, result.Patch!.Status);
        Assert.Null(core.StepById("review"));
    }

    [Fact]
    public void Check_29_a_role_outside_the_allowed_list_waits_for_a_person()
    {
        var (_, result) = Propose(ByPlanner,
            "  - { op: insert, after: build, step: { id: tidy, agent: { role: std:roles/implementer, task: std:tasks/repair_failures }, outcomes: { done: success } } }",
            options: new RunOptions { AllowedRoles = ["planner", "fixer"] });

        Assert.Equal(PatchStatus.Pending, result.Patch!.Status);
        Assert.Contains("not in planning.allowed_roles", result.Patch.Diagnostics.Single().Message);
    }

    [Fact]
    public void Check_30_a_patch_that_relaxes_a_floor_is_rejected_and_check_22_one_above_a_cap()
    {
        var (_, widened) = Propose(ByPlanner,
            "  - { op: insert, after: build, step: { id: audit, agent: { role: std:roles/reviewer, task: std:tasks/repair_failures, write_access: repository }, outcomes: { done: success } } }");
        var (_, ceiling) = Propose(ByPlanner, "  - { op: set, target: build, field: limits, value: { attempts: 50 } }",
            options: new RunOptions { StepCeiling = 5 });

        Assert.Equal([30], widened.Patch!.BlockingChecks);
        Assert.Contains("check 13", widened.Patch.Diagnostics.Single().Message);
        Assert.Equal([22], ceiling.Patch!.BlockingChecks);
        Assert.Contains("above the cap of 5", ceiling.Patch.Diagnostics.Single().Message);
    }

    [Fact]
    public void Check_3_a_duplicate_id_and_an_id_reused_from_the_runs_history_are_rejected()
    {
        var (_, duplicate) = Propose(ByPlanner, "  - { op: insert, after: build, step: { id: ship, action: analysis.run } }");
        var core = Lib.Start("planned");
        core.Advance();
        var removed = core.Apply(Human(Patch(Doc(ByHuman, "  - { op: remove, target: ship }", resumeAt: "plan"))));
        var reused = core.Apply(Human(Patch(Doc(ByHuman, "  - { op: insert, after: review, step: { id: ship, action: analysis.run } }", revision: 2, resumeAt: "plan"))));

        Assert.Equal([3], duplicate.Patch!.BlockingChecks);
        Assert.Equal(PatchStatus.Applied, removed.Patch!.Status);
        Assert.Equal([3], reused.Patch!.BlockingChecks);
        Assert.Contains("used earlier in this run", reused.Patch.Diagnostics.Single().Message);
    }

    [Fact]
    public void Check_17_a_patch_that_makes_the_plan_too_large_is_rejected()
    {
        var branches = string.Join("\n", Enumerable.Range(0, 2001).Select(i => $"  - {{ op: add_branch, target: lanes, step: {{ id: lane_{i}, action: test.run }} }}"));
        var (_, result) = Propose(ByHuman, branches);

        Assert.Equal([17], result.Patch!.BlockingChecks);
    }

    [Fact]
    public void Insert_before_chain_and_set_rewrite_routes_as_docs_03_section_10_2_says()
    {
        var core = Lib.Start("planned");
        core.Advance();
        var result = core.Apply(Human(Patch(Doc(ByHuman, """
              - { op: insert, before: review, step: { id: gate, action: analysis.run } }
              - { op: chain, target: ship, on_other: plan, step: { id: announce, action: notify.send } }
              - { op: set, target: build, field: routes.failed, value: build }
            """, resumeAt: "plan"))));

        Assert.Equal(PatchStatus.Applied, result.Patch!.Status);
        Assert.Equal(new ResolvedStep("gate", false), Route(core, "build", "passed"));
        Assert.Equal(new ResolvedStep("build", false), Route(core, "build", "failed"));
        Assert.Equal(new ResolvedStep("review", false), Route(core, "gate", "passed"));
        Assert.Equal(new ResolvedStep("announce", false), Route(core, "ship", "sent"));
        Assert.Equal(new ResolvedStep("plan", false), Route(core, "ship", Actions.AttemptsExhausted));
    }

    static Resolved Route(RunCore core, string step, string outcome) =>
        core.StepById(step)!.ResolvedRoutes.Single(r => r.Outcome == outcome).Target;

    [Fact]
    public void Branches_change_only_before_the_parallel_starts()
    {
        var core = Lib.Start("planned");
        var driver = new Driver(core) { Respond = (id, _) => id == "ship" ? null : new StepCompleted(id, core.StepById(id)!.Outcomes()[0].Name) };
        driver.Run();
        var add = Patch(Doc(ByHuman, "  - { op: add_branch, target: lanes, step: { id: lane_a, action: test.run } }", resumeAt: "ship"));

        Assert.Equal(PatchStatus.Applied, core.Apply(Human(add)).Patch!.Status);
        core.Apply(new StepCompleted("ship", "sent"));
        core.Advance();
        var late = Patch(Doc(ByHuman, "  - { op: add_branch, target: lanes, step: { id: lane_b, action: test.run } }", revision: 2, resumeAt: "ship"));

        Assert.Contains("lane_a", core.State.InFlight);
        Assert.Equal([28], core.Apply(Human(late)).Patch!.BlockingChecks);
    }

    [Fact]
    public void Plan_expand_chains_one_step_per_item_and_the_same_list_gives_the_same_patch()
    {
        var core = Lib.Start("expanding");
        core.Advance();
        var items = new List<YNode> { Item("unit"), Item("e2e") };
        var (patch, error) = PlanExpansion.Build(core, "expand", items);
        var (again, _) = PlanExpansion.Build(core, "expand", items);
        Assert.Null(error);
        Assert.Equal(Show(patch!), Show(again!));

        var result = core.Apply(new StepCompleted("expand", "expanded", Patch: patch));
        new Driver(core) { Respond = (id, _) => new StepCompleted(id, core.StepById(id)!.Outcomes()[0].Name) }.Run();

        Assert.Equal(PatchStatus.Applied, result.Patch!.Status);
        Assert.Equal(["expand", "expand_0", "expand_1", "done", "report"], Driver.Path(core));
        Assert.Equal(new ResolvedStep("report", false), Route(core, "expand_0", "failed"));
    }

    [Fact]
    public void Plan_expand_of_an_empty_list_goes_straight_on()
    {
        var core = Lib.Start("expanding");
        core.Advance();
        var (patch, _) = PlanExpansion.Build(core, "expand", []);
        core.Apply(new StepCompleted("expand", "expanded", Patch: patch));
        new Driver(core) { Respond = (id, _) => new StepCompleted(id, core.StepById(id)!.Outcomes()[0].Name) }.Run();

        Assert.Equal(["expand", "done", "report"], Driver.Path(core));
    }

    static YNode Item(string tier)
    {
        var at = new SourceLocation("item", 1, 1);
        return new YMap([new(new YScalar("tier", true, at), new YScalar(tier, true, at))], at);
    }

    static string Show(PatchDocument patch) =>
        $"{patch.BaseRevision} {patch.ResumeAt} " + string.Join(" | ", patch.Changes.Select(c => $"{c.Op} {c.Target} {c.OnOther} {c.Step?.ToFlow()}"));
}
