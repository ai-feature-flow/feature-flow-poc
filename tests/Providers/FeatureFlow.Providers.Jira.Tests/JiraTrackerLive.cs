using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.Jira.Tests;

/// <summary>
/// The tracker contract against a real Jira Cloud site, named only by the environment (LOG 266): <c>FF_LIVE_JIRA_URL</c>
/// (the site), <c>FF_LIVE_JIRA_PROJECT</c> (a project key whose workflow has To Do, In Progress, In Review and Done),
/// <c>FF_LIVE_JIRA_USER</c> (the account's email) and <c>FF_LIVE_JIRA_TOKEN</c> (its API token, never printed). Every issue
/// it creates also carries the label <c>feature-flow-test</c>; <see cref="DisposeAsync"/> moves each one to done.
/// </summary>
[Trait("Category", "Live")]
public sealed class JiraTrackerLiveContract : TrackerContract, IAsyncLifetime
{
    public const string TestLabel = "feature-flow-test";

    readonly JiraTracker tracker;
    readonly List<IssueRef> created = [];

    public JiraTrackerLiveContract()
    {
        static string Required(string name) =>
            Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : throw new InvalidOperationException($"{name} is not set");
        var url = Required("FF_LIVE_JIRA_URL");
        var credential = Required("FF_LIVE_JIRA_USER") + ":" + Required("FF_LIVE_JIRA_TOKEN");
        tracker = new JiraTracker(api = new JiraApi(_ => Task.FromResult(ProviderResult<string>.Success(credential)), url), Required("FF_LIVE_JIRA_PROJECT"));
        Tracker = new Recorded(tracker, created);
    }

    readonly JiraApi api;

    [Fact]
    public async Task A_blocks_link_reads_on_the_site_as_the_first_issue_blocks_the_second()
    {
        var from = Ok(await Tracker.CreateAsync(new IssueSpec("Blocker", "", [Unique]), $"{Unique}-blocker", Ct));
        var to = Ok(await Tracker.CreateAsync(new IssueSpec("Blocked", "", [Unique]), $"{Unique}-blocked", Ct));

        Ok(await Tracker.LinkAsync(from, to, "blocks", Ct));

        var links = Ok(await api.SendAsync(HttpMethod.Get, $"/rest/api/3/issue/{from.Key}?fields=issuelinks", null, Ct)).Json!["fields"]!["issuelinks"]!.AsArray();
        var link = Assert.Single(links);
        Assert.Equal(("blocks", to.Key), (link!["type"]!["outward"]!.GetValue<string>(), link["outwardIssue"]?["key"]?.GetValue<string>()));
    }

    protected override ITrackerProvider Tracker { get; }

    protected override (string First, string Second) Statuses => ("in_progress", "done");

    protected override async Task<IReadOnlyList<string>> CommentsAsync(IssueRef issue) =>
        Ok(await tracker.CommentsAsync(issue.Key, Ct)).Select(c => JiraTracker.Text(c["body"])).ToList();

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        var failed = new List<string>();
        foreach (var issue in created.Distinct())
            if (await tracker.TransitionAsync(issue, "done", CancellationToken.None) is { Ok: false } refused)
                failed.Add($"{issue.Key}: {refused.Error!.Message}");
        Assert.True(failed.Count == 0, "the clean-up could not close: " + string.Join("; ", failed));
    }

    /// <summary>The provider, labelling each create <see cref="TestLabel"/> and noting each issue it returns.</summary>
    sealed class Recorded(ITrackerProvider inner, List<IssueRef> created) : ITrackerProvider
    {
        public ProviderDescription Describe() => inner.Describe();

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

        public Task<ProviderResult<Issue>> GetIssueAsync(IssueRef issue, CancellationToken cancellationToken) => inner.GetIssueAsync(issue, cancellationToken);

        public Task<ProviderResult<IReadOnlyList<Issue>>> SearchAsync(IssueQuery query, CancellationToken cancellationToken) => inner.SearchAsync(query, cancellationToken);

        public async Task<ProviderResult<IssueRef>> CreateAsync(IssueSpec spec, string key, CancellationToken cancellationToken)
        {
            var result = await inner.CreateAsync(spec with { Labels = [.. spec.Labels ?? [], TestLabel] }, key, cancellationToken);
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
}
