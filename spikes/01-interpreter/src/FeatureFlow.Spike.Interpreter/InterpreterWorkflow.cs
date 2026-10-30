using FeatureFlow.Spike.Contracts;
using Temporalio.Workflows;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// The interpreter. One workflow type for every run; differences come from the
/// plan, not from code. docs/01 3.1.
/// </summary>
[Workflow]
public partial class InterpreterWorkflow
{
    /// <summary>
    /// Continue-as-new threshold. Review 12 A2: derived from Temporal's limits
    /// with a margin, not from where a test happened to trigger.
    ///
    /// Temporal warns at 10,240 events or 10 MB and hard-fails at 51,200 events
    /// or 50 MB. Measured cost is 6.28 events per step, so the warning threshold
    /// is reached at about 1,630 steps. Half of that is 5,120 events, about 815
    /// steps — comfortably clear of the warning, and few enough continue-as-new
    /// points that the R3 window is not opened often.
    /// </summary>
    public const int DefaultHistoryEventThreshold = 5120;

    private InterpreterState state = new();

    /// <summary>A decision arriving from the API. docs/01 3.1: durable wait, no timeout.</summary>
    private readonly Dictionary<string, string> decisions = new(StringComparer.Ordinal);

    /// <summary>A patch arriving from the API (human steering) or a child run.</summary>
    private readonly Queue<PlanPatch> pendingPatches = new();

    private bool continueAsNewRequested;

    /// <summary>
    /// Review 13 A1: a barrier. When the input asks for it, the workflow waits
    /// for a release signal before executing anything, so a harness can start
    /// every run first and then release them all together. Without this, runs
    /// start staggered and their steps never actually overlap, which is what
    /// invalidated the first slot measurement.
    /// </summary>
    private bool released;

    /// <summary>
    /// Per-step detail, kept out of carried state. Review 12 A1: this is what
    /// goes to the record store. It is rebuilt from the record store on replay,
    /// so it must never be read to make a routing decision.
    /// </summary>
    private readonly Dictionary<string, StepRunDetail> stepDetails =
        new(StringComparer.Ordinal);

    [WorkflowRun]
    public async Task<string> RunAsync(InterpreterInput input)
    {
        state = input.State ?? new InterpreterState { Plan = input.Plan };
        state.Plan ??= input.Plan;

        var threshold = input.HistoryEventThreshold ?? DefaultHistoryEventThreshold;

        // Review 13 A1: hold every step until the harness releases them all.
        if (input.WaitForRelease && !released)
        {
            await Workflow.WaitConditionAsync(() => released);
        }

        // Spike 3: dispatch on first completion. Versioned so Spike 1's recorded
        // histories (batch-and-wait-all) still replay.
        if (Workflow.Patched(DispatchAnyPatch))
        {
            runInput = input;
            return await RunDispatchAnyAsync(threshold);
        }

        while (true)
        {
            // docs/01 3.1: continue-as-new only at a safe point — between steps,
            // never mid-step, and never with a pending decision (R3).
            if (ShouldContinueAsNew(threshold))
            {
                RecordSample();
                state.ContinueAsNewCount++;

                // Review 12 A1: carried state must be bounded by plan size, not
                // by steps executed. Drop per-step bookkeeping for steps that
                // have finished before carrying the state forward.
                state.PruneCompleted();
                state.CarriedStateBytes = MeasureCarriedState();

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
                RecordSample();
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
                var outcome = decision == "approved" ? Outcomes.Approved : Outcomes.Rejected;
                CompleteStep(step.Id, outcome);
                state.StepsExecuted++;
            }

            if (gated.Count > 0)
            {
                Route(gated, gated.ToDictionary(
                    s => s.Id,
                    s => new StepOutcome(s.Id, state.StepOutcomes[s.Id].Outcome, null),
                    StringComparer.Ordinal));
                if (state.TerminalStatus is not null)
                {
                    // Yield before looping, so the workflow task is not held by
                    // a synchronous loop.
                    await Workflow.DelayAsync(TimeSpan.FromMilliseconds(200));
                    continue;
                }
            }

            // A parallel step is structural: it does not execute. Mark it
            // started so its branches become ready, then let the join settle it
            // (docs/01 3.3.1).
            foreach (var parallel in PlanReady.Ready(state).Where(s => s.Kind == "parallel").ToList())
            {
                state.StepOutcomes[parallel.Id] = new StepOutcomeRecord { Outcome = "started" };
            }

            var executable = PlanReady.Ready(state)
                .Where(s => s.Gate is null && s.Kind != "parallel")
                .ToList();

            // docs/09 7.0: a branch reports its terminal outcome to the join.
            // max_concurrency bounds how many branches are in flight; it is a
            // plan value, not an interpreter one (docs/01 3.3.1).
            executable = ApplyConcurrencyLimit(executable);

            if (executable.Count == 0)
            {
                // Nothing may start yet because a concurrency limit is holding
                // it back. Yield, or this becomes a synchronous loop that
                // starves the workflow task and blocks queries.
                await Workflow.DelayAsync(TimeSpan.FromMilliseconds(200));
                continue;
            }

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
            if (!state.StepOutcomes.TryGetValue(parallel.Id, out var parallelRun))
                continue;
            // "started" means the branches are in flight; the join has not
            // settled yet.
            if (parallelRun.Outcome != "started")
                continue;

            var inputs = ParallelInputs.FromInputs(parallel.Inputs);
            var branches = inputs.Branches.Where(b => state.Plan.ById(b) is not null).ToList();
            if (branches.Count == 0)
                continue;

            var done = branches.Where(b => state.StepOutcomes.ContainsKey(b)).ToList();
            var succeeded = done.Count(b => state.StepOutcomes[b].Outcome == Outcomes.Completed);
            var failed = done.Count(b =>
                state.StepOutcomes[b].Outcome == Outcomes.Failed ||
                state.StepOutcomes[b].Outcome == Outcomes.AttemptsExhausted);

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

            CompleteStep(parallel.Id, outcome);
            state.StepsExecuted++;

            Route(new List<Step> { parallel }, new Dictionary<string, StepOutcome>(StringComparer.Ordinal)
            {
                [parallel.Id] = new StepOutcome(parallel.Id, outcome, null),
            });
        }
    }

    /// <summary>
    /// Record a step's outcome in carried state, and its detail in the record
    /// store. Review 12 A1: only what an expression can read is carried.
    /// </summary>
    private void CompleteStep(string stepId, string outcome)
    {
        var detail = stepDetails.TryGetValue(stepId, out var existing)
            ? existing
            : new StepRunDetail { StepId = stepId };
        detail.Outcome = outcome;

        state.StepOutcomes[stepId] = new StepOutcomeRecord
        {
            Outcome = outcome,
            Outputs = new Dictionary<string, string>(detail.Outputs, StringComparer.Ordinal),
        };

        stepDetails[stepId] = detail;
        RecordStore.ForRun(Workflow.Info.WorkflowId).RecordStepRun(detail);
    }

    /// <summary>
    /// Review 12 A1: report the continue-as-new input size per continue-as-new,
    /// so it can be shown to be bounded by plan size rather than steps executed.
    /// </summary>
    private long MeasureCarriedState()
    {
        try
        {
            return System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(state).Length;
        }
        catch
        {
            return -1;
        }
    }

    private void RecordSample()
    {
        var sample = new HistorySample
        {
            ContinueAsNewIndex = state.ContinueAsNewCount,
            HistoryLengthBytes = Workflow.CurrentHistorySize,
            HistoryEventCount = Workflow.CurrentHistoryLength,
            StepsExecuted = state.StepsExecuted,
            CarriedStateBytes = MeasureCarriedState(),
        };
        RecordStore.ForRun(Workflow.Info.WorkflowId).RecordHistorySample(sample);
        lastSample = sample;
    }

    /// <summary>The most recent sample, for the query. Not carried: it is one record.</summary>
    private HistorySample? lastSample;

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
        // Detail lives outside carried state (review 12 A1). It is rebuilt from
        // the record store, so it must never drive a routing decision.
        var run = stepDetails.TryGetValue(step.Id, out var existing)
            ? existing
            : new StepRunDetail { StepId = step.Id };

        string? sessionRef = run.Attempts_.LastOrDefault()?.SessionRef;
        var lastCommit = run.Attempts_.LastOrDefault()?.LastCommit;
        var lastTurns = run.Attempts_.LastOrDefault()?.SessionTurns ?? 0;
        var infrastructureFailures = 0;

        // Review 13 B1: the clock for "no successful contact" starts here.
        state.LastContactAt ??= Workflow.UtcNow;

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
                            stepDetails[step.Id] = run;
                            state.LastInfrastructureError +=
                                $" [counted as restart: 3 resumes without progress, attempts={attempts}]";
                        }
                    }

                    if (attempts >= allowance)
                    {
                        run.Outcome = Outcomes.AttemptsExhausted;
                        run.Attempts = attempts;
                        state.AttemptBudgets[step.Id] = attempts;
                        stepDetails[step.Id] = run;
                        state.StepsExecuted++;
                        return new StepOutcome(step.Id, Outcomes.AttemptsExhausted, null);
                    }

                    // Review 13 B1: the stalled check applies on this path too.
                    // A heartbeat timeout is the common way a long outage
                    // surfaces, so leaving it out meant the state never appeared.
                    var sinceHb = Workflow.UtcNow - (state.LastContactAt ?? Workflow.UtcNow);
                    if (sinceHb >= StalledAfter)
                    {
                        state.StalledOnInfrastructure = true;
                        state.StalledSince ??= Workflow.UtcNow;
                        state.StalledStepId = step.Id;
                    }

                    await Workflow.DelayAsync(BackoffFor(run.InfrastructureFailures));
                    continue;
                }

                // Infrastructure: the platform or the sandbox went away. Free
                // retry, no attempt consumed (docs/01 3.7).
                //
                // Review 12 A4: no count limit. A fixed 1s retry against an
                // immediately refusing endpoint both fails the step after about
                // 25 minutes and adds ~6 events per retry, which is R20 in
                // another form. Exponential backoff with a cap instead, and past
                // a threshold the run is marked stalled on infrastructure (D24)
                // and alerts. It never fails on its own; a human may abort it.
                run.Resumes++;
                run.InfrastructureFailures++;
                infrastructureFailures++;

                // Review 12 A4: record the failure as it happens, so a stalled
                // run is observable while it is stalled rather than only after
                // the step finishes.
                stepDetails[step.Id] = run;
                RecordStore.ForRun(Workflow.Info.WorkflowId).RecordStepRun(run);

                var backoff = BackoffFor(infrastructureFailures);
                state.LastInfrastructureError =
                    $"step {step.Id} infrastructure failure {infrastructureFailures}: " +
                    $"{ex.GetType().Name}: {ex.Message} (backoff {backoff.TotalSeconds:F0}s)";

                // Review 13 B1: time-based. The run is stalled once it has been
                // without successful contact for StalledAfter, regardless of how
                // many retries that took. FirstContactAt is set when the step
                // starts; every successful activity result resets it.
                var sinceContact = Workflow.UtcNow - (state.LastContactAt ?? Workflow.UtcNow);
                if (sinceContact >= StalledAfter)
                {
                    state.StalledOnInfrastructure = true;
                    state.StalledSince ??= Workflow.UtcNow;
                    state.StalledStepId = step.Id;
                }

                await Workflow.DelayAsync(backoff);
                continue;
            }

            progress = progress with { EndedAt = Workflow.UtcNow };
            run.Attempts_.Add(progress);

            if (result.Outcome == "infrastructure")
            {
                run.Resumes++;
                continue;
            }

            // Successful contact: reset the stalled clock and clear the state.
            // Only reached when the activity actually returned work, not when it
            // reported an infrastructure failure.
            state.LastContactAt = Workflow.UtcNow;
            if (state.StalledOnInfrastructure)
            {
                state.StalledOnInfrastructure = false;
                state.StalledSince = null;
                state.StalledStepId = null;
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
                // Spike 3: declared outputs (e.g. branch_changed) are what a
                // guarded route reads, so they must reach carried state.
                run.Outputs = result.Outputs is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(result.Outputs, StringComparer.Ordinal);
                stepDetails[step.Id] = run;
                CompleteStep(step.Id, result.Outcome);
                state.StepsExecuted++;
                return new StepOutcome(step.Id, result.Outcome, result.PatchRef);
            }

            attempts++;
            if (attempts >= allowance)
            {
                run.Outcome = Outcomes.AttemptsExhausted;
                run.Attempts = attempts;
                state.AttemptBudgets[step.Id] = attempts;
                stepDetails[step.Id] = run;
                CompleteStep(step.Id, Outcomes.AttemptsExhausted);
                state.StepsExecuted++;
                return new StepOutcome(step.Id, Outcomes.AttemptsExhausted, null);
            }
        }
    }

    /// <summary>
    /// Tunables. These are static and mutable so a test can shorten them: the
    /// real values make failure-path tests take many minutes each, which is the
    /// single biggest cost in running this spike.
    /// </summary>
    public static TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Added to a step's simulated duration to give the activity room to finish.
    /// Tunable so a test can make an activity time out on purpose.
    /// </summary>
    public static int StartToClosePadding = 120;

    /// <summary>
    /// Review 12 A4: exponential backoff with a cap, so a long infrastructure
    /// outage neither fails the step nor floods history with retries.
    /// </summary>
    public static TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Review 12 A4: past this many consecutive infrastructure failures the run
    /// is marked stalled on infrastructure (D24) and alerts. It does not fail.
    /// </summary>
    /// <summary>
    /// Review 13 B1: stalled is **time-based**, not count-based. A count of 8
    /// failures took about 8 minutes to reach, because each failure costs a
    /// heartbeat timeout plus backoff. Time-based answers open question 3
    /// without shortening heartbeats.
    /// </summary>
    public static TimeSpan StalledAfter = TimeSpan.FromMinutes(5);

    private static TimeSpan BackoffFor(int failures)
    {
        // 1s, 2s, 4s, 8s ... capped at MaxBackoff.
        var seconds = Math.Pow(2, Math.Min(failures - 1, 20));
        var capped = Math.Min(seconds, MaxBackoff.TotalSeconds);
        return TimeSpan.FromSeconds(capped);
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
        StartToCloseTimeout = TimeSpan.FromSeconds(
            ParseSeconds(step.SimulatedDuration, 5) + StartToClosePadding),
        HeartbeatTimeout = HeartbeatTimeout,
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

            // docs/09 6.2: { resume: true } is valid only on a plan.replan step,
            // and it means "continue the run from where the replan left it".
            // The step that produced it has done its work, so it must not run
            // again — without this the replan re-runs forever, because nothing
            // else consumes the resume directive.
            if (target.IsResume)
            {
                state.StepsExecuted++;
                return;
            }

            // A route back to a step that has already run (a loop, such as
            // verify_fix -> run_tests) re-enters it. Without clearing its
            // outcome PlanReady never treats it as ready again, nothing is
            // left to run, and the workflow ends "completed" — a false success.
            if (target.Step is not null)
                state.StepOutcomes.Remove(target.Step);
        }
    }

    /// <summary>
    /// Human steering. docs/01 3.4: a Temporal update via the API.
    /// </summary>
    [WorkflowUpdate]
    public Task<string> SubmitPatchAsync(PlanPatch patch)
    {
        var result = ApplyPatch.Apply(state.Plan, patch);

        // Review 12 A1: the patch record goes to the record store, not to
        // carried state. R7 still requires it to be recorded rather than
        // silently dropped.
        RecordStore.ForRun(Workflow.Info.WorkflowId).RecordPatch(new PatchRecord
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

    /// <summary>Review 13 A1: release the barrier, so all runs start together.</summary>
    [WorkflowSignal]
    public Task ReleaseAsync() => Task.FromResult(released = true);

    [WorkflowQuery]
    public InterpreterState State() => state;

    /// <summary>
    /// Per-step detail for diagnosis. Not carried across continue-as-new: it is
    /// read from the record store.
    /// </summary>
    [WorkflowQuery]
    public Dictionary<string, StepRunDetail> StepRuns() =>
        new(stepDetails, StringComparer.Ordinal);

    [WorkflowQuery]
    public HistorySample? LastSample() => lastSample;

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

    /// <summary>Review 13 A1: wait for a release signal before executing.</summary>
    public bool WaitForRelease { get; set; }

    /// <summary>Spike 3 N1: child plans by plan_ref, resolved before the run starts.</summary>
    public Dictionary<string, Plan>? ChildPlans { get; set; }

    /// <summary>Spike 3: the task queue each run.child step's child runs on (one worker per ticket).</summary>
    public Dictionary<string, string>? ChildQueues { get; set; }

    /// <summary>Spike 3 N2: the merge lock workflow id this run must hold across forward-merge-to-merge.</summary>
    public string? MergeLock { get; set; }

    /// <summary>Spike 3 N2: the task queue the lock workflow runs on.</summary>
    public string? MergeLockQueue { get; set; }

    /// <summary>Spike 3 N2: the merge lock passed to children (the integration branch's lock).</summary>
    public string? ChildMergeLock { get; set; }
}
