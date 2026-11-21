using FeatureFlow.Engine.Actions;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace FeatureFlow.Engine;

/// <summary>
/// The one workflow type every run uses, at every level (docs/02 section 2): a loop around the interpreter's pure core
/// (LOG 66). It starts what the core asks for, awaits the first thing to happen, gives it back to the core, and records what
/// changed. Every routing decision and the state it needs are the core's, children and waits included; the shell holds what
/// is Temporal's: activity and child-workflow calls, timers, signal and update handlers, cancellation, the drain and the
/// hand-over, inbound signals that arrive before the core exists, and record numbering (docs/02 section 3; the architecture
/// test <c>ShellRuleTests</c> holds it to that). Continue-as-new carries the core's snapshot and those few things over;
/// children are started with the abandon parent-close policy and signal the parent's workflow id, never a run id. M7's areas
/// are in their own files: run-time overrides (<c>RunWorkflow.Overrides.cs</c>), sessions (<c>RunWorkflow.Sessions.cs</c>)
/// and inbound wakes (<c>RunWorkflow.Inbound.cs</c>).
/// </summary>
[Workflow(RunWorkflowNames.Workflow)]
public partial class RunWorkflow
{
    static readonly RetryPolicy Forever = new() { InitialInterval = TimeSpan.FromSeconds(1), MaximumInterval = TimeSpan.FromSeconds(10), MaximumAttempts = 0 };

    static readonly ActivityOptions Light = new() { StartToCloseTimeout = TimeSpan.FromMinutes(2), RetryPolicy = Forever };

    readonly RunStart input;

    // Carried across continue-as-new. Set in the initialiser, because Temporal can deliver signals and updates
    // before the run method starts (DR14).
    readonly List<string> pendingChildEnds;
    readonly List<ParentPatchSignal> pendingParentPatches;
    readonly List<HandOver> handOvers;
    long timelineSequence;
    bool cancelledByParent;

    // This run only.
    RunCore? core;
    RunPolicy? policy;
    string lastRecordedStatus = "";
    bool draining;
    bool dirty;
    int interruptedThisRun;
    int inFlightActivities;
    int activitiesStartedWhileDraining;
    int childEndsWhileDraining;
    int answersWhileDraining;
    readonly Dictionary<string, CancellationTokenSource> stepTokens = [];
    readonly HashSet<string> interrupted = new(StringComparer.Ordinal);
    CancellationTokenSource? childWatch;
    readonly HashSet<string> confirmsStarted = new(StringComparer.Ordinal);

    [WorkflowInit]
    public RunWorkflow(RunStart input)
    {
        this.input = input;
        var c = input.Carried;
        pendingChildEnds = c?.PendingChildEnds.ToList() ?? [];
        pendingParentPatches = c?.PendingParentPatches.ToList() ?? [];
        handOvers = c?.HandOvers.ToList() ?? [];
        timelineSequence = c?.TimelineSequence ?? 0;
        cancelledByParent = c?.CancelledByParent ?? false;
        if (c is not null)
            lastRecordedStatus = "running";
    }

    string AgentQueue => TaskQueues.Agent(Workflow.Info.TaskQueue);

    LibraryReference Template => LibraryReference.TryParse(input.Template) ?? throw new ApplicationFailureException($"'{input.Template}' is not a template reference", nonRetryable: true);

    [WorkflowRun]
    public async Task<string> RunAsync(RunStart start)
    {
        if (input.Carried is null)
        {
            var prepared = await Workflow.ExecuteActivityAsync((RunActivities a) => a.PrepareAsync(input), Light);
            if (prepared.Errors.Count > 0)
                return await RefuseAsync(prepared.Errors);
            policy = prepared.Policy;
            var libraries = RunActivities.Build(prepared.Libraries, new DiagnosticBag());
            var (started, diagnostics) = RunCore.Start(libraries, Template, RunActivities.RunInfoOf(input) with { DefaultBranches = prepared.DefaultBranches },
                policy.ToOptions());
            core = started ?? throw new ApplicationFailureException("the plan passed run start but does not load: " + string.Join("; ", diagnostics.Take(3)));
            // docs/09 section 1: a warning changes nothing, and is listed in the run start record (the timeline).
            if (prepared.Warnings.Count > 0)
                foreach (var warning in prepared.Warnings)
                    core.Note("warning", "", warning);
            // docs/05 section 7: the effective policy is part of the run start record.
            if (prepared.EffectivePolicy is { Count: > 0 } effective)
                foreach (var line in effective)
                    core.Note("policy", "", line);
        }
        else
        {
            var snapshot = await Workflow.ExecuteActivityAsync((RunActivities a) => a.LoadLibrariesAsync(input.LockRef), Light);
            policy = input.Policy!;
            core = RunCore.Resume(RunActivities.Build(snapshot, new DiagnosticBag()), Template, input.Carried.Core, policy.ToOptions());
            // Timers of waits carried over a hand-over are armed again (docs/02 section 3: timers are re-created from state).
            foreach (var timer in core.Timers())
                _ = TimerAsync(timer);
            // Children an earlier run started can no longer be awaited here; a watch notices one that ends without
            // signalling (M3 follow-up 10).
            if (core.Children.Count > 0)
                _ = WatchCarriedChildrenAsync(core.Children.Select(c => c.WorkflowId).ToList());
        }

        var events = Math.Max(input.HandOverEvents, ConfigurationReader.MinimumHandOverEvents);
        while (true)
        {
            dirty = false;
            ProcessSignals();
            if (!draining && core.Status == RunStatus.Running
                && (Workflow.CurrentHistoryLength >= events || Workflow.CurrentHistorySize >= HandOverBytes))
                draining = true;
            if (draining)
                InterruptWaits();

            await ExecuteAsync(draining ? core.Flush() : core.Advance());
            await RecordAsync();

            if (core.Status != RunStatus.Running)
                return await FinishAsync();

            if (draining && inFlightActivities == 0 && !await HandOverAsync())
                return await FinishAsync();

            await Workflow.WaitConditionAsync(() => dirty);
        }
    }

    // ---- Signals, updates and the query ------------------------------------------------------------

    [WorkflowSignal(RunWorkflowNames.ChildEnded)]
    public Task ChildEndedAsync(string childWorkflowId)
    {
        if (draining)
            childEndsWhileDraining++;
        if (!pendingChildEnds.Contains(childWorkflowId))
            pendingChildEnds.Add(childWorkflowId);
        dirty = true;
        return Task.CompletedTask;
    }

    [WorkflowSignal(RunWorkflowNames.ParentPatch)]
    public Task ParentPatchAsync(ParentPatchSignal patch)
    {
        pendingParentPatches.Add(patch);
        dirty = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// An agent asked a question (docs/04 section 3.4): the core holds it as a wait on the running step, idempotently by
    /// the question's id (LOG 59), and asks for its timer.
    /// </summary>
    [WorkflowSignal(RunWorkflowNames.QuestionOpened)]
    public async Task QuestionOpenedAsync(QuestionSignal question)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        core!.Apply(new QuestionOpened(question.QuestionId, question.StepId, question.StepKey, question.Text, question.Options,
            TimeSpan.FromSeconds(question.TimeoutSeconds), Workflow.UtcNow));
        dirty = true;
    }

    /// <summary>The agent worker could not reach a step's model (LOG 326): shown on the timeline, nothing else changes.</summary>
    [WorkflowSignal(RunWorkflowNames.ModelUnreachable)]
    public Task ModelUnreachableAsync(ModelUnreachableSignal signal)
    {
        if (core is null)
            return Task.CompletedTask;
        core.Note("model_unreachable", signal.StepId, signal.Cause);
        dirty = true;
        return Task.CompletedTask;
    }

    /// <summary>A wait's timer (<see cref="ArmTimer"/>): when it fires, the core decides what it means, such as a question timing out.</summary>
    async Task TimerAsync(ArmTimer timer)
    {
        var wait = timer.At - Workflow.UtcNow;
        if (wait > TimeSpan.Zero)
            await Workflow.DelayAsync(wait);
        core!.Apply(new WaitTimerFired(timer.WaitId, Workflow.UtcNow));
        dirty = true;
    }

    [WorkflowSignal(RunWorkflowNames.ParentCancelled)]
    public Task ParentCancelledAsync()
    {
        cancelledByParent = true;
        dirty = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The update validator refuses what the core's routing refuses (LOG 58): an answer is accepted while the run drains
    /// for continue-as-new. Before the core is ready it accepts, and the handler checks.
    /// </summary>
    [WorkflowUpdateValidator(nameof(AnswerAsync))]
    public void ValidateAnswer(AnswerArgs answer)
    {
        if (core?.Route(answer, submitting.Contains(answer.WaitId)) is Refused refused)
            throw new ApplicationFailureException(refused.Error);
    }

    [WorkflowUpdate(RunWorkflowNames.Answer)]
    public async Task<AnswerReply> AnswerAsync(AnswerArgs answer)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        if (draining)
            answersWhileDraining++;
        switch (core!.Route(answer, submitting.Contains(answer.WaitId)))
        {
            case Duplicate:
                return new AnswerReply("duplicate");
            case Refused refused:
                throw new ApplicationFailureException(refused.Error);
            case Submits submits:
                return await SubmitAsync(answer, submits.StepId);
        }

        var session = core.SessionStep(answer.WaitId)?.Id;
        var result = core.Answer(answer);
        if (!result.Accepted)
            throw new ApplicationFailureException(string.Join("; ", result.Errors));
        dirty = true;
        if (session is not null)
            await CloseSessionAsync(answer, session);
        return new AnswerReply("recorded");
    }

    [WorkflowUpdateValidator(nameof(AbortAsync))]
    public void ValidateAbort(AbortArgs abort)
    {
        if (core is not null && core.Status != RunStatus.Running)
            throw new ApplicationFailureException($"the run has ended ({core.Status.Name()})");
    }

    [WorkflowUpdate(RunWorkflowNames.Abort)]
    public async Task<AnswerReply> AbortAsync(AbortArgs abort)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        closePullRequests |= abort.ClosePullRequests;
        var result = core!.Apply(new RunAborted($"{abort.Reason} (by {abort.Actor})"));
        if (!result.Accepted)
            throw new ApplicationFailureException(string.Join("; ", result.Errors));
        dirty = true;
        return new AnswerReply("aborted");
    }

    [WorkflowQuery(RunWorkflowNames.View)]
    public RunView View() => new(
        core?.Status.Name() ?? "starting",
        core is not null,
        draining,
        handOvers.Count,
        Workflow.CurrentHistoryLength,
        core?.WaitViews() ?? [],
        core?.InFlight.ToList() ?? [],
        core?.Children.ToList() ?? [],
        handOvers.ToList());

    // ---- Carrying out the core's commands ----------------------------------------------------------

    void ProcessSignals()
    {
        if (core is null)
            return;
        if (cancelledByParent && core.Status == RunStatus.Running)
            core.Apply(new RunAborted("the parent run no longer needs this run"));

        foreach (var signal in pendingParentPatches.ToList())
        {
            pendingParentPatches.Remove(signal);
            var childStep = core.ChildStep(signal.ChildWorkflowId);
            var d = new DiagnosticBag();
            var patch = PatchReader.Read(YamlWriter.Read(signal.Patch, "patch"), d);
            if (childStep is null || patch is null || d.HasErrors)
            {
                core.Note("patch_rejected", childStep ?? "", $"from {signal.ChildWorkflowId}: " + string.Join("; ", d.Where(x => x.Blocks).Select(x => x.Message).DefaultIfEmpty("not from a running child")));
                continue;
            }

            core.Apply(new ChildPatchProposed(childStep, patch, signal.ProposerStepId));
        }

        // A child's end is confirmed by reading the child; while draining, the read waits for the next run.
        if (draining)
            return;
        foreach (var id in pendingChildEnds.Where(id => !confirmsStarted.Contains(id)).ToList())
        {
            confirmsStarted.Add(id);
            _ = ConfirmChildAsync(id);
        }
    }

    async Task ExecuteAsync(IReadOnlyList<Command> commands)
    {
        foreach (var command in commands)
        {
            switch (command)
            {
                case StartStep s:
                    await StartStepAsync(s);
                    break;
                case StartChild s:
                    await StartChildAsync(s);
                    break;
                case CancelStep c:
                    if (stepTokens.Remove(c.StepId, out var token))
                        token.Cancel();
                    break;
                case CancelChild c:
                    await SignalQuietlyAsync(c.WorkflowId, RunWorkflowNames.ParentCancelled, []);
                    break;
                case CleanUp c:
                    await CleanUpAsync(c.StepId);
                    break;
                case OpenGate { Session: true } o:
                    await OpenSessionAsync(o);
                    break;
                case ArmTimer t:
                    _ = TimerAsync(t);
                    break;
                case SendPatchToParent p when input.ParentWorkflowId is { } parent:
                    await SignalQuietlyAsync(parent, RunWorkflowNames.ParentPatch,
                        [new ParentPatchSignal(Workflow.Info.WorkflowId, p.StepId, PatchReader.Write(p.Patch))]);
                    break;
                case OverrideLibrary o:
                    // A person approved an agent's library override (docs/05 section 6): it is theirs, recorded as their override.
                    try
                    {
                        await OverrideLibraryAsync(o.Item, o.Content, null, o.Actor, o.Reason);
                    }
                    catch (ApplicationFailureException e)
                    {
                        core!.Note("library_override_refused", "", $"{o.Item} ({o.WaitId}): {e.Message}");
                    }

                    break;
            }
        }
    }

    /// <summary>Signals another workflow by its workflow id only, so the signal reaches whichever run is current.</summary>
    static async Task SignalQuietlyAsync(string workflowId, string signal, object?[] args)
    {
        try
        {
            await Workflow.GetExternalWorkflowHandle(workflowId).SignalAsync(signal, args);
        }
        catch (Exception e) when (e is ApplicationFailureException or TemporalException)
        {
            // The other run has ended; there is nothing to tell it.
        }
    }

    /// <summary>Steps whose <c>clean_up</c> reset failed with a work error, set just before their start in the same batch.</summary>
    readonly Dictionary<string, string> cleanUpFailures = new(StringComparer.Ordinal);

    /// <summary>A step whose <c>clean_up</c> reset failed does not run on a branch that was not reset: it cannot answer.</summary>
    bool CleanUpFailed(string stepId)
    {
        if (!cleanUpFailures.Remove(stepId, out var error))
            return false;
        core!.Apply(new StepCannotAnswer(stepId, $"its clean_up could not reset the work branch: {error}"));
        dirty = true;
        return true;
    }

    async Task StartStepAsync(StartStep s)
    {
        if (CleanUpFailed(s.StepId))
            return;
        var (spec, error) = core!.StepSpec(s.StepId);
        if (error is not null)
        {
            core.Apply(new StepCannotAnswer(s.StepId, error));
            dirty = true;
            return;
        }

        var call = CallOf(spec!, new StepIdentity(input.RunId, Template.Name, s.StepId, s.Execution, core.StepKey(s.StepId, s.Execution)));
        _ = RunStepAsync(s, spec!.Activity, call, spec.Activity == StepActivity.Engine ? ScopeOf(s.StepId) : null);
    }

    /// <summary>A step's activity call, mapped from the core's <see cref="StepSpec"/> (docs/04 sections 2 and 3).</summary>
    StepCall CallOf(StepSpec spec, StepIdentity identity)
    {
        var branch = core!.WorkBranch();
        AgentStepSpec? agent = null;
        if (spec.Agent is { } a)
        {
            var binding = policy?.AgentFor(a.Role);
            agent = new AgentStepSpec(a.WriteAccess, a.Tools, a.TaskText, a.TaskLibrary, a.Skills, a.CorrectionTurns, a.QuestionsMax,
                a.QuestionsTimeout ?? AgentLimits.DefaultQuestionsTimeout, a.Push, a.OnUncommitted, a.Level,
                input.Repositories.ToDictionary(r => r, _ => branch),
                // A child's parent branch is its parent run's work branch; otherwise the agent worker reads the default branch from the code host.
                input.ParentBranch is { } parentBranch ? input.Repositories.ToDictionary(r => r, _ => parentBranch) : new Dictionary<string, string>(),
                a.Consumed, Workflow.Info.WorkflowId, LockRef, TrackerIssue: input.TrackerIssue, Runtime: binding?.AgentRuntime, Model: binding?.Model);
        }

        return CallOf(spec, identity, input.DryRun, agent);
    }

    /// <summary>The run-independent part of <see cref="CallOf(StepSpec, StepIdentity)"/>: the call's kind, inputs and contract.</summary>
    internal static StepCall CallOf(StepSpec spec, StepIdentity identity, bool dryRun, AgentStepSpec? agent)
    {
        var contract = spec.Contract;
        return new StepCall(identity, spec.Activity == StepActivity.Agent ? "agent" : "action", spec.What,
            spec.Inputs.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)),
            new StepContract(
                contract.Outcomes.Select(o => new OutcomeSpec(o.Name, o.Class)).ToList(),
                contract.OutputTypes,
                contract.Artifacts.Select(p => new ArtifactSpec(p.Outcome, p.Name) { Type = p.Type }).ToList(),
                contract.ZeroOutputs.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)))
            {
                Reported = contract.Reported,
            },
            dryRun, spec.Timeout, agent);
    }

    /// <summary>What an engine action knows of the run (<see cref="ActionScope"/>), mapped from the core's <see cref="ScopeFacts"/>.</summary>
    ActionScope ScopeOf(string stepId, (string, IReadOnlyDictionary<string, string>)? submitted = null)
    {
        var facts = core!.ScopeFacts(stepId, submitted);
        return new ActionScope(input.Repositories, facts.Repository, facts.WorkBranch, input.ParentBranch,
            facts.Artifacts.Select(a => new ArtifactEntry(a.StepId, a.Name, a.Type, a.Reference, a.Producer, a.Evidence)).ToList(),
            facts.PullRequests, policy?.EngineMayMerge ?? [], Consumed: facts.Consumed, TrackerIssue: input.TrackerIssue, Declared: facts.Declared,
            LockRef: LockRef, Decision: facts.GateState is { } state ? new StepDecision(facts.GateType!, state) : null);
    }

    /// <summary>
    /// A <c>clean_up</c> route resets the work branch before its target runs again (docs/03 section 4.1). The activity
    /// finds where the step started from in the record store; it records what it did on the run's trace.
    /// </summary>
    async Task CleanUpAsync(string stepId)
    {
        var call = new CleanUpCall(input.RunId, stepId, core!.Executions(stepId), input.Repositories, core.WorkBranch(), core.StepsRun(), input.DryRun);
        BeginActivity();
        try
        {
            var result = await Workflow.ExecuteActivityAsync((RunActivities a) => a.CleanUpAsync(call), Light);
            foreach (var e in result.Events)
                core.Note("clean_up", stepId, e);
            if (result.Error is { } error)
                // The target step does not run on a branch that was not reset: it raises an intervention when it starts.
                cleanUpFailures[stepId] = error;
        }
        finally
        {
            EndActivity();
        }
    }

    /// <summary>
    /// The agent step activity's own timeout: the step's limit and an hour's margin, plus the longest its questions may
    /// stay open, which do not count toward the limit (LOG 106).
    /// </summary>
    static TimeSpan AgentStepTimeout(StepCall call)
    {
        var timeout = (call.Timeout ?? AgentLimits.DefaultTimeout) + TimeSpan.FromHours(1);
        return call.Agent is { QuestionsMax: > 0 } spec ? timeout + spec.QuestionsTimeout * spec.QuestionsMax : timeout;
    }

    /// <summary>An engine action's activity options: an action that polls (CI tiers, sandbox commands) heartbeats; a wait between reads is a retry, not a held slot.</summary>
    static ActivityOptions ActionOptions(StepCall call, RetryPolicy retry, CancellationToken? token = null) => new()
    {
        StartToCloseTimeout = (call.Timeout ?? TimeSpan.FromHours(2)) + TimeSpan.FromMinutes(10),
        HeartbeatTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = retry,
        CancellationToken = token,
        CancellationType = StepCancellation(),
    };

    async Task RunStepAsync(StartStep s, StepActivity activity, StepCall call, ActionScope? scope)
    {
        var (stepId, execution) = (s.StepId, s.Execution);
        var token = new CancellationTokenSource();
        stepTokens[stepId] = token;
        BeginActivity();
        StepOutcome? outcome = null;
        string? expandItems = null;
        try
        {
            switch (activity)
            {
                case StepActivity.Expand:
                    var (name, list) = core!.ExpandList(stepId);
                    if (list is null)
                        outcome = new StepOutcome(StepOutcome.CannotAnswer, null, $"plan.expand: no artifact '{name}' has been produced");
                    else
                        expandItems = await Workflow.ExecuteActivityAsync((RunActivities a) => a.ReadListAsync(list), new ActivityOptions
                        {
                            StartToCloseTimeout = TimeSpan.FromMinutes(2),
                            RetryPolicy = Forever,
                            CancellationToken = token.Token,
                        });
                    break;
                case StepActivity.Agent:
                    // By name: the activity is the agent worker's (Engine.Agent), which Engine may not reference (docs/11 section 2.3).
                    outcome = await Workflow.ExecuteActivityAsync<StepOutcome>(AgentStepActivity, [call], new ActivityOptions
                    {
                        TaskQueue = AgentQueue,
                        StartToCloseTimeout = AgentStepTimeout(call),
                        HeartbeatTimeout = TimeSpan.FromSeconds(30),
                        RetryPolicy = Forever,
                        CancellationToken = token.Token,
                        CancellationType = StepCancellation(),
                    });
                    break;
                default:
                    outcome = await Workflow.ExecuteActivityAsync((RunActivities a) => a.RunActionAsync(call, scope!), ActionOptions(call, Forever, token.Token));
                    break;
            }
        }
        catch (Exception e) when (TemporalException.IsCanceledException(e))
        {
            if (interrupted.Remove(stepId) && core!.IsCurrent(stepId, execution))
                core.Apply(new StepInterrupted(stepId, InterruptionReason(stepId)));
            return;
        }
        catch (ActivityFailureException e) when (interrupted.Contains(stepId) && e.RetryState == Temporalio.Api.Enums.V1.RetryState.CancelRequested)
        {
            // The cancel landed while an attempt ran: the attempt's failure (a wait's "not yet", or an infrastructure error)
            // is not retried once a cancel is requested, and acknowledges the cancel as surely as a cancellation does.
            if (interrupted.Remove(stepId) && core!.IsCurrent(stepId, execution))
                core.Apply(new StepInterrupted(stepId, InterruptionReason(stepId)));
            return;
        }
        catch (ActivityFailureException e)
        {
            // A non-retryable activity failure (an artifact that does not exist, a list artifact with no items)
            // is the step's failure: it reaches the core as a work error, never leaves the step in flight.
            outcome = new StepOutcome(StepOutcome.WorkFailed, null, "the step's activity failed: " + (e.InnerException?.Message ?? e.Message));
        }
        finally
        {
            EndActivity();
            if (stepTokens.GetValueOrDefault(stepId) == token)
            {
                stepTokens.Remove(stepId);
                interrupted.Remove(stepId);
            }
        }

        if (!core!.IsCurrent(stepId, execution))
            return;
        try
        {
            if (expandItems is not null)
            {
                var items = YamlWriter.Read(expandItems, "items") as YSeq ?? throw new FormatException("plan.expand's list is not a list");
                var (patch, error) = PlanExpansion.Build(core, stepId, items.Items);
                if (error is null && Budget is { } budget && WorkItems.ChildSteps(patch!) is > 0 and var adding)
                {
                    error = await WorkItemsRefusalAsync(new WorkItemRequest(budget, adding));
                    if (!core.IsCurrent(stepId, execution))
                        return;
                }

                core.Apply(error is null
                    ? new StepCompleted(stepId, "expanded", Patch: patch)
                    : new StepCannotAnswer(stepId, error));
                return;
            }

            core.NoteCheckInputs(stepId, outcome!.Inputs);
            Apply(stepId, outcome);
        }
        catch (Exception e) when (e is not TemporalException)
        {
            // Whatever goes wrong with a step's result is the step's failure, not the run's (docs/04 section 1).
            if (core.IsCurrent(stepId, execution))
                core.Apply(new StepFailed(stepId, ErrorClass.Work, $"the step's result could not be applied: {e.Message}"));
        }
    }

    /// <summary>The activity name of the agent step, registered by the agent worker.</summary>
    public const string AgentStepActivity = "RunAgentStep";

    /// <summary>
    /// A cancelled step's activity is waited for (M3 follow-up 9), so a cancelled agent step has stopped its agent
    /// and removed its sandbox before the run moves on.
    /// </summary>
    static ActivityCancellationType StepCancellation() => ActivityCancellationType.WaitCancellationCompleted;

    /// <summary>
    /// The core input a step activity's outcome gives when it is not a report: cannot answer (the core raises an
    /// intervention), a work failure, or an action's decision. Null: a report, which <see cref="Apply"/> checks itself.
    /// </summary>
    public static RunInput? Ending(string stepId, StepOutcome outcome) => outcome.Kind switch
    {
        StepOutcome.CannotAnswer => new StepCannotAnswer(stepId, outcome.Reason ?? "cannot answer"),
        StepOutcome.WorkFailed => new StepFailed(stepId, ErrorClass.Work, outcome.Reason ?? "work failure"),
        StepOutcome.Decide => new ActionDecisionRequested(stepId, outcome.Reason ?? "a person decides"),
        _ => null,
    };

    void Apply(string stepId, StepOutcome outcome)
    {
        if (outcome.Findings is { } counts)
            core!.Apply(new FindingsChanged(counts, outcome.FindingsVersion));
        if (Ending(stepId, outcome) is { } ending)
        {
            if (ending is ActionDecisionRequested)
                foreach (var e in outcome.Events ?? [])
                    core!.Note("agent", stepId, e);
            core!.Apply(ending);
            return;
        }

        foreach (var e in outcome.Events ?? [])
            core!.Note("agent", stepId, e);
        var report = outcome.Report!;
        PatchDocument? patch = null;
        if (report.Patch is { } text)
        {
            var d = new DiagnosticBag();
            patch = PatchReader.Read(YamlWriter.Read(text, "patch"), d);
            if (patch is null || d.HasErrors)
            {
                core!.Apply(new StepFailed(stepId, ErrorClass.Work, "the patch does not read: " + string.Join("; ", d.Where(x => x.Blocks).Select(x => x.Message))));
                return;
            }
        }

        var result = core!.Apply(new StepCompleted(stepId, report.Outcome,
            report.Outputs.ToDictionary(kv => kv.Key, kv => YamlWriter.ReadPlain(kv.Value)),
            report.Artifacts, patch, report.PatchToParent));
        if (result.Accepted && report.LibraryOverride is { } proposal)
            ProposeLibraryOverride(stepId, proposal);
        if (!result.Accepted)
        {
            // The agent worker's correction turns have run (docs/04 section 3.3); a report the core still refuses fails the attempt.
            core.Apply(new StepFailed(stepId, ErrorClass.Work, "output contract: " + string.Join("; ", result.Errors)));
        }
    }

    void BeginActivity()
    {
        if (draining)
            activitiesStartedWhileDraining++;
        inFlightActivities++;
    }

    void EndActivity()
    {
        inFlightActivities--;
        dirty = true;
    }

    // ---- Child runs ---------------------------------------------------------------------------------

    async Task StartChildAsync(StartChild s)
    {
        if (CleanUpFailed(s.StepId))
            return;
        ChildSpec spec;
        try
        {
            spec = core!.ChildSpec(s.StepId, s.Execution);
        }
        catch (EvaluationException e)
        {
            core!.Apply(new StepCannotAnswer(s.StepId, "its inputs cannot be evaluated: " + e.Message));
            dirty = true;
            return;
        }

        var budget = Budget;
        if (budget is not null
            && await WorkItemsRefusalAsync(new WorkItemRequest(budget, 1,
                new RunRecord(spec.WorkflowId, Workflow.Info.WorkflowId, spec.Template, "starting", input.Requester, spec.Repositories, input.DryRun, Workflow.UtcNow, null))) is { } refused)
        {
            core.Apply(new StepCannotAnswer(s.StepId, refused));
            dirty = true;
            return;
        }

        // The child's parent branch is this run's work branch, in every repository the child is given (LOG 196).
        var made = await EnsureWorkBranchesAsync(new WorkBranchesCall(spec.Repositories, core.WorkBranch(), input.ParentBranch, input.DryRun));
        foreach (var e in made.Events)
            core.Note("agent", s.StepId, e);
        if (made.Error is { } error)
        {
            core.Apply(new StepCannotAnswer(s.StepId, error));
            dirty = true;
            return;
        }

        var childInput = new RunStart(spec.WorkflowId, spec.Template, spec.Repositories, spec.Parameters,
            LockRef, input.DryRun, input.Requester, Workflow.Info.WorkflowId, s.StepId, input.HandOverEvents, ParentBranch: core.WorkBranch(),
            ParentArtifacts: spec.ParentArtifacts, RootArtifacts: spec.RootArtifacts, TrackerIssue: spec.TrackerIssue, WorkItems: budget);
        // What this run has produced is recorded before a child exists, so the artifact index lists a parent's artifacts
        // before the child's that may consume them (m8-results Foundations, follow-up 4).
        await RecordAsync();
        ChildWorkflowHandle? handle = null;
        try
        {
            handle = await Workflow.StartChildWorkflowAsync((RunWorkflow w) => w.RunAsync(childInput), new ChildWorkflowOptions
            {
                Id = spec.WorkflowId,
                TaskQueue = Workflow.Info.TaskQueue,
                // docs/02 section 3: a child outlives its parent's hand-over; it reports to the parent's workflow id.
                ParentClosePolicy = ParentClosePolicy.Abandon,
            });
        }
        catch (WorkflowAlreadyStartedException)
        {
            // Adopt the existing child (docs/04 section 5.1: idempotent by run id and step key).
        }
        catch (Exception e) when (e is ChildWorkflowFailureException or ApplicationFailureException)
        {
            core.Apply(new StepFailed(s.StepId, ErrorClass.Work, "the child run could not be started: " + e.Message));
            dirty = true;
            return;
        }

        core.Apply(new ChildStarted(s.StepId, s.Execution, spec.WorkflowId));
        if (handle is not null)
            _ = AwaitChildAsync(handle, spec.WorkflowId);
    }

    async Task<WorkBranchesResult> EnsureWorkBranchesAsync(WorkBranchesCall call)
    {
        BeginActivity();
        try
        {
            return await Workflow.ExecuteActivityAsync((RunActivities a) => a.EnsureWorkBranchesAsync(call), Light);
        }
        finally
        {
            EndActivity();
        }
    }

    /// <summary>
    /// The run that started a child is told by Temporal when the child closes, however it ended: a child that ends
    /// without signalling (terminated, or failed before it could) is confirmed like one that signalled (M3 follow-up 10).
    /// </summary>
    async Task AwaitChildAsync(ChildWorkflowHandle handle, string childId)
    {
        try
        {
            await handle.GetResultAsync();
        }
        catch (TemporalException)
        {
            // Failed, terminated, cancelled or timed out: the confirm read says which.
        }

        ChildClosed(childId);
    }

    /// <summary>A hint that a child has closed, as its signal is (events wake, reads confirm).</summary>
    void ChildClosed(string childId)
    {
        if (core!.ChildStep(childId) is not null && !pendingChildEnds.Contains(childId))
        {
            pendingChildEnds.Add(childId);
            dirty = true;
        }
    }

    /// <summary>
    /// Children carried over a hand-over are watched by one activity that returns when any of them closes (M3 follow-up
    /// 10), and then again for the rest. It holds no state, so a hand-over interrupts it and the next run starts it again.
    /// </summary>
    async Task WatchCarriedChildrenAsync(IReadOnlyList<string> carried)
    {
        while (true)
        {
            var watching = carried.Where(id => core!.ChildStep(id) is not null && !pendingChildEnds.Contains(id)).ToList();
            if (watching.Count == 0 || draining)
                return;
            using var token = new CancellationTokenSource();
            childWatch = token;
            BeginActivity();
            IReadOnlyList<string> closed;
            try
            {
                closed = await Workflow.ExecuteActivityAsync((RunActivities a) => a.WatchChildrenAsync(watching), new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromDays(1),
                    HeartbeatTimeout = TimeSpan.FromMinutes(1),
                    RetryPolicy = Forever,
                    CancellationToken = token.Token,
                    // It only reads, so the hand-over need not wait for it to stop.
                    CancellationType = ActivityCancellationType.TryCancel,
                });
            }
            catch (Exception e) when (TemporalException.IsCanceledException(e))
            {
                return;
            }
            finally
            {
                EndActivity();
                childWatch = null;
            }

            foreach (var id in closed)
                ChildClosed(id);
        }
    }

    async Task ConfirmChildAsync(string childWorkflowId)
    {
        BeginActivity();
        ChildRead read;
        try
        {
            read = await Workflow.ExecuteActivityAsync((RunActivities a) => a.ReadChildAsync(childWorkflowId), Light);
        }
        finally
        {
            EndActivity();
        }

        if (read.Status == "Running")
        {
            confirmsStarted.Remove(childWorkflowId);
            return;
        }

        pendingChildEnds.Remove(childWorkflowId);
        confirmsStarted.Remove(childWorkflowId);
        if (core!.ChildStep(childWorkflowId) is null)
            return;
        var status = read.Status == "Completed" ? RunStatuses.Parse(read.Result ?? "") ?? RunStatus.Failed : RunStatus.Failed;
        core.Apply(new ChildEnded(childWorkflowId, status, read.Artifacts));
    }

    // ---- Delivery: the work-item budget and declarative tracker updates (M6) ------------------------

    /// <summary>The run tree's work-item budget: the root's own, passed to every child unchanged; null when it is unlimited.</summary>
    WorkItemBudget? Budget => input.WorkItems ?? (input.ParentWorkflowId is null ? WorkItems.Root(input.RunId, core!.Plan, policy) : null);

    /// <summary>Asks the record store whether the tree may create more runs; why not, or null.</summary>
    async Task<string?> WorkItemsRefusalAsync(WorkItemRequest request)
    {
        BeginActivity();
        try
        {
            return await Workflow.ExecuteActivityAsync((RunActivities a) => a.ReserveWorkItemsAsync(request), Light);
        }
        finally
        {
            EndActivity();
        }
    }

    /// <summary>How many of the timeline's unrecorded entries have been examined for tracker updates, and the updates found but not yet made.</summary>
    int trackedThrough;
    readonly Queue<TrackerUpdate> trackerUpdates = new();

    /// <summary>
    /// The next declarative <c>tracker</c> update (docs/03 section 2.5) for what the run did since its last record, in
    /// order, or null. A run with no tracker issue makes none. Synchronous, so the caller's last check and its record
    /// see the same timeline: a step that ends while an update is awaited is examined before anything is recorded.
    /// </summary>
    TrackerUpdate? NextTrackerUpdate()
    {
        if (input.TrackerIssue is null || !TrackerUpdates.Declared(core!.Plan))
            return null;
        var trace = core.Timeline;
        while (trackerUpdates.Count == 0 && trackedThrough < trace.Count)
            foreach (var update in TrackerUpdates.Due(core.Plan, [trace[trackedThrough++]]))
                trackerUpdates.Enqueue(update);
        return trackerUpdates.TryDequeue(out var next) ? next : null;
    }

    /// <summary>
    /// A tracker update's tries: five, each at most 30 s, all within two minutes, then given up and noted, because a status
    /// label is not worth stalling the run for, whether the tracker fails fast or hangs (LOG 186).
    /// </summary>
    public static readonly ActivityOptions TrackerTries = new()
    {
        StartToCloseTimeout = TimeSpan.FromSeconds(30),
        ScheduleToCloseTimeout = TimeSpan.FromMinutes(2),
        RetryPolicy = new() { InitialInterval = TimeSpan.FromSeconds(2), BackoffCoefficient = 2, MaximumInterval = TimeSpan.FromSeconds(30), MaximumAttempts = 5 },
    };

    /// <summary>Makes one tracker update and notes it on the timeline. An update that fails never fails the step, nor holds up the run.</summary>
    async Task TrackAsync(TrackerUpdate update)
    {
        var call = new TrackerCall(input.TrackerIssue!, update.Status, update.StepId, input.DryRun);
        string note;
        try
        {
            note = await Workflow.ExecuteActivityAsync((RunActivities a) => a.UpdateTrackerAsync(call), TrackerTries);
        }
        catch (ActivityFailureException e)
        {
            note = $"could not move {call.Issue} to {call.Status}: the tracker did not answer in {TrackerTries.RetryPolicy!.MaximumAttempts} tries ({e.InnerException?.Message ?? e.Message})";
        }

        core!.Note("tracker", update.StepId, $"{update.Why}: {note}");
    }

    // ---- Records, hand-over and the end ------------------------------------------------------------

    /// <summary>
    /// Writes what changed since the last record: new timeline entries, opened and closed waits, answered and timed-out
    /// questions, produced artifacts (the artifact index, docs/02 section 5), lock records and the run's status. Returns
    /// whether there was anything.
    /// </summary>
    async Task<bool> RecordAsync()
    {
        // Checked again after each update, with no await between the last check and the timeline taken below.
        while (NextTrackerUpdate() is { } update)
            await TrackAsync(update);
        var now = Workflow.UtcNow;
        var timeline = core!.TakeTimeline().Select(t => new TimelineRecord(input.RunId, ++timelineSequence, t.Kind, t.StepId, t.Detail, now)).ToList();
        trackedThrough = 0;

        var (shown, closedQuestions) = core.TakeWaitChanges();
        var waits = shown.Select(w => w.Opened is { } d
            ? new WaitRecord(input.RunId, w.WaitId, d.Kind, w.StepId, d.Type, d.Reason, d.Choices, true, now)
            : new WaitRecord(input.RunId, w.WaitId, "", "", "", "", [], false, now)).ToList();
        // The agent endpoint long-polls a question's record for its answer or timeout (docs/04 section 3.4).
        var questions = closedQuestions.Select(q => new QuestionRecord(q.StepKey, input.RunId, q.WaitId, q.Text, q.Options,
            q.Answered ? QuestionRecord.Answered : QuestionRecord.TimedOut, q.Answer, now)).ToList();
        var artifacts = core.TakeProduced().Select(a => new ArtifactRecord(input.RunId, a.StepId, a.Execution, a.Name, a.Type, a.Reference, a.Producer, a.Evidence, now)).ToList();
        var locks = lockRecords.ToList();
        lockRecords.Clear();

        var status = core.Status.Name();
        var runs = status == lastRecordedStatus ? [] : new List<RunRecord> { RunRecordOf(status, null, now) };
        lastRecordedStatus = status;
        if (timeline.Count == 0 && waits.Count == 0 && runs.Count == 0 && questions.Count == 0 && artifacts.Count == 0 && locks.Count == 0)
            return false;
        var batch = new RecordBatch(runs, waits, timeline, Locks: locks.Count == 0 ? null : locks, Questions: questions.Count == 0 ? null : questions,
            Artifacts: artifacts.Count == 0 ? null : artifacts);
        await Workflow.ExecuteActivityAsync((RunActivities a) => a.RecordAsync(batch), Light);
        return true;
    }

    RunRecord RunRecordOf(string status, string? detail, DateTimeOffset now) =>
        new(input.RunId, input.ParentWorkflowId, input.Template, status, input.Requester, input.Repositories, input.DryRun, now, detail);

    async Task<string> RefuseAsync(IReadOnlyList<string> errors)
    {
        var now = Workflow.UtcNow;
        var timeline = errors.Select(e => new TimelineRecord(input.RunId, ++timelineSequence, "refused", "", e, now)).ToList();
        var batch = new RecordBatch([RunRecordOf("failed", "refused at run start: " + string.Join("; ", errors.Take(3)), now)], null, timeline);
        await Workflow.ExecuteActivityAsync((RunActivities a) => a.RecordAsync(batch), Light);
        await TellParentAsync();
        return "failed";
    }

    async Task<string> FinishAsync()
    {
        // The run may have ended while the last record was being written: record what is left.
        await ExecuteAsync(core!.Flush());
        await RecordAsync();
        foreach (var token in stepTokens.Values)
            token.Cancel();
        // A cancelled step's activity is waited for (M3 follow-up 9): an agent step stops its agent and removes its
        // sandbox before the run ends.
        if (inFlightActivities > 0)
            await Workflow.WaitConditionAsync(() => inFlightActivities == 0);
        if (closePullRequests)
        {
            await CloseAbortedPullRequestsAsync();
            await RecordAsync();
        }

        await TellParentAsync();
        return core!.Status.Name();
    }

    async Task TellParentAsync()
    {
        if (input.ParentWorkflowId is { } parent)
            await SignalQuietlyAsync(parent, RunWorkflowNames.ChildEnded, [Workflow.Info.WorkflowId]);
    }

    /// <summary>
    /// LOG 139: a wait (<see cref="RunCore.WaitsBetweenReads"/>) may last days, so a draining run does not wait for it. Its
    /// activity is cancelled, and once the cancellation completes the core returns the step to pending at the same
    /// execution (<see cref="StepInterrupted"/>), which the next run starts again with the same identity, so the records
    /// the execution keeps (when it began waiting, the head it waits on) carry over. Other activities drain.
    /// </summary>
    void InterruptWaits()
    {
        childWatch?.Cancel();
        foreach (var (stepId, token) in stepTokens.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
        {
            if (interrupted.Contains(stepId) || !core!.WaitsBetweenReads(stepId))
                continue;
            interrupted.Add(stepId);
            interruptedThisRun++;
            token.Cancel();
        }
    }

    /// <summary>
    /// History bytes at which a run hands over whatever its event count (docs/02 section 3: the event threshold or
    /// the payload byte limit, whichever comes first). Temporal warns at 10 MB of history; this stays under it.
    /// </summary>
    public const int HandOverBytes = 8 * 1024 * 1024;

    /// <summary>
    /// Continues-as-new once nothing is in flight. DR15: an answer accepted during the drain is applied by its
    /// handler; the hand-over waits for every handler, so the answer is in the carried state (LOG 58). Whatever
    /// a handler produced is carried out and recorded first, and the state is taken with no await after the last
    /// check. Returns false when the run ended meanwhile.
    /// </summary>
    async Task<bool> HandOverAsync()
    {
        var handOverRecorded = false;
        while (true)
        {
            await Workflow.WaitConditionAsync(() => Workflow.AllHandlersFinished);
            var late = core!.Flush();
            await ExecuteAsync(late);
            var recorded = await RecordAsync();
            if (core.Status != RunStatus.Running)
                return false;
            if (late.Count > 0 || recorded || !Workflow.AllHandlersFinished || inFlightActivities > 0)
                continue;
            if (!handOverRecorded)
            {
                // The timeline shows each hand-over (M3).
                handOverRecorded = true;
                core.Note("continued_as_new", "", $"run {handOvers.Count + 1} follows at {Workflow.CurrentHistoryLength} history events");
                continue;
            }

            throw ContinueAsNew();
        }
    }

    /// <summary>
    /// The one carry-over path (docs/02 section 3): the core's snapshot, which holds everything that routes, and what is
    /// Temporal's. This run's hand-over record carries what arrived or started while it drained (LOG A2-0i).
    /// </summary>
    ContinueAsNewException ContinueAsNew()
    {
        handOvers.Add(new HandOver(handOvers.Count, Workflow.CurrentHistoryLength, inFlightActivities, core!.Children.Count, core.WaitViews().Count,
            pendingChildEnds.Count, interruptedThisRun, activitiesStartedWhileDraining, childEndsWhileDraining, answersWhileDraining));
        var carried = new ShellState(core.Snapshot(), pendingChildEnds.ToList(), pendingParentPatches.ToList(), timelineSequence, handOvers.ToList(), cancelledByParent);
        // The run's lock carries its library overrides (docs/08 section 5.2).
        var next = input with { Policy = policy, Carried = carried, LockRef = LockRef };
        return Workflow.CreateContinueAsNewException((RunWorkflow w) => w.RunAsync(next));
    }
}
