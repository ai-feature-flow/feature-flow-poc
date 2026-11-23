namespace FeatureFlow.Providers.Abstractions;

/// <summary>A content-addressed artifact: <c>sha256:&lt;hex&gt;</c> (docs/06 section 3.12).</summary>
public sealed record ArtifactRef(string Id);

public interface IArtifactStoreProvider : IProvider
{
    Task<ProviderResult<ArtifactRef>> PutAsync(Stream content, CancellationToken cancellationToken);

    Task<ProviderResult<Stream>> GetAsync(ArtifactRef reference, CancellationToken cancellationToken);

    Task<ProviderResult<Uri>> GetSignedUrlAsync(ArtifactRef reference, TimeSpan ttl, CancellationToken cancellationToken);
}

public interface ISecretsProvider : IProvider
{
    Task<ProviderResult<string>> ResolveAsync(string reference, CancellationToken cancellationToken);
}

// Records the record store holds (docs/02 section 5). The API reads only these, never Temporal queries.

/// <summary>
/// A run: its status, template and parent. Written again whenever it changes; a write replaces a record no newer than itself (by <see cref="UpdatedAt"/>), so a late write never undoes a newer one.
/// <see cref="Level"/> is its plan's level, <see cref="ParentStepId"/> the parent's step that started it, <see cref="TrackerIssue"/>
/// the issue key it was started for with the issue's title and link where read, and <see cref="StartedAt"/> when it first
/// started; all null on a run recorded before them.
/// </summary>
public sealed record RunRecord(
    string RunId,
    string? ParentRunId,
    string Template,
    string Status,
    string Requester,
    IReadOnlyList<string> Repositories,
    bool DryRun,
    DateTimeOffset UpdatedAt,
    string? Detail,
    string? Level = null,
    string? ParentStepId = null,
    string? TrackerIssue = null,
    string? TrackerTitle = null,
    string? TrackerUrl = null,
    DateTimeOffset? StartedAt = null);

/// <summary>
/// A durable wait for a person: a decision, an intervention or a patch approval, open or answered. A record with <see cref="Open"/> false closes the wait and keeps the rest of what the opening record said.
/// <see cref="Prompt"/> is the text to show (a gate's rendered prompt, a question's text, else the engine's reason); "reason" means only a person's (LOG 389).
/// <see cref="Consumed"/> is what a gate's step consumed when it opened, by reference (LOG 394).
/// <see cref="ReasonRequired"/> is the choices a person must give a reason for, those that route the step as a failure or end the run (LOG 502); null on a wait recorded before it.
/// <see cref="Cause"/> is why an intervention stopped its step, and <see cref="Patch"/> the patch a patch approval waits on; null otherwise.
/// </summary>
public sealed record WaitRecord(
    string RunId,
    string WaitId,
    string Kind,
    string StepId,
    string DecisionType,
    string Prompt,
    IReadOnlyList<string> Choices,
    bool Open,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConsumedRef>? Consumed = null,
    IReadOnlyList<string>? ReasonRequired = null,
    WaitCause? Cause = null,
    WaitPatch? Patch = null);

/// <summary>
/// Why an intervention stopped a step: the step, the outcome it ended with, the core's reason, the step's own last summary,
/// its attempts used and allowed (null for a decision), and every time it was sent back, oldest first.
/// </summary>
public sealed record WaitCause(
    string StepId,
    string? Outcome,
    string Reason,
    string? Said,
    int? AttemptsUsed,
    long? AttemptsAllowed,
    IReadOnlyList<SentBack> SentBack);

/// <summary>A time a step was sent back: its execution, the step and outcome that sent it back where known, and the text.</summary>
public sealed record SentBack(int Execution, string? ByStep, string? Outcome, string Text);

/// <summary>
/// A plan patch a patch approval waits on: the proposer's type and step, its reason, why it needs approval, the step the
/// run resumes at, the plan revision it applies to, whether a child run proposed it, and the patch as
/// <c>PatchReader.Write</c> wrote it.
/// </summary>
public sealed record WaitPatch(
    string ProposerType,
    string? ProposerStepId,
    string? Reason,
    IReadOnlyList<string> NeedsApproval,
    string? ResumeAt,
    long BaseRevision,
    bool FromChild,
    string Document);

/// <summary>
/// A step's counters in a run, written when they change; the last write per run and step wins: how often it ran, its
/// attempts used, allowed (with grants; null for a decision) and granted.
/// </summary>
public sealed record StepStateRecord(
    string RunId,
    string StepId,
    int Executions,
    int AttemptsUsed,
    long? AttemptsAllowed,
    long AttemptsGranted,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A pull request as a run's step last read it, written when it changed; the last write per run, repository and number
/// wins. <see cref="Files"/> and <see cref="Checks"/> are null where that read did not fetch them.
/// </summary>
public sealed record PullRequestRecord(
    string RunId,
    string Repository,
    int Number,
    string StepId,
    PullRequestState State,
    IReadOnlyList<PullRequestFile>? Files,
    CheckStatus? Checks,
    DateTimeOffset ReadAt);

/// <summary>A registered repository: its name (<c>owner/name</c>), host, link and default branch, and who registered it and when. The last write per name wins.</summary>
public sealed record RepositoryRecord(string Name, string Host, string? Url, string? DefaultBranch, string RegisteredBy, DateTimeOffset At);

/// <summary>An artifact a gate's step consumed: its key as staged (<c>name</c>, <c>parent/name</c>, <c>child/&lt;step&gt;/name</c>), its name and reference.</summary>
public sealed record ConsumedRef(string Key, string Name, string? Ref);

/// <summary>An open wait of a run still going, with its run: what <see cref="Queries.OpenWaits"/> returns.</summary>
public sealed record OpenWaitRecord(WaitRecord Wait, RunRecord Run);

/// <summary>One entry of a run's timeline, numbered in the order the run recorded it.</summary>
public sealed record TimelineRecord(string RunId, long Sequence, string Kind, string StepId, string? Detail, DateTimeOffset At);

/// <summary>
/// An answer or override from a person, recorded by the API with the authenticated actor before it is delivered (F8).
/// <see cref="Reason"/> is the person's reason (docs/05 section 5).
/// </summary>
public sealed record AnswerRecord(string RunId, string WaitId, string Actor, string Answer, string Result, DateTimeOffset At, string? Reason = null);

/// <summary>
/// A change the record store wrote, numbered in commit order by the store itself as it appends: a run's records changed
/// (<c>run_changed</c>), or one of its waits opened or closed (<c>wait_opened</c>, <c>wait_closed</c>, with <see cref="WaitId"/>).
/// </summary>
public sealed record ChangeRecord(long Sequence, string RunId, string Kind, string? WaitId, DateTimeOffset At);

/// <summary>
/// The kinds of <see cref="ChangeRecord"/>, and the changes a batch makes, which every record store writes in the same
/// transaction as the batch (docs/06 section 3.12, LOG 397).
/// </summary>
public static class ChangeKinds
{
    public const string RunChanged = "run_changed";
    public const string WaitOpened = "wait_opened";
    public const string WaitClosed = "wait_closed";

    /// <summary>
    /// One <c>run_changed</c> per distinct run whose runs, timeline, answers, overrides, artifacts, sessions, findings,
    /// finding events, step states or pull requests the batch writes, in the order first written; then one
    /// <c>wait_opened</c> or <c>wait_closed</c> per wait record. Nothing for a batch of other records (repositories are no run's).
    /// </summary>
    public static IReadOnlyList<(string RunId, string Kind, string? WaitId)> Of(RecordBatch batch)
    {
        var runs = new List<string>();
        foreach (var runId in (batch.Runs ?? []).Select(r => r.RunId)
                     .Concat((batch.Timeline ?? []).Select(t => t.RunId))
                     .Concat((batch.Answers ?? []).Select(a => a.RunId))
                     .Concat((batch.Overrides ?? []).Select(o => o.RunId))
                     .Concat((batch.Artifacts ?? []).Select(a => a.RunId))
                     .Concat((batch.Sessions ?? []).Select(s => s.RunId))
                     .Concat((batch.Findings ?? []).Select(f => f.RunId))
                     .Concat((batch.FindingEvents ?? []).Select(e => e.RunId))
                     .Concat((batch.StepStates ?? []).Select(s => s.RunId))
                     .Concat((batch.PullRequests ?? []).Select(p => p.RunId)))
            if (!runs.Contains(runId, StringComparer.Ordinal))
                runs.Add(runId);
        return
        [
            .. runs.Select(r => (r, RunChanged, (string?)null)),
            .. (batch.Waits ?? []).Select(w => (w.RunId, w.Open ? WaitOpened : WaitClosed, (string?)w.WaitId)),
        ];
    }
}

/// <summary>A run's library lock, by artifact reference (docs/08 section 5.1).</summary>
public sealed record LockRecord(string RunId, string LockRef, DateTimeOffset At);

// Records of agent steps (docs/02 section 4.2). The hook endpoint and engine MCP server keep nothing in a
// replica's memory: any replica serves any step from its token and these records.

/// <summary>
/// An agent step attempt, written by the agent step activity before the agent starts, keyed by the step's
/// idempotency key: its contract and limits, which the <c>report</c> and <c>ask_human</c> tools enforce.
/// </summary>
public sealed record AgentStepRecord(
    string StepKey,
    string RunId,
    string WorkflowId,
    string StepId,
    string Level,
    StepContract Contract,
    int CorrectionTurns,
    int QuestionsMax,
    long QuestionsTimeoutSeconds,
    DateTimeOffset At,
    bool Revoked = false);

/// <summary>A call of the <c>report</c> tool: accepted, or rejected with its errors. Appended, numbered per step.</summary>
public sealed record ReportRecord(string StepKey, int Sequence, bool Accepted, StepReport? Report, IReadOnlyList<string> Errors, DateTimeOffset At);

/// <summary>
/// A question an agent asked (docs/04 section 3.4), by the id the sandbox client gave it (LOG 59). Written
/// <c>open</c> by the endpoint and <c>answered</c> or <c>timed_out</c> by the run; the last write wins.
/// </summary>
public sealed record QuestionRecord(
    string StepKey,
    string RunId,
    string QuestionId,
    string Text,
    IReadOnlyList<string> Options,
    string Status,
    string? Answer,
    DateTimeOffset At)
{
    public const string Open = "open";
    public const string Answered = "answered";
    public const string TimedOut = "timed_out";
}

/// <summary>Something that happened during an agent step: a hook call and its decision, a push, model usage. Appended.</summary>
public sealed record AgentEventRecord(string StepKey, string Kind, string Detail, DateTimeOffset At);

// The findings ledger (docs/01 section 6, docs/02 section 5): one FindingRecord per finding, holding its current state,
// and its history as FindingEventRecords. A parent's view includes its descendants' ledgers, never the reverse.

/// <summary>
/// A finding in a run's ledger, written again whenever it changes; the last write wins. <see cref="FindingId"/> is
/// <see cref="Fingerprints.LedgerId"/> of the run that first raised it; <see cref="Finding"/> is the report entry as last
/// raised. <see cref="RaisedRevision"/> is the work branch head when it was raised or last reopened (<c>tests_added</c>
/// compares against it); <see cref="StateRevision"/> the head when its state last changed (a re-raise compares the code
/// at the fingerprint against it). <see cref="Fixes"/> counts moves to <c>fixed</c>, <see cref="Reraises"/> re-raises,
/// <see cref="Reopens"/> re-raises that reopened it; <see cref="ReraisedUnchanged"/> is whether the latest re-raise found
/// the code unchanged.
/// </summary>
public sealed record FindingRecord(
    string RunId,
    string FindingId,
    string Fingerprint,
    ReportedFinding Finding,
    string Source,
    string? Model,
    string State,
    string? RaisedRevision,
    string? StateRevision,
    int Reraises,
    int Fixes,
    int Reopens,
    bool ReraisedUnchanged,
    DateTimeOffset UpdatedAt);

/// <summary>What a <see cref="FindingEventRecord"/> records.</summary>
public static class FindingEventKinds
{
    public const string Raised = "raised";
    public const string Reraised = "reraised";
    public const string Reopened = "reopened";

    /// <summary>A state change from a triage: <c>findings.triage</c>, or a person's decision.</summary>
    public const string Triaged = "triaged";
}

/// <summary>
/// One entry of a finding's history, appended: what happened, the state after it, who did it (a step, an external
/// source, or the authenticated person, F8) and why, at which work branch revision, and for a re-raise whether the code
/// at the fingerprint had changed since the state last changed. <see cref="Key"/> makes the append idempotent: one entry
/// per run, finding and key, so a retried action adds nothing.
/// </summary>
public sealed record FindingEventRecord(
    string RunId,
    string FindingId,
    string Key,
    string Kind,
    string State,
    string Actor,
    string? Reason,
    string? StepId,
    string? Revision,
    bool? CodeChanged,
    DateTimeOffset At);

/// <summary>
/// An artifact a run's step produced (the evidence index, docs/02 section 5): the step and execution, the name and
/// declared type, the reference, the engine action that made it (null for an agent step or decision), and whether its
/// type is marked evidence. One per run, step, execution and name, so a retried append adds nothing. A parent reads its
/// completed children's here (a trace across children, docs/04 section 10.2).
/// </summary>
public sealed record ArtifactRecord(
    string RunId,
    string StepId,
    int Execution,
    string Name,
    string? Type,
    string Ref,
    string? Action,
    bool Evidence,
    DateTimeOffset At);

// M7: people and steering (docs/05 section 5, docs/06 sections 3.10 and 3.11).

/// <summary>
/// A run-time override (docs/05 section 5), recorded by the API with the authenticated actor (F8), its reason (null when
/// none was given, LOG 502) and the
/// change, before it is delivered and again with the result, as answers are: appended, never replaced. <see cref="Key"/> is
/// the catalogue key the caller was authorised for (<c>Model.Overrides</c>), a <c>_protected</c> or <c>_beyond</c> variant
/// included; <see cref="Result"/> is <c>sent</c>, then the workflow's reply or <c>refused: ...</c>.
/// </summary>
public sealed record OverrideRecord(
    string RunId,
    string OverrideId,
    string Key,
    string Actor,
    string? Reason,
    string? StepId,
    IReadOnlyDictionary<string, string> Change,
    string Result,
    DateTimeOffset At);

/// <summary>
/// A notification sent (docs/06 section 3.10), once per <see cref="NotificationEvents.Key"/>: the first write is kept, so a
/// restarted sender finds it and does not post again. <see cref="Thread"/> is the thread it was posted in; a root run's
/// first message, posted with none, is the thread its children reply in.
/// </summary>
public sealed record NotificationRecord(
    string Key,
    string RunId,
    string? StepId,
    string Event,
    MessageRef Message,
    MessageRef? Thread,
    DateTimeOffset At);

/// <summary>The states of a <see cref="SessionRecord"/>.</summary>
public static class SessionStatuses
{
    public const string Prepared = "prepared";
    public const string Submitted = "submitted";
    public const string Closed = "closed";
}

/// <summary>
/// An interactive session (docs/04 section 4.4) on a decision's wait, written again whenever it changes; the last write
/// wins. <see cref="Person"/> is the authenticated actor it is for. On submission, <see cref="SubmittedRevision"/> is the
/// exact commit imported and <see cref="Artifacts"/> the declared artifacts it produced, by name.
/// </summary>
public sealed record SessionRecord(
    string SessionId,
    string RunId,
    string WaitId,
    string StepId,
    string Person,
    string Repository,
    string Branch,
    string BaseRevision,
    string Status,
    string? SubmittedRevision,
    IReadOnlyDictionary<string, string>? Artifacts,
    DateTimeOffset At);

/// <summary>
/// A pre-submit check a session ran (docs/04 section 4.4), by session and check id, the last write winning: the later
/// step it stands for, the commit, and <see cref="Inputs"/>, a digest of the task, tools and model it was given, which the
/// later step's own run must match (parity, docs/09 section 4.2). <see cref="Status"/> is <c>running</c>, <c>passed</c> or
/// <c>failed</c>; <see cref="ResultRef"/> the result artifact.
/// </summary>
public sealed record SessionCheckRecord(
    string SessionId,
    string CheckId,
    string StepId,
    string Revision,
    string Inputs,
    string Status,
    string? ResultRef,
    DateTimeOffset At);

public sealed record RecordBatch(
    IReadOnlyList<RunRecord>? Runs = null,
    IReadOnlyList<WaitRecord>? Waits = null,
    IReadOnlyList<TimelineRecord>? Timeline = null,
    IReadOnlyList<AnswerRecord>? Answers = null,
    IReadOnlyList<LockRecord>? Locks = null,
    IReadOnlyList<AgentStepRecord>? AgentSteps = null,
    IReadOnlyList<ReportRecord>? Reports = null,
    IReadOnlyList<QuestionRecord>? Questions = null,
    IReadOnlyList<AgentEventRecord>? AgentEvents = null,
    IReadOnlyList<FindingRecord>? Findings = null,
    IReadOnlyList<FindingEventRecord>? FindingEvents = null,
    IReadOnlyList<ArtifactRecord>? Artifacts = null,
    IReadOnlyList<OverrideRecord>? Overrides = null,
    IReadOnlyList<NotificationRecord>? Notifications = null,
    IReadOnlyList<SessionRecord>? Sessions = null,
    IReadOnlyList<SessionCheckRecord>? SessionChecks = null,
    IReadOnlyList<StepStateRecord>? StepStates = null,
    IReadOnlyList<PullRequestRecord>? PullRequests = null,
    IReadOnlyList<RepositoryRecord>? Repositories = null);

/// <summary>
/// The record store (docs/06 section 3.12). Queries are named, not SQL; the catalogue is <see cref="Queries"/>.
/// </summary>
public interface IRecordStoreProvider : IProvider
{
    Task<ProviderResult<Unit>> AppendAsync(RecordBatch records, CancellationToken cancellationToken);

    Task<ProviderResult<IReadOnlyList<T>>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken);
}

/// <summary>The named queries every record store answers, with the record type each returns.</summary>
public static class Queries
{
    /// <summary><see cref="RunRecord"/>: the run named by <c>run_id</c>.</summary>
    public const string Run = "run";

    /// <summary><see cref="RunRecord"/>: every run, most recently updated first; <c>parent_run_id</c> narrows to one run's children.</summary>
    public const string Runs = "runs";

    /// <summary><see cref="WaitRecord"/>: a run's waits; <c>open</c> = <c>true</c> narrows to open ones.</summary>
    public const string Waits = "waits";

    /// <summary><see cref="TimelineRecord"/>: a run's timeline in order; <c>after</c> skips entries up to that sequence number.</summary>
    public const string Timeline = "timeline";

    /// <summary><see cref="AnswerRecord"/>: a run's answers in order.</summary>
    public const string Answers = "answers";

    /// <summary><see cref="LockRecord"/>: a run's lock.</summary>
    public const string Lock = "lock";

    /// <summary><see cref="AgentStepRecord"/>: the agent step attempt named by <c>step_key</c>.</summary>
    public const string AgentStep = "agent_step";

    /// <summary><see cref="ReportRecord"/>: a step's report calls in order, by <c>step_key</c>.</summary>
    public const string Reports = "reports";

    /// <summary><see cref="QuestionRecord"/>: the question named by <c>question_id</c>.</summary>
    public const string Question = "question";

    /// <summary><see cref="QuestionRecord"/>: a step's questions, by <c>step_key</c>.</summary>
    public const string Questions = "questions";

    /// <summary><see cref="AgentEventRecord"/>: a step's events in order, by <c>step_key</c>.</summary>
    public const string AgentEvents = "agent_events";

    /// <summary>
    /// <see cref="FindingRecord"/>: the ledger view of the run named by <c>run_id</c>: its findings and every descendant
    /// run's (by <see cref="RunRecord.ParentRunId"/>), ordered by run id, then finding id.
    /// </summary>
    public const string Findings = "findings";

    /// <summary><see cref="FindingEventRecord"/>: one finding's history in the order appended, by <c>run_id</c> (the finding's own run) and <c>finding_id</c>.</summary>
    public const string FindingEvents = "finding_events";

    /// <summary><see cref="ArtifactRecord"/>: the artifacts of the run named by <c>run_id</c> and of every descendant run, in the order appended.</summary>
    public const string Artifacts = "artifacts";

    /// <summary><see cref="OverrideRecord"/>: a run's overrides in the order appended, by <c>run_id</c>.</summary>
    public const string Overrides = "overrides";

    /// <summary><see cref="NotificationRecord"/>: the notification named by <c>key</c>, or none.</summary>
    public const string Notification = "notification";

    /// <summary><see cref="NotificationRecord"/>: a run's notifications in the order first written, by <c>run_id</c>.</summary>
    public const string Notifications = "notifications";

    /// <summary><see cref="SessionRecord"/>: the session named by <c>session_id</c>.</summary>
    public const string Session = "session";

    /// <summary><see cref="SessionRecord"/>: a run's sessions, by <c>run_id</c>, ordered by session id.</summary>
    public const string Sessions = "sessions";

    /// <summary><see cref="SessionCheckRecord"/>: a session's checks, by <c>session_id</c>, ordered by check id.</summary>
    public const string SessionChecks = "session_checks";

    /// <summary>
    /// <see cref="OpenWaitRecord"/>: every open wait of a run that is <c>starting</c> or <c>running</c>, with its run,
    /// oldest first, at most 500. No parameters.
    /// </summary>
    public const string OpenWaits = "open_waits";

    /// <summary><see cref="ChangeRecord"/>: the changes after the sequence <c>after</c>, ascending, at most <c>limit</c> (up to 500).</summary>
    public const string Changes = "changes";

    /// <summary><see cref="ChangeRecord"/>: the latest change, or none. No parameters.</summary>
    public const string LatestChange = "latest_change";

    /// <summary><see cref="StepStateRecord"/>: a run's step counters, by <c>run_id</c>, the last write per step, ordered by step id.</summary>
    public const string StepStates = "step_states";

    /// <summary><see cref="PullRequestRecord"/>: a run's pull requests, by <c>run_id</c>, the last write per repository and number, ordered by repository, then number.</summary>
    public const string PullRequests = "pull_requests";

    /// <summary><see cref="RepositoryRecord"/>: every registered repository, the last write per name, ordered by name. No parameters.</summary>
    public const string Repositories = "repositories";

    /// <summary><see cref="AnswerRecord"/>: the answers recorded with the actor <c>actor</c> across runs, newest first, at most <c>limit</c> (up to 500).</summary>
    public const string RecentAnswers = "recent_answers";
}
