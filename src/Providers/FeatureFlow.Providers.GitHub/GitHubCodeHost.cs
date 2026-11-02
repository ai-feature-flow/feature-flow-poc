using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// The GitHub code host provider (docs/06 section 3.4). M5 stub: the providers builder implements it, mapping every
/// documented success response, and registers its factory in <see cref="GitHubProviders"/>. CodeHostContract states
/// the behaviour.
/// </summary>
public sealed class GitHubCodeHost : ICodeHostProvider
{
    public ProviderDescription Describe() => new("code_host", "github", "0.1.0", new CodeHostCapabilities(true, false, true, true, false, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<RepoFile?>> ReadFileAsync(RepoRef repo, string path, string reference, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestSpec spec, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pullRequest, string? comment, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pullRequest, string key, CancellationToken cancellationToken) => throw Todo();

    public Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(RepoRef repo, string revision, CancellationToken cancellationToken) => throw Todo();

    static NotImplementedException Todo() => new("GitHubCodeHost: M5");
}
