using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>
/// The tracker contract against GitHub Issues on the fixture repository. <see cref="Recorded"/> notes every issue the
/// provider creates; <see cref="DisposeAsync"/> closes each one and deletes the test's label, so nothing stays open. The identity is the
/// GitHub App (<c>FF_SECRETS_FILE</c>), which needs the <c>issues</c> permission; with <c>FF_GITHUB_TRACKER_TOKEN</c> set
/// (the name of an environment variable holding a token), that token is used instead. Neither is ever printed.
/// </summary>
[Trait("Category", "Live")]
public sealed class GitHubIssuesTrackerLiveContract : TrackerContract, IAsyncLifetime
{
    static readonly IReadOnlyDictionary<string, string> Write = new Dictionary<string, string> { ["issues"] = "write", ["metadata"] = "read" };

    readonly GitHubApi api;
    readonly List<IssueRef> created = [];

    public GitHubIssuesTrackerLiveContract()
    {
        api = Environment.GetEnvironmentVariable("FF_GITHUB_TRACKER_TOKEN") is { Length: > 0 } variable
            ? new GitHubApi(() => new TokenBroker(Environment.GetEnvironmentVariable(variable) ?? throw new InvalidOperationException($"{variable} is not set")))
            : new LiveFixture().Api;
        Tracker = new Recorded(new GitHubIssuesTracker(api, LiveFixture.Repo.FullName), created);
    }

    protected override ITrackerProvider Tracker { get; }

    protected override (string First, string Second) Statuses => ("in_progress", "done");

    protected override async Task<IReadOnlyList<string>> CommentsAsync(IssueRef issue)
    {
        var number = issue.Key[(issue.Key.IndexOf('#') + 1)..];
        var comments = (await api.ListAsync(LiveFixture.Repo, Write, $"/repos/{LiveFixture.Repo.FullName}/issues/{number}/comments", j => j as JsonArray, Ct)).Unwrap();
        return comments.Select(c => c["body"]!.GetValue<string>()).ToList();
    }

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        var token = CancellationToken.None;
        var failed = new List<string>();
        foreach (var issue in created.Distinct())
            if (await Tracker.TransitionAsync(issue, "done", token) is { Ok: false } refused)
                failed.Add($"{issue.Key}: {refused.Error!.Message}");
        await api.SendAsync(LiveFixture.Repo, Write, HttpMethod.Delete, $"/repos/{LiveFixture.Repo.FullName}/labels/{Uri.EscapeDataString(Unique)}", null, token);
        Assert.True(failed.Count == 0, "the clean-up could not close: " + string.Join("; ", failed));
    }

    /// <summary>The provider, noting each issue a create returns.</summary>
    sealed class Recorded(ITrackerProvider inner, List<IssueRef> created) : ITrackerProvider
    {
        public ProviderDescription Describe() => inner.Describe();

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

        public Task<ProviderResult<Issue>> GetIssueAsync(IssueRef issue, CancellationToken cancellationToken) => inner.GetIssueAsync(issue, cancellationToken);

        public Task<ProviderResult<IReadOnlyList<Issue>>> SearchAsync(IssueQuery query, CancellationToken cancellationToken) => inner.SearchAsync(query, cancellationToken);

        public async Task<ProviderResult<IssueRef>> CreateAsync(IssueSpec spec, string key, CancellationToken cancellationToken)
        {
            var result = await inner.CreateAsync(spec, key, cancellationToken);
            if (result.Ok)
                created.Add(result.Value!);
            return result;
        }

        public Task<ProviderResult<IssueRef>> UpdateAsync(IssueRef issue, IssueSpec spec, CancellationToken cancellationToken) => inner.UpdateAsync(issue, spec, cancellationToken);

        public Task<ProviderResult<Unit>> TransitionAsync(IssueRef issue, string status, CancellationToken cancellationToken) => inner.TransitionAsync(issue, status, cancellationToken);

        public Task<ProviderResult<Unit>> LinkAsync(IssueRef from, IssueRef to, string linkType, CancellationToken cancellationToken) =>
            inner.LinkAsync(from, to, linkType, cancellationToken);

        public Task<ProviderResult<CommentRef>> CommentAsync(IssueRef issue, string body, string key, CancellationToken cancellationToken) =>
            inner.CommentAsync(issue, body, key, cancellationToken);
    }

    /// <summary>A token from the environment, for a run where the App lacks the <c>issues</c> permission.</summary>
    sealed class TokenBroker(string token) : ICredentialBroker
    {
        public ProviderDescription Describe() => new("credential_broker", "token", "0", new NoCapabilities());

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

        public Task<ProviderResult<Credential>> IssueAsync(Actor actor, string targetSystem, CredentialScope scope, TimeSpan ttl, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<Credential>.Success(new Credential(targetSystem, scope, token, DateTimeOffset.UtcNow.AddHours(1), actor)));

        public Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken cancellationToken) =>
            IssueAsync(credential.Actor, credential.TargetSystem, credential.Scope, TimeSpan.FromHours(1), cancellationToken);
    }
}
