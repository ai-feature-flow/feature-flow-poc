using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.Ci;

/// <summary>
/// The CI provider interface. docs/04 section 5.
///
/// Two rules shape it. <see cref="TriggerAsync"/> starts a run and returns a
/// reference without waiting, and <see cref="GetRunStatusAsync"/> is
/// non-blocking: waiting is an engine mechanism (review 05 P1), not a provider
/// method. The step polls <see cref="GetRunStatusAsync"/> inside one
/// long-running activity (docs/05 1.2.1).
/// </summary>
public interface ICiProvider : IProvider
{
    Task<ProviderResult<CiRunRef>> TriggerAsync(
        RepoRef repo, string tier, string commitSha, string idempotencyKey, CancellationToken ct);

    Task<ProviderResult<CiRunStatus>> GetRunStatusAsync(CiRunRef run, CancellationToken ct);

    Task<ProviderResult<CiRunResult>> ReadResultsAsync(CiRunRef run, CancellationToken ct);

    Task<ProviderResult<LogRef>> ReadLogsAsync(CiRunRef run, CancellationToken ct);

    Task<ProviderResult<IReadOnlyList<ArtifactRef>>> GetArtifactsAsync(CiRunRef run, CancellationToken ct);
}

/// <summary>docs/04 section 5. Capabilities are typed records, never strings.</summary>
/// <param name="Trigger">Can start a run.</param>
/// <param name="ReadLogs">Can return a log reference for a run.</param>
/// <param name="Artifacts">Can list artifact references for a run.</param>
/// <param name="InboundEvents">Can receive push events and translate them into engine events (docs/04 1.3).</param>
public sealed record CiCapabilities(
    bool Trigger,
    bool ReadLogs,
    bool Artifacts,
    bool InboundEvents);

/// <summary>
/// A repository, as the engine names it. docs/05 section 4: a SHA alone is not
/// unique across repositories, so every CI call carries the repository too.
/// </summary>
public sealed record RepoRef(string Owner, string Name)
{
    /// <summary>The canonical <c>owner/name</c> form.</summary>
    public string FullName => $"{Owner}/{Name}";

    public override string ToString() => FullName;

    public static RepoRef Parse(string ownerSlashName)
    {
        if (string.IsNullOrWhiteSpace(ownerSlashName))
            throw new ArgumentException("repository is empty", nameof(ownerSlashName));

        var slash = ownerSlashName.IndexOf('/');
        if (slash <= 0 || slash == ownerSlashName.Length - 1)
            throw new ArgumentException(
                $"repository '{ownerSlashName}' must be of the form <owner>/<name>",
                nameof(ownerSlashName));

        return new RepoRef(ownerSlashName[..slash], ownerSlashName[(slash + 1)..]);
    }
}

/// <summary>
/// A reference to a CI run. Carries everything a later call needs, so no method
/// has to re-derive the repository or the tier from the run id alone.
/// </summary>
/// <param name="RunId">The provider's run identifier, as text so it survives serialisation.</param>
/// <param name="Repo">The repository the run belongs to.</param>
/// <param name="Tier">The test tier the run was started for.</param>
/// <param name="CommitSha">The commit the run was started at.</param>
/// <param name="IdempotencyKey">The key the caller passed to <see cref="ICiProvider.TriggerAsync"/>.</param>
/// <param name="Url">A human-readable link to the run. Never a source of truth.</param>
/// <param name="Event">The GitHub event that produced the run: <c>workflow_dispatch</c>, <c>push</c> or <c>merge_group</c>.</param>
/// <param name="WorkflowFile">The workflow file that ran, e.g. <c>integration.yml</c>.</param>
public sealed record CiRunRef(
    string RunId,
    RepoRef Repo,
    string Tier,
    string CommitSha,
    string IdempotencyKey,
    string? Url = null,
    string? Event = null,
    string? WorkflowFile = null)
{
    /// <summary>
    /// Whether this run came from the merge queue. docs/04 section 5: the
    /// provider must report results for queue builds, because the merge queue
    /// path depends on it.
    /// </summary>
    public bool IsQueueBuild =>
        string.Equals(Event, GitHubActionsCiProvider.MergeGroupEvent, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The lifecycle state of a run. Separate from <see cref="CiConclusion"/>,
/// because a run can be finished with any conclusion including none.
/// </summary>
public enum CiRunState
{
    /// <summary>Accepted by the provider but not started. Checks may not have registered yet.</summary>
    Queued,

    /// <summary>Started and progressing.</summary>
    InProgress,

    /// <summary>Finished. <see cref="CiRunStatus.Conclusion"/> says how.</summary>
    Completed,

    /// <summary>The provider does not have this run.</summary>
    Unknown,
}

/// <summary>
/// How a finished run ended. A failing check is <see cref="Failed"/>, which the
/// step treats as a work outcome, not infrastructure (docs/06 7.2).
/// </summary>
public enum CiConclusion
{
    /// <summary>Not finished yet, or finished without a conclusion.</summary>
    None,
    Success,
    Failed,
    Cancelled,
    Skipped,
    TimedOut,
    ActionRequired,
    Neutral,
}

/// <summary>
/// What <see cref="ICiProvider.GetRunStatusAsync"/> returns. Non-blocking: it
/// reports what the provider knows right now.
/// </summary>
/// <param name="State">Where the run is in its lifecycle.</param>
/// <param name="Conclusion">How it ended. <see cref="CiConclusion.None"/> while unfinished.</param>
/// <param name="ChecksRegistered">Whether any job or check has appeared yet.</param>
/// <param name="TotalChecks">How many jobs the run has, once any have registered.</param>
/// <param name="CompletedChecks">How many of those have finished.</param>
/// <param name="Url">A human-readable link to the run.</param>
/// <param name="UpdatedAt">When the provider last changed the run.</param>
public sealed record CiRunStatus(
    CiRunState State,
    CiConclusion Conclusion = CiConclusion.None,
    bool ChecksRegistered = false,
    int TotalChecks = 0,
    int CompletedChecks = 0,
    string? Url = null,
    DateTimeOffset? UpdatedAt = null)
{
    /// <summary>Whether the run has finished. The poll loop's exit condition.</summary>
    public bool IsTerminal => State == CiRunState.Completed || State == CiRunState.Unknown;

    /// <summary>
    /// Whether the run is waiting for checks that have not registered yet.
    /// docs/05 section 5: "must handle checks that register late". A caller that
    /// sees this keeps polling rather than concluding there is no CI.
    /// </summary>
    public bool AwaitingChecks => !IsTerminal && !ChecksRegistered;
}

/// <summary>One job in a run, as <see cref="ICiProvider.ReadResultsAsync"/> reports it.</summary>
public sealed record CiJobResult(
    string Id,
    string Name,
    CiRunState State,
    CiConclusion Conclusion = CiConclusion.None,
    int? StepsTotal = null,
    int? StepsCompleted = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null)
{
    public bool IsTerminal => State == CiRunState.Completed || State == CiRunState.Unknown;
}

/// <summary>
/// What <see cref="ICiProvider.ReadResultsAsync"/> returns. Structured, and
/// bounded: the log body and the artifact content stay behind references
/// (docs/05 section 5, DP10).
/// </summary>
/// <param name="Run">The run the results are for.</param>
/// <param name="State">Where the run is.</param>
/// <param name="Conclusion">How it ended.</param>
/// <param name="Jobs">The jobs, once any have registered.</param>
/// <param name="Complete">Whether every job has finished. False means <c>partial</c>.</param>
/// <param name="Url">A human-readable link to the run.</param>
public sealed record CiRunResult(
    CiRunRef Run,
    CiRunState State,
    CiConclusion Conclusion = CiConclusion.None,
    IReadOnlyList<CiJobResult>? Jobs = null,
    bool Complete = false,
    string? Url = null)
{
    public IReadOnlyList<CiJobResult> JobsOrEmpty => Jobs ?? Array.Empty<CiJobResult>();

    /// <summary>How many jobs finished with each conclusion.</summary>
    public IReadOnlyDictionary<string, int> ConclusionCounts =>
        JobsOrEmpty
            .GroupBy(j => j.Conclusion.ToString(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
}

/// <summary>
/// A reference to a log, not the log body. docs/05 section 5: "Returns a log
/// reference, not the log body". The body is fetched through the artifact store
/// or a signed URL, so a large log never enters workflow state (DP10).
/// </summary>
/// <param name="Uri">Where the log can be fetched from.</param>
/// <param name="Kind">What the reference points at: an API endpoint or a signed download.</param>
/// <param name="SizeBytes">The size when the provider reports one. Null when unknown.</param>
/// <param name="ExpiresAt">When a signed reference stops working. Null when it does not expire.</param>
/// <param name="ContentType">The media type when known.</param>
public sealed record LogRef(
    string Uri,
    string Kind = LogRefKind.Api,
    long? SizeBytes = null,
    DateTimeOffset? ExpiresAt = null,
    string? ContentType = null)
{
    public override string ToString() => $"{Kind}:{Uri}";
}

/// <summary>The kinds of log reference. Named, not free text.</summary>
public static class LogRefKind
{
    /// <summary>An API endpoint that redirects to a download. Requires a credential.</summary>
    public const string Api = "api";

    /// <summary>A pre-signed download URL. No credential needed, but it expires.</summary>
    public const string SignedUrl = "signed-url";
}

/// <summary>
/// A reference to an artifact, not its content. Same rule as <see cref="LogRef"/>.
/// </summary>
public sealed record ArtifactRef(
    string Id,
    string Name,
    string Uri,
    long? SizeBytes = null,
    string? Digest = null,
    DateTimeOffset? ExpiresAt = null,
    string? ContentType = null)
{
    public override string ToString() => $"{Name} ({Id})";
}
