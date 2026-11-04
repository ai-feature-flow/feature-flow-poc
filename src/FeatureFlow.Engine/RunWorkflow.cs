using FeatureFlow.Engine.ActionSteps;
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
/// The one workflow type every run uses, at every level (docs/02 section 2): a thin loop around the
/// interpreter's pure core (LOG 66). It starts what the core asks for, awaits the first thing to happen, gives
/// it back to the core, and records what changed. Continue-as-new follows docs/02 section 3: drain activities,
/// wait for running handlers (DR15), and carry children, open decisions and the core's state over; children are
/// started with the abandon parent-close policy and signal the parent's workflow id, never a run id. M7's areas are in
/// their own files: run-time overrides (<c>RunWorkflow.Overrides.cs</c>), sessions (<c>RunWorkflow.Sessions.cs</c>) and
/// inbound wakes (<c>RunWorkflow.Inbound.cs</c>).
/// </summary>
[Workflow(RunWorkflowNames.Workflow)]
public partial class RunWorkflow
{
    static readonly RetryPolicy Forever = new() { InitialInterval = TimeSpan.FromSeconds(1), MaximumInterval = TimeSpan.FromSeconds(10), MaximumAttempts = 0 };

    static readonly ActivityOptions Light = new() { StartToCloseTimeout = TimeSpan.FromMinutes(2), RetryPolicy = Forever };

    readonly RunStart input;

    // Carried across continue-as-new. Set in the initialiser, because Temporal can deliver signals and updates
    // before the run method starts (DR14).
    readonly List<ChildRun> children;
    readonly List<string> pendingChildEnds;
    readonly List<ParentPatchSignal> pendingParentPatches;
    readonly List<AppliedAnswer> answered;
    readonly List<string> recordedOpenWaits;
    readonly List<HandOver> handOvers;
    long timelineSequence;
    bool cancelledByParent;
    int activitiesStartedWhileDraining;
    int childEndsWhileDraining;
    int answersWhileDraining;
    readonly List<OpenQuestion> questions;
    readonly List<string> closedQuestions;

    // This run only.
    RunCore? core;
    LibrarySet? libraries;
    RunPolicy? policy;
    readonly List<QuestionRecord> questionRecords = [];
    string lastRecordedStatus = "";
    bool draining;
    bool dirty;
    int interruptedThisRun;
    int inFlightActivities;
    readonly Dictionary<string, CancellationTokenSource> stepTokens = [];
    readonly HashSet<string> interrupted = new(StringComparer.Ordinal);
    CancellationTokenSource? childWatch;
    readonly HashSet<string> confirmsStarted = new(StringComparer.Ordinal);

    [WorkflowInit]
    public RunWorkflow(RunStart input)
    {
        this.input = input;
        var c = input.Carried;
        children = c?.Children.ToList() ?? [];
        pendingChildEnds = c?.PendingChildEnds.ToList() ?? [];
        pendingParentPatches = c?.PendingParentPatches.ToList() ?? [];
        answered = c?.Answered.ToList() ?? [];
        recordedOpenWaits = c?.RecordedOpenWaits.ToList() ?? [];
        handOvers = c?.HandOvers.ToList() ?? [];
        timelineSequence = c?.TimelineSequence ?? 0;
        cancelledByParent = c?.CancelledByParent ?? false;
        activitiesStartedWhileDraining = c?.ActivitiesStartedWhileDraining ?? 0;
        childEndsWhileDraining = c?.ChildEndsWhileDraining ?? 0;
        answersWhileDraining = c?.AnswersWhileDraining ?? 0;
        questions = c?.Questions?.ToList() ?? [];
        closedQuestions = c?.ClosedQuestions?.ToList() ?? [];
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
            libraries = RunActivities.Build(prepared.Libraries, new DiagnosticBag());
            var (started, diagnostics) = RunCore.Start(libraries, Template, RunActivities.RunInfoOf(input) with { DefaultBranches = prepared.DefaultBranches },
                policy.ToOptions() with { BranchFromPattern = Workflow.Patched(BranchPatternPatch), Steering = Workflow.Patched(SteeringPatch) });
            core = started ?? throw new ApplicationFailureException("the plan passed run start but does not load: " + string.Join("; ", diagnostics.Take(3)));
            // docs/09 section 1: a warning changes nothing, and is listed in the run start record (the timeline).
            if (prepared.Warnings.Count > 0 && Workflow.Patched(StartWarningsPatch))
                foreach (var warning in prepared.Warnings)
                    core.State.Trace.Add(new TraceEntry("warning", "", warning));
        }
        else
        {
            var snapshot = await Workflow.ExecuteActivityAsync((RunActivities a) => a.LoadLibrariesAsync(input.LockRef), Light);
            policy = input.Policy!;
            libraries = RunActivities.Build(snapshot, new DiagnosticBag());
            core = RunCore.Resume(libraries, Template, input.Carried.Core,
                policy.ToOptions() with { BranchFromPattern = Workflow.Patched(BranchPatternPatch), Steering = Workflow.Patched(SteeringPatch) });
            // Questions carried over a hand-over get their timers again (docs/02 section 3: timers are re-created from state).
            foreach (var open in questions.ToList())
                _ = TimeOutQuestionAsync(open);
            // Children an earlier run started can no longer be awaited here; a watch notices one that ends without
            // signalling (M3 follow-up 10).
            if (children.Count > 0 && Workflow.Patched(ChildWatchPatch))
                _ = WatchCarriedChildrenAsync(children.Select(c => c.WorkflowId).ToList());
        }

        // The floor on the event threshold and the byte threshold came after histories were recorded; older
        // histories replay without them (docs/09 section 4.1).
        var limits = Workflow.Patched(HandOverLimitsPatch);
        var events = limits ? Math.Max(input.HandOverEvents, ConfigurationReader.MinimumHandOverEvents) : input.HandOverEvents;
        while (true)
        {
            dirty = false;
            ProcessSignals();
            if (!draining && core.State.Status == RunStatus.Running
                && (Workflow.CurrentHistoryLength >= events || (limits && Workflow.CurrentHistorySize >= HandOverBytes)))
                draining = true;
            if (draining)
                InterruptWaits();

            await ExecuteAsync(draining ? core.Flush() : core.Advance());
            await RecordAsync();

            if (core.State.Status != RunStatus.Running)
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
    /// An agent asked a question (docs/04 section 3.4). The shell, not the core, holds it: it is a wait on a running
    /// step, which the step's activity serves, not a step of the plan. Idempotent by the question's id (LOG 59).
    /// </summary>
    [WorkflowSignal(RunWorkflowNames.QuestionOpened)]
    public Task QuestionOpenedAsync(QuestionSignal question)
    {
        if (questions.Any(q => q.Question.QuestionId == question.QuestionId) || closedQuestions.Contains(question.QuestionId))
            return Task.CompletedTask;
        var open = new OpenQuestion(question, Workflow.UtcNow + TimeSpan.FromSeconds(question.TimeoutSeconds));
        questions.Add(open);
        _ = TimeOutQuestionAsync(open);
        dirty = true;
        return Task.CompletedTask;
    }

    /// <summary>On timeout the agent is told no answer came, and carries on (docs/04 section 3.4; LOG 18).</summary>
    async Task TimeOutQuestionAsync(OpenQuestion open)
    {
        var wait = open.Deadline - Workflow.UtcNow;
        if (wait > TimeSpan.Zero)
            await Workflow.DelayAsync(wait);
        if (!questions.Remove(open))
            return;
        closedQuestions.Add(open.Question.QuestionId);
        questionRecords.Add(QuestionRecordOf(open.Question, QuestionRecord.TimedOut, null));
        core?.State.Trace.Add(new TraceEntry("question_timed_out", open.Question.StepId, open.Question.QuestionId));
        dirty = true;
    }

    QuestionRecord QuestionRecordOf(QuestionSignal q, string status, string? answer) =>
        new(q.StepKey, input.RunId, q.QuestionId, q.Text, q.Options, status, answer, Workflow.UtcNow);

    [WorkflowSignal(RunWorkflowNames.ParentCancelled)]
    public Task ParentCancelledAsync()
    {
        cancelledByParent = true;
        dirty = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// The update validator rejects only unknown and conflicting answers (LOG 58): an answer is accepted while
    /// the run drains for continue-as-new. Before the core is ready it accepts, and the handler checks.
    /// </summary>
    [WorkflowUpdateValidator(nameof(AnswerAsync))]
    public void ValidateAnswer(AnswerArgs answer)
    {
        if (Previous(answer) is { } previous)
        {
            if (previous.Key != Key(answer))
                throw new ApplicationFailureException($"conflict: {answer.WaitId} was already answered ({previous.Key})");
            return;
        }

        if (closedQuestions.Contains(answer.WaitId))
            throw new ApplicationFailureException($"timed out: the agent has carried on without an answer to {answer.WaitId}");
        if (questions.Any(q => q.Question.QuestionId == answer.WaitId))
        {
            if (string.IsNullOrWhiteSpace(answer.Outcome))
                throw new ApplicationFailureException($"{answer.WaitId} is a question: answer with text");
            return;
        }

        if (core is not null && core.Check(ToInput(answer)) is { } error)
            throw new ApplicationFailureException(error);
    }

    [WorkflowUpdate(RunWorkflowNames.Answer)]
    public async Task<AnswerReply> AnswerAsync(AnswerArgs answer)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        if (draining)
            answersWhileDraining++;
        if (Previous(answer) is { } previous)
            return previous.Key == Key(answer)
                ? new AnswerReply("duplicate")
                : throw new ApplicationFailureException($"conflict: {answer.WaitId} was already answered ({previous.Key})");
        if (questions.FirstOrDefault(q => q.Question.QuestionId == answer.WaitId) is { } question)
        {
            questions.Remove(question);
            closedQuestions.Add(answer.WaitId);
            questionRecords.Add(QuestionRecordOf(question.Question, QuestionRecord.Answered, answer.Outcome));
            answered.Add(new AppliedAnswer(answer.WaitId, Key(answer)));
            core!.State.Trace.Add(new TraceEntry("question_answered", question.Question.StepId, $"{answer.WaitId} by {answer.Actor}"));
            dirty = true;
            return new AnswerReply("recorded");
        }

        if (closedQuestions.Contains(answer.WaitId))
            throw new ApplicationFailureException($"timed out: the agent has carried on without an answer to {answer.WaitId}");
        var result = core!.Apply(ToInput(answer));
        if (!result.Accepted)
            throw new ApplicationFailureException(string.Join("; ", result.Errors));
        answered.Add(new AppliedAnswer(answer.WaitId, Key(answer)));
        core.State.Trace.Add(new TraceEntry("answered", answer.WaitId, $"{Key(answer)} by {answer.Actor}"));
        dirty = true;
        return new AnswerReply("recorded");
    }

    [WorkflowUpdateValidator(nameof(AbortAsync))]
    public void ValidateAbort(AbortArgs abort)
    {
        if (core is not null && core.Check(new RunAborted(abort.Reason)) is { } error)
            throw new ApplicationFailureException(error);
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
        core?.State.Status.Name() ?? "starting",
        core is not null,
        draining,
        handOvers.Count,
        Workflow.CurrentHistoryLength,
        core?.State.Waits.Select(w => new WaitView(w.Id, w.Kind.ToString(), w.StepId, w.Reason)).ToList() ?? [],
        core?.State.InFlight.ToList() ?? [],
        children.ToList(),
        handOvers.ToList(),
        activitiesStartedWhileDraining,
        childEndsWhileDraining,
        answersWhileDraining);

    AppliedAnswer? Previous(AnswerArgs answer) => answered.FirstOrDefault(a => a.WaitId == answer.WaitId);

    static string Key(AnswerArgs a) =>
        a.Outcome ?? string.Join(" ", new[] { a.Action, a.StepId, a.CleanUp ? "clean_up" : null, a.Attempts > 0 ? a.Attempts.ToString(System.Globalization.CultureInfo.InvariantCulture) : null, a.Status }.Where(x => x is not null));

    /// <summary>The core input an answer stands for, by the kind of the wait it names.</summary>
    RunInput ToInput(AnswerArgs a)
    {
        var wait = core?.State.Waits.FirstOrDefault(w => w.Id == a.WaitId)
                   ?? throw new ApplicationFailureException($"no wait is open as {a.WaitId}");
        switch (wait.Kind)
        {
            case WaitKind.Decision:
                return new DecisionAnswered(wait.StepId, a.Outcome ?? throw new ApplicationFailureException($"{a.WaitId} is a decision: answer with an outcome"));
            case WaitKind.PatchApproval:
                return a.Action switch
                {
                    "approve" => new PatchAnswered(a.WaitId, true, a.Actor),
                    "reject" => new PatchAnswered(a.WaitId, false, a.Actor),
                    _ => throw new ApplicationFailureException($"{a.WaitId} is a patch approval: answer approve or reject"),
                };
            default:
                // A breakpoint's continue may come as its outcome (ff run answer <run> <wait> continue).
                var kind = (wait.Kind == WaitKind.Breakpoint ? a.Action ?? a.Outcome : a.Action) switch
                {
                    "retry" => RecoveryKind.Retry,
                    "go_to" => RecoveryKind.GoTo,
                    "replan" => RecoveryKind.Replan,
                    "grant_attempts" => RecoveryKind.GrantAttempts,
                    "end" => RecoveryKind.End,
                    "continue" when wait.Kind == WaitKind.Breakpoint => RecoveryKind.Continue,
                    _ => throw new ApplicationFailureException(wait.Kind == WaitKind.Breakpoint
                        ? $"{a.WaitId} is a breakpoint: answer continue, retry, go_to, replan, grant_attempts or end"
                        : $"{a.WaitId} is an intervention: answer retry, go_to, replan, grant_attempts or end"),
                };
                RunStatus? status = a.Status is null ? null : RunStatuses.Parse(a.Status);
                return new InterventionAnswered(a.WaitId, new Recovery(kind, a.StepId, a.CleanUp, a.Attempts, status, a.BeyondHardCeiling));
        }
    }

    // ---- Carrying out the core's commands ----------------------------------------------------------

    void ProcessSignals()
    {
        if (core is null)
            return;
        if (cancelledByParent && core.State.Status == RunStatus.Running)
            core.Apply(new RunAborted("the parent run no longer needs this run"));

        foreach (var signal in pendingParentPatches.ToList())
        {
            pendingParentPatches.Remove(signal);
            var child = children.LastOrDefault(c => c.WorkflowId == signal.ChildWorkflowId);
            var d = new DiagnosticBag();
            var patch = PatchReader.Read(YamlWriter.Read(signal.Patch, "patch"), d);
            if (child is null || patch is null || d.HasErrors)
            {
                core.State.Trace.Add(new TraceEntry("patch_rejected", child?.StepId ?? "", $"from {signal.ChildWorkflowId}: " + string.Join("; ", d.Where(x => x.Blocks).Select(x => x.Message).DefaultIfEmpty("not from a running child"))));
                continue;
            }

            core.Apply(new ChildPatchProposed(child.StepId, patch, signal.ProposerStepId));
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
                case CancelStep c:
                    if (stepTokens.Remove(c.StepId, out var token))
                        token.Cancel();
                    if (children.LastOrDefault(x => x.StepId == c.StepId) is { } child)
                    {
                        children.Remove(child);
                        // An abort that closes pull requests closes the children's too (the run tree's), told first.
                        if (closePullRequests && core!.State.Status == RunStatus.Aborted)
                            await SignalQuietlyAsync(child.WorkflowId, ParentClosesPullRequests, []);
                        await SignalQuietlyAsync(child.WorkflowId, RunWorkflowNames.ParentCancelled, []);
                    }

                    break;
                case CleanUp c when Workflow.Patched(CleanUpPatch):
                    await CleanUpAsync(c.StepId);
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
                        core!.State.Trace.Add(new TraceEntry("library_override_refused", "", $"{o.Item} ({o.WaitId}): {e.Message}"));
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

    async Task StartStepAsync(StartStep s)
    {
        var step = core!.StepById(s.StepId)!;
        if (cleanUpFailures.Remove(step.Id, out var cleanUpError))
        {
            core.Apply(new StepCannotAnswer(step.Id, $"its clean_up could not reset the work branch: {cleanUpError}"));
            dirty = true;
            return;
        }

        Dictionary<string, object?> inputs;
        try
        {
            inputs = core.ResolveInputs(step.Id);
        }
        catch (EvaluationException e)
        {
            core.Apply(new StepCannotAnswer(step.Id, "its inputs cannot be evaluated: " + e.Message));
            dirty = true;
            return;
        }

        if (step is { Form: StepForm.Action, Action: "run.child" })
        {
            await StartChildAsync(step, s.Execution, inputs);
            return;
        }

        if (Workflow.Patched(StepStartPatch) && MissingConsumed(step) is { } missing)
        {
            core.Apply(new StepCannotAnswer(step.Id, missing));
            dirty = true;
            return;
        }

        var identity = new StepIdentity(input.RunId, Template.Name, step.Id, s.Execution, $"{input.RunId}/{step.Id}/{s.Execution}");
        ActionScope? scope = null;
        // Behind the marker: a history recorded before M5 neither computes a scope nor renders a prompt, so a render
        // that fails cannot replace the activity it scheduled. Only a run whose actions run against providers (fixed at
        // its start) gets a scope; otherwise, and in a dry run, the stand-in serves the action.
        if (step.Form == StepForm.Action && EngineActions.Built.Contains(step.Action!) && Workflow.Patched(StepStartPatch)
            && policy?.BuiltActions == true && !input.DryRun)
        {
            try
            {
                scope = ScopeOf(step);
                foreach (var name in Actions.All.GetValueOrDefault(step.Action!)?.ReferenceInputs ?? [])
                    if (inputs.GetValueOrDefault(name) is string reference && core.RenderPrompt(step.Id, reference) is { } rendered)
                        inputs[name] = rendered;
            }
            catch (EvaluationException e)
            {
                core.Apply(new StepCannotAnswer(step.Id, "a prompt it names cannot be rendered: " + e.Message));
                dirty = true;
                return;
            }
        }

        AgentStepSpec? agent = null;
        if (step.Form == StepForm.Agent && Workflow.Patched(AgentStepPatch))
        {
            try
            {
                agent = AgentSpecOf(step);
            }
            catch (EvaluationException e)
            {
                core.Apply(new StepCannotAnswer(step.Id, "its task cannot be rendered: " + e.Message));
                dirty = true;
                return;
            }
        }

        var call = new StepCall(identity, step.Form == StepForm.Agent ? "agent" : "action",
            step.Form == StepForm.Agent ? step.Agent!.Role?.Text ?? "" : step.Action!,
            inputs.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)),
            Contract(step), input.DryRun, Duration(step.Limits?.Timeout), agent);
        _ = RunStepAsync(step, s.Execution, call, scope);
    }

    /// <summary>
    /// Check 32 at step start (docs/09 section 2): every artifact the step consumes, and does not mark <c>optional</c>,
    /// exists: from its own run, its parent or root as they were when this run started, or a child as it ended. Before
    /// <see cref="ChildInputsPatch"/>, only artifacts from its own run were checked.
    /// </summary>
    string? MissingConsumed(Step step)
    {
        var crossRun = Workflow.Patched(ChildInputsPatch);
        var missing = step.Consumes.Where(c => !c.Optional && !IsLedgerView(c)
                                               && (crossRun ? ConsumedArtifact(step, c) is null : c.From == "current" && LatestArtifact(step, c.Name) is null))
            .Select(ConsumedKey).Distinct().ToList();
        return missing.Count == 0 ? null : $"check 32 ({Checks.Title(Checks.ConsumedArtifacts)}): {step.Id} consumes {string.Join(", ", missing)}, which no step has produced";
    }

    /// <summary>
    /// The artifact a step consumes (docs/03 section 7.2): from this run, the latest of its name nearest the step; from the
    /// parent or root, as it was when this run started (the root of a root run is the run itself); from a child, as the
    /// child ended (its <c>run.child</c> step holds it). Null when there is none.
    /// </summary>
    string? ConsumedArtifact(Step step, ArtifactIn consumed)
    {
        const string child = "child:";
        return consumed.From switch
        {
            "current" => LatestArtifact(step, consumed.Name),
            "parent" => input.ParentArtifacts?.GetValueOrDefault(consumed.Name),
            "root" => input.ParentWorkflowId is null ? LatestArtifact(step, consumed.Name) : input.RootArtifacts?.GetValueOrDefault(consumed.Name),
            _ when consumed.From.StartsWith(child, StringComparison.Ordinal) && core!.FindStep(consumed.From[child.Length..], step.Scope) is { Action: "run.child" } starter =>
                core.State.Steps.GetValueOrDefault(starter.Id)?.Current?.Artifacts.GetValueOrDefault(consumed.Name),
            _ => null,
        };
    }

    /// <summary>
    /// The name a consumed artifact goes by in the step's inputs and its sandbox: its own from this run, else with its
    /// source in front (<c>parent/contract</c>, <c>child/ticket_a/summary</c>), so a step can consume one name from two runs.
    /// </summary>
    /// <summary>The run's reserved <c>findings_ledger</c>: the ledger's view, staged by the agent worker, never a produced artifact (LOG 63, 167).</summary>
    static bool IsLedgerView(ArtifactIn consumed) => consumed.From == "current" && consumed.Name == FindingsLedger.ViewName;

    static string ConsumedKey(ArtifactIn consumed) =>
        consumed.From == "current" ? consumed.Name : $"{consumed.From.Replace(':', '/')}/{consumed.Name}";

    /// <summary>What an engine action knows of the run (<see cref="ActionScope"/>), computed from the plan and state.</summary>
    ActionScope ScopeOf(Step step)
    {
        var artifacts = new List<ArtifactEntry>();
        var pullRequests = new List<string>();
        foreach (var other in core!.Plan.AllSteps.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            if (core.State.Steps.GetValueOrDefault(other.Id)?.Current is not { } current)
                continue;
            // A run.child step holds its child's artifacts, which are not this run's.
            foreach (var (name, reference) in current.Artifacts.Where(_ => other.Action != "run.child").OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                var type = other.Produces.FirstOrDefault(p => p.Name == name)?.Type;
                var evidence = IsEvidence(type);
                artifacts.Add(new ArtifactEntry(other.Id, name, type?.Text, reference, other.Form == StepForm.Action ? other.Action : null, evidence));
            }
            if (other.Action == "scm.pull_request" && current.Outputs.GetValueOrDefault("pull_request") is string pr && pr.Length > 0)
                pullRequests.Add(pr);
        }

        string? repository;
        try
        {
            repository = core.ResolveRepository(step.Id);
        }
        catch (EvaluationException)
        {
            // A run with no repositories (the stand-in serves its actions): the action says so if it needs one.
            repository = null;
        }

        // What the step consumes, keyed and resolved as for an agent step at its place, so artifact.validate reads subjects
        // from the parent or root as this run was started with them (LOG 173, 191).
        var consumed = new Dictionary<string, string>(StringComparer.Ordinal);
        var declared = step.Consumes.Where(c => !IsLedgerView(c)).Select(ConsumedKey).Distinct().ToList();
        if (Workflow.Patched(ChildInputsPatch))
            foreach (var c in step.Consumes.Where(c => !IsLedgerView(c)))
                if (ConsumedArtifact(step, c) is { } reference)
                    consumed.TryAdd(ConsumedKey(c), reference);

        return new ActionScope(input.Repositories, repository, core.WorkBranch(), input.ParentBranch, artifacts, pullRequests, policy?.EngineMayMerge ?? [],
            Consumed: Workflow.Patched(ChildInputsPatch) ? consumed : null, TrackerIssue: input.TrackerIssue,
            Declared: Workflow.Patched(ChildInputsPatch) ? declared : null);
    }

    /// <summary>
    /// A <c>clean_up</c> route resets the work branch before its target runs again (docs/03 section 4.1). The activity
    /// finds where the step started from in the record store; it records what it did on the run's trace.
    /// </summary>
    async Task CleanUpAsync(string stepId)
    {
        var executions = core!.State.Steps.GetValueOrDefault(stepId)?.Executions ?? 0;
        var ran = core.State.Steps.Where(kv => kv.Value.Executions > 0).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
        var call = new CleanUpCall(input.RunId, stepId, executions, input.Repositories, core.WorkBranch(), ran, input.DryRun);
        BeginActivity();
        try
        {
            var result = await Workflow.ExecuteActivityAsync((RunActivities a) => a.CleanUpAsync(call), Light);
            foreach (var e in result.Events)
                core.State.Trace.Add(new TraceEntry("clean_up", stepId, e));
            if (result.Error is { } error)
                // The target step does not run on a branch that was not reset: it raises an intervention when it starts.
                cleanUpFailures[stepId] = error;
        }
        finally
        {
            EndActivity();
        }
    }

    /// <summary>The patch marker of run start's warnings on the timeline (M5): histories recorded before it list none.</summary>
    public const string StartWarningsPatch = "m5-start-warnings";

    /// <summary>The patch marker of the <c>clean_up</c> branch reset (M5): histories recorded before it reset nothing.</summary>
    public const string CleanUpPatch = "m5-clean-up";

    /// <summary>The patch marker of M5's step start: check 32, and an engine action's scope and rendered prompts.</summary>
    public const string StepStartPatch = "m5-step-start";

    /// <summary>What a step must report, for its provider.</summary>
    static StepContract Contract(Step step)
    {
        var zero = RunCore.ZeroOutputs(step);
        return new StepContract(
            step.Outcomes().Where(o => o.Name != Actions.AttemptsExhausted).Select(o => new OutcomeSpec(o.Name, o.Class.Name())).ToList(),
            zero.Keys.ToDictionary(n => n, n => RunCore.OutputType(step, n) ?? "dyn"),
            step.Produces.Select(p => new ArtifactSpec(p.Outcome, p.Name)).ToList(),
            zero.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)))
        {
            Reported = step.Outputs.Select(o => o.Name).ToList(),
        };
    }

    /// <summary>The patch marker of the agent step's settings (M4): histories recorded before it call the agent step without them.</summary>
    public const string AgentStepPatch = "m4-agent-step";

    /// <summary>The patch marker of the work branch following <c>metadata.branch_pattern</c> (LOG 97).</summary>
    public const string BranchPatternPatch = "m4-branch-pattern";

    /// <summary>The patch marker of waiting for a cancelled step's activity to finish (M3 follow-up 9).</summary>
    public const string WaitCancellationPatch = "m4-wait-cancellation";

    /// <summary>The patch marker of an agent step's activity timeout allowing for its open questions (LOG 106).</summary>
    public const string QuestionWaitPatch = "log-106-question-wait";

    /// <summary>
    /// An agent step's settings from the plan (docs/04 section 3): the role's write access and tool classes, the
    /// rendered task, limits, branches, and the latest artifact of each name it consumes. Workflow code holds the
    /// library (LOG 83), so the task is rendered here, deterministically.
    /// </summary>
    AgentStepSpec AgentSpecOf(Step step)
    {
        var agent = step.Agent!;
        var role = agent.Role?.Reference is { } roleRef ? libraries?.Resolve(roleRef)?.Role(roleRef.Name) : null;
        var (taskText, taskLibrary) = core!.RenderTask(step.Id);
        var limits = step.Limits;
        var questionsMax = limits?.QuestionsMax ?? (limits?.QuestionsTimeout is not null ? 3 : 0);
        var branch = core.WorkBranch();
        var consumed = new Dictionary<string, string>(StringComparer.Ordinal);
        var crossRun = Workflow.Patched(ChildInputsPatch);
        foreach (var c in step.Consumes)
            if (IsLedgerView(c))
                // The ledger changes outside the run's artifacts: the agent worker stages its view when the step starts (LOG 63).
                consumed[c.Name] = FindingsLedger.ViewName;
            else if ((crossRun ? ConsumedArtifact(step, c) : LatestArtifact(step, c.Name)) is { } reference)
                consumed.TryAdd(crossRun ? ConsumedKey(c) : c.Name, reference);
        return new AgentStepSpec(
            agent.WriteAccess ?? role?.WriteAccess ?? "none",
            role?.Tools ?? ["read", "search"],
            taskText,
            taskLibrary,
            agent.Skills.Select(s => s.Reference?.ToString()).OfType<string>().ToList(),
            (int)(limits?.CorrectionTurns ?? 2),
            (int)questionsMax,
            Duration(limits?.QuestionsTimeout) ?? AgentLimits.DefaultQuestionsTimeout,
            agent.Push,
            agent.OnUncommitted,
            core.Plan.Document.Level ?? "run",
            input.Repositories.ToDictionary(r => r, _ => branch),
            // A child's parent branch is its parent run's work branch; otherwise the agent worker reads the default branch from the code host.
            input.ParentBranch is { } parentBranch ? input.Repositories.ToDictionary(r => r, _ => parentBranch) : new Dictionary<string, string>(),
            consumed,
            Workflow.Info.WorkflowId,
            LockRef,
            TrackerIssue: input.TrackerIssue);
    }

    /// <summary>
    /// The agent step activity's own timeout: the step's limit and an hour's margin, plus, behind
    /// <see cref="QuestionWaitPatch"/>, the longest its questions may stay open, which do not count toward the limit (LOG 106).
    /// </summary>
    static TimeSpan AgentStepTimeout(StepCall call)
    {
        var timeout = (call.Timeout ?? AgentLimits.DefaultTimeout) + TimeSpan.FromHours(1);
        return call.Agent is { QuestionsMax: > 0 } spec && Workflow.Patched(QuestionWaitPatch) ? timeout + spec.QuestionsTimeout * spec.QuestionsMax : timeout;
    }

    /// <summary>A duration such as <c>30m</c> or <c>4h</c> (docs/03 section 2.1).</summary>
    static TimeSpan? Duration(string? text) =>
        text is { Length: > 1 } t && long.TryParse(t[..^1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? t[^1] switch { 's' => TimeSpan.FromSeconds(n), 'm' => TimeSpan.FromMinutes(n), 'h' => TimeSpan.FromHours(n), 'd' => TimeSpan.FromDays(n), _ => null }
            : null;

    async Task RunStepAsync(Step step, int execution, StepCall call, ActionScope? scope = null)
    {
        var token = new CancellationTokenSource();
        stepTokens[step.Id] = token;
        BeginActivity();
        StepOutcome? outcome = null;
        string? expandItems = null;
        try
        {
            if (step.Action == "plan.expand")
            {
                var list = ListArtifact(step);
                if (list is null)
                    outcome = new StepOutcome(StepOutcome.CannotAnswer, null, $"plan.expand: no artifact '{step.With?["from"].ScalarText()}' has been produced");
                else
                    expandItems = await Workflow.ExecuteActivityAsync((RunActivities a) => a.ReadListAsync(list), new ActivityOptions
                    {
                        StartToCloseTimeout = TimeSpan.FromMinutes(2),
                        RetryPolicy = Forever,
                        CancellationToken = token.Token,
                    });
            }
            else if (step.Form == StepForm.Agent)
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
            else
                outcome = await Workflow.ExecuteActivityAsync((RunActivities a) => a.RunActionAsync(call, scope), new ActivityOptions
                {
                    // An action that polls (CI tiers, sandbox commands) heartbeats; a wait between reads is a retry, not a held slot.
                    StartToCloseTimeout = (call.Timeout ?? TimeSpan.FromHours(2)) + TimeSpan.FromMinutes(10),
                    HeartbeatTimeout = scope is null ? null : TimeSpan.FromMinutes(2),
                    RetryPolicy = Forever,
                    CancellationToken = token.Token,
                    CancellationType = StepCancellation(),
                });
        }
        catch (Exception e) when (TemporalException.IsCanceledException(e))
        {
            if (interrupted.Remove(step.Id) && Current(step.Id, execution))
                core!.Apply(new StepInterrupted(step.Id, "interrupted to hand over to the next run; it resumes there (LOG 139)"));
            return;
        }
        catch (ActivityFailureException e) when (interrupted.Contains(step.Id) && e.RetryState == Temporalio.Api.Enums.V1.RetryState.CancelRequested)
        {
            // The cancel landed while an attempt ran: the attempt's failure (a wait's "not yet", or an infrastructure error)
            // is not retried once a cancel is requested, and acknowledges the cancel as surely as a cancellation does.
            if (interrupted.Remove(step.Id) && Current(step.Id, execution))
                core!.Apply(new StepInterrupted(step.Id, "interrupted to hand over to the next run; it resumes there (LOG 139)"));
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
            if (stepTokens.GetValueOrDefault(step.Id) == token)
            {
                stepTokens.Remove(step.Id);
                interrupted.Remove(step.Id);
            }

            CloseQuestionsOf(call.Step.IdempotencyKey);
        }

        if (!Current(step.Id, execution))
            return;
        try
        {
            if (expandItems is not null)
            {
                var items = YamlWriter.Read(expandItems, "items") as YSeq ?? throw new FormatException("plan.expand's list is not a list");
                var (patch, error) = PlanExpansion.Build(core!, step.Id, items.Items);
                if (error is null && Budget is { } budget && WorkItems.ChildSteps(patch!) is > 0 and var adding && Workflow.Patched(WorkItemsPatch))
                {
                    error = await WorkItemsRefusalAsync(new WorkItemRequest(budget, adding));
                    if (!Current(step.Id, execution))
                        return;
                }

                core!.Apply(error is null
                    ? new StepCompleted(step.Id, "expanded", Patch: patch)
                    : new StepCannotAnswer(step.Id, error));
                return;
            }

            Apply(step, outcome!);
        }
        catch (Exception e) when (e is not TemporalException)
        {
            // Whatever goes wrong with a step's result is the step's failure, not the run's (docs/04 section 1).
            if (Current(step.Id, execution))
                core!.Apply(new StepFailed(step.Id, ErrorClass.Work, $"the step's result could not be applied: {e.Message}"));
        }
    }

    /// <summary>The activity name of the agent step, registered by the agent worker.</summary>
    public const string AgentStepActivity = "RunAgentStep";

    /// <summary>
    /// A cancelled step's activity is waited for (M3 follow-up 9), so a cancelled agent step has stopped its agent
    /// and removed its sandbox before the run moves on. Histories recorded before this change keep the old type.
    /// </summary>
    static ActivityCancellationType StepCancellation() =>
        Workflow.Patched(WaitCancellationPatch) ? ActivityCancellationType.WaitCancellationCompleted : ActivityCancellationType.TryCancel;

    /// <summary>A step that ended no longer waits on its questions: their waits close unanswered.</summary>
    void CloseQuestionsOf(string stepKey)
    {
        foreach (var open in questions.Where(q => q.Question.StepKey == stepKey).ToList())
        {
            questions.Remove(open);
            closedQuestions.Add(open.Question.QuestionId);
            dirty = true;
        }
    }

    /// <summary>Whether a step's result still belongs to the run: the step is running, at the execution that started it.</summary>
    bool Current(string stepId, int execution) =>
        core!.State.Status == RunStatus.Running && core.State.InFlight.Contains(stepId) && core.State.Steps.GetValueOrDefault(stepId)?.Executions == execution;

    void Apply(Step step, StepOutcome outcome)
    {
        if (outcome.Findings is { } counts)
            core!.Apply(new FindingsChanged(counts, outcome.FindingsVersion));
        switch (outcome.Kind)
        {
            case StepOutcome.CannotAnswer:
                core!.Apply(new StepCannotAnswer(step.Id, outcome.Reason ?? "cannot answer"));
                return;
            case StepOutcome.WorkFailed:
                core!.Apply(new StepFailed(step.Id, ErrorClass.Work, outcome.Reason ?? "work failure"));
                return;
        }

        foreach (var e in outcome.Events ?? [])
            core!.State.Trace.Add(new TraceEntry("agent", step.Id, e));
        var report = outcome.Report!;
        PatchDocument? patch = null;
        if (report.Patch is { } text)
        {
            var d = new DiagnosticBag();
            patch = PatchReader.Read(YamlWriter.Read(text, "patch"), d);
            if (patch is null || d.HasErrors)
            {
                core!.Apply(new StepFailed(step.Id, ErrorClass.Work, "the patch does not read: " + string.Join("; ", d.Where(x => x.Blocks).Select(x => x.Message))));
                return;
            }
        }

        var completed = new StepCompleted(step.Id, report.Outcome,
            report.Outputs.ToDictionary(kv => kv.Key, kv => YamlWriter.ReadPlain(kv.Value)),
            report.Artifacts, patch, report.PatchToParent);
        var result = core!.Apply(completed);
        if (result.Accepted)
            IndexArtifacts(step, completed.Artifacts);
        if (result.Accepted && report.LibraryOverride is { } proposal)
            ProposeLibraryOverride(step, proposal);
        if (!result.Accepted)
        {
            // Correction turns arrive in M4; until then an output contract error fails the attempt (docs/04 section 3.1, step 5).
            core.Apply(new StepFailed(step.Id, ErrorClass.Work, "output contract: " + string.Join("; ", result.Errors)));
        }
    }

    /// <summary>The artifact a plan.expand step reads: the latest one of its <c>from</c> name, nearest its own scope.</summary>
    string? ListArtifact(Step step) => step.With?["from"].ScalarText() is { } name ? LatestArtifact(step, name) : null;

    /// <summary>Whether an artifact type is marked <c>evidence: true</c> (F7).</summary>
    bool IsEvidence(ItemRef? type) => type?.Reference is { } typeRef && libraries?.Resolve(typeRef)?.ArtifactType(typeRef.Name)?.Evidence == true;

    /// <summary>Artifacts steps produced since the last record, for the record store's artifact index.</summary>
    readonly List<ArtifactRecord> artifactRecords = [];

    /// <summary>
    /// The artifact index (docs/02 section 5): each artifact a step produced, by run, step, execution and name, written with
    /// the step's outcome. A parent reads its children's here (<see cref="RunActivities.ReadChildAsync"/>).
    /// </summary>
    void IndexArtifacts(Step step, IReadOnlyDictionary<string, string>? artifacts)
    {
        var execution = core!.State.Steps.GetValueOrDefault(step.Id)?.Current?.Number ?? 0;
        foreach (var (name, reference) in (artifacts ?? new Dictionary<string, string>()).OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var type = step.Produces.FirstOrDefault(p => p.Name == name)?.Type;
            artifactRecords.Add(new ArtifactRecord(input.RunId, step.Id, execution, name, type?.Text, reference,
                step.Form == StepForm.Action ? step.Action : null, IsEvidence(type), Workflow.UtcNow));
        }
    }

    /// <summary>The latest artifact of a name in this run, nearest a step's own scope (the run's top level when null).</summary>
    string? LatestArtifact(Step? step, string name)
    {
        var prefix = step?.Scope.Prefix ?? "";
        return core!.State.Steps
            .Where(kv => kv.Value.Current?.Artifacts.ContainsKey(name) == true && core.StepById(kv.Key) is not { Action: "run.child" })
            .OrderByDescending(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value.Current!.Artifacts[name])
            .FirstOrDefault();
    }

    /// <summary>The latest artifact of every name this run holds, nearest a step (the run's top level when null): what a child sees as its parent's.</summary>
    SortedDictionary<string, string> RunArtifacts(Step? near)
    {
        // A run.child step's artifacts are its child's: LatestArtifact skips them, so a name only a child produced is left out.
        var names = core!.State.Steps.SelectMany(kv => kv.Value.Current?.Artifacts.Keys ?? Enumerable.Empty<string>()).Distinct();
        var artifacts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in names)
            if (LatestArtifact(near, name) is { } reference)
                artifacts[name] = reference;
        return artifacts;
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

    async Task StartChildAsync(Step step, int execution, Dictionary<string, object?> inputs)
    {
        var childId = $"{Workflow.Info.WorkflowId}.{step.Id}.{execution}";
        var template = inputs.GetValueOrDefault("template") as string ?? "";
        var given = inputs.GetValueOrDefault("parameters") as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();
        // docs/03 section 9.2: the parent's resolved parameters for names the child declares, and its artifacts as they are now.
        var crossRun = Workflow.Patched(ChildInputsPatch);
        var parameters = (crossRun ? core!.ChildParameters(template, given) : given)
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value));
        var artifacts = crossRun ? RunArtifacts(step) : null;
        var repositories = (inputs.GetValueOrDefault("repositories") as IEnumerable<object?> ?? input.Repositories)
            .Select(r => RepositoryNames.As(r?.ToString() ?? "", input.Repositories)).ToList();
        var budget = Budget;
        if (budget is not null && Workflow.Patched(WorkItemsPatch)
            && await WorkItemsRefusalAsync(new WorkItemRequest(budget, 1,
                new RunRecord(childId, Workflow.Info.WorkflowId, template, "starting", input.Requester, repositories, input.DryRun, Workflow.UtcNow, null))) is { } refused)
        {
            core!.Apply(new StepCannotAnswer(step.Id, refused));
            dirty = true;
            return;
        }

        if (Workflow.Patched(ParentBranchesPatch))
        {
            // The child's parent branch is this run's work branch, in every repository the child is given (LOG 196).
            var made = await EnsureWorkBranchesAsync(new WorkBranchesCall(repositories, core!.WorkBranch(), input.ParentBranch, input.DryRun));
            foreach (var e in made.Events)
                core.State.Trace.Add(new TraceEntry("agent", step.Id, e));
            if (made.Error is { } error)
            {
                core.Apply(new StepCannotAnswer(step.Id, error));
                dirty = true;
                return;
            }
        }

        var childInput = new RunStart(childId, template, repositories, parameters,
            LockRef, input.DryRun, input.Requester, Workflow.Info.WorkflowId, step.Id, input.HandOverEvents, ParentBranch: core!.WorkBranch(),
            ParentArtifacts: artifacts, RootArtifacts: crossRun ? input.ParentWorkflowId is null ? artifacts : input.RootArtifacts : null,
            TrackerIssue: inputs.GetValueOrDefault("tracker_issue") as string, WorkItems: budget);
        ChildWorkflowHandle? handle = null;
        try
        {
            handle = await Workflow.StartChildWorkflowAsync((RunWorkflow w) => w.RunAsync(childInput), new ChildWorkflowOptions
            {
                Id = childId,
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
            core!.Apply(new StepFailed(step.Id, ErrorClass.Work, "the child run could not be started: " + e.Message));
            dirty = true;
            return;
        }

        children.Add(new ChildRun(step.Id, execution, childId));
        core!.State.Trace.Add(new TraceEntry("child_started", step.Id, childId));
        if (handle is not null && Workflow.Patched(ChildWatchPatch))
            _ = AwaitChildAsync(handle, childId);
    }

    /// <summary>The patch marker of a parent creating its work branch in a child's repositories before starting it (LOG 196).</summary>
    public const string ParentBranchesPatch = "m6-parent-branches";

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

    /// <summary>The patch marker of a child's inherited parameters and artifacts, and of consumes across runs (M3 follow-ups 2 and 3).</summary>
    public const string ChildInputsPatch = "m6-child-inputs";

    /// <summary>The patch marker of watching children for an end they did not signal (M3 follow-up 10).</summary>
    public const string ChildWatchPatch = "m6-child-watch";

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
        if (children.Any(c => c.WorkflowId == childId) && !pendingChildEnds.Contains(childId))
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
            var watching = carried.Where(id => children.Any(c => c.WorkflowId == id) && !pendingChildEnds.Contains(id)).ToList();
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

    // ---- Delivery: the work-item budget and declarative tracker updates (M6) ------------------------

    /// <summary>The patch marker of the work-item budget (F11): histories recorded before it start children unchecked.</summary>
    public const string WorkItemsPatch = "m6-work-items";

    /// <summary>The patch marker of declarative tracker updates: histories recorded before it made none.</summary>
    public const string TrackerUpdatesPatch = "m6-tracker-updates";

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
        var trace = core.State.Trace;
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
        if (!Workflow.Patched(TrackerUpdatesPatch))
            return;
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

        core!.State.Trace.Add(new TraceEntry("tracker", update.StepId, $"{update.Why}: {note}"));
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
        if (children.LastOrDefault(c => c.WorkflowId == childWorkflowId) is not { } child)
            return;
        children.Remove(child);
        var status = read.Status == "Completed" ? RunStatuses.Parse(read.Result ?? "") ?? RunStatus.Failed : RunStatus.Failed;
        // A refused end stops the step for a person; histories recorded before the marker dropped it.
        if (Current(child.StepId, child.Execution) && core!.Apply(new StepCompleted(child.StepId, RunCore.ChildOutcome(status), Artifacts: read.Artifacts)) is { Accepted: false } refused
            && Workflow.Patched(ChildInputsPatch))
            core.Apply(new StepCannotAnswer(child.StepId, "the child's end could not be applied: " + string.Join("; ", refused.Errors)));
    }

    // ---- Records, hand-over and the end ------------------------------------------------------------

    /// <summary>Writes what changed since the last record: new timeline entries, opened and closed waits, the run's status. Returns whether there was anything.</summary>
    async Task<bool> RecordAsync()
    {
        // Checked again after each update, with no await between the last check and the timeline taken below.
        while (NextTrackerUpdate() is { } update)
            await TrackAsync(update);
        var now = Workflow.UtcNow;
        var timeline = core!.State.Trace.Select(t => new TimelineRecord(input.RunId, ++timelineSequence, t.Kind, t.StepId, t.Detail, now)).ToList();
        core.State.Trace.Clear();
        trackedThrough = 0;

        var waits = new List<WaitRecord>();
        var open = core.State.Waits.Select(w => w.Id).ToList();
        foreach (var wait in core.State.Waits.Where(w => !recordedOpenWaits.Contains(w.Id)))
            waits.Add(WaitRecordOf(wait, true, now));
        // Questions are the shell's, not the core's: recorded and closed below.
        bool Question(string id) => questions.Any(q => q.Question.QuestionId == id) || closedQuestions.Contains(id);
        foreach (var closed in recordedOpenWaits.Where(id => !open.Contains(id) && !Question(id)).ToList())
            waits.Add(new WaitRecord(input.RunId, closed, "", "", "", "", [], false, now));
        var keep = recordedOpenWaits.Where(Question).ToList();
        recordedOpenWaits.Clear();
        recordedOpenWaits.AddRange(open);
        recordedOpenWaits.AddRange(keep);
        var level = core.Plan.Document.Level ?? "run";
        foreach (var q in questions.Where(q => !recordedOpenWaits.Contains(q.Question.QuestionId)))
        {
            waits.Add(new WaitRecord(input.RunId, q.Question.QuestionId, "question", q.Question.StepId, $"{level}.question", q.Question.Text, q.Question.Options, true, now));
            recordedOpenWaits.Add(q.Question.QuestionId);
        }

        foreach (var closed in recordedOpenWaits.Where(closedQuestions.Contains).ToList())
        {
            waits.Add(new WaitRecord(input.RunId, closed, "", "", "", "", [], false, now));
            recordedOpenWaits.Remove(closed);
        }

        var answers = questionRecords.ToList();
        questionRecords.Clear();
        var artifacts = artifactRecords.ToList();
        artifactRecords.Clear();
        var locks = lockRecords.ToList();
        lockRecords.Clear();

        var status = core.State.Status.Name();
        var runs = status == lastRecordedStatus ? [] : new List<RunRecord> { RunRecordOf(status, null, now) };
        lastRecordedStatus = status;
        if (timeline.Count == 0 && waits.Count == 0 && runs.Count == 0 && answers.Count == 0 && artifacts.Count == 0 && locks.Count == 0)
            return false;
        var batch = new RecordBatch(runs, waits, timeline, Locks: locks.Count == 0 ? null : locks, Questions: answers.Count == 0 ? null : answers,
            Artifacts: artifacts.Count == 0 ? null : artifacts);
        await Workflow.ExecuteActivityAsync((RunActivities a) => a.RecordAsync(batch), Light);
        return true;
    }

    RunRecord RunRecordOf(string status, string? detail, DateTimeOffset now) =>
        new(input.RunId, input.ParentWorkflowId, input.Template, status, input.Requester, input.Repositories, input.DryRun, now, detail);

    WaitRecord WaitRecordOf(OpenWait wait, bool open, DateTimeOffset now)
    {
        var level = core!.Plan.Document.Level ?? "run";
        var step = core.StepById(wait.StepId);
        var (type, choices) = wait.Kind switch
        {
            WaitKind.Decision => (step?.Decision?.Type ?? $"{level}.decision", step?.DeclaredOutcomes.Select(o => o.Name).ToList() ?? []),
            WaitKind.PatchApproval => ($"{level}.patch_approval", new List<string> { "approve", "reject" }),
            WaitKind.Breakpoint => ($"{level}.breakpoint", new List<string> { "continue", "retry", "go_to", "replan", "grant_attempts", "end" }),
            _ => ($"{level}.escalation", new List<string> { "retry", "go_to", "replan", "grant_attempts", "end" }),
        };
        return new WaitRecord(input.RunId, wait.Id, wait.Kind.ToString().ToLowerInvariant(), wait.StepId, type, wait.Reason, choices, open, now);
    }

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
        // sandbox before the run ends. Histories recorded before this change ended without waiting.
        if (inFlightActivities > 0 && Workflow.Patched(WaitCancellationPatch))
            await Workflow.WaitConditionAsync(() => inFlightActivities == 0);
        if (closePullRequests)
        {
            await CloseAbortedPullRequestsAsync();
            await RecordAsync();
        }

        await TellParentAsync();
        return core!.State.Status.Name();
    }

    async Task TellParentAsync()
    {
        if (input.ParentWorkflowId is { } parent)
            await SignalQuietlyAsync(parent, RunWorkflowNames.ChildEnded, [Workflow.Info.WorkflowId]);
    }

    /// <summary>The patch marker of interrupting waits at a hand-over (LOG 139): histories recorded before it drain them.</summary>
    public const string InterruptWaitsPatch = "m6-interrupt-waits";

    /// <summary>
    /// LOG 139: a wait (<see cref="EngineActions.Waits"/>) may last days, so a draining run does not wait for it. Its
    /// activity is cancelled, and once the cancellation completes the core returns the step to pending at the same
    /// execution (<see cref="StepInterrupted"/>), which the next run starts again with the same identity, so the records
    /// the execution keeps (when it began waiting, the head it waits on) carry over. Other activities drain.
    /// </summary>
    void InterruptWaits()
    {
        childWatch?.Cancel();
        foreach (var (stepId, token) in stepTokens.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
        {
            if (interrupted.Contains(stepId) || core!.StepById(stepId) is not { Form: StepForm.Action, Action: { } action } || !EngineActions.Waits.Contains(action)
                || !Workflow.Patched(InterruptWaitsPatch))
                continue;
            interrupted.Add(stepId);
            interruptedThisRun++;
            token.Cancel();
        }
    }

    /// <summary>The patch marker of the hand-over threshold's floor and byte limit.</summary>
    public const string HandOverLimitsPatch = "m3-hand-over-limits";

    /// <summary>
    /// History bytes at which a run hands over whatever its event count (docs/02 section 3: the event threshold or
    /// the payload byte limit, whichever comes first). Temporal warns at 10 MB of history; this stays under it.
    /// </summary>
    public const int HandOverBytes = 8 * 1024 * 1024;

    /// <summary>The patch marker of the hand-over record (docs/09 section 4.1): histories recorded before it take the old path.</summary>
    public const string RecordHandOverPatch = "m3-record-hand-over";

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
            if (core.State.Status != RunStatus.Running)
                return false;
            if (late.Count > 0 || recorded || !Workflow.AllHandlersFinished || inFlightActivities > 0)
                continue;
            if (!handOverRecorded && Workflow.Patched(RecordHandOverPatch))
            {
                // The timeline shows each hand-over (M3); histories recorded before this change replay without it.
                handOverRecorded = true;
                core.State.Trace.Add(new TraceEntry("continued_as_new", "", $"run {handOvers.Count + 1} follows at {Workflow.CurrentHistoryLength} history events"));
                continue;
            }

            throw ContinueAsNew();
        }
    }

    ContinueAsNewException ContinueAsNew()
    {
        handOvers.Add(new HandOver(handOvers.Count, Workflow.CurrentHistoryLength, inFlightActivities, children.Count, core!.State.Waits.Count, pendingChildEnds.Count,
            interruptedThisRun));
        var carried = new ShellState(core.Snapshot(), children.ToList(), pendingChildEnds.ToList(), pendingParentPatches.ToList(), answered.ToList(),
            recordedOpenWaits.ToList(), timelineSequence, handOvers.ToList(), cancelledByParent,
            activitiesStartedWhileDraining, childEndsWhileDraining, answersWhileDraining, questions.ToList(), closedQuestions.ToList());
        // The run's lock carries its library overrides (docs/08 section 5.2).
        var next = input with { Policy = policy, Carried = carried, LockRef = LockRef };
        return Workflow.CreateContinueAsNewException((RunWorkflow w) => w.RunAsync(next));
    }
}
