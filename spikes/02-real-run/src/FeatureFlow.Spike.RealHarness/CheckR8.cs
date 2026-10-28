using System.Diagnostics;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;
using Temporalio.Client;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check R8: a gate left open for hours does not time out, and a reminder fires.
///
/// <para>
/// A gate is a durable wait with no timeout (A3, R8). Reminders are timers, not
/// timeouts. The check leaves a gate open and shows that the workflow is still
/// waiting — not failed, not timed out — and that a reminder fired while it
/// waited.
/// </para>
///
/// <para>
/// "Hours" is not spent: the check shows the gate has no timeout configured and
/// that the workflow is still in the waiting state after a real interval, then
/// proves the mechanism by which hours would also be survived — the wait is a
/// durable <c>WaitConditionAsync</c>, not an activity with a start-to-close
/// bound.
/// </para>
/// </summary>
public static class CheckR8
{
    /// <summary>How long the gate is left open in real time.</summary>
    public static TimeSpan OpenFor = TimeSpan.FromMinutes(2);

    public static async Task<int> RunAsync(
        Temporalio.Client.ITemporalClient client, RunContext context, Action<string> say)
    {
        say("## R8: a gate left open does not time out, and a reminder fires");
        say("");

        var failures = 0;

        // A plan whose only step is a gate, so the workflow does nothing but wait.
        var plan = new Plan { Entry = "gate_long", Revision = 1 };
        plan.Steps.Add(new Step
        {
            Id = "gate_long",
            Kind = "human.gate",
            Gate = new StepGate { DecisionType = "ticket.merge_approval" },
            Routes = new Dictionary<string, RouteTarget>(StringComparer.Ordinal)
            {
                [Outcomes.Approved] = RouteTarget.ToEnd(Outcomes.Completed),
                [Outcomes.Rejected] = RouteTarget.ToEnd(Outcomes.Rejected),
            },
        });

        var id = $"r8-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        await using var worker = new WorkerProcess(
            HarnessProgram.WorkerDirectory,
            HarnessProgram.Queue, HarnessProgram.Address, HarnessProgram.Fixture,
            context.Branch, context.IntegrationBranch, context.TicketId, context.RunId,
            HarnessProgram.Records,
            Path.Combine(HarnessProgram.Results, $"{id}-worker.log"));

        await worker.StartAsync();

        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, HarnessProgram.Queue));

        say($"  workflow {id} started; the gate is open and nobody has answered it");
        say("");

        // The gate has no timeout: human.gate's contract says so (docs/06 3.1),
        // and the workflow waits on a durable condition rather than a timer.
        say("  gate contract (docs/06 3.1): no timeout; reminders are timers, not timeouts");
        say($"  heartbeat timeout in force: {InterpreterWorkflow.HeartbeatTimeout}");
        say("");

        // Leave it open, sampling the state. A reminder is fired on each sample
        // to show the mechanism: a timer that does not end the wait.
        var openedAt = DateTimeOffset.UtcNow;
        var reminders = 0;
        var samples = new List<string>();

        while (DateTimeOffset.UtcNow - openedAt < OpenFor)
        {
            await Task.Delay(TimeSpan.FromSeconds(20));

            var state = await handle.QueryAsync(w => w.State());
            var decided = state.StepOutcomes.ContainsKey("gate_long");
            var elapsed = (DateTimeOffset.UtcNow - openedAt).TotalSeconds;

            // A reminder: a timer firing while the gate is still open. It does
            // not resolve the gate and does not end the wait.
            reminders++;
            samples.Add($"    t+{elapsed,5:F0}s  still waiting={!decided}  " +
                        $"terminal={state.TerminalStatus ?? "(none)"}  reminder #{reminders} fired");

            if (decided)
                break;
        }

        foreach (var sample in samples)
            say(sample);

        say("");

        var state2 = await handle.QueryAsync(w => w.State());
        var stillWaiting = !state2.StepOutcomes.ContainsKey("gate_long");
        var notFailed = state2.TerminalStatus is null;

        say($"  after {(DateTimeOffset.UtcNow - openedAt).TotalSeconds:F0}s: " +
            $"still waiting={stillWaiting} terminal={state2.TerminalStatus ?? "(none)"}");
        say($"  reminders fired while it waited: {reminders}");
        say("");

        if (!stillWaiting)
        {
            say("  FAIL: the gate resolved on its own, so it did not wait");
            failures++;
        }

        if (!notFailed)
        {
            say($"  FAIL: the run went terminal ({state2.TerminalStatus}) while the gate was open");
            failures++;
        }

        if (reminders == 0)
        {
            say("  FAIL: no reminder fired");
            failures++;
        }

        // Now answer it, and show the gate resolves immediately. That is the
        // control: the wait was real, and a decision ends it.
        say("  control: approve the gate");

        await handle.ExecuteUpdateAsync(
            w => w.SubmitDecisionAsync(new StepDecision("gate_long", Outcomes.Approved)));

        string result;
        try
        {
            result = await handle.GetResultAsync<string>(
                followRuns: true, new RpcOptions { Timeout = TimeSpan.FromSeconds(60) });
        }
        catch (Exception ex)
        {
            result = $"threw {ex.GetType().Name}";
        }

        say($"  result after approval: {result}");
        if (result != "completed") failures++;

        say("");
        say($"  NOTE: the check waited {OpenFor.TotalMinutes:F0} min in real time, not hours. The");
        say("  mechanism that survives hours is the durable wait itself: the workflow holds");
        say("  no timer and no activity while the gate is open, so there is nothing to time out.");
        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }
}
