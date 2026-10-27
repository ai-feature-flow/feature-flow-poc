using FeatureFlow.Spike.Contracts;
using Temporalio.Workflows;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// The interpreter. One workflow type for every run; differences come from the
/// plan, not from code. docs/01 3.1.
/// </summary>
[Workflow]
public class InterpreterWorkflow
{
    /// <summary>Continue-as-new threshold. Set from measurement 5.</summary>
    public const int DefaultHistoryEventThreshold = 2000;

    private InterpreterState state = new();

    /// <summary>A decision arriving from the API. docs/01 3.1: durable wait, no timeout.</summary>
    private readonly Dictionary<string, string> decisions = new(StringComparer.Ordinal);

    /// <summary>A patch arriving from the API (human steering) or a child run.</summary>
    private readonly Queue<PlanPatch> pendingPatches = new();

    private bool continueAsNewRequested;

    [WorkflowRun]
    public async Task<string> RunAsync(InterpreterInput input)
    {
        state = input.State ?? new InterpreterState { Plan = input.Plan };
        state.Plan ??= input.Plan;

        var threshold = input.HistoryEventThreshold ?? DefaultHistoryEventThreshold;

        while (true)
        {
            // docs/01 3.1: continue-as-new only at a safe point — between steps,
            // never mid-step, and never with a pending decision (R3).
            if (ShouldContinueAsNew(threshold))
            {
                state.HistorySamples.Add(new HistorySample
                {
                    ContinueAsNewIndex = state.ContinueAsNewCount,
                    HistoryLengthBytes = Workflow.CurrentHistorySize,
                    HistoryEventCount = Workflow.CurrentHistoryLength,
                    StepsExecuted = state.StepsExecuted,
                });
                state.ContinueAsNewCount++;
                throw Workflow.CreateContinueAsNewException<InterpreterWorkflow>(
                    w => w.RunAsync(new InterpreterInput
                    {
                        Plan = state.Plan,
                        State = state,
                        HistoryEventThreshold = threshold,
                    }));
            }

            var ready = PlanReady.Ready(state);
            if (ready.Count == 0)
            {
                // Record the final history size. Measurement 1.
                state.HistorySamples.Add(new HistorySample
                {
                    ContinueAsNewIndex = state.ContinueAsNewCount,
                    HistoryLengthBytes = Workflow.CurrentHistorySize,
                    HistoryEventCount = Workflow.CurrentHistoryLength,
                    StepsExecuted = state.StepsExecuted,
                });
                return state.TerminalStatus ?? Outcomes.Completed;
            }

            // Gates: a durable wait with no timeout. docs/01 3.1. A gate is
            // resolved here and then routed like any other step.
            var gated = ready.Where(s => s.Gate is not null).ToList();
            foreach (var step in gated)
            {
                await Workflow.WaitConditionAsync(() => decisions.ContainsKey(step.Id));
                var decision = decisions[step.Id];
                decisions.Remove(step.Id);
                state.StepRuns[step.Id] = new StepRun
                {
                    StepId = step.Id,
                    Outcome = decision == "approved" ? Outcomes.Approved : Outcomes.Rejected,
                };
                state.StepsExecuted++;
            }

            if (gated.Count > 0)
            {
                Route(gated, gated.ToDictionary(
                    s => s.Id,
                    s => new StepOutcome(s.Id, state.StepRuns[s.Id].Outcome, null),
                    StringComparer.Ordinal));
                if (state.TerminalStatus is not null)
                    continue;
            }

            // A parallel step is structural: it does not execute. Mark it
            // started so its branches become ready, then let the join settle it
            // (docs/01 3.3.1).
            foreach (var parallel in PlanReady.Ready(state).Where(s => s.Kind == "parallel").ToList())
            {
                state.StepRuns[parallel.Id] = new StepRun
                {
                    StepId = parallel.Id,
                    Outcome = "started",
                };
            }

            var executable = PlanReady.Ready(state)
                .Where(s => s.Gate is null && s.Kind != "parallel")
                .ToList();
            if (executable.Count == 0)
                continue;

            // docs/09 7.0: a branch reports its terminal outcome to the join.
            // max_concurrency bounds how many branches are in flight; it is a
            // plan value, not an interpreter one (docs/01 3.3.1).
            executable = ApplyConcurrencyLimit(executable);

            var results = await ExecuteAsync(executable);
            Route(executable, results);
            SettleJoins();
        }
    }

    private List<Step> ApplyConcurrencyLimit(List<Step> executable)
    {
        var limited = new List<Step>();
        foreach (var step in executable)
        {
            var parent = ParentParallel(step.Id);
            if (parent is null)
            {
                limited.Add(step);
                continue;
            }

            var parallel = ParallelInputs.FromInputs(parent.Inputs);
            var siblingsInFlight = parallel.Branches.Count(b => state.Cursors.Contains(b));
            if (siblingsInFlight >= parallel.MaxConcurrency)
                continue;
            limited.Add(step);
        }
        return limited;
    }

    private Step? ParentParallel(string branchId) =>
        state.Plan.Steps.FirstOrDefault(s =>
            s.Kind == "parallel" &&
            ParallelInputs.FromInputs(s.Inputs).Branches.Contains(branchId));

    /// <summary>
    /// docs/01 3.3.1: the join is satisfied when the policy is met. all waits
    /// for every branch; any completes when the first branch reaches a terminal
    /// outcome; n completes when n branches succeed.
    /// </summary>
    private void SettleJoins()
    {
        foreach (var parallel in state.Plan.Steps.Where(s => s.Kind == "parallel").ToList())
        {
            if (!state.StepRuns.TryGetValue(parallel.Id, out var parallelRun))
                continue;
            // "started" means the branches are in flight; the join has not
            // settled yet.
            if (parallelRun.Outcome != "started")
                continue;

            var inputs = ParallelInputs.FromInputs(parallel.Inputs);
            var branches = inputs.Branches.Where(b => state.Plan.ById(b) is not null).ToList();
            if (branches.Count == 0)
                continue;

            var done = branches.Where(b => state.StepRuns.ContainsKey(b)).ToList();
            var succeeded = done.Count(b => state.StepRuns[b].Outcome == Outcomes.Completed);
            var failed = done.Count(b =>
                state.StepRuns[b].Outcome == Outcomes.Failed ||
                state.StepRuns[b].Outcome == Outcomes.AttemptsExhausted);

            var satisfied = inputs.Join switch
            {
                "any" => done.Count >= 1,
                "n" => succeeded >= (inputs.JoinCount ?? branches.Count),
                _ => done.Count == branches.Count,
            };

            if (!satisfied)
                continue;

            var outcome = failed > 0 && inputs.Join != "any"
                ? Outcomes.AnyFailed
                : Outcomes.Completed;

            state.StepRuns[parallel.Id] = new StepRun
            {
                StepId = parallel.Id,
                Outcome = outcome,
            };
            state.StepsExecuted++;

            Route(new List<Step> { parallel }, new Dictionary<string, StepOutcome>(StringComparer.Ordinal)
            {
                [parallel.Id] = new StepOutcome(parallel.Id, outcome, null),
            });
        }
    }

    private bool ShouldContinueAsNew(int threshold)
    {
        if (continueAsNewRequested)
            return true;
        // Never mid-step, and never with a pending decision (R3).
        if (state.Cursors.Count > 0)
            return false;
        if (decisions.Count > 0)
            return false;
        return Workflow.CurrentHistoryLength >= threshold;
    }

    private async Task<Dictionary<string, StepOutcome>> ExecuteAsync(List<Step> ready)
    {
        var tasks = new Dictionary<string, Task<StepOutcome>>(StringComparer.Ordinal);
        foreach (var step in ready)
        {
            // A cursor means "in flight". docs/01 3.3.1: one cursor per
            // in-flight branch. It is cleared when the step's result lands.
            state.Cursors.Add(step.Id);
            tasks[step.Id] = ExecuteOneAsync(step);
        }

        await Task.WhenAll(tasks.Values);

        var results = new Dictionary<string, StepOutcome>(StringComparer.Ordinal);
        foreach (var (id, task) in tasks)
        {
            results[id] = await task;
            state.Cursors.Remove(id);
        }
        return results;
    }

    private async Task<StepOutcome> ExecuteOneAsync(Step step)
    {
        var attempts = state.AttemptBudgets.TryGetValue(step.Id, out var used) ? used : 0;
        var allowance = step.Limits?.Attempts ?? 3;
        var run = state.StepRuns.TryGetValue(step.Id, out var existing)
            ? existing
            : new StepRun { StepId = step.Id };

        string? sessionRef = run.Attempts_.LastOrDefault()?.SessionRef;
        var lastCommit = run.Attempts_.LastOrDefault()?.LastCommit;
        var lastTurns = run.Attempts_.LastOrDefault()?.SessionTurns ?? 0;
        var infrastructureFailures = 0;

        while (true)
        {
            var resumed = attempts > 0;
            var progress = new AttemptProgress(
                attempts + 1, resumed, lastCommit, lastTurns, Workflow.UtcNow);

            AgentRunResult result;
            try
            {
                result = step.Kind == "agent.run"
                    ? await Workflow.ExecuteActivityAsync(
                        (ISpikeActivities a) => a.RunAgentAsync(new AgentRunRequest(
                            step.Id,
                            $"sbx-{step.Id}",
                            sessionRef,
                            ParseSeconds(step.SimulatedDuration, 5),
                            true,
                            2,
                            attempts + 1,
                            resumed)),
                        ActivityOptions(step))
                    : await Workflow.ExecuteActivityAsync(
                        (ISpikeActivities a) => a.RunGenericStepAsync(new GenericStepRequest(
                            step.Id, step.Kind, ParseSeconds(step.SimulatedDuration, 1))),
                        ActivityOptions(step));
            }
            catch (Exception ex)
            {
                // A heartbeat timeout is how a worker crash surfaces: the worker
                // stopped heartbeating, so the activity times out and the retry
                // re-attaches by handle (docs/08 2.1, H2). It is infrastructure,
                // so it does not consume an attempt — but it IS a resume, and
                // docs/01 3.7 makes a resume free only with progress. The last
                // heartbeat carries the progress evidence (docs/01 3.6).
                var heartbeat = LastHeartbeat(ex);
                if (heartbeat is not null)
                {
                    run.Resumes++;
                    run.InfrastructureFailures++;
                    state.LastInfrastructureError =
                        $"step {step.Id} heartbeat timeout after {heartbeat.SessionTurns} turns " +
                        $"commit={heartbeat.LastCommit} handle={heartbeat.CommandHandle}";

                    sessionRef = heartbeat.SessionRef ?? sessionRef;
                    run.CommandHandle = heartbeat.CommandHandle ?? run.CommandHandle;
                    run.SandboxKey = heartbeat.SandboxKey ?? run.SandboxKey;
                    run.SessionRef = sessionRef;

                    // Measurement 6: a restart would have produced a different
                    // handle or key. Same handle and key means it resumed.
                    if (run.CommandHandle is not null && heartbeat.CommandHandle is not null &&
                        !string.Equals(run.CommandHandle, heartbeat.CommandHandle, StringComparison.Ordinal))
                    {
                        run.RestartedWhenShouldHaveResumed = true;
                    }

                    var progressed =
                        (heartbeat.LastCommit is not null && heartbeat.LastCommit != lastCommit) ||
                        heartbeat.SessionTurns > lastTurns;

                    if (progressed)
                    {
                        state.ResumesWithoutProgress[step.Id] = 0;
                        lastCommit = heartbeat.LastCommit ?? lastCommit;
                        lastTurns = heartbeat.SessionTurns;
                    }
                    else
                    {
                        var without = state.ResumesWithoutProgress.TryGetValue(step.Id, out var w) ? w : 0;
                        without++;
                        state.ResumesWithoutProgress[step.Id] = without;
                        if (without >= 3)
                        {
                            // docs/01 3.7: after 3 consecutive resumes with no
                            // progress, the next counts as a restart.
                            attempts++;
                            state.ResumesWithoutProgress[step.Id] = 0;
                            // Persist it: the workflow may continue-as-new or
                            // replay before the step finishes, and an attempt
                            // counted only in a local would be lost.
                            state.AttemptBudgets[step.Id] = attempts;
                            run.Attempts = attempts;
                            state.StepRuns[step.Id] = run;
                            state.LastInfrastructureError +=
                                $" [counted as restart: 3 resumes without progress, attempts={attempts}]";
                        }
                    }

                    if (attempts >= allowance)
                    {
                        run.Outcome = Outcomes.AttemptsExhausted;
                        run.Attempts = attempts;
                        state.AttemptBudgets[step.Id] = attempts;
                        state.StepRuns[step.Id] = run;
                        state.StepsExecuted++;
                        return new StepOutcome(step.Id, Outcomes.AttemptsExhausted, null);
                    }

                    await Workflow.DelayAsync(TimeSpan.FromSeconds(1));
                    continue;
                }

                // Infrastructure: the platform or the sandbox went away. Free
                // retry, no attempt consumed (docs/01 3.7). Bounded, because an
                // unbounded synchronous retry loop deadlocks the workflow task.
                run.Resumes++;
                run.InfrastructureFailures++;
                infrastructureFailures++;
                state.LastInfrastructureError =
                    $"step {step.Id} failure {infrastructureFailures}: {ex.GetType().Name}: {ex.Message}";
                if (infrastructureFailures >= 50)
                    throw;
                await Workflow.DelayAsync(TimeSpan.FromSeconds(1));
                continue;
            }

            progress = progress with { EndedAt = Workflow.UtcNow };
            run.Attempts_.Add(progress);

            if (result.Outcome == "infrastructure")
            {
                run.Resumes++;
                continue;
            }

            // docs/01 3.7: progress is a new commit, or the session having grown.
            var madeProgress =
                (result.LastCommit is not null && result.LastCommit != lastCommit) ||
                result.SessionTurns > lastTurns;

            if (madeProgress)
            {
                state.ResumesWithoutProgress[step.Id] = 0;
            }
            else if (resumed)
            {
                var without = state.ResumesWithoutProgress.TryGetValue(step.Id, out var w) ? w : 0;
                without++;
                state.ResumesWithoutProgress[step.Id] = without;
                if (without >= 3)
                {
                    // The next one counts as a restart, not a resume.
                    attempts++;
                    state.ResumesWithoutProgress[step.Id] = 0;
                }
            }

            sessionRef = result.SessionRef ?? sessionRef;
            lastCommit = result.LastCommit ?? lastCommit;
            lastTurns = result.SessionTurns;

            // Resume evidence for the harness (docs/01 3.6).
            run.CommandHandle = result.CommandHandle ?? run.CommandHandle;
            run.SandboxKey = result.SandboxKey ?? run.SandboxKey;
            run.SessionRef = sessionRef;
            run.InfrastructureFailures += result.InfrastructureRetries;

            if (result.Outcome == Outcomes.Completed || result.Outcome == Outcomes.Failed ||
                result.Outcome == Outcomes.Blocked || result.Outcome == Outcomes.Approved ||
                result.Outcome == Outcomes.Rejected || result.Outcome == Outcomes.FindingsOpen ||
                result.Outcome == Outcomes.Ready || result.Outcome == Outcomes.Partial)
            {
                run.Outcome = result.Outcome;
                run.Attempts = attempts + 1;
                state.AttemptBudgets[step.Id] = run.Attempts;
                state.StepRuns[step.Id] = run;
                state.StepsExecuted++;
                return new StepOutcome(step.Id, result.Outcome, result.PatchRef);
            }

            attempts++;
            if (attempts >= allowance)
            {
                run.Outcome = Outcomes.AttemptsExhausted;
                run.Attempts = attempts;
                state.AttemptBudgets[step.Id] = attempts;
                state.StepRuns[step.Id] = run;
                state.StepsExecuted++;
                return new StepOutcome(step.Id, Outcomes.AttemptsExhausted, null);
            }
        }
    }

    /// <summary>
    /// Read the last heartbeat off a heartbeat timeout. docs/01 3.6: heartbeats
    /// carry progress, so a stalled agent is distinguishable from a slow one.
    /// Returns null for any other failure, which is not a resume.
    /// </summary>
    private static AgentProgress? LastHeartbeat(Exception ex)
    {
        // A heartbeat timeout reaches the workflow wrapped in an
        // ActivityFailureException, so unwrap before matching on the type.
        var candidate = ex;
        while (candidate is not null)
        {
            if (candidate is Temporalio.Exceptions.TimeoutFailureException timeout &&
                timeout.TimeoutType == Temporalio.Api.Enums.V1.TimeoutType.Heartbeat)
            {
                var details = timeout.LastHeartbeatDetails;
                if (details is not null && details.Count > 0)
                {
                    try
                    {
                        return details.ElementAt<AgentProgress>(0);
                    }
                    catch (Exception)
                    {
                        return null;
                    }
                }
                return null;
            }
            candidate = candidate.InnerException;
        }
        return null;
    }

    private static ActivityOptions ActivityOptions(Step step) => new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(ParseSeconds(step.SimulatedDuration, 5) + 120),
        HeartbeatTimeout = TimeSpan.FromSeconds(30),
        RetryPolicy = new Temporalio.Common.RetryPolicy
        {
            // The interpreter owns the attempt budget (docs/01 3.7), so Temporal
            // must not retry on its own: one activity attempt per interpreter
            // attempt, otherwise the workflow never sees the failure and the
            // progress rule never runs.
            MaximumAttempts = 1,
            InitialInterval = TimeSpan.FromSeconds(1),
            BackoffCoefficient = 1.5f,
            MaximumInterval = TimeSpan.FromSeconds(10),
        },
    };

    private static int ParseSeconds(string? iso, int fallback)
    {
        if (string.IsNullOrEmpty(iso))
            return fallback;
        if (int.TryParse(iso, out var plain))
            return plain;
        try
        {
            return (int)System.Xml.XmlConvert.ToTimeSpan(iso).TotalSeconds;
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>
    /// Advance the plan. docs/01 3.1: cursors advance, open or satisfy joins.
    /// A step that has run is in StepRuns, which is what makes the next step
    /// ready — cursors are only for in-flight work.
    /// </summary>
    private void Route(List<Step> ready, Dictionary<string, StepOutcome> results)
    {
        foreach (var step in ready)
        {
            if (!results.TryGetValue(step.Id, out var outcome))
                continue;
            var target = PlanReady.ResolveRoute(step, outcome.Outcome, state);
            if (target is null)
                continue;
            if (target.End is not null)
            {
                state.TerminalStatus = target.End;
                return;
            }
        }
    }

    /// <summary>
    /// Human steering. docs/01 3.4: a Temporal update via the API.
    /// </summary>
    [WorkflowUpdate]
    public Task<string> SubmitPatchAsync(PlanPatch patch)
    {
        var result = ApplyPatch.Apply(state.Plan, patch);
        state.PatchRecords.Add(new PatchRecord
        {
            BaseRevision = patch.BaseRevision,
            NewRevision = result.Kind == PatchResultKind.Applied ? result.NewRevision : null,
            Result = result.Kind.ToString(),
            Reason = result.Reasons.Count > 0 ? string.Join("; ", result.Reasons) : null,
            ProposedBy = patch.ProposedBy,
            RecordedAt = Workflow.UtcNow,
        });

        if (result.Kind == PatchResultKind.Applied && result.Plan is not null)
        {
            state.Plan = result.Plan;
            if (result.ResumeAt is not null)
                state.Cursors.Add(result.ResumeAt);
        }

        return Task.FromResult(result.Kind.ToString());
    }

    /// <summary>
    /// R3: a patch arriving while the workflow is continuing-as-new must be
    /// rejected as stale, so the API retries against the current run.
    /// </summary>
    [WorkflowUpdateValidator(nameof(SubmitPatchAsync))]
    public void ValidateSubmitPatch(PlanPatch patch)
    {
        if (continueAsNewRequested)
            throw new ApplicationException("stale: workflow is continuing-as-new");
    }

    [WorkflowUpdate]
    public Task SubmitDecisionAsync(StepDecision decision)
    {
        decisions[decision.StepId] = decision.Decision;
        return Task.CompletedTask;
    }

    [WorkflowUpdateValidator(nameof(SubmitDecisionAsync))]
    public void ValidateSubmitDecision(StepDecision decision)
    {
        if (continueAsNewRequested)
            throw new ApplicationException("stale: workflow is continuing-as-new");
    }

    [WorkflowSignal]
    public Task RequestContinueAsNewAsync() => Task.FromResult(continueAsNewRequested = true);

    [WorkflowQuery]
    public InterpreterState State() => state;

    [WorkflowQuery]
    public List<HistorySample> HistorySamples() => state.HistorySamples;

    [WorkflowQuery]
    public int HistoryEventCount() => Workflow.CurrentHistoryLength;

    [WorkflowQuery]
    public long HistorySizeBytes() => Workflow.CurrentHistorySize;
}

public sealed record StepOutcome(string StepId, string Outcome, string? PatchRef);

public sealed record StepDecision(string StepId, string Decision);

public sealed class InterpreterInput
{
    public Plan Plan { get; set; } = new();

    public InterpreterState? State { get; set; }

    public int? HistoryEventThreshold { get; set; }
}
