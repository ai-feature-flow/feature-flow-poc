using FeatureFlow.Engine.Actions;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
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
/// session's files at it, and the decision takes the submitted outcome with the files as its artifacts and the person as
/// actor. Any other answer closes the session. What a session opens with, imports and checks is the core's
/// (<see cref="RunCore.SessionFacts"/>, <see cref="RunCore.SessionImport"/>, <see cref="RunCore.CheckTarget"/>).
/// </summary>
public partial class RunWorkflow
{
    /// <summary>A session's activities have a person waiting on them: a few tries, then the person is told, rather than a wait for ever.</summary>
    static readonly ActivityOptions SessionActivity = new()
    {
        StartToCloseTimeout = TimeSpan.FromMinutes(5),
        RetryPolicy = new RetryPolicy { InitialInterval = TimeSpan.FromSeconds(1), MaximumInterval = TimeSpan.FromSeconds(10), MaximumAttempts = 5 },
    };

    // This run only: a check id already started, and a submission being imported, by wait id. A submission is an activity
    // in flight, not state, so it stays here; the core is told of it when it routes an answer (RunCore.Route).
    readonly HashSet<string> sessionChecks = new(StringComparer.Ordinal);
    readonly HashSet<string> submitting = new(StringComparer.Ordinal);

    // ---- Opening ---------------------------------------------------------------------------------------

    async Task OpenSessionAsync(OpenGate opened)
    {
        if (!core!.Apply(new GateSessionOpened(opened.WaitId)).Accepted)
            return;
        var wait = opened.WaitId;
        SessionOpen open;
        try
        {
            var f = core.SessionFacts(opened.StepId);
            open = new SessionOpen(input.RunId, opened.StepId, opened.Execution, SessionNames.Id(input.RunId, wait), wait, f.Repository,
                SessionNames.Branch(input.RunId, wait), core.WorkBranch(), input.ParentBranch, f.Folder, f.Files, f.Prompt, f.Context, f.ChecksFrom,
                f.SubmitOutcome, f.AbandonOutcome, input.DryRun);
        }
        catch (EvaluationException e)
        {
            core.State.Trace.Add(new TraceEntry("session_failed", opened.StepId, $"{SessionNames.Id(input.RunId, wait)}: {e.Message}"));
            return;
        }

        BeginActivity();
        try
        {
            var result = await Workflow.ExecuteActivityAsync((RunActivities a) => a.OpenSessionAsync(open), SessionActivity);
            core.State.Trace.Add(new TraceEntry("session_opened", opened.StepId,
                $"{open.SessionId}: {open.Repository} branch {open.Branch} at {Revisions.Short(result.BaseRevision)}"));
        }
        catch (ActivityFailureException e)
        {
            // The decision stays open: the person can still abandon it, and a retry of the step opens a new session.
            core.State.Trace.Add(new TraceEntry("session_failed", opened.StepId, $"{open.SessionId}: {e.InnerException?.Message ?? e.Message}"));
        }
        finally
        {
            EndActivity();
        }
    }

    // ---- Answers: a submission, or any other answer that closes the session -------------------------------

    async Task<AnswerReply> SubmitAsync(AnswerArgs answer, string stepId)
    {
        submitting.Add(answer.WaitId);
        IReadOnlyDictionary<string, string> artifacts;
        BeginActivity();
        try
        {
            var (imported, error) = await ImportSessionAsync(stepId, answer.Revision!, "submit");
            artifacts = imported ?? throw new ApplicationFailureException($"session {SessionNames.Id(input.RunId, answer.WaitId)} was not submitted: {error}");
        }
        finally
        {
            EndActivity();
            submitting.Remove(answer.WaitId);
        }

        var result = core!.Answer(answer, artifacts);
        if (!result.Accepted)
            throw new ApplicationFailureException(string.Join("; ", result.Errors));
        core.State.Trace.Add(new TraceEntry("session_submitted", stepId,
            $"{SessionNames.Id(input.RunId, answer.WaitId)} at {answer.Revision} by {answer.Actor}: {string.Join(", ", artifacts.Keys.Order(StringComparer.Ordinal))}"));
        dirty = true;
        await EndSessionAsync(stepId, answer, SessionStatuses.Submitted, artifacts);
        return new AnswerReply("recorded");
    }

    Task CloseSessionAsync(AnswerArgs answer, string stepId) => EndSessionAsync(stepId, answer, SessionStatuses.Closed, null);

    async Task EndSessionAsync(string stepId, AnswerArgs answer, string status, IReadOnlyDictionary<string, string>? artifacts)
    {
        string repository;
        try
        {
            (repository, _) = core!.SessionWorkspace(stepId);
        }
        catch (EvaluationException)
        {
            repository = "";
        }

        var record = new SessionRecord(SessionNames.Id(input.RunId, answer.WaitId), input.RunId, answer.WaitId, stepId, answer.Actor, repository,
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
    /// The session's files at <paramref name="revision"/>, imported by the core's import action (<see cref="RunCore.SessionImport"/>) as
    /// the decision's artifacts (the file must exist; YAML and JSON must parse; the content is stored by hash). Null with the
    /// reason when they cannot be.
    /// </summary>
    async Task<(IReadOnlyDictionary<string, string>? Artifacts, string? Error)> ImportSessionAsync(string stepId, string revision, string purpose)
    {
        SessionImport import;
        try
        {
            import = core!.SessionImport(stepId);
        }
        catch (EvaluationException e)
        {
            return (null, e.Message);
        }

        var inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["repository"] = import.Repository,
            ["folder"] = import.Folder,
            ["files"] = import.Files.Select(f => (object?)new Dictionary<string, object?> { ["path"] = f.Path, ["name"] = f.Artifact, ["type"] = f.Type }).ToList(),
            ["commit"] = revision,
        };
        var contract = new StepContract([new OutcomeSpec(import.Outcome, "success")], new Dictionary<string, string>(),
            import.Files.Select(f => new ArtifactSpec(import.Outcome, f.Artifact)).ToList(), new Dictionary<string, string>());
        var call = new StepCall(new StepIdentity(input.RunId, Template.Name, stepId, import.Execution, $"{input.RunId}/{stepId}/{import.Execution}/{purpose}"),
            "action", import.ImportAction, inputs.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)), contract, input.DryRun, null);
        var scope = new ActionScope(input.Repositories, import.Repository, core.WorkBranch(), input.ParentBranch, [], [], [], LockRef: LockRef);
        try
        {
            var outcome = await Workflow.ExecuteActivityAsync((RunActivities a) => a.RunActionAsync(call, scope), SessionActivity);
            if (outcome is not { Kind: StepOutcome.Reported, Report: { } report })
                return (null, outcome.Reason ?? "the files could not be imported");
            var names = import.Files.Select(f => f.Artifact).ToHashSet(StringComparer.Ordinal);
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
        if (core is not null && !sessionChecks.Contains(check.CheckId) && core.CheckTarget(check, draining, submitting.Contains).Error is { } error)
            throw new ApplicationFailureException(error);
    }

    /// <summary>Starts a pre-submit check and replies at once; the check records its progress and result (<see cref="SessionCheckRecord"/>).</summary>
    [WorkflowUpdate(SessionNames.Check)]
    public async Task<AnswerReply> SessionCheckAsync(SessionCheckArgs check)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        if (sessionChecks.Contains(check.CheckId))
            return new AnswerReply("duplicate");
        var (target, session, error) = core!.CheckTarget(check, draining, submitting.Contains);
        if (error is not null)
            throw new ApplicationFailureException(error);
        sessionChecks.Add(check.CheckId);
        BeginActivity();
        _ = RunCheckAsync(check, session!, target!);
        return new AnswerReply("started");
    }

    async Task RunCheckAsync(SessionCheckArgs check, string session, string target)
    {
        try
        {
            await RecordCheckAsync(check, target, "", "running", null);
            var (artifacts, importError) = await ImportSessionAsync(session, check.Revision, "check-" + check.CheckId);
            if (artifacts is null)
            {
                await EndCheckAsync(check, target, "", "failed", null, $"the session's files at {Revisions.Short(check.Revision)} cannot be imported: {importError}");
                return;
            }

            var (spec, error) = core!.StepSpec(target, atStart: false);
            if (error is not null)
            {
                await EndCheckAsync(check, target, "", "failed", null, error);
                return;
            }

            // The execution the step will run as; the identity is not part of what the digest compares.
            var identity = new StepIdentity(input.RunId, Template.Name, target, core.Executions(target) + 1, SessionNames.CheckKey(input.RunId, target, check.CheckId));
            var call = CallOf(spec!, identity);
            var scope = ScopeOf(target, (session, artifacts));
            StepOutcome outcome;
            try
            {
                outcome = await Workflow.ExecuteActivityAsync((RunActivities a) => a.RunActionAsync(call, scope), ActionOptions(call, SessionActivity.RetryPolicy!));
            }
            catch (ActivityFailureException e)
            {
                await EndCheckAsync(check, target, "", "failed", null, e.InnerException?.Message ?? e.Message);
                return;
            }

            // What the activity itself received, as the step's own digest is (RunCore.NoteCheckInputs).
            var digest = outcome.Inputs ?? "";
            var report = outcome.Report;
            var passed = report is not null && core.IsSuccess(target, report.Outcome);
            await EndCheckAsync(check, target, digest, passed ? "passed" : "failed", report?.Artifacts.Values.FirstOrDefault(),
                report is not null ? report.Outcome : outcome.Reason ?? outcome.Kind);
        }
        finally
        {
            EndActivity();
        }
    }

    Task RecordCheckAsync(SessionCheckArgs check, string target, string digest, string status, string? result)
    {
        var batch = new RecordBatch(SessionChecks: [new SessionCheckRecord(check.SessionId, check.CheckId, target, check.Revision, digest, status, result, Workflow.UtcNow)]);
        return Workflow.ExecuteActivityAsync((RunActivities a) => a.RecordAsync(batch), Light);
    }

    async Task EndCheckAsync(SessionCheckArgs check, string target, string digest, string status, string? result, string detail)
    {
        // The detail (the step's outcome, or why the check could not run) goes on the run's timeline, which the API shows with the check.
        core!.State.Trace.Add(new TraceEntry("session_check", target, $"{check.SessionId} {check.CheckId} at {Revisions.Short(check.Revision)} by {check.Actor}: {status}: {detail}"));
        dirty = true;
        await RecordCheckAsync(check, target, digest, status, result);
    }
}
