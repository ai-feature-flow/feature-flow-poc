using FeatureFlow.Model;
using FeatureFlow.Model.Plans;

namespace FeatureFlow.Interpreter;

/// <summary>
/// The decision an engine action raises (docs/04 section 4.5; LOG 331): a decision-kind wait on an action step, of type
/// <c>&lt;level&gt;.merge_action</c>, answered <see cref="Approve"/> or <see cref="Reject"/>. Approval lets the step ask a
/// person to merge; rejection ends the step <see cref="Declined"/>, as closing the pull request does (LOG 349).
/// <c>scm.merge</c> is the only action that raises an action decision: <c>RunCore.ActionDecisionOf</c> and
/// <c>RunCore.IsActionDecision</c> take any decision wait on an action step to be this one, so an action that raises a
/// decision of another type must change them.
/// </summary>
public static class ActionDecisions
{
    public const string Approve = "approve";
    public const string Reject = "reject";

    /// <summary>The step's outcome when the decision is rejected.</summary>
    public const string Declined = "declined";

    public static readonly IReadOnlyList<string> Choices = [Approve, Reject];

    /// <summary>The decision type an action raises at a run's level.</summary>
    public static string Type(string level) => $"{level}.merge_action";
}

/// <summary>
/// The decision an engine action raises while it runs (docs/04 section 4.5; LOG 331): <c>scm.merge</c>'s
/// <c>&lt;level&gt;.merge_action</c>. It is a decision-kind wait on the action step, not a step of its own, so approval
/// rules on its type apply as on any decision. The step goes on watching the code host while it is open: an approval
/// restarts the same execution so it asks a person to merge, a rejection ends the step <c>declined</c>, and a step that
/// ends first (a merge on the code host, LOG 341) closes it as answered there.
/// </summary>
public sealed partial class RunCore
{
    /// <summary>The open decision a running action raised, if any.</summary>
    OpenWait? ActionDecisionOf(string stepId) =>
        State.Waits.FirstOrDefault(w => w.Kind == WaitKind.Decision && w.StepId == stepId && StepById(stepId)?.Form == StepForm.Action);

    /// <summary>Whether a wait is a decision an action raised: its step goes on running while it is open.</summary>
    bool IsActionDecision(OpenWait wait) => wait.Kind == WaitKind.Decision && StepById(wait.StepId)?.Form == StepForm.Action;

    /// <summary>The type of the decision the run's actions raise, at the run's level.</summary>
    public string ActionDecisionType => ActionDecisions.Type(Plan.Document.Level ?? "run");

    /// <summary>The state of the decision a step's execution raised: null when none, <c>open</c>, or the answer given.</summary>
    public string? ActionDecisionState(string stepId) =>
        ActionDecisionOf(stepId) is not null ? "open" : State.Steps.GetValueOrDefault(stepId)?.Current?.ActionAnswer;

    /// <summary>
    /// Opens the action's decision. The step's activity has ended, so the same execution is started again at once and goes
    /// on watching while the decision is open (<see cref="WaitingOn"/> does not hold it): no attempt is used.
    /// </summary>
    ApplyResult RequestActionDecision(ActionDecisionRequested r)
    {
        if (StepById(r.StepId) is not { Form: StepForm.Action } step || !State.InFlight.Contains(r.StepId))
            return ApplyResult.Refused($"{r.StepId} is not a running action");
        if (ActionDecisionState(step.Id) is { } state)
            return ApplyResult.Refused($"{step.Id} already raised its decision in this execution ({state})");
        var wait = new OpenWait(NewWaitId("decision"), WaitKind.Decision, step.Id, null, r.Reason);
        State.Waits.Add(wait);
        State.InFlight.Remove(step.Id);
        State.Pending.Insert(0, new Entry(step.Id, false, Resumes: true));
        Trace("decision_opened", step.Id, $"{wait.Id} ({ActionDecisionType}): {r.Reason}");
        Progressed();
        return ApplyResult.Ok;
    }

    /// <summary>Why an answer to an action's decision would be refused, or null.</summary>
    static string? ActionAnswerError(DecisionAnswered a) =>
        ActionDecisions.Choices.Contains(a.Outcome)
            ? a.Artifacts is { Count: > 0 } ? $"{a.StepId}: an answer to its decision produces no artifacts" : null
            : $"{a.StepId}: '{a.Outcome}' is not an answer to its decision ({string.Join(", ", ActionDecisions.Choices)})";

    /// <summary>
    /// A person answered the action's decision. Approve: the running watch is stopped and the same execution started
    /// again, now asking a person to merge. Reject: the step ends <c>declined</c> and its watch is cancelled (LOG 349).
    /// </summary>
    ApplyResult AnswerActionDecision(Step step, OpenWait wait, DecisionAnswered a)
    {
        if (ActionAnswerError(a) is { } error)
            return ApplyResult.Refused(error);
        State.Waits.Remove(wait);
        var record = Record(step.Id);
        var running = State.InFlight.Remove(step.Id);
        State.Pending.RemoveAll(e => e.StepId == step.Id && e.Resumes);
        if (running)
            outbox.Add(new CancelStep(step.Id));
        if (a.Outcome == ActionDecisions.Approve)
        {
            record.Current!.ActionAnswer = a.Outcome;
            State.Pending.Insert(0, new Entry(step.Id, false, Resumes: true));
            return ApplyResult.Ok;
        }

        if (IsMerge(step))
            record.AttemptsUsed++;
        Finish(step, record, ActionDecisions.Declined);
        return ApplyResult.Ok;
    }

    /// <summary>
    /// An action ended while its decision was open: the decision closes. Ended with an outcome (a person merged or closed
    /// the pull request on the code host) it is answered there, not through Feature Flow, and nothing else is raised (LOG 341).
    /// </summary>
    void CloseActionDecision(string stepId, string? outcome)
    {
        if (ActionDecisionOf(stepId) is not { } wait)
            return;
        if (outcome is not null)
            Trace("answered", wait.Id, $"on the code host, not through Feature Flow: {stepId} ended {outcome} before {ActionDecisionType} was answered");
        CloseWaitFor(wait);
    }
}
