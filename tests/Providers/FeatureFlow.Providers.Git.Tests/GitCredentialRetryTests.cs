using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Git.Tests;

/// <summary>
/// docs/06 section 1.1 (LOG 147, review F5): an operation refused as a configuration fault runs once more, after a short
/// wait, with a freshly issued credential, and only then is the refusal returned. git sends a credential only to an
/// https remote, which these tests cannot serve, so the retry and the credential are tested at the provider's seams.
/// </summary>
public sealed class GitCredentialRetryTests
{
    static readonly RepoRef Repo = new("o/r");
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class CountingBroker : ICredentialBroker
    {
        public int Issued { get; private set; }

        public ProviderDescription Describe() => new("credential_broker", "test", "0", new NoCapabilities());

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

        public Task<ProviderResult<Credential>> IssueAsync(Actor actor, string targetSystem, CredentialScope scope, TimeSpan ttl, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<Credential>.Success(new Credential(targetSystem, scope, $"ghs_{++Issued}", DateTimeOffset.UtcNow.AddHours(1), actor)));

        public Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken cancellationToken) =>
            IssueAsync(credential.Actor, credential.TargetSystem, credential.Scope, TimeSpan.FromHours(1), cancellationToken);
    }

    static GitVcs Vcs(CountingBroker broker) => new("https://git.example/{repo}.git", Path.Combine(Path.GetTempPath(), "ff-git-retry-unused"),
        () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => broker)
    {
        FreshCredentialDelay = TimeSpan.FromMilliseconds(20),
    };

    [Theory]
    [InlineData("forbidden", 1, true)]
    [InlineData("unauthenticated", 1, true)]
    [InlineData("forbidden", 2, false)]
    public async Task A_refused_operation_runs_once_more_with_a_fresh_credential(string fault, int refusals, bool recovers)
    {
        var vcs = Vcs(new CountingBroker());
        var calls = new List<bool>();
        var started = System.Diagnostics.Stopwatch.StartNew();

        var result = await vcs.WithFreshCredentialOnFaultAsync(fresh =>
        {
            calls.Add(fresh);
            return Task.FromResult(calls.Count <= refusals ? ProviderResult<string>.Failure(fault, "refused") : ProviderResult<string>.Success("done"));
        }, Ct);

        Assert.Equal([false, true], calls);
        Assert.Equal(recovers, result.Ok);
        // Timers tick in whole milliseconds, so a 20 ms delay can measure a little under 20 ms on the stopwatch.
        Assert.True(started.Elapsed >= TimeSpan.FromMilliseconds(15), "the retry waited first");
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("rejected")]
    public async Task Other_errors_are_not_retried_here(string reason)
    {
        var calls = 0;
        var result = await Vcs(new CountingBroker()).WithFreshCredentialOnFaultAsync(_ =>
        {
            calls++;
            return Task.FromResult(ProviderResult<string>.Failure(reason, "no"));
        }, Ct);

        Assert.Equal((1, reason), (calls, result.Error!.Reason));
    }

    [Fact]
    public async Task A_fresh_environment_issues_a_new_credential_and_a_normal_one_reuses_the_cache()
    {
        var broker = new CountingBroker();
        var vcs = Vcs(broker);

        var first = (await vcs.EnvironmentAsync(Repo, write: false, fresh: false, Ct)).Unwrap();
        (await vcs.EnvironmentAsync(Repo, write: false, fresh: false, Ct)).Unwrap();
        Assert.Equal(1, broker.Issued);

        var fresh = (await vcs.EnvironmentAsync(Repo, write: false, fresh: true, Ct)).Unwrap();
        Assert.Equal(2, broker.Issued);
        Assert.NotEqual(first.Values.Single(v => v.StartsWith("AUTHORIZATION", StringComparison.Ordinal)),
            fresh.Values.Single(v => v.StartsWith("AUTHORIZATION", StringComparison.Ordinal)));
    }
}
