using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model.Plans;
using Temporalio.Workflows;

namespace FeatureFlow.Engine;

/// <summary>Inbound wakes (<c>RunWorkflowNames.Wake</c>, <c>WakeSignal</c>): an event ends a wait's delay early; the wait reads again to confirm. M7, W3.</summary>
public partial class RunWorkflow
{
    /// <summary>Waits a wake has cancelled, by step, with the activity's token and what woke them, until the cancellation lands.</summary>
    readonly Dictionary<string, (CancellationTokenSource Token, string Reason)> woken = new(StringComparer.Ordinal);

    /// <summary>
    /// docs/02 section 2.3, events wake and reads confirm: a wait (<see cref="EngineActions.Waits"/>) sits between reads in
    /// its activity's retry delay, which only a new attempt ends. A wake cancels the wait's activity, as a hand-over does
    /// (LOG 139), and the core restarts the same execution at once (<c>StepInterrupted</c>): no attempt is used, the
    /// records the execution keeps carry over, and the restarted activity reads again. The event itself decides nothing,
    /// so a forged, wrong or repeated wake costs one read. A wake naming a step wakes only that step's wait; one naming
    /// none wakes every wait of the run (coordinator decision, M7). Ignored before the core is ready and while draining,
    /// when the waits are being interrupted anyway.
    /// </summary>
    [WorkflowSignal(RunWorkflowNames.Wake)]
    public Task WakeAsync(WakeSignal wake)
    {
        if (core is null || draining)
            return Task.CompletedTask;
        foreach (var (stepId, token) in stepTokens.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
        {
            if ((wake.StepId is not null && wake.StepId != stepId) || interrupted.Contains(stepId)
                || core.StepById(stepId) is not { Form: StepForm.Action, Action: { } action } || !EngineActions.Waits.Contains(action))
                continue;
            interrupted.Add(stepId);
            woken[stepId] = (token, $"woken by {wake.Source} {wake.Event}: it reads again now (events wake, reads confirm)");
            token.Cancel();
        }

        return Task.CompletedTask;
    }

    /// <summary>Why a step's activity was interrupted: a wake, if one cancelled this very activity; otherwise a hand-over (LOG 139).</summary>
    string InterruptionReason(string stepId) =>
        woken.Remove(stepId, out var wake) && wake.Token == stepTokens.GetValueOrDefault(stepId)
            ? wake.Reason
            : "interrupted to hand over to the next run; it resumes there (LOG 139)";
}
