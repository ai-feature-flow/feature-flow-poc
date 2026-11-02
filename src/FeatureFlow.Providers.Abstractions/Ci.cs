namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// A CI job to trigger: the workflow (for GitHub Actions, the workflow file) and its inputs, as the repository
/// manifest declares them for a tier delegated to CI (docs/04 section 9.1).
/// </summary>
public sealed record CiJobRef(string Workflow, IReadOnlyDictionary<string, string> Inputs);

/// <summary>A CI run: its repository, the CI system's id for it, and the revision it builds.</summary>
public sealed record CiRunRef(RepoRef Repo, string Id, string Revision, string? Url = null);

/// <summary>A run's progress (<see cref="CheckStates"/>) and, once completed, its conclusion (<see cref="Conclusions"/>).</summary>
public sealed record CiRunStatus(string State, string? Conclusion);

/// <summary>One job of a completed run and its conclusion.</summary>
public sealed record CiJobResult(string Name, string Conclusion);

/// <summary>The formats of result files: test results (<c>trx</c>, <c>junit</c>) and static analysis (<c>sarif</c>).</summary>
public static class ResultFormats
{
    public const string Trx = "trx";
    public const string Junit = "junit";
    public const string Sarif = "sarif";
}

/// <summary>A result file a run produced, copied into the artifact store, unparsed. The engine parses it (docs/04 section 9).</summary>
public sealed record CiResultFile(string Name, string Format, ArtifactRef Content);

/// <summary>A completed run's conclusion, its jobs, and the result files it produced.</summary>
public sealed record CiRunResult(string Conclusion, IReadOnlyList<CiJobResult> Jobs, IReadOnlyList<CiResultFile> Files);

/// <summary>
/// Which CI runs should report for a head (docs/06 section 3.5): those that will run, and those that may (their
/// triggers cannot be evaluated here, such as path filters). Both empty: the repository has no CI for this head.
/// Names are the <see cref="CheckRun.Suite"/> names the code host reports checks under.
/// </summary>
public sealed record ExpectedRuns(IReadOnlyList<string> Runs, IReadOnlyList<string> MayRun)
{
    public static readonly ExpectedRuns None = new([], []);

    public bool Any => Runs.Count > 0 || MayRun.Count > 0;
}

/// <summary>
/// The CI provider (docs/06 section 3.5). Runs are triggered and polled, never awaited. A trigger takes an
/// idempotency key, so a retry adopts the run it started.
/// </summary>
public interface ICiProvider : IProvider
{
    Task<ProviderResult<CiRunRef>> TriggerAsync(RepoRef repo, CiJobRef job, string revision, string key, CancellationToken cancellationToken);

    Task<ProviderResult<CiRunStatus>> GetRunStatusAsync(CiRunRef run, CancellationToken cancellationToken);

    /// <summary>A completed run's result; <c>validation_failed</c> while it is still running.</summary>
    Task<ProviderResult<CiRunResult>> ReadResultsAsync(CiRunRef run, CancellationToken cancellationToken);

    /// <summary>The run's logs, copied into the artifact store.</summary>
    Task<ProviderResult<ArtifactRef>> ReadLogsAsync(CiRunRef run, CancellationToken cancellationToken);

    /// <summary>Which runs should report for <paramref name="revision"/>, on a pull request when <paramref name="pullRequest"/> is given.</summary>
    Task<ProviderResult<ExpectedRuns>> GetExpectedRunsAsync(RepoRef repo, string revision, PullRequestRef? pullRequest, CancellationToken cancellationToken);
}
