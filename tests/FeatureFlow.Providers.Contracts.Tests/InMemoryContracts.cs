using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.Contracts.Tests;

// The contract suites run against the in-memory providers. The providers builder adds subclasses for Git,
// GitHub and GitHub Actions beside their own tests.

static class World
{
    public static (InMemoryForge Forge, RepoRef Repo) Fresh()
    {
        var forge = new InMemoryForge();
        return (forge, forge.CreateRepository("ai-feature-flow/contract"));
    }

    public static Task<string> Commit(InMemoryForge forge, RepoRef repo, string reference, IReadOnlyDictionary<string, string?> changes, string? from) =>
        Task.FromResult(forge.Commit(repo, reference, changes, from: from));
}

public sealed class InMemoryVcsContract : VcsContract
{
    readonly InMemoryForge forge;

    public InMemoryVcsContract()
    {
        (forge, Repo) = World.Fresh();
        Vcs = new InMemoryVcs(forge);
    }

    protected override IVcsProvider Vcs { get; }

    protected override RepoRef Repo { get; }

    protected override Task<string> CommitOnRemoteAsync(string reference, IReadOnlyDictionary<string, string?> changes, string? from = null) =>
        World.Commit(forge, Repo, reference, changes, from);

    protected override Task<string?> ReadOnRemoteAsync(string revision, string path) => Task.FromResult(forge.ReadFile(Repo, revision, path));
}

public sealed class InMemoryCodeHostContract : CodeHostContract
{
    readonly InMemoryForge forge;

    public InMemoryCodeHostContract()
    {
        (forge, Repo) = World.Fresh();
        CodeHost = new InMemoryCodeHost(forge);
    }

    protected override ICodeHostProvider CodeHost { get; }

    protected override RepoRef Repo { get; }

    protected override Task<string> CommitOnRemoteAsync(string branch, IReadOnlyDictionary<string, string?> changes, string? from = null) =>
        World.Commit(forge, Repo, branch, changes, from);

    protected override Task<string> MergeAsPersonAsync(PullRequestRef pullRequest)
    {
        forge.MergePullRequest(pullRequest, "a-person");
        return Task.FromResult("a-person");
    }
}

public sealed class InMemoryCiContract : CiContract, IDisposable
{
    readonly InMemoryForge forge;
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-ci-contract-" + Guid.NewGuid().ToString("N"));

    public InMemoryCiContract()
    {
        (forge, Repo) = World.Fresh();
        var store = new FileSystemArtifactStore(folder);
        Ci = new InMemoryCi(forge, () => store);
        forge.ScriptCi("ci.yml", new CiScript(Conclusions.Success, [new CiScriptFile("tests.trx", ResultFormats.Trx, "<TestRun />")], PollsUntilDone: 2, Log: "ok"));
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    protected override ICiProvider Ci { get; }

    protected override RepoRef Repo { get; }

    protected override CiJobRef PassingJob { get; } = new("ci.yml", new Dictionary<string, string>());

    protected override Task<string> HeadAsync(bool withCi)
    {
        forge.ConfigureCi(Repo, withCi);
        return Task.FromResult(forge.Resolve(Repo, "main")!);
    }
}
