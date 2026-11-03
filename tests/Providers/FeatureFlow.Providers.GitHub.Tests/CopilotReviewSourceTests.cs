using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>Copilot review as GitHub documents it: reviews, requested reviewers and review comments on a pull request.</summary>
static class CopilotGitHub
{
    public static readonly RepoRef Repo = new("o/r");
    public static readonly string Head = new('a', 40);
    public static readonly string Older = new('b', 40);

    public static string Review(long id, string commit, string login = CopilotReviewSource.Reviewer) =>
        new JsonObject { ["id"] = id, ["commit_id"] = commit, ["state"] = "COMMENTED", ["user"] = new JsonObject { ["login"] = login, ["type"] = "Bot" } }.ToJsonString();

    public static string Reviewers(params string[] logins) =>
        new JsonObject { ["users"] = new JsonArray(logins.Select(l => (JsonNode)new JsonObject { ["login"] = l }).ToArray()), ["teams"] = new JsonArray() }.ToJsonString();

    public const string Comments = """
        [
          { "id": 1, "path": "src/Orders.cs", "line": 12, "body": "**Possible null dereference**\n\n`order` may be null here.\n\n```suggestion\nif (order is null) return;\n```",
            "diff_hunk": "@@ -10,3 +10,4 @@ class Orders\n {\n-    var a = 1;\n+    var total = order.Total;" },
          { "id": 2, "path": "src/Orders.cs", "start_line": 20, "line": 21, "body": "This loop never ends.",
            "diff_hunk": "@@ -18,2 +18,4 @@\n void Run() {\n+  while (true)\n+  { }" }
        ]
        """;

    /// <summary>Pull request 7 reviewed by Copilot at <see cref="Head"/> (review 55); pull request 8 where Copilot cannot be asked.</summary>
    public static FakeGitHub World() => new FakeGitHub()
        .On(HttpMethod.Get, "~/pulls/7/reviews/55/comments", 200, Comments)
        .On(HttpMethod.Get, "~/pulls/7/reviews", 200, $"[{Review(40, Older)}, {Review(55, Head)}, {Review(60, Head, "a-person")}]")
        .On(HttpMethod.Get, "~/pulls/8/reviews", 200, "[]")
        .On(HttpMethod.Get, "~/pulls/8/requested_reviewers", 200, Reviewers())
        .On(HttpMethod.Post, "~/pulls/8/requested_reviewers", 422, """{"message":"Reviews may only be requested from collaborators."}""")
        .On(HttpMethod.Get, "~/pulls/404/reviews", 404, """{"message":"Not Found"}""");
}

/// <summary>The findings source contract against Copilot review, over a recorded GitHub.</summary>
public sealed class CopilotReviewSourceContract : FindingsSourceContract, IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-copilot-" + Guid.NewGuid().ToString("N"));
    readonly FileSystemArtifactStore store;

    public CopilotReviewSourceContract()
    {
        store = new FileSystemArtifactStore(folder);
        Source = new CopilotReviewSource(CopilotGitHub.World().Api(), () => store);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    protected override IFindingsSourceProvider Source { get; }

    protected override IArtifactStoreProvider Store => store;

    protected override Task<FindingsTarget> ReviewedTargetAsync() =>
        Task.FromResult(new FindingsTarget(CopilotGitHub.Repo, CopilotGitHub.Head, new PullRequestRef(CopilotGitHub.Repo, 7)));

    protected override Task<FindingsTarget> UnavailableTargetAsync() =>
        Task.FromResult(new FindingsTarget(CopilotGitHub.Repo, CopilotGitHub.Head, new PullRequestRef(CopilotGitHub.Repo, 8)));
}

public sealed class CopilotReviewSourceTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-copilot-" + Guid.NewGuid().ToString("N"));
    readonly FileSystemArtifactStore store;

    public CopilotReviewSourceTests() => store = new FileSystemArtifactStore(folder);

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    CopilotReviewSource Source(FakeGitHub github, bool blocking = false) => new(github.Api(), () => store, blocking);

    static FindingsTarget Target(int pr, string? revision = null) => new(CopilotGitHub.Repo, revision ?? CopilotGitHub.Head, new PullRequestRef(CopilotGitHub.Repo, pr));

    async Task<FindingsReport> ReadAsync(CopilotReviewSource source, CollectHandle handle)
    {
        await using var content = (await store.GetAsync((await source.ReadFindingsAsync(handle, Ct)).Unwrap(), Ct)).Unwrap();
        return ArtifactShapes.ReadLibrary<FindingsReport>(await new StreamReader(content, Encoding.UTF8).ReadToEndAsync(Ct), "report");
    }

    [Fact]
    public async Task Each_review_comment_becomes_a_finding_on_the_lines_it_is_about_with_a_suggestion_as_its_correction()
    {
        var source = Source(CopilotGitHub.World());
        var handle = (await source.RequestAsync(Target(7), "k", Ct)).Unwrap();

        var report = await ReadAsync(source, handle);

        Assert.Equal(2, report.Findings.Count);
        var first = report.Findings[0];
        Assert.Equal((CopilotReviewSource.Rule, "Possible null dereference", "medium", false, FindingBases.Reasoned), (first.Rule, first.Title, first.Severity, first.Blocking, first.Basis));
        Assert.Equal(new FindingLocation("o/r", "src/Orders.cs", null, "    var total = order.Total;"), first.Location);
        Assert.Equal("if (order is null) return;", first.RequiredCorrection);
        Assert.Equal("  while (true)\n  { }", report.Findings[1].Location.Snippet);
        Assert.Null(report.Findings[1].RequiredCorrection);
    }

    [Fact]
    public async Task A_comment_on_unchanged_lines_has_the_same_fingerprint_after_the_hunk_moves()
    {
        var moved = """[{ "id": 9, "path": "src/Orders.cs", "line": 40, "body": "**Possible null dereference**", "diff_hunk": "@@ -38,3 +38,4 @@\n {\n+    var total = order.Total;" }]""";
        var github = new FakeGitHub()
            .On(Get, "~/pulls/7/reviews/70/comments", 200, moved)
            .On(Get, "~/pulls/7/reviews", 200, $"[{CopilotGitHub.Review(70, CopilotGitHub.Head)}]");
        var before = (await ReadAsync(Source(CopilotGitHub.World()), (await Source(CopilotGitHub.World()).RequestAsync(Target(7), "k", Ct)).Unwrap())).Findings[0];

        var after = (await ReadAsync(Source(github), (await Source(github).RequestAsync(Target(7), "k", Ct)).Unwrap())).Findings[0];

        Assert.Equal(Fingerprints.Of(before), Fingerprints.Of(after));
    }

    [Fact]
    public async Task Copilots_newest_review_of_the_revision_is_read()
    {
        var github = new FakeGitHub()
            .On(Get, "~/pulls/7/reviews/91/comments", 200, """[{ "id": 3, "path": "src/New.cs", "line": 1, "body": "Newest", "diff_hunk": "@@ -1 +1 @@\n+x" }]""")
            .On(Get, "~/pulls/7/reviews/90/comments", 200, """[{ "id": 2, "path": "src/Old.cs", "line": 1, "body": "Older", "diff_hunk": "@@ -1 +1 @@\n+y" }]""")
            .On(Get, "~/pulls/7/reviews", 200, $"[{CopilotGitHub.Review(91, CopilotGitHub.Head)}, {CopilotGitHub.Review(90, CopilotGitHub.Head)}]");
        var source = Source(github);

        var report = await ReadAsync(source, (await source.RequestAsync(Target(7), "k", Ct)).Unwrap());

        Assert.Equal("src/New.cs", Assert.Single(report.Findings).Location.File);
    }

    [Fact]
    public async Task The_instance_can_make_copilots_findings_blocking()
    {
        var source = Source(CopilotGitHub.World(), blocking: true);

        var report = await ReadAsync(source, (await source.RequestAsync(Target(7), "k", Ct)).Unwrap());

        Assert.All(report.Findings, f => Assert.True(f.Blocking));
    }

    [Fact]
    public async Task Copilot_is_asked_once_then_pending_until_it_reviews_the_revision()
    {
        var reviews = "[]";
        var reviewers = CopilotGitHub.Reviewers();
        var github = new FakeGitHub()
            .On(Get, "~/pulls/9/reviews/80/comments", 200, "[]")
            .On(Get, "~/pulls/9/reviews", _ => Json(reviews))
            .On(Get, "~/pulls/9/requested_reviewers", _ => Json(reviewers))
            .On(Post, "~/pulls/9/requested_reviewers", _ =>
            {
                reviewers = CopilotGitHub.Reviewers(CopilotReviewSource.Reviewer);
                return Json($$"""{ "number": 9, "requested_reviewers": [ { "login": "{{CopilotReviewSource.Reviewer}}" } ] }""", 201);
            });
        var source = Source(github);

        var handle = (await source.RequestAsync(Target(9), "k", Ct)).Unwrap();
        var again = (await source.RequestAsync(Target(9), "k", Ct)).Unwrap();
        var pending = (await source.GetStatusAsync(handle, Ct)).Unwrap();
        reviews = $"[{CopilotGitHub.Review(70, CopilotGitHub.Older)}]";
        var reviewedAnOlderHead = (await source.GetStatusAsync(handle, Ct)).Unwrap();
        reviews = $"[{CopilotGitHub.Review(80, CopilotGitHub.Head)}]";
        reviewers = CopilotGitHub.Reviewers();
        var ready = (await source.GetStatusAsync(handle, Ct)).Unwrap();

        Assert.Equal(handle, again);
        var post = Assert.Single(github.Called(Post, "~/pulls/9/requested_reviewers"));
        Assert.Contains(CopilotReviewSource.Reviewer, post.Body, StringComparison.Ordinal);
        Assert.Equal((CollectStates.Pending, CollectStates.Pending, CollectStates.Ready), (pending.State, reviewedAnOlderHead.State, ready.State));
        Assert.Empty((await ReadAsync(source, handle)).Findings);
    }

    [Fact]
    public async Task A_request_github_accepts_without_adding_copilot_is_unavailable()
    {
        var github = new FakeGitHub()
            .On(Get, "~/pulls/9/reviews", 200, "[]")
            .On(Get, "~/pulls/9/requested_reviewers", 200, CopilotGitHub.Reviewers())
            .On(Post, "~/pulls/9/requested_reviewers", 201, """{ "number": 9, "requested_reviewers": [] }""");
        var source = Source(github);

        var status = (await source.GetStatusAsync((await source.RequestAsync(Target(9), "k", Ct)).Unwrap(), Ct)).Unwrap();

        Assert.Equal(CollectStates.Unavailable, status.State);
        Assert.Contains("not enabled", status.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_with_no_pull_request_is_unavailable_without_asking_github()
    {
        var github = new FakeGitHub();
        var source = Source(github);

        var handle = (await source.RequestAsync(new FindingsTarget(CopilotGitHub.Repo, CopilotGitHub.Head), "k", Ct)).Unwrap();
        var status = (await source.GetStatusAsync(handle, Ct)).Unwrap();

        Assert.Equal(CollectStates.Unavailable, status.State);
        Assert.Empty(github.Calls);
    }

    [Fact]
    public async Task Copilot_asked_earlier_who_has_left_the_request_without_a_review_is_unavailable()
    {
        var github = new FakeGitHub()
            .On(Get, "~/pulls/9/reviews", 200, "[]")
            .On(Get, "~/pulls/9/requested_reviewers", 200, CopilotGitHub.Reviewers("a-person"));

        var status = (await Source(github).GetStatusAsync(new CollectHandle($"copilot:o/r#9@{CopilotGitHub.Head}"), Ct)).Unwrap();

        Assert.Equal(CollectStates.Unavailable, status.State);
    }

    [Fact]
    public async Task A_pull_request_github_does_not_know_is_not_found()
    {
        var status = await Source(CopilotGitHub.World()).GetStatusAsync(new CollectHandle($"copilot:o/r#404@{CopilotGitHub.Head}"), Ct);

        Assert.Equal("not_found", status.Error?.Reason);
    }

    static HttpResponseMessage Json(string body, int status = 200) =>
        new((System.Net.HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
}
