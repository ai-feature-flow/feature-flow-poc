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

public sealed record RecordBatch(
    IReadOnlyList<RunRecord>? Runs = null,
    IReadOnlyList<WaitRecord>? Waits = null,
    IReadOnlyList<TimelineRecord>? Timeline = null,
    IReadOnlyList<AnswerRecord>? Answers = null,
    IReadOnlyList<LockRecord>? Locks = null,
    IReadOnlyList<AgentStepRecord>? AgentSteps = null,
    IReadOnlyList<ReportRecord>? Reports = null,
    IReadOnlyList<QuestionRecord>? Questions = null,
    IReadOnlyList<AgentEventRecord>? AgentEvents = null);

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
}
