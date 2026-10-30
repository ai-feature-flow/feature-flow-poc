namespace FeatureFlow.Spike.Contracts;

/// <summary>docs/04 section 4.3 and docs/05 section 1.3.</summary>
public enum ErrorClass
{
    /// <summary>Retry with backoff. Does not consume an attempt (docs/01 3.7).</summary>
    Infrastructure,

    /// <summary>The work itself was wrong or refused. Consumes an attempt.</summary>
    Work,
}

public sealed record ProviderError(
    ErrorClass Class,
    string Reason,
    TimeSpan? RetryAfter = null,
    string? RemoteId = null,
    string? Detail = null);

public sealed record ProviderResult<T>(T? Value, ProviderError? Error)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsOk => Error is null;

    public static ProviderResult<T> Ok(T value) => new(value, null);

    public static ProviderResult<T> Fail(ProviderError error) => new(default, error);

    public static ProviderResult<T> Infrastructure(string reason, TimeSpan? retryAfter = null, string? detail = null) =>
        new(default, new ProviderError(ErrorClass.Infrastructure, reason, retryAfter, null, detail));

    public static ProviderResult<T> Work(string reason, string? detail = null) =>
        new(default, new ProviderError(ErrorClass.Work, reason, null, null, detail));
}

/// <summary>
/// What an agent.run activity reports. Carries the resume evidence from
/// docs/01 3.6: the commit resumed from and the session reference used.
/// </summary>
public sealed record AgentRunResult(
    string Outcome,
    string? LastCommit = null,
    string? SessionRef = null,
    int SessionTurns = 0,
    string? CommandHandle = null,
    string? SandboxKey = null,
    string? PatchRef = null,
    string? ErrorReason = null,
    int InfrastructureRetries = 0,
    Dictionary<string, string>? Outputs = null);

/// <summary>
/// What an agent.run activity heartbeats. docs/01 3.6: "Heartbeats carry
/// progress (last commit, session id), so a stalled agent is distinguishable
/// from a slow one." The workflow reads the last heartbeat off a heartbeat
/// timeout to decide whether a resume made progress (docs/01 3.7).
/// </summary>
public sealed record AgentProgress(
    string? LastCommit,
    int SessionTurns,
    string? SessionRef,
    string? CommandHandle,
    string? SandboxKey,
    double ElapsedSeconds);

/// <summary>
/// Progress evidence for the attempt rule. docs/01 3.7: progress is a new commit
/// pushed to the branch, or the agent session having grown since the previous
/// attempt.
/// </summary>
public sealed record AttemptProgress(
    int AttemptNumber,
    bool Resumed,
    string? LastCommit,
    int SessionTurns,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt = null,
    string? SessionRef = null);
