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
        var (w, host) = World("forbidden", 0);
        await using var _ = w;
        w.CommitWork("a.txt", "work");
        Assert.Equal("updated", (await w.RunAsync("scm.catch_up", null, "catch_up")).Report?.Outcome);
        w.CommitWork("b.txt", "more work");
        host().Failures = int.MaxValue;

        var result = await w.Actions.CleanUpAsync(w.RunId, "catch_up", 1, [w.Repo.FullName], ActionWorld.WorkBranch, ["catch_up"],
            TestContext.Current.CancellationToken);

        Assert.Contains("configuration fault", result.Error, StringComparison.Ordinal);
        Assert.Equal(1, host().Calls - 1);
    }

    [Fact]
    public async Task A_clean_up_refused_in_a_later_repository_still_reports_the_resets_it_made_before()
    {
        var (w, host) = World("forbidden", 0);
        await using var _ = w;
        var ct = TestContext.Current.CancellationToken;
        w.CommitWork("a.txt", "work");
        Assert.Equal("updated", (await w.RunAsync("scm.catch_up", null, "catch_up")).Report?.Outcome);
        var start = w.Work!;
        w.CommitWork("b.txt", "more work");
        // A second repository the step touched, which the code host refuses.
        var web = w.Forge.CreateRepository("acme/web");
        var webStart = w.Forge.Commit(web, "refs/heads/" + ActionWorld.WorkBranch, new Dictionary<string, string?> { ["w.txt"] = "web" });
        (await w.Records.AppendAsync(new RecordBatch(AgentEvents: [new AgentEventRecord($"{w.RunId}/catch_up/1", "start", $"acme/web {webStart}", DateTimeOffset.UtcNow)]), ct)).Unwrap();
        host().Only = "acme/web";
        host().Failures = int.MaxValue;

        var result = await w.Actions.CleanUpAsync(w.RunId, "catch_up", 1, [w.Repo.FullName, "acme/web"], ActionWorld.WorkBranch, ["catch_up"], ct);

        Assert.Contains("configuration fault", result.Error, StringComparison.Ordinal);
        Assert.Equal(start, w.Work);
        Assert.Contains(result.Events, e => e.StartsWith($"clean_up: reset acme/api {ActionWorld.WorkBranch}", StringComparison.Ordinal));
    }
}
