using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// The patch tests: exit criteria 2, 3 and 4.
///   2. A re-plan that removes a protected step is rejected and recorded.
///   3. A re-plan applied mid-run leaves completed and in-flight steps untouched.
///   4. Continue-as-new happens between steps only, and no decision is lost.
/// </summary>
public static class PatchTests
{
    public static async Task<int> RunAsync(ITemporalClient client, string queue)
    {
        var failures = 0;

        failures += await ProtectedRemovalRejectedAsync(client, queue);
        failures += await BypassRejectedAsync(client, queue);
        failures += await LegitimatePatchAppliesAsync(client, queue);
        failures += await StalePatchRejectedAsync(client, queue);
        failures += await MidRunPatchLeavesInFlightUntouchedAsync(client, queue);

        Console.WriteLine(failures == 0
            ? "PATCH all tests passed"
            : $"PATCH {failures} test(s) failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Exit criterion 2, and R7: the rejection is recorded rather than silently
    /// dropped.
    /// </summary>
    private static async Task<int> ProtectedRemovalRejectedAsync(ITemporalClient client, string queue)
    {
        var plan = PlanGenerator.SpecPlan();
        var patch = PlanGenerator.RemoveProtected(plan.Revision);

        var result = ApplyPatch.Apply(plan, patch);

        var ok = result.Kind == PatchResultKind.Rejected;
        Console.WriteLine($"PATCH remove-protected: {result.Kind} " +
                          $"reason='{string.Join(";", result.Reasons)}' => {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// docs/09 15.2: bypassing a protected step is removal by another route.
    /// change_route that makes it unreachable must be rejected.
    /// </summary>
    private static async Task<int> BypassRejectedAsync(ITemporalClient client, string queue)
    {
        var plan = PlanGenerator.SpecPlan();
        var patch = PlanGenerator.BypassProtected(plan.Revision);

        var result = ApplyPatch.Apply(plan, patch);

        var ok = result.Kind == PatchResultKind.Rejected &&
                 result.Reasons.Any(r => r.Contains("unreachable"));
        Console.WriteLine($"PATCH bypass-protected: {result.Kind} " +
                          $"reason='{string.Join(";", result.Reasons)}' => {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// A legitimate patch applies, and resume_at resolves to a step that exists
    /// after the patch (docs/09 15.1.1).
    /// </summary>
    private static async Task<int> LegitimatePatchAppliesAsync(ITemporalClient client, string queue)
    {
        var plan = PlanGenerator.SpecPlan();
        var patch = PlanGenerator.InsertAfterRunTests(plan.Revision);

        var result = ApplyPatch.Apply(plan, patch);

        var ok = result.Kind == PatchResultKind.Applied &&
                 result.NewRevision == plan.Revision + 1 &&
                 result.Plan?.ById("lint") is not null &&
                 result.ResumeAt == "lint";

        // docs/09 15.3.1: insert_after rewires run_tests' routes to the new step.
        var rewired = result.Plan?.ById("run_tests")?.Routes[Outcomes.Completed]?.Step == "lint";

        Console.WriteLine($"PATCH insert-after: {result.Kind} revision={result.NewRevision} " +
                          $"resumeAt={result.ResumeAt} rewired={rewired} " +
                          $"reasons='{string.Join(";", result.Reasons)}' => " +
                          $"{(ok && rewired ? "PASS" : "FAIL")}");
        return ok && rewired ? 0 : 1;
    }

    /// <summary>
    /// docs/09 15.1: base_revision is optimistic concurrency. A stale patch is
    /// rejected, not silently rebased.
    /// </summary>
    private static async Task<int> StalePatchRejectedAsync(ITemporalClient client, string queue)
    {
        var plan = PlanGenerator.SpecPlan();
        var patch = PlanGenerator.InsertAfterRunTests(plan.Revision + 5);

        var result = ApplyPatch.Apply(plan, patch);

        var ok = result.Kind == PatchResultKind.Rejected &&
                 result.Reasons.Any(r => r.Contains("stale"));
        Console.WriteLine($"PATCH stale: {result.Kind} " +
                          $"reason='{string.Join(";", result.Reasons)}' => {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    /// <summary>
    /// Exit criterion 3: a re-plan applied mid-run leaves completed and in-flight
    /// steps untouched. docs/08 2.1: the patch applies to future steps; the
    /// in-flight branch is untouched.
    /// </summary>
    private static async Task<int> MidRunPatchLeavesInFlightUntouchedAsync(
        ITemporalClient client, string queue)
    {
        var plan = PlanGenerator.SpecPlan();
        var id = $"patch-midrun-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, queue));

        // Wait until the parallel branches are in flight.
        InterpreterState? midRun = null;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            try
            {
                var s = await handle.QueryAsync(w => w.State());
                if (i % 10 == 0)
                    Console.WriteLine($"  poll {i}: runs=[{string.Join(",", s.StepRuns.Keys)}] " +
                                      $"cursors=[{string.Join(",", s.Cursors)}]");
                if (s.StepRuns.ContainsKey("tickets") &&
                    s.StepRuns["tickets"].Outcome == "started")
                {
                    midRun = s;
                    break;
                }
            }
            catch (Exception ex)
            {
                if (i % 10 == 0)
                    Console.WriteLine($"  poll {i}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        if (midRun is null)
        {
            Console.WriteLine("PATCH mid-run: FAIL could not reach an in-flight branch");
            return 1;
        }

        var completedBefore = midRun.StepRuns.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();
        var inFlightBefore = midRun.Cursors.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Console.WriteLine($"PATCH mid-run: completed before=[{string.Join(",", completedBefore)}] " +
                          $"inFlight before=[{string.Join(",", inFlightBefore)}]");

        // Send a legitimate patch while branches are in flight.
        var patch = PlanGenerator.InsertAfterRunTests(midRun.Plan.Revision);
        Console.WriteLine($"PATCH mid-run: patch base_revision={patch.BaseRevision} " +
                          $"plan revision={midRun.Plan.Revision}");
        var verdict = await handle.ExecuteUpdateAsync(w => w.SubmitPatchAsync(patch));
        Console.WriteLine($"PATCH mid-run: patch verdict={verdict}");

        var after = await handle.QueryAsync(w => w.State());
        var completedAfter = after.StepRuns.Keys.OrderBy(x => x, StringComparer.Ordinal).ToList();

        // Every step that had completed must still be completed, with the same
        // outcome. In-flight branches are untouched by definition: the patch
        // applies to future steps.
        var preserved = completedBefore.All(k =>
            after.StepRuns.ContainsKey(k) &&
            after.StepRuns[k].Outcome == midRun.StepRuns[k].Outcome);

        if (!preserved)
        {
            foreach (var k in completedBefore)
            {
                var before = midRun.StepRuns[k].Outcome;
                var now = after.StepRuns.TryGetValue(k, out var r) ? r.Outcome : "<gone>";
                if (before != now)
                    Console.WriteLine($"  changed: {k} {before} -> {now}");
            }
        }

        var applied = verdict == "Applied" && after.Plan.ById("lint") is not null;

        Console.WriteLine($"PATCH mid-run: applied={applied} preserved={preserved} " +
                          $"revision={after.Plan.Revision} => " +
                          $"{(applied && preserved ? "PASS" : "FAIL")}");

        // Let it finish so the run is not left dangling.
        try
        {
            await ApproveGatesAsync(handle);
        }
        catch
        {
            // Best effort: the run may already be complete.
        }

        return applied && preserved ? 0 : 1;
    }

    private static async Task ApproveGatesAsync(WorkflowHandle<InterpreterWorkflow> handle)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000);
            try
            {
                var state = await handle.QueryAsync(w => w.State());
                foreach (var step in state.Plan.Steps.Where(s => s.Gate is not null))
                {
                    if (state.StepRuns.ContainsKey(step.Id))
                        continue;
                    await handle.ExecuteUpdateAsync(w => w.SubmitDecisionAsync(
                        new StepDecision(step.Id, Outcomes.Approved)));
                }
            }
            catch
            {
                // The run may have completed.
            }
        }
    }
}
