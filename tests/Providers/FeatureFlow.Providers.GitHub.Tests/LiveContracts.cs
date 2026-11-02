using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>
/// The code host contract against GitHub itself, on the fixture repository. The contract names branches <c>main</c>
/// and <c>ff/...</c>; <see cref="Scoped"/> maps them under the test's prefix on the way in and back on the way out,
/// so the provider makes real calls but never touches the fixture's own branches. Only the default branch and its
/// protection are read from the real <c>main</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class GitHubCodeHostContract : CodeHostContract, IAsyncLifetime
{
    readonly LiveFixture fixture = new();

    public GitHubCodeHostContract() => CodeHost = new Scoped(new GitHubCodeHost(fixture.Api), fixture);

    public async ValueTask InitializeAsync() => await fixture.CreateBaseAsync();

    public async ValueTask DisposeAsync() => await fixture.CleanUpAsync();

    protected override ICodeHostProvider CodeHost { get; }

    protected override RepoRef Repo => LiveFixture.Repo;

    protected override async Task<string> CommitOnRemoteAsync(string branch, IReadOnlyDictionary<string, string?> changes, string? from = null)
    {
        var parent = await fixture.ResolveAsync(fixture.Branch(branch)) ?? await fixture.ResolveAsync(fixture.Branch(from ?? "main"))
            ?? throw new InvalidOperationException($"{from ?? "main"} does not exist");
        var commit = await fixture.CommitAsync(parent, changes);
        await fixture.SetBranchAsync(fixture.Branch(branch), commit);
        return commit;
    }

    protected override async Task<string> MergeAsPersonAsync(PullRequestRef pullRequest) => await fixture.MergeAsync(pullRequest.Number);

    /// <summary>Maps branch names to and from the test's prefix; a 40-character revision passes unchanged.</summary>
    sealed class Scoped(ICodeHostProvider inner, LiveFixture fixture) : ICodeHostProvider
    {
        PullRequestSpec In(PullRequestSpec spec) => spec with { Head = fixture.Branch(spec.Head), Base = fixture.Branch(spec.Base) };

        string Reference(string reference) => reference.Length == 40 && reference.All(Uri.IsHexDigit) ? reference : fixture.Branch(reference);

        public ProviderDescription Describe() => inner.Describe();

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

        public Task<ProviderResult<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken cancellationToken) => inner.GetDefaultBranchAsync(repo, cancellationToken);

        public Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken cancellationToken) =>
            inner.GetBranchProtectionAsync(repo, branch, cancellationToken);

        public Task<ProviderResult<RepoFile?>> ReadFileAsync(RepoRef repo, string path, string reference, CancellationToken cancellationToken) =>
            inner.ReadFileAsync(repo, path, Reference(reference), cancellationToken);

        public Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key, CancellationToken cancellationToken) =>
            inner.OpenPullRequestAsync(repo, In(spec), $"{fixture.Prefix}/{key}", cancellationToken);

        public Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestSpec spec, CancellationToken cancellationToken) =>
            inner.UpdatePullRequestAsync(pullRequest, In(spec), cancellationToken);

        public async Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pullRequest, CancellationToken cancellationToken)
        {
            var state = await inner.GetPullRequestAsync(pullRequest, cancellationToken);
            return state.Ok ? ProviderResult<PullRequestState>.Success(state.Value! with { Head = fixture.Unbranch(state.Value.Head), Base = fixture.Unbranch(state.Value.Base) }) : state;
        }

        public Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pullRequest, string? comment, CancellationToken cancellationToken) =>
            inner.ClosePullRequestAsync(pullRequest, comment, cancellationToken);

        public Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pullRequest, string key, CancellationToken cancellationToken) =>
            inner.CreateRevertPullRequestAsync(pullRequest, $"{fixture.Prefix}/{key}", cancellationToken);

        public Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(RepoRef repo, string revision, CancellationToken cancellationToken) =>
            inner.GetCheckStatusAsync(repo, revision, cancellationToken);
    }
}

/// <summary>
/// The CI contract against GitHub Actions on the fixture repository, with its <c>unit.yml</c> workflow, which runs the
/// fixture's unit tests and uploads a TRX file. A head with CI is the fixture's <c>main</c>; one without is an orphan
/// commit with no workflows. Keys are prefixed per test, so a rerun never adopts an earlier run.
/// </summary>
[Trait("Category", "Live")]
public sealed class GitHubActionsCiContract : CiContract, IAsyncLifetime
{
    readonly LiveFixture fixture = new();
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-gha-live-" + Guid.NewGuid().ToString("N"));

    public GitHubActionsCiContract()
    {
        var store = new FileSystemArtifactStore(folder);
        Ci = new Keyed(new GitHubActionsCi(fixture.Api, () => store), fixture);
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await fixture.CleanUpAsync();
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    protected override ICiProvider Ci { get; }

    protected override RepoRef Repo => LiveFixture.Repo;

    protected override CiJobRef PassingJob { get; } = new("unit.yml", new Dictionary<string, string>());

    protected override TimeSpan RunTimeout => TimeSpan.FromMinutes(10);

    protected override TimeSpan PollInterval => TimeSpan.FromSeconds(10);

    protected override async Task<string> HeadAsync(bool withCi)
    {
        if (withCi)
            return await fixture.ResolveAsync("main") ?? throw new InvalidOperationException("the fixture has no main");
        var orphan = await fixture.CommitAsync(null, new Dictionary<string, string?> { ["README.md"] = "no CI here\n" }, "No CI");
        await fixture.SetBranchAsync(fixture.Branch("no-ci"), orphan);
        return orphan;
    }

    sealed class Keyed(ICiProvider inner, LiveFixture fixture) : ICiProvider
    {
        public ProviderDescription Describe() => inner.Describe();

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

        public async Task<ProviderResult<CiRunRef>> TriggerAsync(RepoRef repo, CiJobRef job, string revision, string key, CancellationToken cancellationToken)
        {
            var scoped = $"{fixture.Prefix}/{key}";
            fixture.ForgetClaim(GitHubActionsCi.ClaimFor(scoped));
            var run = await inner.TriggerAsync(repo, job, revision, scoped, cancellationToken);
            LiveFixture.Log($"ci trigger {key}: {(run.Ok ? $"run {run.Value!.Id} {run.Value.Url}" : run.Error!.Reason + " " + run.Error.Message)}");
            return run;
        }

        public Task<ProviderResult<CiRunStatus>> GetRunStatusAsync(CiRunRef run, CancellationToken cancellationToken) => inner.GetRunStatusAsync(run, cancellationToken);

        public async Task<ProviderResult<CiRunResult>> ReadResultsAsync(CiRunRef run, CancellationToken cancellationToken)
        {
            var result = await inner.ReadResultsAsync(run, cancellationToken);
            LiveFixture.Log($"ci results {run.Id}: {(result.Ok ? $"{result.Value!.Conclusion}; files {string.Join(", ", result.Value.Files.Select(f => $"{f.Name} ({f.Format})"))}" : result.Error!.Reason)}");
            return result;
        }

        public Task<ProviderResult<ArtifactRef>> ReadLogsAsync(CiRunRef run, CancellationToken cancellationToken) => inner.ReadLogsAsync(run, cancellationToken);

        public Task<ProviderResult<ExpectedRuns>> GetExpectedRunsAsync(RepoRef repo, string revision, PullRequestRef? pullRequest, CancellationToken cancellationToken) =>
            inner.GetExpectedRunsAsync(repo, revision, pullRequest, cancellationToken);
    }
}

/// <summary>
/// Review finding 7 on GitHub itself: an engine squash leaves its pull request closed and not merged, and its revert is
/// built from the squash commit. The test squash-merges as the engine does (a commit with the head's tree and the
/// merge trailer, pushed to the base; the pull request closed), then asks for the revert.
/// </summary>
[Trait("Category", "Live")]
public sealed class GitHubSquashRevertLive : IAsyncLifetime
{
    readonly LiveFixture fixture = new();

    public async ValueTask InitializeAsync() => await fixture.CreateBaseAsync();

    public async ValueTask DisposeAsync() => await fixture.CleanUpAsync();

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task An_engine_squash_is_reverted_into_the_same_base()
    {
        var host = new GitHubCodeHost(fixture.Api);
        var (main, work) = (fixture.Branch("main"), fixture.Branch("ff/squash"));
        var start = (await fixture.ResolveAsync(main))!;
        var head = await fixture.CommitAsync(start, new Dictionary<string, string?> { ["work.txt"] = "squashed work\n" }, "The work");
        await fixture.SetBranchAsync(work, head);
        var pr = (await host.OpenPullRequestAsync(LiveFixture.Repo, new PullRequestSpec("Squash me", "Body", work, main), $"{fixture.Prefix}/key-squash", Ct)).Unwrap();
        var squash = await fixture.CommitAsync(start, new Dictionary<string, string?> { ["work.txt"] = "squashed work\n" },
            $"Squash me\n\nFeature-Flow-Merge: {fixture.Prefix}/run/scm.merge/1");
        await fixture.SetBranchAsync(main, squash);
        (await host.ClosePullRequestAsync(pr, null, Ct)).Unwrap();

        var revert = (await host.CreateRevertPullRequestAsync(pr, $"{fixture.Prefix}/key-squash-revert", Ct)).Unwrap();
        var again = (await host.CreateRevertPullRequestAsync(pr, $"{fixture.Prefix}/key-squash-revert", Ct)).Unwrap();
        var state = (await host.GetPullRequestAsync(revert, Ct)).Unwrap();

        Assert.Equal(revert, again);
        Assert.Equal((PullRequestStates.Open, main), (state.State, state.Base));
        Assert.Null((await host.ReadFileAsync(LiveFixture.Repo, "work.txt", state.HeadRevision, Ct)).Unwrap());
        Assert.NotNull((await host.ReadFileAsync(LiveFixture.Repo, "README.md", state.HeadRevision, Ct)).Unwrap());
        LiveFixture.Log($"squash revert: pull request {revert.Number} reverts squash {squash[..10]} of #{pr.Number}");
    }
}
