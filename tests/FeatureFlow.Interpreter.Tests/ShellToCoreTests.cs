using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// What moved from the Temporal shell into the core (A2, review F8): the artifacts a step consumes and check 32, the artifacts
/// the run produced, a step's specification and an action's scope, and child runs started, carried, ended and cancelled.
/// </summary>
public class ShellToCoreTests
{
    const string Artifacts = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: artifacts, level: ticket }
        artifact_types: [std:artifact_types/delivery_plan]
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: plan
            agent: { role: std:roles/planner, task: std:tasks/repair_failures }
            outcomes: { planned: success, blocked: blocked }
            produces: [{ name: notes, type: std:artifact_types/delivery_plan }]
            routes: { planned: use }
          - id: use
            agent: { role: std:roles/planner, task: std:tasks/repair_failures }
            consumes: [{ name: notes }, { name: contract, from: parent }, { name: extra, optional: true }, { name: findings_ledger }]
            outcomes: { planned: success, blocked: blocked }
            routes: { planned: ship }
          - id: ship
            action: artifact.compose
            with: { name: release, members: [team] }
            consumes: [{ name: notes }]
            routes: { composed: { end: completed } }
        """;

    const string Child = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: child, level: ticket }
        steps:
          - id: only
            action: artifact.compose
            with: { name: x, members: [team] }
            routes: { composed: { end: completed } }
        """;

    const string Parent = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: parent, level: epic }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: kid
            action: run.child
            with: { template: probe:templates/child, repositories: [api], parameters: {} }
            routes: { completed: { end: completed } }
        """;

    static readonly Probe Lib = new("shell-to-core", ("artifacts", Artifacts), ("child", Child), ("parent", Parent), ("pulls", Pulls),
        ("bad_child", BadChild), ("bad_expand", BadExpand));

    // Inputs that load (an expression is checked when it is evaluated) and that evaluate, at step start, to values outside
    // the action's contract: a list for run.child's tracker_issue, a run id for plan.expand's shape (LOG AFb-1).
    const string BadChild = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: bad_child, level: epic }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: kid
            action: run.child
            with: { template: probe:templates/child, tracker_issue: { expr: "run.repositories" } }
            routes: { completed: { end: completed } }
        """;

    const string BadExpand = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: bad_expand, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: expand
            action: plan.expand
            with:
              from: tiers
              shape: { expr: "run.id" }
              target: expand
              on_other: done
              step: { action: test.run, with: { tier_tags: ["$.tier"] } }
          - id: done
            action: artifact.compose
            with: { name: x, members: [team] }
            routes: { composed: { end: completed } }
        """;

    [Fact]
    public void A_run_child_step_whose_inputs_evaluate_outside_its_contract_cannot_start_its_child()
    {
        var core = Lib.Start("bad_child");
        Assert.Equal([new StartChild("kid", 1)], core.Advance());

        var (spec, error) = core.ChildSpec("kid", 1);

        Assert.Null(spec);
        Assert.Contains("check 31", error, StringComparison.Ordinal);
        Assert.Contains("run.child input 'tracker_issue' must be a string, not a list", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plan_expand_step_whose_inputs_evaluate_outside_its_contract_cannot_start()
    {
        var core = Lib.Start("bad_expand");
        Assert.Equal([new StartStep("expand", 1)], core.Advance());

        var (spec, error) = core.StepSpec("expand");

        Assert.Null(spec);
        Assert.Contains("check 31", error, StringComparison.Ordinal);
        Assert.Contains("plan.expand input 'shape' must be one of parallel, chain, not 'run-1'", error, StringComparison.Ordinal);
    }

    const string Pulls = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: pulls, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: open
            action: scm.pull_request
            with: { title: work }
            routes: { ready: publish }
          - id: publish
            action: artifact.publish
            with: { repository: acme/docs, folder: docs, files: [] }
            routes: { published: ship }
          - id: ship
            action: artifact.compose
            with: { name: release, members: [team] }
            routes: { composed: { end: completed } }
        """;

    [Fact]
    public void An_actions_scope_names_the_work_pull_request_and_the_run_lists_every_pull_request_its_actions_opened()
    {
        var core = Lib.Start("pulls");
        core.Advance();
        foreach (var (step, outcome, pr) in new[] { ("open", "ready", "acme/api#1"), ("publish", "published", "acme/docs#2") })
        {
            var outputs = new Dictionary<string, object?>(RunCore.ZeroOutputs(core.StepById(step)!)) { ["pull_request"] = pr };
            Assert.True(core.Apply(new StepCompleted(step, outcome, outputs)).Accepted);
            core.Advance();
        }

        // Read from the descriptors (OpensPullRequest, LOG 385), not the actions' names.
        Assert.Equal(["acme/api#1"], core.ScopeFacts("ship").PullRequests);
        Assert.Equal(["acme/api#1", "acme/docs#2"], core.PullRequestsOpened());
    }

    const string Plan = "std:artifact_types/delivery_plan";

    static RunInfo ChildRun(IReadOnlyDictionary<string, string>? parentArtifacts) =>
        new("run-1", ["acme/api"], new Dictionary<string, YNode>(), ParentId: "run-0", ParentArtifacts: parentArtifacts, RootArtifacts: parentArtifacts);

    /// <summary>Runs <c>plan</c> to its end with <c>notes</c>, so <c>use</c> has started.</summary>
    static RunCore AtUse(IReadOnlyDictionary<string, string>? parentArtifacts)
    {
        var core = Lib.Start("artifacts", run: ChildRun(parentArtifacts));
        core.Advance();
        Assert.True(core.Apply(new StepCompleted("plan", "planned", RunCore.ZeroOutputs(core.StepById("plan")!), new Dictionary<string, string> { ["notes"] = "sha256:notes" })).Accepted);
        Assert.Contains(new StartStep("use", 1), core.Advance());
        return core;
    }

    [Fact]
    public void A_steps_consumed_artifacts_resolve_from_their_sources_and_check_32_names_only_a_missing_required_one()
    {
        var core = AtUse(new Dictionary<string, string> { ["contract"] = "sha256:contract" });

        Assert.Equal(
            [
                new ConsumedArtifact("notes", "notes", false, false, "sha256:notes"),
                new ConsumedArtifact("parent/contract", "contract", false, false, "sha256:contract"),
                new ConsumedArtifact("extra", "extra", true, false, null),
                new ConsumedArtifact("findings_ledger", "findings_ledger", false, true, null),
            ],
            core.Consumed("use"));
        Assert.Null(core.MissingConsumed("use"));

        var without = AtUse(null);
        Assert.Equal("check 32 (consumed artifacts exist): use consumes parent/contract, which no step has produced", without.MissingConsumed("use"));
    }

    [Fact]
    public void A_steps_spec_holds_its_contract_and_what_it_consumes_and_refuses_a_start_check_32_refuses()
    {
        var (spec, error) = AtUse(new Dictionary<string, string> { ["contract"] = "sha256:contract" }).StepSpec("use");

        Assert.Null(error);
        Assert.Equal(StepActivity.Agent, spec!.Activity);
        Assert.Equal("std:roles/planner", spec.What);
        Assert.Equal([new OutcomeFact("planned", "success"), new OutcomeFact("blocked", "blocked")], spec.Contract.Outcomes);
        Assert.Equal(new Dictionary<string, string> { ["notes"] = "sha256:notes", ["parent/contract"] = "sha256:contract", ["findings_ledger"] = "findings_ledger" },
            spec.Agent!.Consumed);
        Assert.Equal("planner", spec.Agent.Role);
        Assert.NotNull(spec.Agent.TaskText);

        var without = AtUse(null);
        Assert.StartsWith("check 32", without.StepSpec("use").Error);
        // A pre-submit check stands for the step without check 32 (docs/09 section 4.2).
        Assert.Null(without.StepSpec("use", atStart: false).Error);
    }

    [Fact]
    public void An_actions_scope_lists_the_runs_artifacts_and_with_a_submission_sees_the_submitted_files()
    {
        var core = AtUse(new Dictionary<string, string> { ["contract"] = "sha256:contract" });
        Assert.True(core.Apply(new StepCompleted("use", "planned", RunCore.ZeroOutputs(core.StepById("use")!))).Accepted);
        Assert.Contains(new StartStep("ship", 1), core.Advance());

        var (spec, _) = core.StepSpec("ship");
        Assert.Equal((StepActivity.Engine, "artifact.compose"), (spec!.Activity, spec.What));
        var scope = core.ScopeFacts("ship");
        Assert.Equal([new HeldArtifact("plan", "notes", Plan, "sha256:notes", null, false)], scope.Artifacts);
        Assert.Equal(new Dictionary<string, string> { ["notes"] = "sha256:notes" }, scope.Consumed);
        Assert.Equal(["notes"], scope.Declared);
        Assert.Equal("ff/ticket/run-1", scope.WorkBranch);

        var submitted = core.ScopeFacts("ship", ("plan", new Dictionary<string, string> { ["notes"] = "sha256:submitted" }));
        Assert.Equal([new HeldArtifact("plan", "notes", Plan, "sha256:submitted", null, false)], submitted.Artifacts);
        Assert.Equal("sha256:submitted", submitted.Consumed["notes"]);
    }

    [Fact]
    public void The_artifacts_a_step_produced_are_drained_for_the_index()
    {
        var core = AtUse(null);

        Assert.Equal([new ProducedArtifact("plan", 1, "notes", Plan, "sha256:notes", null, false)], core.State.Produced);
    }

    [Fact]
    public void A_child_step_starts_its_child_which_is_carried_ends_its_step_and_is_cancelled_with_it()
    {
        var core = Lib.Start("parent");
        Assert.Equal([new StartChild("kid", 1)], core.Advance());
        var (spec, error) = core.ChildSpec("kid", 1);
        Assert.Null(error);
        Assert.Equal(("run-1.kid.1", "probe:templates/child", "acme/api"), (spec!.WorkflowId, spec.Template, Assert.Single(spec.Repositories)));
        Assert.True(core.Apply(new ChildStarted("kid", 1, spec.WorkflowId)).Accepted);
        Assert.Equal("kid", core.ChildStep("run-1.kid.1"));

        var resumed = RunCore.Resume(Lib.Libraries, Probe.Ref("parent"), core.Snapshot());
        Assert.Equal([new ChildRun("kid", 1, "run-1.kid.1")], resumed.State.Children);
        Assert.True(resumed.Apply(new ChildEnded("run-1.kid.1", RunStatus.Completed, null)).Accepted);
        Assert.Empty(resumed.State.Children);
        Assert.Equal(RunStatus.Completed, resumed.State.Status);

        Assert.True(core.Apply(new RunAborted("stop")).Accepted);
        var commands = core.Flush();
        Assert.Contains(new CancelStep("kid"), commands);
        Assert.Contains(new CancelChild("kid", "run-1.kid.1"), commands);
        Assert.Empty(core.State.Children);
    }
}
