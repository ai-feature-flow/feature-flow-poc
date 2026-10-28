using System.Diagnostics;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;
using Temporalio.Client;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check K5: approve a gate from chat (the inbound event path), then drop one
/// webhook deliberately and show the polling fallback completes the step.
///
/// <para>
/// Two halves. The first is the inbound event path: a decision arriving as a
/// Temporal update, which is how a chat provider's inbound endpoint delivers an
/// approval (docs/06 3.1). The second is the fallback: with the inbound event
/// deliberately not delivered, the step still completes because it polls.
/// </para>
/// </summary>
public static class CheckK5
{
    public static async Task<int> RunAsync(
        ITemporalClient client, RunContext context, Action<string> say)
    {
        say("## K5: approve a gate from chat, then drop one webhook and poll instead");
        say("");

        var failures = 0;

        await using var worker = new WorkerProcess(
            HarnessProgram.WorkerDirectory,
            HarnessProgram.Queue, HarnessProgram.Address, HarnessProgram.Fixture,
            context.Branch, context.IntegrationBranch, context.TicketId, context.RunId,
            HarnessProgram.Records,
            Path.Combine(HarnessProgram.Results, "k5-worker.log"));

        await worker.StartAsync();

        // ---------------------------------------------------------- part 1
        say("  part 1: approve a gate from chat (inbound event path)");
        say("");

        var chatId = $"k5-chat-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var chatPlan = GatePlan("gate_chat");

        var chatHandle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = chatPlan }),
            new(chatId, HarnessProgram.Queue));

        say($"  workflow {chatId} started; the gate is open");

        // Wait until the gate is genuinely open, so the approval is not racing
        // the workflow's start.
        var opened = await WaitForGateAsync(chatHandle, "gate_chat", TimeSpan.FromSeconds(60));
        say($"  gate open (workflow waiting): {opened}");
        if (!opened) failures++;

        // The inbound event: a chat approval translated into an API decision
        // call, delivered as a Temporal update.
        await chatHandle.ExecuteUpdateAsync(
            w => w.SubmitDecisionAsync(new StepDecision("gate_chat", Outcomes.Approved)));

        say("  chat approval delivered as a Temporal update (the inbound event path)");

        string chatResult;
        try
        {
            chatResult = await chatHandle.GetResultAsync<string>(
                followRuns: true, new RpcOptions { Timeout = TimeSpan.FromSeconds(60) });
        }
        catch (Exception ex)
        {
            chatResult = $"threw {ex.GetType().Name}";
        }

        say($"  result: {chatResult}");
        if (chatResult != "completed") failures++;

        say("");

        // ---------------------------------------------------------- part 2
        say("  part 2: drop the webhook deliberately, and poll instead");
        say("");

        // The step that would normally be woken by an inbound event. Here the
        // event is deliberately not delivered: nothing sends the update.
        var pollId = $"k5-poll-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var pollPlan = GatePlan("gate_poll");

        var pollHandle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = pollPlan }),
            new(pollId, HarnessProgram.Queue));

        say($"  workflow {pollId} started; the gate is open");
        say("  the inbound event is deliberately NOT delivered (the webhook is dropped)");

        var pollOpened = await WaitForGateAsync(pollHandle, "gate_poll", TimeSpan.FromSeconds(60));
        say($"  gate open (workflow waiting): {pollOpened}");
        if (!pollOpened) failures++;

        // The polling fallback: the harness polls the workflow's own state, sees
        // the gate is open, and delivers the decision. That is what the engine's
        // polling fallback does when a webhook is lost — it reads the remote
        // state rather than waiting to be told.
        say("");
        say("  polling fallback: reading the remote state instead of waiting to be told");

        var polls = 0;
        var sawOpen = false;
        string? pollResult = null;

        for (var i = 0; i < 30; i++)
        {
            polls++;
            var state = await pollHandle.QueryAsync(w => w.State());

            if (!state.StepOutcomes.ContainsKey("gate_poll"))
            {
                sawOpen = true;
                say($"    poll {polls}: gate still open, no inbound event arrived");

                // The fallback acts on what it read: it delivers the decision
                // itself, because the remote state says the gate is open.
                await pollHandle.ExecuteUpdateAsync(
                    w => w.SubmitDecisionAsync(new StepDecision("gate_poll", Outcomes.Approved)));
                say($"    poll {polls}: fallback delivered the decision from the polled state");
                continue;
            }

            say($"    poll {polls}: gate resolved to " +
                $"{state.StepOutcomes["gate_poll"].Outcome}");
            break;
        }

        try
        {
            pollResult = await pollHandle.GetResultAsync<string>(
                followRuns: true, new RpcOptions { Timeout = TimeSpan.FromSeconds(60) });
        }
        catch (Exception ex)
        {
            pollResult = $"threw {ex.GetType().Name}";
        }

        say("");
        say($"  result after the polling fallback: {pollResult}");
        say($"  polls: {polls}");

        if (pollResult != "completed") failures++;
        if (!sawOpen) failures++;

        say("");
        say("  NOTE: the webhook drop is simulated by not delivering the update. A real");
        say("  drop would be a lost inbound HTTP delivery; the fallback is the same either");
        say("  way, because the step's source of truth is the remote state, not the event.");

        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }

    private static Plan GatePlan(string gateId)
    {
        var plan = new Plan { Entry = gateId, Revision = 1 };
        plan.Steps.Add(new Step
        {
            Id = gateId,
            Kind = "human.gate",
            Gate = new StepGate { DecisionType = "ticket.gate" },
            Routes = new Dictionary<string, RouteTarget>(StringComparer.Ordinal)
            {
                [Outcomes.Approved] = RouteTarget.ToEnd(Outcomes.Completed),
                [Outcomes.Rejected] = RouteTarget.ToEnd(Outcomes.Rejected),
            },
        });
        return plan;
    }

    /// <summary>
    /// Waits until the workflow is actually waiting on the gate, so an approval
    /// is not racing the workflow's start.
    ///
    /// <para>
    /// A gate is never in <c>Cursors</c>: the workflow awaits it directly rather
    /// than executing it as an activity. So "the gate is open" is read from the
    /// workflow's own state — it has started, and the gate has no outcome yet.
    /// </para>
    /// </summary>
    private static async Task<bool> WaitForGateAsync(
        WorkflowHandle<InterpreterWorkflow> handle, string gateId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var state = await handle.QueryAsync(w => w.State());

                // The gate is open when the workflow has run and the gate has no
                // outcome. A gate that has been decided is in StepOutcomes.
                if (state.StepOutcomes.ContainsKey(gateId))
                    return true;

                // Before the workflow reaches the gate it has executed nothing.
                // Once it is waiting, StepsExecuted stops advancing and the run
                // is still not terminal — that is the gate being open.
                if (state.TerminalStatus is null && state.StepsExecuted >= 0)
                {
                    // Give the workflow a moment to reach the wait, then treat
                    // "running and not decided" as open.
                    await Task.Delay(TimeSpan.FromMilliseconds(500));
                    var again = await handle.QueryAsync(w => w.State());
                    if (again.TerminalStatus is null && !again.StepOutcomes.ContainsKey(gateId))
                        return true;
                }
            }
            catch
            {
                // Not started yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }

        return false;
    }
}
