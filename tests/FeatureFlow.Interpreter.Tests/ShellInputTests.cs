using System.Text.Json;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// What the core gained for the Temporal shell (M3): carried state for continue-as-new, answers to
/// interventions (docs/01 section 7.2), aborting, a step that cannot answer, a child's patch to its parent
/// (docs/03 section 9.4), and a step's evaluated inputs.
/// </summary>
public class ShellInputTests
{
    const string Retrying = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: retrying, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
          limits: { attempts: 2 }
        parameters:
          ticket_id: { type: string, required: true }
        steps:
          - id: prepare
            action: notify.send
            with: { message: { expr: "'ticket ' + params.ticket_id" }, audience: [team] }
          - id: work
            action: test.run
            routes: { passed: { end: completed }, baseline_failed: plan }
          - id: plan
            agent:
              role: std:roles/planner
              task: std:tasks/repair_failures
              planning: { targets: [self] }
            outcomes: { planned: success, blocked: blocked }
            routes: { planned: work }
        """;

    /// <summary>A child template whose planner may change its parent's plan.</summary>
    const string Child = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: child, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: plan
            agent:
              role: std:roles/planner
              task: std:tasks/repair_failures
              planning: { targets: [parent] }
            outcomes: { planned: success, blocked: blocked }
            routes: { planned: { end: completed } }
        """;

    /// <summary>A parent running one child, then a protected notice.</summary>
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
            with: { template: probe:templates/child, level: ticket, repositories: [acme/api], parameters: {} }
          - id: ship
            action: notify.send
            protected: true
            routes: { sent: { end: completed } }
        """;

    static readonly Probe Lib = new("shell", ("retrying", Retrying), ("child", Child), ("parent", Parent));

    static RunInfo TicketRun(string? parent = null) =>
        new("run-1", ["acme/api"], new Dictionary<string, YNode> { ["ticket_id"] = new YScalar("FF-7", true, new SourceLocation("t", 1, 1)) }, parent);

    static RunCore Retrying_core(out Driver driver)
    {
        var (core, d) = RunCore.Start(Lib.Libraries, Probe.Ref("retrying"), TicketRun());
        Assert.True(core is not null, string.Join("\n", d));
        driver = new Driver(core!);
        return core!;
    }

    [Fact]
    public void Inputs_are_evaluated_from_the_steps_with()
    {
        var core = Retrying_core(out var driver);
        driver.Advance();
        var inputs = core.ResolveInputs("prepare");
        Assert.Equal("ticket FF-7", inputs["message"]);
        Assert.Equal(new List<object?> { "team" }, inputs["audience"]);
    }

    [Fact]
    public void Granting_attempts_at_an_exhausted_step_runs_it_again_and_retry_alone_escalates_again()
    {
        var core = Retrying_core(out var driver);
        driver.Respond = (id, _) => id == "work" ? new StepFailed(id, ErrorClass.Work, "flaky") : new StepCompleted(id, "sent");
        driver.Run();
        var wait = Assert.Single(core.State.Waits);
        Assert.Equal(WaitKind.Intervention, wait.Kind);
        Assert.Equal(2, core.State.Steps["work"].Executions);

        Assert.True(core.Apply(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.Retry))).Accepted);
        driver.Advance();
        var again = Assert.Single(core.State.Waits);
        Assert.NotEqual(wait.Id, again.Id);
        Assert.Equal(2, core.State.Steps["work"].Executions);

        Assert.True(core.Apply(new InterventionAnswered(again.Id, new Recovery(RecoveryKind.GrantAttempts, Attempts: 1))).Accepted);
        driver.Respond = (id, _) => new StepCompleted(id, "passed");
        driver.Run();
        Assert.Equal(3, core.State.Steps["work"].Executions);
        Assert.Equal(RunStatus.Completed, core.State.Status);
    }

    [Fact]
    public void Go_to_with_clean_up_and_re_plan_enter_the_named_step()
    {
        var core = Retrying_core(out var driver);
        driver.Respond = (id, _) => id == "work" ? new StepFailed(id, ErrorClass.Work, "x") : new StepCompleted(id, id == "plan" ? "planned" : "sent");
        driver.Run();
        var wait = core.State.Waits.Single();

        var refused = core.Apply(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.Replan, "prepare")));
        Assert.False(refused.Accepted);
        Assert.Contains("planning authority", refused.Errors.Single());
        Assert.Contains("not a step", core.Check(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.GoTo, "nowhere"))));

        Assert.True(core.Apply(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.GoTo, "prepare", CleanUp: true))).Accepted);
        var commands = driver.Advance();
        Assert.Contains(commands, c => c is CleanUp { StepId: "prepare" });
        Assert.Contains(commands, c => c is StartStep { StepId: "prepare" });
    }

    [Fact]
    public void A_person_may_end_a_run_failed_but_not_completed()
    {
        var core = Retrying_core(out var driver);
        driver.Respond = (id, _) => id == "work" ? new StepFailed(id, ErrorClass.Work, "x") : new StepCompleted(id, "sent");
        driver.Run();
        var wait = core.State.Waits.Single();
        Assert.NotNull(core.Check(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.End, Status: RunStatus.Completed))));
        Assert.True(core.Apply(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.End, Status: RunStatus.Failed))).Accepted);
        Assert.Equal(RunStatus.Failed, core.State.Status);
        Assert.Contains(driver.Advance(), c => c is CloseWait or EndRun);
    }

    [Fact]
    public void Abort_ends_the_run_aborted_and_cancels_what_runs()
    {
        var core = Retrying_core(out var driver);
        driver.Advance();
        Assert.True(core.Apply(new RunAborted("no longer needed")).Accepted);
        var commands = core.Flush();
        Assert.Contains(commands, c => c is CancelStep { StepId: "prepare" });
        Assert.Contains(commands, c => c is EndRun { Status: RunStatus.Aborted });
        Assert.NotNull(core.Check(new RunAborted("again")));
    }

    [Fact]
    public void A_step_that_cannot_answer_raises_an_intervention_and_retry_runs_it_again()
    {
        var core = Retrying_core(out var driver);
        driver.Advance();
        Assert.True(core.Apply(new StepCannotAnswer("prepare", "inputs cannot be evaluated")).Accepted);
        var wait = Assert.Single(core.State.Waits);
        Assert.Equal("prepare", wait.StepId);
        Assert.True(core.Apply(new InterventionAnswered(wait.Id, new Recovery(RecoveryKind.Retry))).Accepted);
        Assert.Contains(driver.Advance(), c => c is StartStep { StepId: "prepare", Execution: 2 });
    }

    static PatchDocument InsertAfterKid(string? resumeAt) => new(null, new Proposer("agent", "plan"), "another ticket", 1, resumeAt,
        [new PatchOp("insert", "kid", new SourceLocation("p", 1, 1), Position: "after",
            Step: (YMap)YamlWriter.Read("{\"id\": \"kid_extra\", \"action\": \"run.child\", \"with\": {\"template\": \"probe:templates/child\", \"level\": \"ticket\", \"repositories\": [\"acme/api\"], \"parameters\": {}}}"))],
        new SourceLocation("p", 1, 1));

    [Fact]
    public void A_child_planner_sends_its_patch_to_the_parent_and_ends_as_reported()
    {
        var (child, d) = RunCore.Start(Lib.Libraries, Probe.Ref("child"), TicketRun(parent: "run-0"));
        Assert.True(child is not null, string.Join("\n", d));
        child!.Advance();
        var result = child.Apply(new StepCompleted("plan", "planned", Patch: InsertAfterKid(null), PatchToParent: true));
        Assert.True(result.Accepted, string.Join("; ", result.Errors));
        var commands = child.Advance();
        Assert.Contains(commands, c => c is SendPatchToParent { StepId: "plan" });
        Assert.Equal(RunStatus.Completed, child.State.Status);

        var (orphan, _) = RunCore.Start(Lib.Libraries, Probe.Ref("child"), TicketRun());
        orphan!.Advance();
        Assert.Contains("no parent", orphan.Apply(new StepCompleted("plan", "planned", Patch: InsertAfterKid(null), PatchToParent: true)).Errors.Single());
        var (selfOnly, _) = RunCore.Start(Lib.Libraries, Probe.Ref("retrying"), TicketRun(parent: "run-0"));
        var selfDriver = new Driver(selfOnly!);
        selfDriver.Respond = (id, _) => id == "work" ? new StepFailed(id, ErrorClass.Work, "x") : new StepCompleted(id, "sent");
        selfDriver.Run();
        Assert.True(selfOnly!.Apply(new InterventionAnswered(selfOnly.State.Waits.Single().Id, new Recovery(RecoveryKind.GoTo, "plan"))).Accepted);
        selfDriver.Advance();
        Assert.Contains("parent's plan", selfOnly.Apply(new StepCompleted("plan", "planned", Patch: InsertAfterKid(null), PatchToParent: true)).Errors.Single());
    }

    [Fact]
    public void The_parent_applies_a_childs_patch_without_resume_at_and_the_child_step_keeps_running()
    {
        var parent = Lib.Start("parent");
        var driver = new Driver(parent);
        driver.Advance();
        var result = parent.Apply(new ChildPatchProposed("kid", InsertAfterKid(null), "plan"));
        Assert.True(result.Accepted);
        Assert.Equal(PatchStatus.Applied, result.Patch!.Status);
        Assert.Contains("kid", parent.State.InFlight);
        Assert.Equal(2, parent.State.Revision);

        driver.Respond = (id, _) => new StepCompleted(id, id.StartsWith("kid", StringComparison.Ordinal) ? "completed" : "sent");
        driver.Run();
        Assert.Equal(["kid", "kid_extra", "ship"], Driver.Path(parent));
        Assert.Equal(RunStatus.Completed, parent.State.Status);
    }

    [Fact]
    public void A_childs_patch_touching_a_protected_step_waits_for_a_person()
    {
        var parent = Lib.Start("parent");
        new Driver(parent).Advance();
        var patch = new PatchDocument(null, new Proposer("agent", "plan"), "skip the notice", 1, null,
            [new PatchOp("set", "ship", new SourceLocation("p", 1, 1), Field: "routes.sent", Value: YamlWriter.Read("{\"end\": \"failed\"}"))],
            new SourceLocation("p", 1, 1));
        var result = parent.Apply(new ChildPatchProposed("kid", patch, "plan"));
        Assert.Equal(PatchStatus.Pending, result.Patch!.Status);
        var wait = parent.State.Waits.Single();
        Assert.Equal(("kid", true), (wait.StepId, wait.FromChild));

        Assert.True(parent.Apply(new PatchAnswered(wait.Id, Approve: true)).Accepted);
        Assert.Equal(2, parent.State.Revision);
        Assert.Contains("kid", parent.State.InFlight);
    }

    /// <summary>
    /// A run carried across continue-as-new at every point behaves as one that never was: the epic's main path,
    /// with a plan.expand patch, a parallel of children and an open decision, is snapshotted, written to JSON,
    /// read back and resumed after every input.
    /// </summary>
    [Fact]
    public void A_run_carried_after_every_input_takes_the_same_path_as_one_that_is_not()
    {
        var std = new LibrarySet(Library.Library.Open(Probe.StdRoot, new DiagnosticBag())!);
        var template = new LibraryReference("std", null, "templates", "epic_delivery");
        var parameters = new Dictionary<string, YNode>
        {
            ["epic_name"] = new YScalar("billing", true, new SourceLocation("t", 1, 1)),
            ["engine_can_merge"] = new YScalar("true", true, new SourceLocation("t", 1, 1)),
        };
        var run = new RunInfo("epic-1", ["acme/api"], parameters);
        var items = (YSeq)YamlWriter.Read("[{\"name\": \"a\", \"repositories\": [\"acme/api\"]}, {\"name\": \"b\", \"repositories\": [\"acme/api\"]}]");

        RunCore Drive(bool carry)
        {
            var (core, d) = RunCore.Start(std, template, run);
            Assert.True(core is not null, string.Join("\n", d));
            var queue = new List<(string Id, bool Decision)>();
            for (var i = 0; i < 500 && core!.State.Status == RunStatus.Running; i++)
            {
                foreach (var command in core.Advance())
                    switch (command)
                    {
                        case StartStep s: queue.Add((s.StepId, false)); break;
                        case OpenDecision o: queue.Add((o.StepId, true)); break;
                        case CancelStep c: queue.RemoveAll(q => q.Id == c.StepId); break;
                    }

                if (queue.Count == 0)
                    break;
                var (id, decision) = queue[0];
                queue.RemoveAt(0);
                var step = core.StepById(id)!;
                RunInput input;
                if (decision)
                    input = new DecisionAnswered(id, step.DeclaredOutcomes.First().Name);
                else if (step.Action == "plan.expand")
                    input = new StepCompleted(id, "expanded", Patch: PlanExpansion.Build(core, id, items.Items).Patch);
                else
                {
                    var outcome = step.Outcomes().First(o => o.Class == OutcomeClass.Success).Name;
                    var outputs = RunCore.ZeroOutputs(step);
                    var artifacts = step.Produces.Where(p => p.Outcome is null || p.Outcome == outcome).ToDictionary(p => p.Name, p => $"sha256:{id}/{p.Name}");
                    input = new StepCompleted(id, outcome, outputs, artifacts);
                }

                var result = core.Apply(input);
                Assert.True(result.Accepted, string.Join("; ", result.Errors));
                if (carry)
                {
                    var commands = core.Flush();
                    foreach (var command in commands)
                        switch (command)
                        {
                            case StartStep s: queue.Add((s.StepId, false)); break;
                            case OpenDecision o: queue.Add((o.StepId, true)); break;
                            case CancelStep c: queue.RemoveAll(q => q.Id == c.StepId); break;
                        }

                    var json = JsonSerializer.Serialize(core.Snapshot());
                    core = RunCore.Resume(std, template, JsonSerializer.Deserialize<CarriedCore>(json)!);
                }
            }

            return core!;
        }

        var plain = Drive(carry: false);
        var carried = Drive(carry: true);
        Assert.True(plain.State.Status == RunStatus.Completed, string.Join("\n", plain.State.Trace.TakeLast(8)) + "\n" + string.Join("\n", plain.State.Waits));
        Assert.Equal(Driver.Path(plain), Driver.Path(carried));
        Assert.Equal(plain.State.Status, carried.State.Status);
        Assert.Equal(plain.State.Revision, carried.State.Revision);
        Assert.Contains("tickets.children_1", Driver.Path(carried));
    }
}
