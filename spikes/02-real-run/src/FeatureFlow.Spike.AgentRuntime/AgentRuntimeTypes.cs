using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.AgentRuntime;

/// <summary>
/// The agent runtime provider. docs/04 section 3 for the signatures, docs/05
/// section 3 for the per-method contracts.
///
/// Four rules shape every method here:
///
/// 1. **Start and poll, never await.** <see cref="StartAsync"/> returns a handle
///    and does not wait for the agent to finish. <see cref="GetProgressAsync"/>
///    and <see cref="GetResultAsync"/> are non-blocking. This is R18/R20 from
///    Spike 1 and review 06 C1: a command may run for hours, and awaiting it
///    inside one call would both break the "short and bounded" rule and lose the
///    run on a worker crash.
/// 2. **Every method returns <see cref="ProviderResult{T}"/>.** The provider
///    classifies, because it knows its system (docs/01 4.3). Classification is
///    never done by string-matching exception text.
/// 3. **Nothing here names a product.** The instance name comes from
///    <see cref="IProvider.Describe"/>, so a second runtime is added by
///    registration, not by editing the engine (docs/09 11.1).
/// 4. **Credentials come from the broker.** The provider never reads a
///    credential from configuration (docs/04 section 1).
/// </summary>
public interface IAgentRuntimeProvider : IProvider
{
    /// <summary>
    /// Starts the runtime inside the sandbox and returns a session handle. It
    /// does **not** wait for the agent to finish (docs/05 section 3).
    /// </summary>
    /// <param name="sandbox">The sandbox the runtime runs in.</param>
    /// <param name="task">What the agent is asked to do.</param>
    /// <param name="limits">The bounds for this attempt.</param>
    /// <param name="idempotencyKey">
    /// <c>hash(runId, stepId, stableInputs)</c>. Stable inputs exclude anything
    /// that changes per attempt, so a resumed attempt produces the same key as
    /// the attempt it continues (docs/05 1.1).
    /// </param>
    Task<ProviderResult<AgentSession>> StartAsync(
        SandboxHandle sandbox, AgentTask task, AgentLimits limits, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Resumes a prior session. Requires <see cref="AgentRuntimeCapabilities.SessionResume"/>.
    /// Takes the sandbox handle as well as the session reference, because the
    /// session lives inside a sandbox that may have been re-attached or
    /// recreated (docs/04 section 3).
    /// </summary>
    Task<ProviderResult<AgentSession>> ResumeAsync(
        SandboxHandle sandbox, string sessionRef, AgentLimits limits, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Non-blocking progress read. Returns the evidence the attempt-budget rule
    /// needs: a new commit on the branch, or session growth (docs/01 3.7).
    /// </summary>
    Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken ct);

    /// <summary>
    /// The source of truth for the outcome. The session-end hook is a signal,
    /// not the source of truth (review 05 P3, docs/05 3.1).
    /// </summary>
    Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken ct);

    /// <summary>Idempotent. Stops the runtime; does not dispose the sandbox.</summary>
    Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken ct);
}

/// <summary>
/// The agent runtime capability record. docs/04 section 3. Capabilities are
/// typed records, never strings, so a step can ask a precise question and fail
/// with a named missing capability.
/// </summary>
/// <param name="StructuredOutput">Can the runtime return a typed result, not only prose.</param>
/// <param name="Streaming">Can output be read incrementally.</param>
/// <param name="ToolAllowlist">Can the runtime be given a tool allowlist.</param>
/// <param name="MaxContextTokens">The context window, or 0 when unknown.</param>
/// <param name="Hooks">Can the runtime call back to the engine per tool call (docs/04 3.1).</param>
/// <param name="SessionResume">Can a retry resume the prior session rather than start a new one.</param>
/// <param name="UsageReporting">Can the runtime report its own token or credit usage (docs/08 2.5).</param>
/// <param name="ModelConfiguration">Can the model be set per run, rather than fixed by the installation.</param>
/// <param name="CustomEndpoint">Can the runtime be pointed at an OpenAI-compatible router endpoint (BYOK).</param>
public sealed record AgentRuntimeCapabilities(
    bool StructuredOutput,
    bool Streaming,
    bool ToolAllowlist,
    int MaxContextTokens,
    bool Hooks,
    bool SessionResume,
    bool UsageReporting,
    bool ModelConfiguration,
    bool CustomEndpoint)
{
    /// <summary>
    /// The capability names a role may put in <c>runtime.requires</c>
    /// (docs/09 11.1). The role names a capability, never a product.
    /// </summary>
    public static IReadOnlyList<string> Names { get; } = new[]
    {
        "structured_output",
        "streaming",
        "tool_allowlist",
        "hooks",
        "session_resume",
        "usage_reporting",
        "model_configuration",
        "custom_endpoint",
    };

    /// <summary>
    /// Whether this runtime declares the named capability. Unknown names are
    /// false rather than an error, so a role written against a richer runtime
    /// degrades to a named missing capability instead of a crash.
    /// </summary>
    public bool Has(string name) => name switch
    {
        "structured_output" => StructuredOutput,
        "streaming" => Streaming,
        "tool_allowlist" => ToolAllowlist,
        "hooks" => Hooks,
        "session_resume" => SessionResume,
        "usage_reporting" => UsageReporting,
        "model_configuration" => ModelConfiguration,
        "custom_endpoint" => CustomEndpoint,
        _ => false,
    };

    /// <summary>Which of <paramref name="required"/> this runtime does not declare.</summary>
    public IReadOnlyList<string> Missing(IEnumerable<string> required) =>
        required.Where(r => !Has(r)).ToList();

    /// <summary>A one-line rendering, for the capability table docs/08 2.2 asks for.</summary>
    public IReadOnlyList<string> Lines()
    {
        static string Yes(bool value) => value ? "yes" : "no";

        return new[]
        {
            $"structured_output   {Yes(StructuredOutput)}",
            $"streaming           {Yes(Streaming)}",
            $"tool_allowlist      {Yes(ToolAllowlist)}",
            $"hooks               {Yes(Hooks)}",
            $"session_resume      {Yes(SessionResume)}",
            $"usage_reporting     {Yes(UsageReporting)}",
            $"model_configuration {Yes(ModelConfiguration)}",
            $"custom_endpoint     {Yes(CustomEndpoint)}",
            $"max_context_tokens  {(MaxContextTokens > 0 ? MaxContextTokens.ToString() : "unknown")}",
        };
    }
}

/// <summary>
/// A sandbox the runtime runs in. Transport-neutral: a handle is data, so it
/// survives serialisation into workflow state and a retry can re-attach by key.
/// </summary>
/// <param name="Key">The idempotency key the sandbox was created with.</param>
/// <param name="WorkingDirectory">Where the repository is checked out.</param>
/// <param name="Os">"linux" or "windows".</param>
/// <param name="Persistent">Whether the sandbox outlives a worker crash (docs/04 section 2).</param>
public sealed record SandboxHandle(
    string Key,
    string WorkingDirectory,
    string Os = "windows",
    bool Persistent = false);

/// <summary>
/// What the agent is asked to do. The prompt is content and comes from the
/// rendered task (docs/09 12.2); the model and endpoint are configuration and
/// are deliberately **not** here (docs/09 11.1, brief lesson 5).
/// </summary>
/// <param name="Prompt">The rendered prompt.</param>
/// <param name="Role">The role the agent runs as. Privilege comes from the role (docs/09 11.0).</param>
/// <param name="WriteAccess">"none", "branch" or "repository". A tightening of the role's value only.</param>
/// <param name="Tools">The tool allowlist, from the role. Null means the runtime's default.</param>
/// <param name="Branch">The branch the agent works on, for progress evidence.</param>
/// <param name="Repository">The repository, as <c>owner/name</c>.</param>
public sealed record AgentTask(
    string Prompt,
    string Role,
    string WriteAccess = "branch",
    IReadOnlyList<string>? Tools = null,
    string? Branch = null,
    string? Repository = null);

/// <summary>
/// The bounds for one attempt.
/// </summary>
/// <param name="MaxWallClock">The outer bound. Exceeding it is a **work** outcome, not infrastructure (docs/06 2.1).</param>
/// <param name="MaxTurns">A turn ceiling, where the runtime supports one.</param>
/// <param name="MaxCredits">A credit ceiling, where the runtime supports one.</param>
/// <param name="HeartbeatInterval">How often the step heartbeats. docs/06 2.1: 30s.</param>
public sealed record AgentLimits(
    TimeSpan? MaxWallClock = null,
    int? MaxTurns = null,
    double? MaxCredits = null,
    TimeSpan? HeartbeatInterval = null)
{
    /// <summary>docs/06 section 2.1: <c>start_to_close</c> default 4h, heartbeat 30s.</summary>
    public static AgentLimits Default { get; } = new(
        MaxWallClock: TimeSpan.FromHours(4),
        HeartbeatInterval: TimeSpan.FromSeconds(30));
}

/// <summary>
/// A handle to a running agent session. Everything a later call needs is here,
/// so no method has to re-derive it and the handle survives serialisation into
/// workflow state.
/// </summary>
/// <param name="SessionRef">The runtime's own session reference. Stored in the artifact store for resume (docs/01 3.6).</param>
/// <param name="Sandbox">The sandbox the session lives in.</param>
/// <param name="IdempotencyKey">The key the caller passed to start or resume.</param>
/// <param name="StartedAt">When the session started.</param>
/// <param name="Resumed">Whether this handle came from a resume rather than a fresh start.</param>
/// <param name="ProcessId">The OS process id, where the runtime runs as a local process.</param>
/// <param name="TranscriptPath">Where the runtime writes its transcript, where it does.</param>
/// <param name="UsagePath">Where the runtime writes its usage statistics, where it does.</param>
/// <param name="Deadline">When this attempt runs out of time. Carried on the handle so a later call can classify a timeout without engine state.</param>
public sealed record AgentSession(
    string SessionRef,
    SandboxHandle Sandbox,
    string IdempotencyKey,
    DateTimeOffset StartedAt,
    bool Resumed = false,
    int? ProcessId = null,
    string? TranscriptPath = null,
    string? UsagePath = null,
    DateTimeOffset? Deadline = null)
{
    /// <summary>Whether this attempt is past its wall-clock bound, at <paramref name="now"/>.</summary>
    public bool PastDeadline(DateTimeOffset now) => Deadline is DateTimeOffset due && now >= due;
}

/// <summary>
/// Progress evidence. docs/01 3.7: progress is a new commit pushed to the
/// branch, or the agent session having grown since the previous attempt. Both
/// are reported here, because a runtime may give one and not the other.
/// </summary>
/// <param name="LastCommit">The head commit on the branch, or null when unknown.</param>
/// <param name="SessionTurns">The session's turn count, or 0 when the runtime does not report one.</param>
/// <param name="SessionRef">The session reference, so a heartbeat can carry it (docs/01 3.6).</param>
/// <param name="Running">Whether the runtime process is still alive.</param>
/// <param name="Elapsed">How long the session has been running.</param>
/// <param name="CommitCount">Commits on the branch ahead of the base, where known.</param>
public sealed record AgentProgress(
    string? LastCommit,
    int SessionTurns,
    string? SessionRef,
    bool Running,
    TimeSpan Elapsed,
    int CommitCount = 0)
{
    /// <summary>
    /// Whether this reading shows progress against <paramref name="previous"/>.
    /// A new commit, or a session that has grown. docs/01 3.7.
    /// </summary>
    public bool ShowsProgressAgainst(AgentProgress? previous)
    {
        if (previous is null)
            return LastCommit is not null || SessionTurns > 0;

        if (LastCommit is not null && !string.Equals(LastCommit, previous.LastCommit, StringComparison.Ordinal))
            return true;

        return SessionTurns > previous.SessionTurns;
    }

    /// <summary>
    /// What a heartbeat carries. docs/01 3.6: "Heartbeats carry progress (last
    /// commit, session id), so a stalled agent is distinguishable from a slow
    /// one."
    /// </summary>
    public string HeartbeatDetail =>
        $"commit={LastCommit ?? "(none)"} turns={SessionTurns} session={SessionRef ?? "(none)"} " +
        $"running={Running} elapsed={Elapsed.TotalSeconds:F0}s";
}

/// <summary>
/// The outcome of an agent session. <see cref="IAgentRuntimeProvider.GetResultAsync"/>
/// is the source of truth, not the session-end hook (review 05 P3).
/// </summary>
/// <param name="Outcome">"completed", "blocked", "failed" or "timeout" (docs/06 2.1).</param>
/// <param name="SessionRef">The session reference, for a later resume.</param>
/// <param name="Summary">The agent's own summary, where it gave one.</param>
/// <param name="LastCommit">The last commit on the branch at completion.</param>
/// <param name="SessionTurns">The turn count at completion.</param>
/// <param name="ExitCode">The process exit code, where the runtime runs as a process.</param>
/// <param name="TimedOut">Whether the run was cut short by its own wall-clock limit.</param>
/// <remarks>
/// <para>
/// <c>Artifacts</c> is deliberately absent. docs/04 section 3 says the result
/// carries produced artifact references, but <c>ArtifactRef</c> is defined twice
/// by sibling projects — <c>FeatureFlow.Spike.CodeHost</c> as
/// (Uri, ContentHash, SizeBytes) and <c>FeatureFlow.Spike.Ci</c> as
/// (Id, Name, Uri, SizeBytes, Digest, ExpiresAt, ContentType). Importing either
/// would pick a side in a conflict this provider does not own, and would add a
/// dependency the agent runtime should not have. The fix is to move
/// <c>ArtifactRef</c> into <c>FeatureFlow.Spike.Contracts</c>, which both
/// already reference; this field returns once that has happened.
/// </para>
/// </remarks>
public sealed record AgentResult(
    string Outcome,
    string? SessionRef = null,
    string? Summary = null,
    string? LastCommit = null,
    int SessionTurns = 0,
    AgentUsage? Usage = null,
    int? ExitCode = null,
    bool TimedOut = false)
{
    /// <summary>
    /// The session has not finished. A provider state, not a step outcome: the
    /// step polls <see cref="IAgentRuntimeProvider.GetProgressAsync"/> and must
    /// not route on this value.
    /// </summary>
    public const string Running = "running";

    public static AgentResult Completed(string? sessionRef = null, string? summary = null) =>
        new("completed", sessionRef, summary);
}

/// <summary>
/// Usage reported by the runtime. docs/01 5.5: every step records tokens, model
/// and compute time. docs/08 2.5: without hooks, cost comes from the runtime's
/// own usage output.
/// </summary>
/// <param name="Model">The model that ran. Configuration, never template content.</param>
/// <param name="InputTokens">Prompt tokens, where reported.</param>
/// <param name="OutputTokens">Completion tokens, where reported.</param>
/// <param name="Credits">The runtime's own credit count, where reported.</param>
/// <param name="CostUsd">Cost in USD, where the runtime or gateway reports it.</param>
/// <param name="Source">Where the numbers came from: "runtime_usage_output", "hook_events" or "none".</param>
public sealed record AgentUsage(
    string? Model = null,
    long? InputTokens = null,
    long? OutputTokens = null,
    double? Credits = null,
    decimal? CostUsd = null,
    string Source = "none")
{
    /// <summary>Whether anything was actually reported.</summary>
    public bool Any =>
        InputTokens is not null || OutputTokens is not null || Credits is not null || CostUsd is not null;

    public override string ToString() =>
        $"model={Model ?? "(unknown)"} in={InputTokens?.ToString() ?? "-"} out={OutputTokens?.ToString() ?? "-"} " +
        $"credits={Credits?.ToString("F2") ?? "-"} source={Source}";
}
