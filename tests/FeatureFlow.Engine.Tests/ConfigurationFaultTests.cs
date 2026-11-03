using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// docs/06 section 1.1 (LOG 147): <c>unauthenticated</c> and <c>forbidden</c> reaching the engine are configuration
/// faults (the provider has already retried once with a fresh credential), so the step, a <c>clean_up</c> or run start
/// stops for a person at once, naming the fault. Other infrastructure errors are still retried without limit.
/// </summary>
public sealed class ConfigurationFaultTests
{
    /// <summary>A code host whose default-branch read fails with <see cref="Fault"/> for its first <see cref="Failures"/> calls.</summary>
    internal sealed class RefusingCodeHost(ICodeHostProvider inner, string fault, int failures) : ICodeHostProvider
    {
        public string Fault { get; set; } = fault;
        public int Failures { get; set; } = failures;
        public int Calls { get; private set; }

        /// <summary>The repositories refused; null refuses every one.</summary>
        public string? Only { get; set; }

        public ProviderDescription Describe() => inner.Describe();
        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

        public Task<ProviderResult<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken cancellationToken) =>
            (Only is null || Only == repo.FullName) && ++Calls <= Failures
                ? Task.FromResult(ProviderResult<string>.Failure(Fault, "GitHub refused the request"))
                : inner.GetDefaultBranchAsync(repo, cancellationToken);

        public Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken cancellationToken) => inner.GetBranchProtectionAsync(repo, branch, cancellationToken);
        public Task<ProviderResult<RepoFile?>> ReadFileAsync(RepoRef repo, string path, string reference, CancellationToken cancellationToken) => inner.ReadFileAsync(repo, path, reference, cancellationToken);
        public Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key, CancellationToken cancellationToken) => inner.OpenPullRequestAsync(repo, spec, key, cancellationToken);
        public Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestSpec spec, CancellationToken cancellationToken) => inner.UpdatePullRequestAsync(pullRequest, spec, cancellationToken);
        public Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) => inner.GetPullRequestAsync(pullRequest, cancellationToken);
        public Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pullRequest, string? comment, CancellationToken cancellationToken) => inner.ClosePullRequestAsync(pullRequest, comment, cancellationToken);
        public Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pullRequest, string key, CancellationToken cancellationToken) => inner.CreateRevertPullRequestAsync(pullRequest, key, cancellationToken);
        public Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(RepoRef repo, string revision, CancellationToken cancellationToken) => inner.GetCheckStatusAsync(repo, revision, cancellationToken);

        public Task<ProviderResult<CommentRef>> AddCommentAsync(PullRequestRef pullRequest, string body, string key, CancellationToken cancellationToken) =>
            inner.AddCommentAsync(pullRequest, body, key, cancellationToken);
    }

    static (ActionWorld World, Func<RefusingCodeHost> Host) World(string fault, int failures)
    {
        RefusingCodeHost? host = null;
        var world = new ActionWorld(codeHost: inner => host = new RefusingCodeHost(inner, fault, failures));
        return (world, () => host!);
    }

    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("forbidden")]
    public async Task A_refused_credential_stops_the_step_for_a_person_at_once(string fault)
    {
        var (w, host) = World(fault, int.MaxValue);
        await using var _ = w;
        w.CommitWork("a.txt", "work");

        var result = await w.RunAsync("scm.pull_request", "{ title: T-1 }", "open_pr");

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("configuration fault", result.Reason, StringComparison.Ordinal);
        Assert.Contains(fault, result.Reason, StringComparison.Ordinal);
        Assert.Equal(1, host().Calls);
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("rate_limited")]
    [InlineData("server_error")]
    public async Task Other_infrastructure_errors_are_thrown_for_Temporal_to_retry(string fault)
    {
        var (w, host) = World(fault, int.MaxValue);
        await using var _ = w;
        w.CommitWork("a.txt", "work");

        await Assert.ThrowsAsync<ActionInfrastructureException>(() => w.RunAsync("scm.pull_request", "{ title: T-1 }", "open_pr"));
        Assert.Equal(1, host().Calls);
    }

    [Fact]
    public async Task A_clean_up_whose_credential_is_refused_stops_for_a_person()
    {
        var (w, _) = World("forbidden", 0);
        await using var _w = w;
        w.CommitWork("a.txt", "work");
        Assert.Equal("updated", (await w.RunAsync("scm.catch_up", null, "catch_up")).Report?.Outcome);
        var moved = w.CommitWork("b.txt", "more work");
        w.Forge.Fail("vcs.Resolve", new ProviderError("forbidden", "GitHub refused the request"));

        var result = await w.Actions.CleanUpAsync(w.RunId, "catch_up", 1, [w.Repo.FullName], ActionWorld.WorkBranch, ["catch_up"],
            TestContext.Current.CancellationToken);

        Assert.Contains("configuration fault", result.Error, StringComparison.Ordinal);
        Assert.Equal(moved, w.Work);
    }

    /// <summary>A clean_up stopped in a later repository (here one that no longer exists) still reports the resets it made before.</summary>
    [Fact]
    public async Task A_clean_up_refused_in_a_later_repository_still_reports_the_resets_it_made_before()
    {
        var (w, _) = World("forbidden", 0);
        await using var _w = w;
        var ct = TestContext.Current.CancellationToken;
        w.CommitWork("a.txt", "work");
        Assert.Equal("updated", (await w.RunAsync("scm.catch_up", null, "catch_up")).Report?.Outcome);
        var start = w.Work!;
        w.CommitWork("b.txt", "more work");
        (await w.Records.AppendAsync(new RecordBatch(AgentEvents: [new AgentEventRecord($"{w.RunId}/catch_up/1", "start", $"acme/gone {start}", DateTimeOffset.UtcNow)]), ct)).Unwrap();

        var result = await w.Actions.CleanUpAsync(w.RunId, "catch_up", 1, [w.Repo.FullName, "acme/gone"], ActionWorld.WorkBranch, ["catch_up"], ct);

        Assert.NotNull(result.Error);
        Assert.Equal(start, w.Work);
        Assert.Contains(result.Events, e => e.StartsWith($"clean_up: reset acme/api {ActionWorld.WorkBranch}", StringComparison.Ordinal));
    }
}
