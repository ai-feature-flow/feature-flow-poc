using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// The GitHub Actions CI provider (docs/06 section 3.5). M5 stub: the providers builder implements it, including
/// <see cref="GetExpectedRunsAsync"/> from the workflow files' triggers. CiContract states the behaviour.
/// </summary>
public sealed class GitHubActionsCi : ICiProvider
{
    public ProviderDescription Describe() => new("ci", "github_actions", "0.1.0", new CiCapabilities(true, true, true, false, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<CiRunRef>> TriggerAsync(RepoRef repo, CiJobRef job, string revision, string key, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<CiRunStatus>> GetRunStatusAsync(CiRunRef run, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<CiRunResult>> ReadResultsAsync(CiRunRef run, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<ArtifactRef>> ReadLogsAsync(CiRunRef run, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<ExpectedRuns>> GetExpectedRunsAsync(RepoRef repo, string revision, PullRequestRef? pullRequest, CancellationToken cancellationToken) =>
        throw Todo();

    static NotImplementedException Todo() => new("GitHubActionsCi: M5");
}
