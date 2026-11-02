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
/// started with the abandon parent-close policy and signal the parent's workflow id, never a run id.
/// </summary>
[Workflow(RunWorkflowNames.Workflow)]
public class RunWorkflow
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

    // This run only.
    RunCore? core;
    RunPolicy? policy;
    string lastRecordedStatus = "";
    bool draining;
    bool dirty;
    int inFlightActivities;
    readonly Dictionary<string, CancellationTokenSource> stepTokens = [];
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
            var (started, diagnostics) = RunCore.Start(RunActivities.Build(prepared.Libraries, new DiagnosticBag()), Template, RunActivities.RunInfoOf(input), policy.ToOptions());
            core = started ?? throw new ApplicationFailureException("the plan passed run start but does not load: " + string.Join("; ", diagnostics.Take(3)));
        }
        else
        {
            var libraries = await Workflow.ExecuteActivityAsync((RunActivities a) => a.LoadLibrariesAsync(input.LockRef), Light);
            policy = input.Policy!;
            core = RunCore.Resume(RunActivities.Build(libraries, new DiagnosticBag()), Template, input.Carried.Core, policy.ToOptions());
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
                    "approve" => new PatchAnswered(a.WaitId, true),
                    "reject" => new PatchAnswered(a.WaitId, false),
                    _ => throw new ApplicationFailureException($"{a.WaitId} is a patch approval: answer approve or reject"),
                };
            default:
                var kind = a.Action switch
                {
                    "retry" => RecoveryKind.Retry,
                    "go_to" => RecoveryKind.GoTo,
                    "replan" => RecoveryKind.Replan,
                    "grant_attempts" => RecoveryKind.GrantAttempts,
                    "end" => RecoveryKind.End,
                    _ => throw new ApplicationFailureException($"{a.WaitId} is an intervention: answer retry, go_to, replan, grant_attempts or end"),
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
                        await SignalQuietlyAsync(child.WorkflowId, RunWorkflowNames.ParentCancelled, []);
                    }

                    break;
                case SendPatchToParent p when input.ParentWorkflowId is { } parent:
                    await SignalQuietlyAsync(parent, RunWorkflowNames.ParentPatch,
                        [new ParentPatchSignal(Workflow.Info.WorkflowId, p.StepId, PatchReader.Write(p.Patch))]);
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

    async Task StartStepAsync(StartStep s)
    {
        var step = core!.StepById(s.StepId)!;
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

        var identity = new StepIdentity(input.RunId, Template.Name, step.Id, s.Execution, $"{input.RunId}/{step.Id}/{s.Execution}");
        var call = new StepCall(identity, step.Form == StepForm.Agent ? "agent" : "action",
            step.Form == StepForm.Agent ? step.Agent!.Role?.Text ?? "" : step.Action!,
            inputs.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)),
            Contract(step), input.DryRun, null);
        _ = RunStepAsync(step, s.Execution, call);
    }

    /// <summary>What a step must report, for its provider.</summary>
    static StepContract Contract(Step step)
    {
        var zero = RunCore.ZeroOutputs(step);
        return new StepContract(
            step.Outcomes().Where(o => o.Name != Actions.AttemptsExhausted).Select(o => new OutcomeSpec(o.Name, o.Class.Name())).ToList(),
            zero.Keys.ToDictionary(n => n, n => RunCore.OutputType(step, n) ?? "dyn"),
            step.Produces.Select(p => new ArtifactSpec(p.Outcome, p.Name)).ToList(),
            zero.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)));
    }

    async Task RunStepAsync(Step step, int execution, StepCall call)
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
                outcome = await Workflow.ExecuteActivityAsync((RunActivities a) => a.RunAgentStepAsync(call), new ActivityOptions
                {
                    TaskQueue = AgentQueue,
                    StartToCloseTimeout = TimeSpan.FromHours(4),
                    HeartbeatTimeout = TimeSpan.FromSeconds(30),
                    RetryPolicy = Forever,
                    CancellationToken = token.Token,
                });
            else
                outcome = await Workflow.ExecuteActivityAsync((RunActivities a) => a.RunActionAsync(call), new ActivityOptions
                {
                    StartToCloseTimeout = TimeSpan.FromHours(1),
                    RetryPolicy = Forever,
                    CancellationToken = token.Token,
                });
        }
        catch (Exception e) when (TemporalException.IsCanceledException(e))
        {
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
                stepTokens.Remove(step.Id);
        }

        if (!Current(step.Id, execution))
            return;
        try
        {
            if (expandItems is not null)
            {
                var items = YamlWriter.Read(expandItems, "items") as YSeq ?? throw new FormatException("plan.expand's list is not a list");
                var (patch, error) = PlanExpansion.Build(core!, step.Id, items.Items);
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

    /// <summary>Whether a step's result still belongs to the run: the step is running, at the execution that started it.</summary>
    bool Current(string stepId, int execution) =>
        core!.State.Status == RunStatus.Running && core.State.InFlight.Contains(stepId) && core.State.Steps.GetValueOrDefault(stepId)?.Executions == execution;

    void Apply(Step step, StepOutcome outcome)
    {
        switch (outcome.Kind)
        {
            case StepOutcome.CannotAnswer:
                core!.Apply(new StepCannotAnswer(step.Id, outcome.Reason ?? "cannot answer"));
                return;
            case StepOutcome.WorkFailed:
                core!.Apply(new StepFailed(step.Id, ErrorClass.Work, outcome.Reason ?? "work failure"));
                return;
        }

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
        if (!result.Accepted)
        {
            // Correction turns arrive in M4; until then an output contract error fails the attempt (docs/04 section 3.1, step 5).
            core.Apply(new StepFailed(step.Id, ErrorClass.Work, "output contract: " + string.Join("; ", result.Errors)));
        }
    }

    /// <summary>The artifact a plan.expand step reads: the latest one of its <c>from</c> name, nearest its own scope.</summary>
    string? ListArtifact(Step step)
    {
        var name = step.With?["from"].ScalarText();
        if (name is null)
            return null;
        var prefix = step.Scope.Prefix;
        return core!.State.Steps
            .Where(kv => kv.Value.Current?.Artifacts.ContainsKey(name) == true)
            .OrderByDescending(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal))
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Value.Current!.Artifacts[name])
            .FirstOrDefault();
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
        var parameters = (inputs.GetValueOrDefault("parameters") as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>())
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value));
        var repositories = (inputs.GetValueOrDefault("repositories") as IEnumerable<object?> ?? input.Repositories).Select(r => r?.ToString() ?? "").ToList();
        var childInput = new RunStart(childId, inputs.GetValueOrDefault("template") as string ?? "", repositories, parameters,
            input.LockRef, input.DryRun, input.Requester, Workflow.Info.WorkflowId, step.Id, input.HandOverEvents);
        try
        {
            await Workflow.StartChildWorkflowAsync((RunWorkflow w) => w.RunAsync(childInput), new ChildWorkflowOptions
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
        if (Current(child.StepId, child.Execution))
            core!.Apply(new StepCompleted(child.StepId, RunCore.ChildOutcome(status)));
    }

    // ---- Records, hand-over and the end ------------------------------------------------------------

    /// <summary>Writes what changed since the last record: new timeline entries, opened and closed waits, the run's status. Returns whether there was anything.</summary>
    async Task<bool> RecordAsync()
    {
        var now = Workflow.UtcNow;
        var timeline = core!.State.Trace.Select(t => new TimelineRecord(input.RunId, ++timelineSequence, t.Kind, t.StepId, t.Detail, now)).ToList();
        core.State.Trace.Clear();

        var waits = new List<WaitRecord>();
        var open = core.State.Waits.Select(w => w.Id).ToList();
        foreach (var wait in core.State.Waits.Where(w => !recordedOpenWaits.Contains(w.Id)))
            waits.Add(WaitRecordOf(wait, true, now));
        foreach (var closed in recordedOpenWaits.Where(id => !open.Contains(id)).ToList())
            waits.Add(new WaitRecord(input.RunId, closed, "", "", "", "", [], false, now));
        recordedOpenWaits.Clear();
        recordedOpenWaits.AddRange(open);

        var status = core.State.Status.Name();
        var runs = status == lastRecordedStatus ? [] : new List<RunRecord> { RunRecordOf(status, null, now) };
        lastRecordedStatus = status;
        if (timeline.Count == 0 && waits.Count == 0 && runs.Count == 0)
            return false;
        var batch = new RecordBatch(runs, waits, timeline);
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
        await TellParentAsync();
        return core!.State.Status.Name();
    }

    async Task TellParentAsync()
    {
        if (input.ParentWorkflowId is { } parent)
            await SignalQuietlyAsync(parent, RunWorkflowNames.ChildEnded, [Workflow.Info.WorkflowId]);
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
        handOvers.Add(new HandOver(handOvers.Count, Workflow.CurrentHistoryLength, inFlightActivities, children.Count, core!.State.Waits.Count, pendingChildEnds.Count));
        var carried = new ShellState(core.Snapshot(), children.ToList(), pendingChildEnds.ToList(), pendingParentPatches.ToList(), answered.ToList(),
            recordedOpenWaits.ToList(), timelineSequence, handOvers.ToList(), cancelledByParent,
            activitiesStartedWhileDraining, childEndsWhileDraining, answersWhileDraining);
        var next = input with { Policy = policy, Carried = carried };
        return Workflow.CreateContinueAsNewException((RunWorkflow w) => w.RunAsync(next));
    }
}
