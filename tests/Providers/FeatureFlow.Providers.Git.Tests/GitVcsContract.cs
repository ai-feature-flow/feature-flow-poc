using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.Git.Tests;

/// <summary>
/// The VCS contract against the Git provider, over a local bare repository per test. No network, so it runs in the
/// default category. The provider reaches the remote through its clone cache, exactly as it reaches GitHub.
/// </summary>
public sealed class GitVcsContract : VcsContract, IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ff-git-contract-" + Guid.NewGuid().ToString("N"));
    readonly BareRepository remote;

    public GitVcsContract()
    {
        Repo = new RepoRef("ai-feature-flow/contract");
        remote = new BareRepository(Path.Combine(root, "remotes"), Repo.FullName);
        Vcs = new GitVcs(BareRepository.Template(Path.Combine(root, "remotes")), Path.Combine(root, "cache"),
            () => throw new InvalidOperationException("no sandbox in the contract"), () => throw new InvalidOperationException("no store in the contract"), () => null);
    }

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    protected override IVcsProvider Vcs { get; }

    protected override RepoRef Repo { get; }

    protected override Task<string> CommitOnRemoteAsync(string reference, IReadOnlyDictionary<string, string?> changes, string? from = null) =>
        Task.FromResult(remote.Commit(reference, changes, from));

    protected override Task<string?> ReadOnRemoteAsync(string revision, string path) => Task.FromResult(remote.Read(revision, path));
}
