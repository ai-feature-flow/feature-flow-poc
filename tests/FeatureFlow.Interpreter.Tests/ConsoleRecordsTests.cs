using System.Text.Json;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// What the core gives the console's records (B1): why an intervention stopped its step, with every time the step was sent
/// back (A11); the patch a patch approval waits on (A8); and each step's counters as they change (A2).
/// </summary>
public class ConsoleRecordsTests
{
    /// <summary>Run #198's loop: <c>record_fixes</c> refuses the verifier's triage as <c>invalid</c> and sends it back.</summary>
    const string Fixing = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: fixing, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: verify_fix
            agent: { role: std:roles/planner, task: std:tasks/repair_failures }
            limits: { attempts: 3 }
            outcomes: { verified: success, blocked: blocked }
            routes: { verified: record_fixes }
          - id: record_fixes
            action: test.run
            routes: { passed: { end: completed }, failed: verify_fix }
        """;

    const string Merging = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: merging, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: merge
            action: scm.merge
            with: { engine_can_merge: false }
            routes: { merged: { end: completed }, declined: { end: rejected }, base_moved: merge, checks_failed: { end: failed } }
        """;

    const string Reviewed = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: reviewed, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: plan
            agent:
              role: std:roles/planner
              task: std:tasks/repair_failures
              planning: { targets: [self], review: required }
            outcomes: { planned: success, blocked: blocked }
          - id: build
            action: test.run
          - id: ship
            action: artifact.compose
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
            with: { template: probe:templates/reviewed, repositories: [acme/api], parameters: {} }
          - id: ship
            action: artifact.compose
            routes: { composed: { end: completed } }
        """;

    static readonly Probe Lib = new("console-records", ("fixing", Fixing), ("merging", Merging), ("reviewed", Reviewed), ("parent", Parent));

    const string Declined = "verify_fix proposed state declined for F-1, which record_fixes does not take (states: fixed, open)";

    /// <summary>Runs <c>verify_fix</c> through the loop: each answer it gives, then <c>record_fixes</c> refuses it.</summary>
    static RunCore Loop(params RunInput[] verifies)
    {
        var core = Lib.Start("fixing");
        foreach (var input in verifies)
        {
            core.Advance();
            Assert.True(core.Apply(input).Accepted);
            if (input is StepCompleted { Outcome: "verified" })
            {
                core.Advance();
                Assert.True(core.Apply(new StepCompleted("record_fixes", "failed", Summary: Declined)).Accepted);
            }
        }

        core.Advance();
        return core;
    }

    static WaitCauseInfo CauseOf(RunCore core)
    {
        var wait = Assert.Single(core.State.Waits);
        Assert.Equal(WaitKind.Intervention, wait.Kind);
        return core.Describe(wait).Cause!;
    }

    [Fact]
    public void An_exhausted_interventions_cause_lists_every_time_record_fixes_sent_the_step_back()
    {
        var core = Loop(
            new StepCompleted("verify_fix", "verified", Summary: "F-1 is declined: the spec never asked for it"),
            new StepCompleted("verify_fix", "verified", Summary: "F-1 is still declined"),
            new StepCompleted("verify_fix", "verified", Summary: "F-1 declined, as before"));

        var cause = CauseOf(core);
        Assert.Equal(("verify_fix", Actions.AttemptsExhausted, "attempts exhausted"), (cause.StepId, cause.Outcome, cause.Reason));
        Assert.Equal("F-1 declined, as before", cause.Said);
        Assert.Equal((3, 3L), (cause.AttemptsUsed, cause.AttemptsAllowed));
        Assert.Equal(
            [new SentBackInfo(1, "record_fixes", "failed", Declined), new SentBackInfo(2, "record_fixes", "failed", Declined), new SentBackInfo(3, "record_fixes", "failed", Declined)],
            cause.SentBack);
    }

    [Fact]
    public void A_blocked_interventions_cause_has_the_steps_own_summary_and_a_failed_attempt_sent_back_by_no_step()
    {
        var core = Loop(
            new StepFailed("verify_fix", ErrorClass.Work, "output contract: outcome 'done' is not an outcome"),
            new StepCompleted("verify_fix", "verified", Summary: "F-1 is declined"),
            new StepCompleted("verify_fix", "blocked", Summary: "record_fixes will not take declined; a person must decide F-1"));

        var cause = CauseOf(core);
        Assert.Equal(("blocked", "blocked escalates: record_fixes will not take declined; a person must decide F-1"), (cause.Outcome, cause.Reason));
        Assert.Equal("record_fixes will not take declined; a person must decide F-1", cause.Said);
        Assert.Equal((3, 3L), (cause.AttemptsUsed, cause.AttemptsAllowed));
        Assert.Equal(
            [new SentBackInfo(1, null, null, "attempt 1 of verify_fix failed: output contract: outcome 'done' is not an outcome"),
             new SentBackInfo(2, "record_fixes", "failed", Declined)],
            cause.SentBack);
    }

    static PatchDocument Patch(string? resumeAt)
    {
        var yaml = $"""
            api_version: feature-flow/v1
            kind: patch
            metadata: {JsonSerializer.Serialize(new Dictionary<string, object> { ["proposed_by"] = new { type = "agent", step_id = "plan" }, ["reason"] = "needs a lint step" })}
            base_revision: 1
            {(resumeAt is null ? "" : $"resume_at: {resumeAt}")}
            changes:
              - {(resumeAt is null ? "{ op: insert, after: kid, step: { id: notice, action: artifact.compose } }" : "{ op: insert, after: build, step: { id: lint, action: analysis.run } }")}
            """;
        var d = new DiagnosticBag();
        var patch = PatchReader.Read(YamlText.Parse(yaml, "patch.yaml", d)!, d);
        Assert.True(!d.HasErrors && patch is not null, string.Join("\n", d));
        return patch!;
    }

    [Fact]
    public void A_patch_approval_holds_its_own_steps_patch_as_written_with_why_it_needs_a_person()
    {
        var core = Lib.Start("reviewed");
        core.Advance();
        Assert.True(core.Apply(new StepCompleted("plan", "planned", Patch: Patch("build"))).Accepted);

        var patch = core.Describe(Assert.Single(core.State.Waits)).Patch!;
        Assert.Equal(("agent", "plan", "needs a lint step", "build", 1L, false), (patch.ProposerType, patch.ProposerStepId, patch.Reason, patch.ResumeAt, patch.BaseRevision, patch.FromChild));
        Assert.Equal(["plan has planning.review: required"], patch.NeedsApproval);
        var written = PatchReader.ReadWritten(patch.Document);
        Assert.Equal(("insert", "build", "plan"), (written.Changes.Single().Op, written.Changes.Single().Target, written.ProposedBy.StepId));
    }

    [Fact]
    public void A_patch_approval_of_a_childs_patch_says_it_came_from_the_child_and_continues_where_the_run_was()
    {
        var core = Lib.Start("parent");
        foreach (var start in core.Advance().OfType<StartChild>())
            core.Apply(new ChildStarted(start.StepId, start.Execution, "run-1.kid.1"));
        Assert.Equal(PatchStatus.Pending, core.Apply(new ChildPatchProposed("kid", Patch(null), "plan", ReviewRequired: true)).Patch!.Status);

        var wait = Assert.Single(core.State.Waits);
        var patch = core.Describe(wait).Patch!;
        Assert.Equal(("kid", "agent", "plan", true, null), (wait.StepId, patch.ProposerType, patch.ProposerStepId, patch.FromChild, patch.ResumeAt));
        Assert.Equal(["plan (in the child run) has planning.review: required"], patch.NeedsApproval);
        Assert.Equal("kid", PatchReader.ReadWritten(patch.Document).Changes.Single().Target);
        Assert.Null(core.Describe(wait).Cause);
    }

    [Fact]
    public void An_agent_steps_counters_are_given_when_they_change_and_all_again_after_a_hand_over()
    {
        var core = Lib.Start("fixing");
        core.Advance();
        Assert.Equal([new StepCounters("verify_fix", 1, 1, 3, 0)], core.TakeStepCounters());
        Assert.Empty(core.TakeStepCounters());

        Assert.True(core.Apply(new StepCompleted("verify_fix", "verified")).Accepted);
        core.Advance();
        var record = core.Allowance(core.StepById("record_fixes")!);
        Assert.Equal([new StepCounters("record_fixes", 1, 1, record, 0)], core.TakeStepCounters());

        var resumed = RunCore.Resume(Lib.Libraries, Probe.Ref("fixing"), core.Snapshot());
        Assert.Equal([new StepCounters("record_fixes", 1, 1, record, 0), new StepCounters("verify_fix", 1, 1, 3, 0)], resumed.TakeStepCounters());
    }

    [Fact]
    public void Grants_at_an_intervention_and_by_override_raise_the_steps_allowed_attempts()
    {
        var core = Loop(
            new StepCompleted("verify_fix", "verified"), new StepCompleted("verify_fix", "verified"), new StepCompleted("verify_fix", "verified"));
        Assert.Contains(new StepCounters("verify_fix", 3, 3, 3, 0), core.TakeStepCounters());

        Assert.True(core.Apply(new InterventionAnswered(core.State.Waits.Single().Id, new Recovery(RecoveryKind.GrantAttempts, Attempts: 1))).Accepted);
        Assert.Equal([new StepCounters("verify_fix", 3, 3, 4, 1)], core.TakeStepCounters());

        Assert.True(core.Apply(new AttemptsGranted("verify_fix", 2, false, "ann")).Accepted);
        Assert.Equal([new StepCounters("verify_fix", 3, 3, 6, 3)], core.TakeStepCounters());
    }

    [Fact]
    public void A_merges_lost_race_uses_no_attempt_and_its_rejected_action_decision_uses_one()
    {
        var core = Lib.Start("merging");
        core.Advance();
        var allowed = core.Allowance(core.StepById("merge")!);
        Assert.Equal([new StepCounters("merge", 1, 0, allowed, 0)], core.TakeStepCounters());

        Assert.True(core.Apply(new StepCompleted("merge", "base_moved")).Accepted);
        core.Advance();
        Assert.Equal([new StepCounters("merge", 2, 0, allowed, 0)], core.TakeStepCounters());

        Assert.True(core.Apply(new ActionDecisionRequested("merge", "may acme/api#1 be merged into main?")).Accepted);
        core.Advance();
        Assert.Empty(core.TakeStepCounters());
        Assert.True(core.Apply(new DecisionAnswered("merge", ActionDecisions.Reject)).Accepted);
        Assert.Equal([new StepCounters("merge", 2, 1, allowed, 0)], core.TakeStepCounters());
    }
}
