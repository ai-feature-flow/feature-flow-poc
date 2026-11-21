using System.Text.Json;
using FeatureFlow.Model;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// The decision an engine action raises (docs/04 section 4.5; LOG 331, 341): <c>scm.merge</c>'s <c>&lt;level&gt;.merge_action</c>,
/// a decision-kind wait on the running step. The step keeps watching while it is open; approve restarts the same execution
/// to ask a person to merge, reject ends it <c>declined</c>, and a merge on the code host first closes it as answered there.
/// </summary>
public class ActionDecisionTests
{
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
            routes:
              merged: { end: completed }
              declined: { end: rejected }
              base_moved: merge
              checks_failed: { end: failed }
        """;

    static readonly Probe Lib = new("action-decision", ("merging", Merging));

    /// <summary>Starts the run, and the merge step asks for its decision.</summary>
    static RunCore AtDecision()
    {
        var core = Lib.Start("merging");
        Assert.Equal([new StartStep("merge", 1)], core.Advance());
        Assert.True(core.Apply(new ActionDecisionRequested("merge", "may acme/api#1 be merged into main?")).Accepted);
        return core;
    }

    [Fact]
    public void The_merge_step_opens_a_merge_action_decision_and_keeps_watching_at_the_same_execution()
    {
        var core = AtDecision();

        var wait = Assert.Single(core.State.Waits);
        Assert.Equal((WaitKind.Gate, "merge"), (wait.Kind, wait.StepId));
        Assert.Equal("ticket.merge_action", core.ActionDecisionTypeOf("merge"));
        Assert.Equal("open", core.ActionDecisionState("merge"));
        // The watch starts again at once, with the same execution: the decision does not hold the step, and no attempt is used.
        Assert.Equal([new StartStep("merge", 1)], core.Advance());
        Assert.Equal((1, 0), (core.State.Steps["merge"].Executions, core.State.Steps["merge"].AttemptsUsed));
        Assert.Contains(core.State.Trace, t => t is { Kind: "decision_opened", StepId: "merge" });
        // Raised once per execution.
        Assert.False(core.Apply(new ActionDecisionRequested("merge", "again")).Accepted);
    }

    [Fact]
    public void Approving_restarts_the_watch_at_the_same_execution_which_now_asks_a_person_to_merge()
    {
        var core = AtDecision();
        core.Advance();

        Assert.Equal("merge: 'merged' is not an answer to its decision (approve, reject)", core.Check(new DecisionAnswered("merge", "merged")));
        Assert.True(core.Apply(new DecisionAnswered("merge", ActionDecisions.Approve)).Accepted);

        Assert.Empty(core.State.Waits);
        Assert.Equal("approve", core.ActionDecisionState("merge"));
        Assert.Equal([new CancelStep("merge"), new StartStep("merge", 1)], core.Advance());

        // The answer is carried across a hand-over.
        var json = JsonSerializer.Serialize(core.Snapshot());
        core = RunCore.Resume(Lib.Libraries, Probe.Ref("merging"), JsonSerializer.Deserialize<CarriedCore>(json)!);
        Assert.Equal("approve", core.ActionDecisionState("merge"));

        Assert.True(core.Apply(new StepCompleted("merge", "merged")).Accepted);
        core.Advance();
        Assert.Equal(RunStatus.Completed, core.State.Status);
    }

    [Fact]
    public void Rejecting_ends_the_step_declined_and_cancels_its_watch()
    {
        var core = AtDecision();
        core.Advance();

        Assert.True(core.Apply(new DecisionAnswered("merge", ActionDecisions.Reject)).Accepted);

        Assert.Empty(core.State.Waits);
        Assert.Equal([new CancelStep("merge"), new EndRun(RunStatus.Rejected)], core.Advance());
        Assert.Equal("declined", core.State.Steps["merge"].Current!.Outcome);
    }

    [Fact]
    public void A_merge_on_the_code_host_before_the_answer_closes_the_decision_as_answered_there_and_the_run_continues()
    {
        var core = AtDecision();
        core.Advance();

        Assert.True(core.Apply(new StepCompleted("merge", "merged")).Accepted);

        Assert.Empty(core.State.Waits);
        var wait = Assert.Single(core.State.Trace, t => t.Kind == "answered");
        Assert.Equal("on the code host, not through Feature Flow: merge ended merged before ticket.merge_action was answered", wait.Detail);
        Assert.DoesNotContain(core.State.Trace, t => t.Kind == "escalated");
        core.Advance();
        Assert.Equal(RunStatus.Completed, core.State.Status);
    }

    [Fact]
    public void A_failed_watch_closes_its_decision_without_an_answer()
    {
        var core = AtDecision();
        core.Advance();

        Assert.True(core.Apply(new StepCannotAnswer("merge", "the pull request cannot be read")).Accepted);

        Assert.Equal(WaitKind.Intervention, Assert.Single(core.State.Waits).Kind);
        Assert.DoesNotContain(core.State.Trace, t => t.Kind == "answered");
        Assert.Contains(core.State.Trace, t => t is { Kind: "wait_closed", StepId: "merge" });
    }
}
