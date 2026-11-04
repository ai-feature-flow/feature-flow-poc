using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Common;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace FeatureFlow.Engine;

/// <summary>
/// Interactive sessions on a decision (docs/04 section 4.4). When a decision with <c>session</c> opens, the run opens the
/// session: a branch, the workspace document and the surface's hand-off. A person, authenticated by the API, runs the
/// checks of the later steps <c>checks_from</c> names, through update <see cref="SessionNames.Check"/>; each check is built
/// by the code that builds the step itself (<see cref="CallOf"/>) and records the digest of what it was given, as the step
/// does when it runs (parity, docs/09 section 4.2). A submission is an answer carrying the exact commit: the run imports the
/// session's files at it with <c>artifact.import</c>, and the decision takes the submitted outcome with the files as its
/// artifacts and the person as actor. Any other answer closes the session.
/// </summary>
public partial class RunWorkflow
{
    /// <summary>The patch marker of interactive sessions (M7): histories recorded before it open no session and record no check digests.</summary>
    public const string SessionsPatch = "m7-sessions";

    /// <summary>A session's activities have a person waiting on them: a few tries, then the person is told, rather than a wait for ever.</summary>
    static readonly ActivityOptions SessionActivity = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy { InitialInterval = TimeSpan.FromSeconds(1), MaximumInterval = TimeSpan.FromSeconds(10), MaximumAttempts = 5 },
    };

    /// <summary>
    /// The decision waits this run opened a session for, behind <see cref="SessionsPatch"/>; carried over a hand-over. Only
    /// these are sessions: a decision whose plan declares <c>session</c> but that opened none (a history recorded before the
    /// marker, a dry run) is answered as any decision is.
    /// </summary>
    readonly HashSet<string> sessionWaits;

    // This run only: a check id already started, and a submission being imported, by wait id.
    readonly HashSet<string> sessionChecks = new(StringComparer.Ordinal);
    readonly HashSet<string> submitting = new(StringComparer.Ordinal);

    bool IsSession(string stepId) => core?.StepById(stepId)?.Decision?.Session is not null;

    /// <summary>The decision step with a session that the open wait <paramref name="waitId"/> is, or null.</summary>
    Step? SessionStep(string waitId) =>
        sessionWaits.Contains(waitId) && core?.State.Waits.FirstOrDefault(w => w.Id == waitId && w.Kind == WaitKind.Decision) is { } wait
                                      && core.StepById(wait.StepId) is { Decision.Session: not null } step
            ? step
            : null;

    // ---- Opening ---------------------------------------------------------------------------------------

    async Task OpenSessionAsync(OpenDecision opened)
    {
        var step = core!.StepById(opened.StepId)!;
        if (core.State.Waits.LastOrDefault(w => w.Kind == WaitKind.Decision && w.StepId == step.Id) is not { } wait)
            return;
        sessionWaits.Add(wait.Id);
        SessionOpen open;
        try
        {
            open = SessionOpenOf(step, wait.Id, opened.Execution);
        }
        catch (EvaluationException e)
        {
            core.State.Trace.Add(new TraceEntry("session_failed", step.Id, $"{SessionNames.Id(input.RunId, wait.Id)}: {e.Message}"));
            return;
        }

        BeginActivity();
        try
        {
            var result = await Workflow.ExecuteActivityAsync((RunActivities a) => a.OpenSessionAsync(open), SessionActivity);
            core.State.Trace.Add(new TraceEntry("session_opened", step.Id,
                $"{open.SessionId}: {open.Repository} branch {open.Branch} at {EngineActions.Short(result.BaseRevision)}"));
        }
        catch (ActivityFailureException e)
        {
            // The decision stays open: the person can still abandon it, and a retry of the step opens a new session.
            core.State.Trace.Add(new TraceEntry("session_failed", step.Id, $"{open.SessionId}: {e.InnerException?.Message ?? e.Message}"));
        }
        finally
        {
            EndActivity();
        }
    }

    SessionOpen SessionOpenOf(Step step, string waitId, int execution)
    {
        var (repository, folder) = Workspace(step);
        var record = core!.State.Steps.GetValueOrDefault(step.Id);
        // A draft starts from the artifact of the file's name the decision consumes, else from the last submission (a re-entry).
        string? Seed(string name) =>
            step.Consumes.FirstOrDefault(c => c.Name == name && !IsLedgerView(c)) is { } consumed && ConsumedArtifact(step, consumed) is { } reference
                ? reference
                : record?.Previous?.Artifacts.GetValueOrDefault(name);
        var context = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in step.Consumes.Where(c => !IsLedgerView(c)))
            if (ConsumedArtifact(step, c) is { } reference)
                context[ConsumedKey(c)] = reference;
        var spec = step.Decision!.Session!;
        var prompt = step.Decision.Prompt is { Unbound: false } p ? core.RenderPrompt(step.Id, p.Text) ?? "" : "";
        return new SessionOpen(input.RunId, step.Id, execution, SessionNames.Id(input.RunId, waitId), waitId, repository, SessionNames.Branch(input.RunId, waitId),
            core.WorkBranch(), input.ParentBranch, folder,
            spec.Files.Select(f => new SessionDocumentFile(f.Path, f.Name, f.Type?.Text, Seed(f.Name))).ToList(),
            prompt, context, spec.ChecksFrom, SubmitOutcome(step), AbandonOutcome(step));
    }

    /// <summary>The session's repository (its own, else the run's first) and folder, evaluated now.</summary>
    (string Repository, string Folder) Workspace(Step step)
    {
        var spec = step.Decision!.Session!;
        var repository = (spec.Repository is { } r ? core!.ResolveSessionValue(step.Id, r, "decision.session.repository") as string : null)
                         ?? input.Repositories.FirstOrDefault()
                         ?? throw new EvaluationException($"{step.Id}: the session names no repository and the run has none");
        var folder = spec.Folder is { } f ? core!.ResolveSessionValue(step.Id, f, "decision.session.folder") as string ?? "" : "";
        return (repository, folder);
    }

    /// <summary>The artifacts an outcome of a step promises: those declared for it, and those declared for every success.</summary>
    static IReadOnlyList<string> Promised(Step step, string outcome) => step.Produces
        .Where(p => p.Outcome == outcome || (p.Outcome is null && step.DeclaredOutcomes.Any(o => o.Name == outcome && o.Class == OutcomeClass.Success)))
        .Select(p => p.Name).Distinct().ToList();

    /// <summary>The outcome a submission takes: the one that promises the session's files (std: <c>submitted</c>), else the first success.</summary>
    static string SubmitOutcome(Step step)
    {
        var files = step.Decision!.Session!.Files.Select(f => f.Name).ToList();
        return step.DeclaredOutcomes.FirstOrDefault(o => Promised(step, o.Name) is { Count: > 0 } promised && files.All(promised.Contains))?.Name
               ?? step.DeclaredOutcomes.FirstOrDefault(o => o.Class == OutcomeClass.Success)?.Name
               ?? step.DeclaredOutcomes.First().Name;
    }

    /// <summary>The outcome abandoning takes: the first declared one that is not a success (std: <c>abandoned</c>).</summary>
    static string? AbandonOutcome(Step step) => step.DeclaredOutcomes.FirstOrDefault(o => o.Class != OutcomeClass.Success)?.Name;

    // ---- Answers: a submission, or any other answer that closes the session -------------------------------

    /// <summary>
    /// Why an answer to a session's decision is refused, or null. Only a submission (an answer with the exact commit) takes
    /// the outcome that promises the session's files, and its artifacts come from the run's own import, never the request.
    /// </summary>
    string? SessionAnswerError(AnswerArgs answer)
    {
        // Fails closed: a submission (an answer with a revision) is only ever for a session this run opened.
        if (SessionStep(answer.WaitId) is not { } step)
            return answer.Revision is null ? null : $"{answer.WaitId} is not an open session of run {input.RunId}: nothing to submit";
        var session = SessionNames.Id(input.RunId, answer.WaitId);
        if (submitting.Contains(answer.WaitId))
            return $"session {session} is being submitted";
        if (answer.Revision is null)
            return answer.Outcome is { } outcome && Promised(step, outcome).Count > 0
                ? $"{answer.WaitId} is session {session}: '{outcome}' takes the session's files, so submit it (ff session submit {session})"
                : null;
        if (answer.Artifacts is not null)
            return $"session {session}: a submission's artifacts are imported by the run from the commit, not given";
        return answer.Outcome == SubmitOutcome(step) ? null : $"session {session}: a submission takes '{SubmitOutcome(step)}', not '{answer.Outcome}'";
    }

    async Task<AnswerReply> SubmitAsync(AnswerArgs answer, Step step)
    {
        submitting.Add(answer.WaitId);
        IReadOnlyDictionary<string, string> artifacts;
        BeginActivity();
        try
        {
            var (imported, error) = await ImportSessionAsync(step, answer.Revision!, "submit");
            artifacts = imported ?? throw new ApplicationFailureException($"session {SessionNames.Id(input.RunId, answer.WaitId)} was not submitted: {error}");
        }
        finally
        {
            EndActivity();
            submitting.Remove(answer.WaitId);
        }

        var result = core!.Apply(new DecisionAnswered(step.Id, answer.Outcome!, artifacts));
        if (!result.Accepted)
            throw new ApplicationFailureException(string.Join("; ", result.Errors));
        answered.Add(new AppliedAnswer(answer.WaitId, Key(answer)));
        IndexArtifacts(step, artifacts);
        core.State.Trace.Add(new TraceEntry("answered", answer.WaitId, $"{Key(answer)} by {answer.Actor}"));
        core.State.Trace.Add(new TraceEntry("session_submitted", step.Id,
            $"{SessionNames.Id(input.RunId, answer.WaitId)} at {answer.Revision} by {answer.Actor}: {string.Join(", ", artifacts.Keys.Order(StringComparer.Ordinal))}"));
        dirty = true;
        await EndSessionAsync(step, answer, SessionStatuses.Submitted, artifacts);
        return new AnswerReply("recorded");
    }

    Task CloseSessionAsync(AnswerArgs answer, Step step) => EndSessionAsync(step, answer, SessionStatuses.Closed, null);

    async Task EndSessionAsync(Step step, AnswerArgs answer, string status, IReadOnlyDictionary<string, string>? artifacts)
    {
        string repository;
        try
        {
            (repository, _) = Workspace(step);
        }
        catch (EvaluationException)
        {
            repository = "";
        }

        var record = new SessionRecord(SessionNames.Id(input.RunId, answer.WaitId), input.RunId, answer.WaitId, step.Id, answer.Actor, repository,
            SessionNames.Branch(input.RunId, answer.WaitId), "", status, status == SessionStatuses.Submitted ? answer.Revision : null, artifacts, Workflow.UtcNow);
        // Counted as an activity, so a run that this answer ended waits for the record before it finishes.
        BeginActivity();
        try
        {
            await Workflow.ExecuteActivityAsync((RunActivities a) => a.EndSessionAsync(record), Light);
        }
        finally
        {
            EndActivity();
        }
    }

    /// <summary>
    /// The session's files at <paramref name="revision"/>, imported by <c>artifact.import</c> as the decision's artifacts
    /// (the file must exist; YAML and JSON must parse; the content is stored by hash). Null with the reason when they cannot be.
    /// </summary>
    async Task<(IReadOnlyDictionary<string, string>? Artifacts, string? Error)> ImportSessionAsync(Step step, string revision, string purpose)
    {
        string repository, folder;
        try
        {
            (repository, folder) = Workspace(step);
        }
        catch (EvaluationException e)
        {
            return (null, e.Message);
        }

        var files = step.Decision!.Session!.Files;
        var execution = core!.State.Steps.GetValueOrDefault(step.Id)?.Executions ?? 0;
        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["repository"] = repository,
            ["folder"] = folder,
            ["files"] = files.Select(f => (object?)new Dictionary<string, object?> { ["path"] = f.Path, ["name"] = f.Name, ["type"] = f.Type?.Text }).ToList(),
            ["commit"] = revision,
        };
        var contract = new StepContract([new OutcomeSpec("imported", "success")], new Dictionary<string, string>(),
            files.Select(f => new ArtifactSpec("imported", f.Name)).ToList(), new Dictionary<string, string>());
        var call = new StepCall(new StepIdentity(input.RunId, Template.Name, step.Id, execution, $"{input.RunId}/{step.Id}/{execution}/{purpose}"),
            "action", "artifact.import", inputs.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)), contract, input.DryRun, null);
        var scope = new ActionScope(input.Repositories, repository, core.WorkBranch(), input.ParentBranch, [], [], []);
        try
        {
            var outcome = await Workflow.ExecuteActivityAsync((RunActivities a) => a.RunActionAsync(call, scope), SessionActivity);
            if (outcome is not { Kind: StepOutcome.Reported, Report: { } report })
                return (null, outcome.Reason ?? "the files could not be imported");
            var names = files.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
            return (report.Artifacts.Where(kv => names.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal), null);
        }
        catch (ActivityFailureException e)
        {
            return (null, e.InnerException?.Message ?? e.Message);
        }
    }

    // ---- Pre-submit checks -------------------------------------------------------------------------------

    [WorkflowUpdateValidator(nameof(SessionCheckAsync))]
    public void ValidateSessionCheck(SessionCheckArgs check)
    {
        if (core is not null && !sessionChecks.Contains(check.CheckId) && CheckTarget(check, out _, out var error) is null)
            throw new ApplicationFailureException(error!);
    }

    /// <summary>Starts a pre-submit check and replies at once; the check records its progress and result (<see cref="SessionCheckRecord"/>).</summary>
    [WorkflowUpdate(SessionNames.Check)]
    public async Task<AnswerReply> SessionCheckAsync(SessionCheckArgs check)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        if (sessionChecks.Contains(check.CheckId))
            return new AnswerReply("duplicate");
        if (CheckTarget(check, out var session, out var error) is not { } target)
            throw new ApplicationFailureException(error!);
        sessionChecks.Add(check.CheckId);
        BeginActivity();
        _ = RunCheckAsync(check, session!, target);
        return new AnswerReply("started");
    }

    /// <summary>The later step a check stands for, and the session's decision step; or null with the reason it is refused.</summary>
    Step? CheckTarget(SessionCheckArgs check, out Step? session, out string? error)
    {
        session = null;
        error = null;
        if (core!.State.Status != RunStatus.Running)
            error = $"the run has ended ({core.State.Status.Name()})";
        else if (draining)
            error = "the run is handing over to its next run: try the check again in a moment";
        else if (SessionNames.WaitOf(check.SessionId, input.RunId) is not { } waitId || (session = SessionStep(waitId)) is null)
            error = $"session {check.SessionId} is not open in run {input.RunId}";
        else if (submitting.Contains(waitId))
            error = $"session {check.SessionId} is being submitted";
        else if (!SessionNames.IsCommit(check.Revision))
            error = $"check a full commit id, not '{check.Revision}': a branch can move";
        else if (!session.Decision!.Session!.ChecksFrom.Contains(check.StepId))
            error = $"{check.StepId} is not a check of session {check.SessionId} (checks_from: {string.Join(", ", session.Decision.Session.ChecksFrom)})";
        else if (core.FindStep(check.StepId, session.Scope) is not { } target)
            error = $"{check.StepId} names no step of the plan";
        else if (target.Action != SessionNames.CheckAction)
            error = $"{check.StepId} is {(target.Form == StepForm.Action ? target.Action : $"a{(target.Form == StepForm.Agent ? "n agent" : " decision")} step")}: " +
                    $"only an {SessionNames.CheckAction} step can be run as a pre-submit check (LOG 238)";
        else
            return target;
        return null;
    }

    async Task RunCheckAsync(SessionCheckArgs check, Step session, Step target)
    {
        try
        {
            await RecordCheckAsync(check, target, "", "running", null);
            var (artifacts, importError) = await ImportSessionAsync(session, check.Revision, "check-" + check.CheckId);
            if (artifacts is null)
            {
                await EndCheckAsync(check, target, "", "failed", null, $"the session's files at {EngineActions.Short(check.Revision)} cannot be imported: {importError}");
                return;
            }

            Dictionary<string, object?> inputs;
            try
            {
                inputs = core!.ResolveInputs(target.Id);
            }
            catch (EvaluationException e)
            {
                await EndCheckAsync(check, target, "", "failed", null, "its inputs cannot be evaluated: " + e.Message);
                return;
            }

            // The execution the step will run as; the identity is not part of what the digest compares.
            var execution = (core.State.Steps.GetValueOrDefault(target.Id)?.Executions ?? 0) + 1;
            var identity = new StepIdentity(input.RunId, Template.Name, target.Id, execution, SessionNames.CheckKey(input.RunId, target.Id, check.CheckId));
            var (call, scope, error) = CallOf(target, identity, inputs);
            if (error is not null)
            {
                await EndCheckAsync(check, target, "", "failed", null, error);
                return;
            }

            scope = AsSubmitted(scope, session, artifacts, target);
            StepOutcome outcome;
            try
            {
                outcome = await Workflow.ExecuteActivityAsync((RunActivities a) => a.RunActionAsync(call!, scope), new ActivityOptions
                {
                    StartToCloseTimeout = (call!.Timeout ?? TimeSpan.FromHours(2)) + TimeSpan.FromMinutes(10),
                    HeartbeatTimeout = scope is null ? null : TimeSpan.FromMinutes(2),
                    RetryPolicy = SessionActivity.RetryPolicy,
                });
            }
            catch (ActivityFailureException e)
            {
                await EndCheckAsync(check, target, "", "failed", null, e.InnerException?.Message ?? e.Message);
                return;
            }

            // What the activity itself received, as the step's own digest is (NoteCheckInputs).
            var digest = outcome.Inputs ?? "";
            var report = outcome.Report;
            var passed = report is not null && target.Outcomes().FirstOrDefault(o => o.Name == report.Outcome)?.Class == OutcomeClass.Success;
            await EndCheckAsync(check, target, digest, passed ? "passed" : "failed", report?.Artifacts.Values.FirstOrDefault(),
                report is not null ? report.Outcome : outcome.Reason ?? outcome.Kind);
        }
        finally
        {
            EndActivity();
        }
    }

    /// <summary>
    /// The scope a later step will have once the session submits these files: the session step holds them as its artifacts,
    /// resolved for the step as <see cref="ScopeOf"/> resolves them, and the step's own artifacts from an earlier execution are
    /// gone, since its start clears them (DR12).
    /// </summary>
    ActionScope? AsSubmitted(ActionScope? scope, Step session, IReadOnlyDictionary<string, string> artifacts, Step target)
    {
        if (scope is null)
            return null;
        var entries = scope.Artifacts.Where(a => a.StepId != session.Id && a.StepId != target.Id).ToList();
        foreach (var (name, reference) in artifacts)
        {
            var type = session.Produces.FirstOrDefault(p => p.Name == name)?.Type;
            entries.Add(new ArtifactEntry(session.Id, name, type?.Text, reference, null, IsEvidence(type)));
        }

        entries = entries.OrderBy(a => a.StepId, StringComparer.Ordinal).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();
        Dictionary<string, string>? consumed = null;
        if (scope.Consumed is not null)
        {
            consumed = new Dictionary<string, string>(scope.Consumed, StringComparer.Ordinal);
            foreach (var c in target.Consumes.Where(c => c.From == "current" && !IsLedgerView(c)))
            {
                // As LatestArtifact picks: nearest the step's scope, then by step id; the target's own are cleared at its start.
                var latest = entries.Where(a => a.Name == c.Name && core!.StepById(a.StepId) is not { Action: "run.child" })
                    .OrderByDescending(a => a.StepId.StartsWith(target.Scope.Prefix, StringComparison.Ordinal))
                    .ThenBy(a => a.StepId, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (latest is not null)
                    consumed[ConsumedKey(c)] = latest.Ref;
                else
                    consumed.Remove(ConsumedKey(c));
            }
        }

        return scope with { Artifacts = entries, Consumed = consumed };
    }

    Task RecordCheckAsync(SessionCheckArgs check, Step target, string digest, string status, string? result)
    {
        var batch = new RecordBatch(SessionChecks: [new SessionCheckRecord(check.SessionId, check.CheckId, target.Id, check.Revision, digest, status, result, Workflow.UtcNow)]);
        return Workflow.ExecuteActivityAsync((RunActivities a) => a.RecordAsync(batch), Light);
    }

    async Task EndCheckAsync(SessionCheckArgs check, Step target, string digest, string status, string? result, string detail)
    {
        // The detail (the step's outcome, or why the check could not run) goes on the run's timeline, which the API shows with the check.
        core!.State.Trace.Add(new TraceEntry("session_check", target.Id, $"{check.SessionId} {check.CheckId} at {EngineActions.Short(check.Revision)} by {check.Actor}: {status}: {detail}"));
        dirty = true;
        await RecordCheckAsync(check, target, digest, status, result);
    }

    /// <summary>
    /// A step that a session's <c>checks_from</c> names records the digest of what its activity received, as its checks
    /// recorded theirs, so the two can be compared (docs/09 section 4.2).
    /// </summary>
    void NoteCheckInputs(Step step, StepOutcome outcome)
    {
        if (outcome.Inputs is not { } digest
            || !core!.Plan.AllSteps.Any(s => s.Decision?.Session is { } spec && spec.ChecksFrom.Any(c => core.FindStep(c, s.Scope)?.Id == step.Id))
            || !Workflow.Patched(SessionsPatch))
            return;
        core.State.Trace.Add(new TraceEntry("check_inputs", step.Id, digest));
    }
}
