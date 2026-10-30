using FeatureFlow.Spike.Contracts;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// Spike 3 additions to the interpreter (docs/17 section 7.1):
/// <list type="bullet">
/// <item>Dispatch on first completion: a branch that finishes frees its slot at
/// once. The Spike 1 loop awaited every in-flight step together, so a slow
/// branch held a free slot and a sibling waited on it (C7).</item>
/// <item>N1 <c>run.child</c> as a Temporal child workflow, id
/// <c>&lt;run id&gt;/&lt;step id&gt;</c> (docs/06 4.1).</item>
/// <item>N2 the merge lock: acquired before <c>scm.merge_base_forward</c>, held
/// only across the lock-held kinds, released when the cursor moves to any
/// other kind, after <c>scm.merge</c>, or when the run ends (docs/17 4.3).</item>
/// </list>
/// </summary>
public partial class InterpreterWorkflow
{
    public const string DispatchAnyPatch = "spike3-dispatch-any";

    public const string IncompleteStatus = "incomplete";

    /// <summary>docs/17 4.3: an engine rule on step kind, not plan content.</summary>
    public static readonly IReadOnlySet<string> LockHeldKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "scm.merge_base_forward", "test.run_tiers", "ci.wait_checks", "scm.merge",
    };

    private InterpreterInput runInput = new();

    private readonly Dictionary<string, Task<StepOutcome>> inflight = new(StringComparer.Ordinal);

    private bool lockHeld;

    private bool lockGranted;

    private async Task<string> RunDispatchAnyAsync(int threshold)
    {
        while (true)
        {
            if (inflight.Count == 0 && !lockHeld && ShouldContinueAsNew(threshold))
            {
                RecordSample();
                state.ContinueAsNewCount++;
                state.PruneCompleted();
                state.CarriedStateBytes = MeasureCarriedState();
                throw Workflow.CreateContinueAsNewException<InterpreterWorkflow>(
                    w => w.RunAsync(new InterpreterInput
                    {
                        Plan = state.Plan,
                        State = state,
                        HistoryEventThreshold = threshold,
                        ChildPlans = runInput.ChildPlans,
                        ChildQueues = runInput.ChildQueues,
                        MergeLock = runInput.MergeLock,
                        MergeLockQueue = runInput.MergeLockQueue,
                        ChildMergeLock = runInput.ChildMergeLock,
                        AgentRunCeiling = runInput.AgentRunCeiling,
                        PerRunCeiling = runInput.PerRunCeiling,
                    }));
            }

            var gates = new List<Step>();
            if (state.TerminalStatus is null)
            {
                foreach (var parallel in PlanReady.Ready(state).Where(s => s.Kind == "parallel").ToList())
                    state.StepOutcomes[parallel.Id] = new StepOutcomeRecord { Outcome = "started" };

                foreach (var step in PlanReady.Ready(state).Where(s => s.Gate is null && s.Kind != "parallel").ToList())
                {
                    if (!SlotFree(step))
                        continue;
                    state.Cursors.Add(step.Id);
                    inflight[step.Id] = ExecuteStepAsync(step);
                }

                gates = PlanReady.Ready(state).Where(s => s.Gate is not null).ToList();
            }

            if (inflight.Count == 0 && gates.Count == 0)
            {
                // Nothing in flight and nothing ready. Design requirement R6
                // (docs/08 1.3): a run that ends without reaching an end: route
                // must not report completed, so a dead end is "incomplete".
                await ReleaseLockAsync("run ended");
                RecordSample();
                return state.TerminalStatus ?? IncompleteStatus;
            }

            await Workflow.WaitConditionAsync(() =>
                inflight.Values.Any(t => t.IsCompleted) ||
                gates.Any(g => decisions.ContainsKey(g.Id)));

            var finished = new List<Step>();

            foreach (var gate in gates.Where(g => decisions.ContainsKey(g.Id)).ToList())
            {
                var decision = decisions[gate.Id];
                decisions.Remove(gate.Id);
                var outcome = decision == "approved" ? Outcomes.Approved : Outcomes.Rejected;
                CompleteStep(gate.Id, outcome);
                state.StepsExecuted++;
                Route(new List<Step> { gate }, new Dictionary<string, StepOutcome>(StringComparer.Ordinal)
                {
                    [gate.Id] = new StepOutcome(gate.Id, outcome, null),
                });
                finished.Add(gate);
            }

            foreach (var id in inflight.Keys.ToList())
            {
                var task = inflight[id];
                if (!task.IsCompleted)
                    continue;
                var result = await task;
                inflight.Remove(id);
                state.Cursors.Remove(id);
                var step = state.Plan.ById(id)!;
                Route(new List<Step> { step }, new Dictionary<string, StepOutcome>(StringComparer.Ordinal)
                {
                    [id] = result,
                });
                finished.Add(step);
            }

            SettleJoins();
            await UpdateLockAsync(finished);
        }
    }

    private bool SlotFree(Step step)
    {
        var parent = ParentParallel(step.Id);
        if (parent is null)
            return true;
        var parallel = ParallelInputs.FromInputs(parent.Inputs);
        var inFlight = parallel.Branches.Count(b => state.Cursors.Contains(b));
        return inFlight < parallel.MaxConcurrency;
    }

    private async Task<StepOutcome> ExecuteStepAsync(Step step)
    {
        if (step.Kind == "run.child")
            return await ExecuteChildAsync(step);

        // Organisation attempt ceilings, enforced on every execution including
        // loop re-entry. Spike 1 compared attempts with the allowance only after
        // a failed attempt, so a review -> fix loop whose steps keep completing
        // was unbounded; run-start check 22 only checks declared limits.
        var used = state.AttemptBudgets.TryGetValue(step.Id, out var u) ? u : 0;
        var total = state.AttemptBudgets.Values.Sum();
        if ((step.Kind == "agent.run" && runInput.AgentRunCeiling is { } perStep && used >= perStep) ||
            (runInput.PerRunCeiling is { } perRun && total >= perRun))
        {
            state.LastInfrastructureError =
                $"step {step.Id}: attempt ceiling reached (step {used}, run {total}); not executed";
            CompleteStep(step.Id, Outcomes.AttemptsExhausted);
            state.StepsExecuted++;
            return new StepOutcome(step.Id, Outcomes.AttemptsExhausted, null);
        }

        if (step.Kind == "scm.merge_base_forward" && runInput.MergeLock is not null && !lockHeld)
            await AcquireLockAsync();

        return await ExecuteOneAsync(step);
    }

    // ---------------------------------------------------------------- N1 run.child

    private async Task<StepOutcome> ExecuteChildAsync(Step step)
    {
        var planRef = StringInput(step, "plan_ref") ?? "";
        if (runInput.ChildPlans is null || !runInput.ChildPlans.TryGetValue(planRef, out var template))
        {
            CompleteStep(step.Id, Outcomes.Failed);
            state.StepsExecuted++;
            return new StepOutcome(step.Id, Outcomes.Failed, null);
        }

        var plan = template.Clone();
        foreach (var (name, value) in ChildParameters(step))
            plan.Parameters[name] = value;

        var queue = runInput.ChildQueues is not null && runInput.ChildQueues.TryGetValue(step.Id, out var q)
            ? q
            : Workflow.Info.TaskQueue;

        string outcome;
        try
        {
            var result = await Workflow.ExecuteChildWorkflowAsync(
                (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput
                {
                    Plan = plan,
                    MergeLock = runInput.ChildMergeLock,
                    MergeLockQueue = runInput.MergeLockQueue,
                    AgentRunCeiling = runInput.AgentRunCeiling,
                    PerRunCeiling = runInput.PerRunCeiling,
                }),
                new ChildWorkflowOptions
                {
                    Id = $"{Workflow.Info.WorkflowId}/{step.Id}",
                    TaskQueue = queue,
                });
            outcome = result == Outcomes.Completed ? Outcomes.Completed : Outcomes.Failed;
        }
        catch (ChildWorkflowFailureException)
        {
            outcome = Outcomes.Failed;
        }

        CompleteStep(step.Id, outcome);
        state.StepsExecuted++;
        return new StepOutcome(step.Id, outcome, null);
    }

    private static string? StringInput(Step step, string name)
    {
        if (!step.Inputs.TryGetValue(name, out var value) || value is null)
            return null;
        return value is System.Text.Json.JsonElement e && e.ValueKind == System.Text.Json.JsonValueKind.String
            ? e.GetString()
            : value.ToString();
    }

    private static IEnumerable<(string Name, string Value)> ChildParameters(Step step)
    {
        if (!step.Inputs.TryGetValue("parameters", out var value) || value is null)
            yield break;

        if (value is System.Text.Json.JsonElement e && e.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                if (p.Value.ValueKind == System.Text.Json.JsonValueKind.String)
                    yield return (p.Name, p.Value.GetString()!);
            }
        }
        else if (value is IDictionary<string, object?> map)
        {
            foreach (var (k, v) in map)
            {
                if (v is string s)
                    yield return (k, s);
            }
        }
    }

    // ---------------------------------------------------------------- N2 merge lock

    private async Task AcquireLockAsync()
    {
        lockGranted = false;
        await Workflow.ExecuteActivityAsync(
            (ILockActivities a) => a.RequestLockAsync(new LockRequest(
                runInput.MergeLock!, Workflow.Info.WorkflowId, runInput.MergeLockQueue ?? Workflow.Info.TaskQueue)),
            new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(1) });
        await Workflow.WaitConditionAsync(() => lockGranted);
        lockHeld = true;
    }

    private async Task UpdateLockAsync(List<Step> finished)
    {
        if (!lockHeld)
            return;

        if (finished.Any(s => s.Kind == "scm.merge"))
        {
            await ReleaseLockAsync("scm.merge returned");
            return;
        }

        if (state.TerminalStatus is not null)
        {
            await ReleaseLockAsync("run ended");
            return;
        }

        var next = PlanReady.Ready(state).Select(s => s.Kind)
            .Concat(inflight.Keys.Select(id => state.Plan.ById(id)?.Kind ?? ""))
            .ToList();
        var other = next.FirstOrDefault(k => !LockHeldKinds.Contains(k));
        if (other is not null)
            await ReleaseLockAsync($"cursor moved to {other}");
    }

    private async Task ReleaseLockAsync(string reason)
    {
        if (!lockHeld || runInput.MergeLock is null)
            return;
        lockHeld = false;
        lockGranted = false;
        try
        {
            await Workflow.GetExternalWorkflowHandle<LockWorkflow>(runInput.MergeLock)
                .SignalAsync(l => l.ReleaseAsync(new LockRelease(Workflow.Info.WorkflowId, reason)));
        }
        catch (TemporalException)
        {
            // The lock workflow is gone; nothing holds the lock.
        }
    }

    [WorkflowSignal]
    public Task LockGrantedAsync(string lockId)
    {
        if (runInput.MergeLock == lockId)
            lockGranted = true;
        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public bool HoldsMergeLock() => lockHeld;
}
