using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>The tracker contract for the GitHub Issues provider, against GitHub Issues in memory (<see cref="FakeGitHubIssues"/>), whose lists lag.</summary>
public sealed class GitHubIssuesTrackerContract : TrackerContract
{
    // The newest issue is left out of every list, as GitHub's lists lag a create.
    readonly FakeGitHubIssues github = new() { Unlisted = 1 };

    public GitHubIssuesTrackerContract() => Tracker = new GitHubIssuesTracker(github.Api(), "o/r");

    protected override ITrackerProvider Tracker { get; }

    protected override (string First, string Second) Statuses => ("in_progress", "done");

    protected override Task<IReadOnlyList<string>> CommentsAsync(IssueRef issue) => Task.FromResult(github.Comments(int.Parse(issue.Key[(issue.Key.IndexOf('#') + 1)..])));
}

/// <summary>The GitHub Issues tracker's mapping of GitHub's answers (docs/06 section 1.1) and of statuses to labels and state.</summary>
public sealed class GitHubIssuesTrackerTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static (string, string) Shown((string State, IReadOnlyList<string> Labels) read) => (read.State, string.Join(",", read.Labels));

    [Fact]
    public async Task Its_tokens_ask_for_issues_and_never_for_code_or_administration()
    {
        var github = new FakeGitHub().On(HttpMethod.Get, "~/issues/7", 200, """{ "id": 1, "number": 7, "title": "T", "body": "", "state": "open", "labels": [] }""");
        var broker = new FakeGitHub.Broker();

        await new GitHubIssuesTracker(github.Api(broker), "o/r").GetIssueAsync(new IssueRef("o/r#7"), Ct);

        var permissions = broker.Issued.Single().Permissions;
        Assert.Equal("read", permissions["issues"]);
        Assert.DoesNotContain("contents", permissions.Keys);
        Assert.DoesNotContain("administration", permissions.Keys);
    }

    [Fact]
    public async Task A_pull_requests_number_or_a_deleted_issue_is_not_found()
    {
        var github = new FakeGitHub()
            .On(HttpMethod.Get, "~/issues/7", 200, """{ "id": 1, "number": 7, "title": "A PR", "state": "open", "labels": [], "pull_request": {} }""")
            .On(HttpMethod.Get, "~/issues/8", 410, """{ "message": "This issue was deleted" }""");
        var tracker = new GitHubIssuesTracker(github.Api(), "o/r");

        Assert.Equal("not_found", (await tracker.GetIssueAsync(new IssueRef("o/r#7"), Ct)).Error!.Reason);
        Assert.Equal("not_found", (await tracker.GetIssueAsync(new IssueRef("#8"), Ct)).Error!.Reason);
        Assert.Equal("not_found", (await tracker.GetIssueAsync(new IssueRef("not a key"), Ct)).Error!.Reason);
    }

    [Fact]
    public async Task A_create_answered_with_an_undocumented_success_is_unexpected_response()
    {
        var github = new FakeGitHub()
            .On(HttpMethod.Get, "~/issues?state=all", 200, "[]")
            .On(HttpMethod.Get, "~/issues/1", 404, """{ "message": "Not Found" }""")
            .On(HttpMethod.Post, "~/issues", 202, "{}");

        var created = await new GitHubIssuesTracker(github.Api(), "o/r").CreateAsync(new IssueSpec("T"), "k", Ct);

        Assert.Equal("unexpected_response", created.Error!.Reason);
    }

    [Fact]
    public async Task A_closed_issue_reads_as_the_status_mapped_to_closed_and_status_labels_are_not_its_labels()
    {
        var github = new FakeGitHub()
            .On(HttpMethod.Get, "~/issues/3", 200, """{ "id": 1, "number": 3, "title": "T", "body": "B", "state": "open", "labels": [{ "name": "status: code review" }, { "name": "bug" }] }""")
            .On(HttpMethod.Get, "~/issues/4", 200, """{ "id": 2, "number": 4, "title": "T", "body": "B", "state": "closed", "labels": [] }""")
            .On(HttpMethod.Get, "~/issues/5", 200, """{ "id": 3, "number": 5, "title": "T", "body": "B", "state": "open", "labels": [] }""");
        var tracker = new GitHubIssuesTracker(github.Api(), "o/r");

        var review = (await tracker.GetIssueAsync(new IssueRef("#3"), Ct)).Unwrap();
        Assert.Equal("code_review", review.Status);
        Assert.Equal(["bug"], review.Labels);
        Assert.Equal("done", (await tracker.GetIssueAsync(new IssueRef("#4"), Ct)).Unwrap().Status);
        Assert.Equal("open", (await tracker.GetIssueAsync(new IssueRef("#5"), Ct)).Unwrap().Status);
    }

    [Fact]
    public async Task A_transition_leaves_one_status_label_and_done_closes_the_issue()
    {
        var github = new FakeGitHubIssues();
        var tracker = new GitHubIssuesTracker(github.Api(), "o/r");
        var issue = (await tracker.CreateAsync(new IssueSpec("T", "B", ["bug"]), "k", Ct)).Unwrap();

        (await tracker.TransitionAsync(issue, "in_progress", Ct)).Unwrap();
        (await tracker.TransitionAsync(issue, "code_review", Ct)).Unwrap();
        Assert.Equal(("open", "bug,status: code review"), Shown(github.Read(2)));

        (await tracker.TransitionAsync(issue, "done", Ct)).Unwrap();
        Assert.Equal(("closed", "bug"), Shown(github.Read(2)));

        (await tracker.TransitionAsync(issue, "in_progress", Ct)).Unwrap();
        Assert.Equal(("open", "bug,status: in progress"), Shown(github.Read(2)));
    }

    [Fact]
    public async Task A_configured_status_map_replaces_the_default()
    {
        var github = new FakeGitHubIssues();
        var tracker = new GitHubIssuesTracker(github.Api(), "o/r", new Dictionary<string, string> { ["shipped"] = "closed", ["doing"] = "wip" });
        var issue = (await tracker.CreateAsync(new IssueSpec("T"), "k", Ct)).Unwrap();

        Assert.Equal("validation_failed", (await tracker.TransitionAsync(issue, "in_progress", Ct)).Error!.Reason);
        (await tracker.TransitionAsync(issue, "doing", Ct)).Unwrap();
        Assert.Equal("doing", (await tracker.GetIssueAsync(issue, Ct)).Unwrap().Status);
    }

    [Fact]
    public async Task An_update_keeps_the_marker_and_the_status_label()
    {
        var github = new FakeGitHubIssues();
        var tracker = new GitHubIssuesTracker(github.Api(), "o/r");
        var issue = (await tracker.CreateAsync(new IssueSpec("T", "Old body"), "k", Ct)).Unwrap();
        (await tracker.TransitionAsync(issue, "code_review", Ct)).Unwrap();

        (await tracker.UpdateAsync(issue, new IssueSpec(Body: "New body", Labels: ["feature"]), Ct)).Unwrap();

        Assert.Equal(issue, (await tracker.CreateAsync(new IssueSpec("Retried"), "k", Ct)).Unwrap());
        var read = (await tracker.GetIssueAsync(issue, Ct)).Unwrap();
        Assert.Equal(("New body", "code_review"), (read.Body, read.Status));
        Assert.Equal(["feature"], read.Labels);
    }

    [Fact]
    public async Task A_create_retried_before_GitHubs_lists_show_the_first_adopts_it()
    {
        var github = new FakeGitHubIssues { Unlisted = 2 };
        var tracker = new GitHubIssuesTracker(github.Api(), "o/r");

        var first = (await tracker.CreateAsync(new IssueSpec("T", Labels: ["x"]), "k", Ct)).Unwrap();
        var retried = (await tracker.CreateAsync(new IssueSpec("T", Labels: ["x"]), "k", Ct)).Unwrap();

        Assert.Equal(first, retried);
        Assert.Single((await tracker.SearchAsync(new IssueQuery(Label: "x"), Ct)).Unwrap());
    }

    [Fact]
    public async Task Custom_fields_are_refused_GitHub_Issues_has_none()
    {
        var tracker = new GitHubIssuesTracker(new FakeGitHubIssues().Api(), "o/r");

        var created = await tracker.CreateAsync(new IssueSpec("T", Fields: new Dictionary<string, string> { ["points"] = "3" }), "k", Ct);

        Assert.Equal("validation_failed", created.Error!.Reason);
    }

    [Fact]
    public async Task A_rate_limit_is_infrastructure_and_carries_its_retry_after()
    {
        var github = new FakeGitHub().On(HttpMethod.Get, "~/issues/7", 403, """{ "message": "API rate limit exceeded" }""",
            new Dictionary<string, string> { ["Retry-After"] = "30" });

        var read = await new GitHubIssuesTracker(github.Api(), "o/r").GetIssueAsync(new IssueRef("#7"), Ct);

        Assert.Equal(("rate_limited", ProviderErrorClass.Infrastructure), (read.Error!.Reason, read.Error.Class));
        Assert.Equal(TimeSpan.FromSeconds(30), read.Error.RetryAfter);
    }
}
