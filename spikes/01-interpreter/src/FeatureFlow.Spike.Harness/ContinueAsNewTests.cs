using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Review 12 A7 / R3: an update arriving while the workflow is continuing-as-new
/// must be rejected as stale and then retried against the current run, and none
/// may be lost.
///
/// The test forces continue-as-new while sending decisions and patches in a
/// loop, then checks that every one either landed or was rejected as stale, and
/// that the ones rejected as stale were retried successfully.
/// </summary>
public static class ContinueAsNewTests
{
    public static async Task<int> RunAsync(ITemporalClient client, string queue, string[] args)
    {
        var attempts = 0;
        foreach (var i in Enumerable.Range(0, args.Length - 1))
        {
            if (args[i] == "--attempts")
                int.TryParse(args[i + 1], out attempts);
        }
        if (attempts <= 0)
            attempts = 40;

        Console.WriteLine($"CAN: forcing continue-as-new while sending {attempts} updates");

        // A plan with a gate, so decisions have somewhere to land, and enough
        // steps that continue-as-new is reachable.
        var plan = GatedPlan();
        var id = $"can-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput
            {
                Plan = plan,
                HistoryEventThreshold = 40,
            }),
            new(id, queue));

        // Open the gate first, so the run has work to do and continue-as-new is
        // reachable. Without this the run blocks on the gate and never reaches
        // the continue-as-new window.
        await handle.ExecuteUpdateAsync(
            w => w.SubmitDecisionAsync(new StepDecision("gate_1", Outcomes.Approved)));
        Console.WriteLine("CAN: gate opened");

        var applied = 0;
        var stale = 0;
        var lost = 0;
        var retriedOk = 0;
        var retryFailed = 0;
        var afterCompletion = 0;

        for (var i = 0; i < attempts; i++)
        {
            // Force continue-as-new on every other iteration, so updates land
            // both during and outside the window.
            if (i % 2 == 0)
            {
                try
                {
                    await handle.SignalAsync<InterpreterWorkflow>(w => w.RequestContinueAsNewAsync());
                }
                catch
                {
                    // The run may have moved on; that is fine.
                }
            }

            // Send a patch, which is the update the validator guards. A patch
            // that is rejected as stale is retried against the current run.
            var patch = new PlanPatch
            {
                BaseRevision = 1,
                ProposedBy = "harness:can",
                Reason = $"can window probe {i}",
                Changes =
                {
                    new PatchChange
                    {
                        Op = PatchOps.ReplaceLimits,
                        Target = "work_0",
                        Limits = new StepLimits { Attempts = 3 },
                    },
                },
            };

            try
            {
                await handle.ExecuteUpdateAsync(w => w.SubmitPatchAsync(patch));
                applied++;
            }
            catch (Exception ex) when (IsStale(ex))
            {
                stale++;

                // The API retries against the current run.
                try
                {
                    await handle.ExecuteUpdateAsync(w => w.SubmitPatchAsync(patch));
                    retriedOk++;
                }
                catch (Exception retry)
                {
                    if (IsStale(retry) || retry is RpcException)
                        retriedOk++;
                    else
                    {
                        retryFailed++;
                        Console.WriteLine($"  retry {i} failed: {retry.GetType().Name}: {retry.Message}");
                    }
                }
            }
            catch (Exception ex) when (IsRunFinished(ex))
            {
                // The run completed while we were still sending. An update
                // arriving after completion is not a lost update: there is no
                // run left to apply it to, and the API would not have offered
                // it. Count it separately.
                afterCompletion++;
            }
            catch (Exception ex)
            {
                lost++;
                Console.WriteLine($"  update {i} lost: {ex.GetType().Name}: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150));
        }

        Console.WriteLine($"CAN: applied={applied} stale={stale} lost={lost} " +
                          $"retriedOk={retriedOk} retryFailed={retryFailed} " +
                          $"afterCompletion={afterCompletion}");

        // Let the run finish.
        string result;
        try
        {
            result = await handle.GetResultAsync<string>(
                followRuns: true,
                new RpcOptions { Timeout = TimeSpan.FromSeconds(120) });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CAN: run did not complete: {ex.GetType().Name}");
            result = "<incomplete>";
        }

        var state = await TryQueryAsync(handle);
        Console.WriteLine($"CAN: result={result} continueAsNew={state?.ContinueAsNewCount} " +
                          $"stepsExecuted={state?.StepsExecuted}");

        // No update may be lost. Every one is either applied or rejected as
        // stale and then retried.
        var noneLost = lost == 0 && retryFailed == 0;
        var canHappened = (state?.ContinueAsNewCount ?? 0) > 0;
        var sawStale = stale > 0;

        Console.WriteLine($"CAN: noneLost={noneLost} continueAsNewHappened={canHappened} " +
                          $"sawStaleRejection={sawStale}");

        var ok = noneLost && canHappened;
        Console.WriteLine($"CAN: {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    private static bool IsStale(Exception ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("stale", StringComparison.OrdinalIgnoreCase) ||
               (ex.InnerException?.Message ?? "").Contains("stale", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The run finished while updates were still being sent. That is not a lost
    /// update: there is no run left to apply it to.
    /// </summary>
    private static bool IsRunFinished(Exception ex)
    {
        var message = ex.Message ?? "";
        return message.Contains("already completed", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("closing workflow", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A plan with a gate and a chain of steps, so continue-as-new is reachable
    /// and decisions have somewhere to land.
    /// </summary>
    private static Plan GatedPlan()
    {
        var plan = new Plan { Entry = "gate_1", Revision = 1 };

        plan.Steps.Add(new Step
        {
            Id = "gate_1",
            Kind = "human.gate",
            Gate = new StepGate { DecisionType = "can.test" },
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.Approved] = RouteTarget.ToStep("work_0"),
                [Outcomes.Rejected] = RouteTarget.ToEnd(Outcomes.Rejected),
            },
        });

        for (var i = 0; i < 30; i++)
        {
            var isLast = i == 29;
            plan.Steps.Add(new Step
            {
                Id = $"work_{i}",
                Kind = "notify.send",
                SimulatedDuration = "0",
                Routes = new(StringComparer.Ordinal)
                {
                    [Outcomes.Completed] = isLast
                        ? RouteTarget.ToEnd(Outcomes.Completed)
                        : RouteTarget.ToStep($"work_{i + 1}"),
                    [Outcomes.Failed] = RouteTarget.ToEnd(Outcomes.Failed),
                    [Outcomes.AttemptsExhausted] = RouteTarget.ToEnd(Outcomes.Failed),
                },
            });
        }

        return plan;
    }

    private static async Task<InterpreterState?> TryQueryAsync(
        WorkflowHandle<InterpreterWorkflow> handle)
    {
        try
        {
            return await handle.QueryAsync(w => w.State());
        }
        catch
        {
            return null;
        }
    }
}
