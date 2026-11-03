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

/// <summary>A run: its status, template and parent. Written again whenever it changes; the last write wins.</summary>
public sealed record RunRecord(
    string RunId,
    string? ParentRunId,
    string Template,
    string Status,
    string Requester,
    IReadOnlyList<string> Repositories,
    bool DryRun,
    DateTimeOffset UpdatedAt,
    string? Detail);

/// <summary>A durable wait for a person: a decision, an intervention or a patch approval, open or answered. A record with <see cref="Open"/> false closes the wait and keeps the rest of what the opening record said.</summary>
public sealed record WaitRecord(
    string RunId,
    string WaitId,
    string Kind,
    string StepId,
    string DecisionType,
    string Reason,
    IReadOnlyList<string> Choices,
    bool Open,
    DateTimeOffset UpdatedAt);

/// <summary>One entry of a run's timeline, numbered in the order the run recorded it.</summary>
public sealed record TimelineRecord(string RunId, long Sequence, string Kind, string StepId, string? Detail, DateTimeOffset At);

/// <summary>An answer or override from a person, recorded by the API with the authenticated actor before it is delivered (F8).</summary>
public sealed record AnswerRecord(string RunId, string WaitId, string Actor, string Answer, string Result, DateTimeOffset At);

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
    IReadOnlyList<ArtifactRecord>? Artifacts = null);

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
}
