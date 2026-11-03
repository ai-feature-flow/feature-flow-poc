using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>
/// The GitHub code host provider's mapping of GitHub's responses, without the network: every documented success of
/// each endpoint, the error classes of docs/06 section 1.1, and an undocumented success as <c>unexpected_response</c>.
/// </summary>
public sealed class GitHubCodeHostTests
{
    static readonly RepoRef Repo = new("o/r");
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly HttpMethod Get = HttpMethod.Get, Post = HttpMethod.Post, Patch = HttpMethod.Patch, Delete = HttpMethod.Delete;

    static (GitHubCodeHost Host, FakeGitHub GitHub, FakeGitHub.Broker Broker) Host()
    {
        var github = new FakeGitHub();
        var broker = new FakeGitHub.Broker();
        return (new GitHubCodeHost(github.Api(broker)), github, broker);
    }

    static string Pr(int number, string state = "open", bool merged = false, string body = "Body", bool draft = false, string? mergedBy = null) =>
        new JsonObject
        {
            ["number"] = number, ["node_id"] = $"PR_{number}", ["state"] = state, ["merged"] = merged, ["title"] = "Title", ["body"] = body, ["draft"] = draft,
            ["head"] = new JsonObject { ["ref"] = "ff/work", ["sha"] = new string('a', 40) },
            ["base"] = new JsonObject { ["ref"] = "main" },
            ["mergeable"] = merged ? null : true,
            ["merged_at"] = merged ? "2026-11-03T00:00:00Z" : null,
            ["merged_by"] = mergedBy is null ? null : new JsonObject { ["login"] = mergedBy },
            ["merge_commit_sha"] = new string('m', 40),
            ["html_url"] = $"https://github.com/o/r/pull/{number}",
            ["created_at"] = "2026-11-03T00:00:00Z",
            ["closed_at"] = state == "closed" ? "2026-11-03T01:00:00Z" : null,
        }.ToJsonString();

    [Fact]
    public async Task The_default_branch_is_read_from_the_repository_with_an_engine_token_scoped_to_it()
    {
        var (host, github, broker) = Host();
        github.On(Get, "~", 200, """{"default_branch":"trunk"}""");

        Assert.Equal("trunk", (await host.GetDefaultBranchAsync(Repo, Ct)).Unwrap());
        Assert.Equal("trunk", (await host.GetDefaultBranchAsync(Repo, Ct)).Unwrap());

        // One token for both calls: tokens are cached until they near their end. A read needs no write (review finding 8).
        var scope = Assert.Single(broker.Issued);
        Assert.Equal(["o/r"], scope.Repositories);
        Assert.All(scope.Permissions.Values, access => Assert.Equal("read", access));
        Assert.False(scope.Permissions.ContainsKey("administration"));
    }

    [Fact]
    public async Task Only_writes_get_a_write_token()
    {
        var (host, github, broker) = Host();
        github.On(Get, "~/pulls?state=open", 200, "[]").On(Post, "~/pulls", 201, """{"number":7}""");

        (await host.OpenPullRequestAsync(Repo, new PullRequestSpec("T", "B", "ff/work", "main"), "k", Ct)).Unwrap();

        Assert.Equal(["read", "write"], broker.Issued.Select(s => s.Permissions["pull_requests"]));
    }

    /// <summary>Review finding 9: the token goes only to the API, and a revision goes into a URL only as a full SHA.</summary>
    [Fact]
    public async Task The_token_is_never_sent_outside_the_API()
    {
        var github = new FakeGitHub();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            github.Api().SendAsync(Repo, GitHubApi.CodeHostReadPermissions, Get, "https://elsewhere.example/x", null, Ct));
        Assert.Empty(github.Calls);
    }

    [Theory]
    [InlineData("main")]
    [InlineData("../../user")]
    [InlineData("aaaa")]
    public async Task Checks_are_read_only_for_a_full_commit_SHA(string revision)
    {
        var (host, github, _) = Host();
        Assert.Equal("not_found", (await host.GetCheckStatusAsync(Repo, revision, Ct)).Error?.Reason);
        Assert.Empty(github.Calls);
    }

    [Theory]
    [InlineData(401, null, "unauthenticated")]
    [InlineData(403, null, "forbidden")]
    [InlineData(403, "0", "rate_limited")]
    [InlineData(429, null, "rate_limited")]
    [InlineData(404, null, "not_found")]
    [InlineData(409, null, "conflict")]
    [InlineData(422, null, "validation_failed")]
    [InlineData(500, null, "server_error")]
    [InlineData(502, null, "server_error")]
    public async Task Error_statuses_map_to_the_classes_of_docs_06(int status, string? remaining, string reason)
    {
        var (host, github, _) = Host();
        github.On(Get, "~", status, """{"message":"nope"}""",
            remaining is null ? null : new Dictionary<string, string> { ["x-ratelimit-remaining"] = remaining, ["x-ratelimit-reset"] = "4102444800" });

        var result = await host.GetDefaultBranchAsync(Repo, Ct);

        Assert.Equal(reason, result.Error?.Reason);
        Assert.Contains("nope", result.Error!.Message);
        // The reset time GitHub sends with an exhausted limit becomes the retry hint.
        if (remaining is not null)
            Assert.NotNull(result.Error.RetryAfter);
    }

    /// <summary>
    /// docs/06 section 1.1 (LOG 147): a refused credential is retried once at once with a freshly issued token, so a
    /// cached token gone stale recovers; a second refusal is the configuration fault, returned for a person.
    /// </summary>
    [Theory]
    [InlineData(401, 1, "trunk")]
    [InlineData(403, 1, "trunk")]
    [InlineData(401, 2, null)]
    [InlineData(403, 2, null)]
    public async Task A_refused_credential_is_retried_once_with_a_fresh_token(int status, int refusals, string? branch)
    {
        var (host, github, broker) = Host();
        var calls = 0;
        github.On(Get, "~", _ => ++calls <= refusals
            ? new HttpResponseMessage((System.Net.HttpStatusCode)status) { Content = new StringContent("""{"message":"Bad credentials"}""") }
            : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"default_branch":"trunk"}""") });

        var result = await host.GetDefaultBranchAsync(Repo, Ct);

        Assert.Equal(branch, result.Value);
        Assert.Equal(branch is null, result.Error?.IsConfigurationFault ?? false);
        Assert.Equal(2, calls);
        Assert.Equal(2, broker.Issued.Count);                  // the retry's token was issued afresh, not taken from the cache
    }

    /// <summary>
    /// GitHub's answer when the App's installation lacks a permission (for example reading protection without
    /// Administration) is a configuration fault: a work error naming it, so run start stops for a person instead of
    /// retrying it as infrastructure.
    /// </summary>
    [Fact]
    public async Task A_missing_App_permission_is_a_configuration_fault_not_retried()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/branches/main", 403, """{"message":"Resource not accessible by integration"}""");

        var result = await host.GetBranchProtectionAsync(Repo, "main", Ct);

        Assert.Equal("rejected", result.Error?.Reason);
        Assert.Equal(ProviderErrorClass.Work, result.Error!.Class);
        Assert.Contains("configuration fault", result.Error.Message);
    }

    public static Exception Transport(string kind) => kind switch
    {
        "http" => new HttpRequestException("connection refused"),
        "io" => new IOException("connection reset while reading"),
        _ => new System.Net.Sockets.SocketException(),
    };

    /// <summary>The PR 6 re-check: a transport failure comes back as an infrastructure result, never as an exception.</summary>
    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("socket")]
    public async Task A_transport_failure_is_unreachable_not_an_exception(string kind)
    {
        var (host, github, _) = Host();
        github.On(Get, "~", _ => throw Transport(kind));

        var result = await host.GetDefaultBranchAsync(Repo, Ct);

        Assert.Equal("unreachable", result.Error?.Reason);
        Assert.Equal(ProviderErrorClass.Infrastructure, result.Error!.Class);
    }

    [Theory]
    [InlineData("http")]
    [InlineData("io")]
    [InlineData("socket")]
    public async Task A_transport_failure_reaching_the_App_is_unreachable_not_an_exception(string kind)
    {
        var github = new FakeGitHub();
        github.On(Post, "/app/installations/", _ => throw Transport(kind));
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var broker = new AppCredentialBroker("5507595", "176108462", _ => Task.FromResult(ProviderResult<string>.Success(key.ExportRSAPrivateKeyPem())), ["o/r"], github,
            "https://api.example");

        var issued = await broker.IssueAsync(Actor.Engine, "github", CredentialScope.Read("o/r"), TimeSpan.FromMinutes(30), Ct);

        Assert.Equal("unreachable", issued.Error?.Reason);
    }

    [Fact]
    public async Task An_undocumented_success_is_an_unexpected_response_never_retried()
    {
        var (host, github, _) = Host();
        // A well-formed body under a status GitHub does not document for this endpoint.
        github.On(Get, "~", 202, """{"default_branch":"main"}""");

        var result = await host.GetDefaultBranchAsync(Repo, Ct);

        Assert.Equal("unexpected_response", result.Error?.Reason);
        Assert.Equal(ProviderErrorClass.Work, result.Error!.Class);
    }

    [Fact]
    public async Task Protection_combines_the_branch_and_its_rulesets()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/branches/main", 200, """{"protected":true,"protection":{"required_status_checks":{"contexts":["legacy"],"checks":[{"context":"legacy"}]}}}""")
            .On(Get, "~/rules/branches/main", 200, """
                [{"type":"pull_request","parameters":{"required_approving_review_count":2}},
                 {"type":"required_status_checks","parameters":{"required_status_checks":[{"context":"unit"}]}},
                 {"type":"merge_queue","parameters":{}},{"type":"non_fast_forward"}]
                """);

        var protection = (await host.GetBranchProtectionAsync(Repo, "main", Ct)).Unwrap();

        Assert.Equal(new BranchProtection("main", true, true, 2, protection.RequiredChecks, true), protection);
        Assert.Equal(["legacy", "unit"], protection.RequiredChecks);
    }

    /// <summary>LOG 147: classic protection counts as protected, though its review rules need administration to read.</summary>
    [Fact]
    public async Task Classic_protection_counts_as_protected()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/branches/main", 200, """{"protected":true,"protection":{"enabled":true,"required_status_checks":{"contexts":[],"checks":[]}}}""")
            .On(Get, "~/rules/branches/main", 200, "[]");

        var protection = (await host.GetBranchProtectionAsync(Repo, "main", Ct)).Unwrap();

        Assert.True(protection.Protected);
    }

    [Fact]
    public async Task An_unprotected_branch_is_not_an_error_and_a_missing_one_is_not_found()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/branches/free", 200, """{"protected":false,"protection":{"enabled":false,"required_status_checks":{"contexts":[],"checks":[]}}}""")
            .On(Get, "~/rules/branches/free", 200, "[]")
            .On(Get, "~/branches/none", 404, """{"message":"Branch not found"}""");

        var free = (await host.GetBranchProtectionAsync(Repo, "free", Ct)).Unwrap();
        Assert.Equal((false, false, 0, false), (free.Protected, free.RequiresPullRequest, free.RequiredApprovals, free.RequiresMergeQueue));
        Assert.Empty(free.RequiredChecks);
        Assert.Equal("not_found", (await host.GetBranchProtectionAsync(Repo, "none", Ct)).Error?.Reason);
    }

    [Fact]
    public async Task A_file_is_read_at_the_commit_its_reference_names()
    {
        var (host, github, _) = Host();
        var sha = new string('c', 40);
        github.On(Get, "~/commits/ff/manifest", 200, sha)
            .On(Get, $"~/contents/feature-flow.yaml?ref={sha}", 200, """{"type":"file","encoding":"base64","size":11,"content":"dGllcnM6\nIFtdCg=="}""")
            .On(Get, $"~/contents/missing.yaml?ref={sha}", 404, """{"message":"Not Found"}""")
            .On(Get, "~/commits/nope", 422, """{"message":"No commit found for SHA: nope"}""");

        var file = (await host.ReadFileAsync(Repo, "feature-flow.yaml", "ff/manifest", Ct)).Unwrap()!;

        Assert.Equal(("tiers: []\n", sha), (file.Content, file.Revision));
        Assert.Equal("sha256:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("tiers: []\n"u8.ToArray())), file.Hash);
        Assert.Null((await host.ReadFileAsync(Repo, "missing.yaml", "ff/manifest", Ct)).Unwrap());
        Assert.Equal("not_found", (await host.ReadFileAsync(Repo, "x", "nope", Ct)).Error?.Reason);
    }

    [Fact]
    public async Task A_file_over_a_megabyte_is_read_raw()
    {
        var sha = new string('c', 40);
        var calls = 0;
        var raw = new FakeGitHub();
        raw.On(Get, $"~/commits/{sha}", 200, sha)
            .On(Get, $"~/contents/big.txt?ref={sha}", _ => ++calls == 1
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("""{"type":"file","encoding":"none","size":2000000,"content":""}""") }
                : new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent("big content") });

        var file = (await new GitHubCodeHost(raw.Api()).ReadFileAsync(Repo, "big.txt", sha, Ct)).Unwrap()!;

        Assert.Equal("big content", file.Content);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Opening_creates_a_pull_request_with_the_key_as_a_hidden_marker()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/pulls?state=open&head=o%3Aff%2Fwork", 200, "[]").On(Post, "~/pulls", 201, """{"number":7}""");

        var pr = (await host.OpenPullRequestAsync(Repo, new PullRequestSpec("T", "Body", "ff/work", "main", Draft: true), "key-1", Ct)).Unwrap();

        Assert.Equal(new PullRequestRef(Repo, 7), pr);
        var sent = JsonNode.Parse(Assert.Single(github.Called(Post, "~/pulls")).Body)!;
        Assert.Equal("Body\n\n<!-- feature-flow:key-1 -->", sent["body"]!.GetValue<string>());
        Assert.Equal(("ff/work", "main", true), (sent["head"]!.GetValue<string>(), sent["base"]!.GetValue<string>(), sent["draft"]!.GetValue<bool>()));
    }

    [Fact]
    public async Task Opening_again_with_the_key_adopts_and_updates_the_open_pull_request()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/pulls?state=open", 200, $"[{Pr(3, body: "other")},{Pr(7, body: "Old\n\n<!-- feature-flow:key-1 -->")}]")
            .On(Get, "~/pulls/7", 200, Pr(7, body: "Old\n\n<!-- feature-flow:key-1 -->"))
            .On(Patch, "~/pulls/7", 200, Pr(7));

        var pr = (await host.OpenPullRequestAsync(Repo, new PullRequestSpec("New", "New body", "ff/work", "main"), "key-1", Ct)).Unwrap();

        Assert.Equal(7, pr.Number);
        Assert.Empty(github.Called(Post, "~/pulls"));
        Assert.Equal("New body\n\n<!-- feature-flow:key-1 -->", JsonNode.Parse(Assert.Single(github.Called(Patch, "~/pulls/7")).Body)!["body"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Validation Failed: No commits between main and ff/work", "validation_failed")]
    [InlineData("Validation Failed: A pull request already exists for o:ff/work.", "conflict")]
    public async Task GitHub_refusing_a_pull_request_is_a_work_error(string message, string reason)
    {
        var (host, github, _) = Host();
        github.On(Get, "~/pulls?state=open", 200, "[]").On(Post, "~/pulls", 422, $$"""{"message":"{{message}}"}""");

        Assert.Equal(reason, (await host.OpenPullRequestAsync(Repo, new PullRequestSpec("T", "B", "ff/work", "main"), "k", Ct)).Error?.Reason);
    }

    [Fact]
    public async Task Changing_draft_goes_through_GraphQL_only_when_it_differs()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/pulls/7", 200, Pr(7, draft: true)).On(Patch, "~/pulls/7", 200, Pr(7))
            .On(Post, "/graphql", 200, """{"data":{"markPullRequestReadyForReview":{"pullRequest":{"isDraft":false}}}}""");

        (await host.UpdatePullRequestAsync(new PullRequestRef(Repo, 7), new PullRequestSpec("T", "B", "ff/work", "main", Draft: false), Ct)).Unwrap();
        (await host.UpdatePullRequestAsync(new PullRequestRef(Repo, 7), new PullRequestSpec("T", "B", "ff/work", "main", Draft: true), Ct)).Unwrap();

        Assert.Contains("markPullRequestReadyForReview", Assert.Single(github.Called(Post, "/graphql")).Body);
    }

    [Fact]
    public async Task A_merged_pull_request_reports_who_merged_and_each_reviewers_latest_review()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/pulls/7/reviews", 200, """
                [{"user":{"login":"ann"},"state":"APPROVED"},{"user":{"login":"bob"},"state":"APPROVED"},{"user":{"login":"bob"},"state":"CHANGES_REQUESTED"},
                 {"user":{"login":"cat"},"state":"APPROVED"},{"user":{"login":"cat"},"state":"COMMENTED"},
                 {"user":{"login":"dan"},"state":"APPROVED"},{"user":{"login":"dan"},"state":"DISMISSED"}]
                """)
            .On(Get, "~/pulls/7", 200, Pr(7, state: "closed", merged: true, mergedBy: "a-person"));

        var state = (await host.GetPullRequestAsync(new PullRequestRef(Repo, 7), Ct)).Unwrap();

        Assert.Equal((PullRequestStates.Merged, "a-person", new string('m', 40)), (state.State, state.MergedBy, state.MergeRevision));
        Assert.Equal(["ann", "cat"], state.ApprovedBy);
        Assert.Equal(["bob"], state.ChangesRequestedBy);
        Assert.Null(state.Mergeable);
    }

    [Fact]
    public async Task A_closed_unmerged_pull_request_is_closed_with_no_merge_details()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/pulls/7/reviews", 200, "[]").On(Get, "~/pulls/7", 200, Pr(7, state: "closed"));

        var state = (await host.GetPullRequestAsync(new PullRequestRef(Repo, 7), Ct)).Unwrap();

        Assert.Equal((PullRequestStates.Closed, null, null), (state.State, state.MergedBy, state.MergeRevision));
    }

    [Fact]
    public async Task Closing_comments_and_closes_an_open_pull_request_and_does_nothing_to_a_closed_one()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/pulls/7", 200, Pr(7)).On(Post, "~/issues/7/comments", 201, "{}").On(Patch, "~/pulls/7", 200, Pr(7, state: "closed"))
            .On(Get, "~/pulls/8", 200, Pr(8, state: "closed", merged: true));

        (await host.ClosePullRequestAsync(new PullRequestRef(Repo, 7), "Superseded.", Ct)).Unwrap();
        (await host.ClosePullRequestAsync(new PullRequestRef(Repo, 8), "Superseded.", Ct)).Unwrap();

        Assert.Single(github.Called(Post, "~/issues/7/comments"));
        Assert.Equal("closed", JsonNode.Parse(Assert.Single(github.Called(Patch, "~/pulls/7")).Body)!["state"]!.GetValue<string>());
        Assert.Empty(github.Called(Post, "~/issues/8"));
        Assert.Empty(github.Called(Patch, "~/pulls/8"));
    }

    [Fact]
    public async Task A_revert_is_made_by_GitHub_once_and_adopted_by_its_marker_after()
    {
        var (host, github, _) = Host();
        var revert = Pr(9, body: "Reverts #7\n\n<!-- feature-flow:key-revert -->");
        var listed = 0;
        github.On(Get, "~/pulls/7", 200, Pr(7, state: "closed", merged: true))
            .On(Get, "~/pulls?state=all&base=main", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                { Content = new StringContent(listed++ == 0 ? "[]" : $"[{revert}]") })
            .On(Post, "/graphql", 200, """{"data":{"revertPullRequest":{"revertPullRequest":{"number":9}}}}""");

        var first = (await host.CreateRevertPullRequestAsync(new PullRequestRef(Repo, 7), "key-revert", Ct)).Unwrap();
        var again = (await host.CreateRevertPullRequestAsync(new PullRequestRef(Repo, 7), "key-revert", Ct)).Unwrap();

        Assert.Equal((9, 9), (first.Number, again.Number));
        var mutation = Assert.Single(github.Called(Post, "/graphql")).Body;
        Assert.Contains("revertPullRequest", mutation);
        Assert.Contains("PR_7", mutation);
        Assert.Contains("feature-flow:key-revert", mutation);
    }

    [Fact]
    public async Task A_revert_of_an_unmerged_pull_request_is_refused_and_GraphQL_errors_are_classified()
    {
        var (host, github, _) = Host();
        github.On(Get, "~/pulls/7", 200, Pr(7)).On(Get, "~/pulls/8", 200, Pr(8, state: "closed", merged: true))
            .On(Get, "~/pulls?state=all", 200, "[]")
            .On(Post, "/graphql", 200, """{"data":null,"errors":[{"type":"UNPROCESSABLE","message":"The revert has a merge conflict"}]}""");

        Assert.Equal("validation_failed", (await host.CreateRevertPullRequestAsync(new PullRequestRef(Repo, 7), "k", Ct)).Error?.Reason);
        Assert.Equal("conflict", (await host.CreateRevertPullRequestAsync(new PullRequestRef(Repo, 8), "k", Ct)).Error?.Reason);
    }

    static readonly string Head = new('a', 40), Squash = new('5', 40), Parent = new('6', 40), Decoy = new('d', 40), Undo = new('b', 40), BaseHead = new('c', 40);

    static string Commit(string sha, string tree, string message, params string[] parents) =>
        new JsonObject
        {
            ["sha"] = sha,
            ["commit"] = new JsonObject { ["tree"] = new JsonObject { ["sha"] = tree }, ["message"] = message },
            ["parents"] = new JsonArray(parents.Select(x => (JsonNode)new JsonObject { ["sha"] = x }).ToArray()),
        }.ToJsonString();

    /// <summary>A pull request the engine squash-merged: closed, not merged, its squash on the base (review finding 7).</summary>
    FakeGitHub SquashMerged(FakeGitHub github, bool withSquash = true, int mergeStatus = 201) =>
        github.On(Get, "~/pulls/7", 200, Pr(7, state: "closed"))
            .On(Get, $"~/git/commits/{Head}", 200, """{"tree":{"sha":"tree-head"}}""")
            .On(Get, $"~/git/commits/{Parent}", 200, """{"tree":{"sha":"tree-parent"}}""")
            .On(Get, "~/commits?sha=main", 200, "[" + string.Join(",",
                new[]
                {
                    Commit(Decoy, "tree-head", "Same tree, no trailer", Parent),
                    Commit(new string('e', 40), "tree-other", "A merge\n\nFeature-Flow-Merge: run-x/scm.merge/1", Parent),
                }.Concat(withSquash ? [Commit(Squash, "tree-head", "Squash ticket\n\nFeature-Flow-Merge: run-1/scm.merge/1", Parent)] : [])) + "]")
            .On(Get, "~/pulls?state=all", 200, "[]")
            .On(Post, "~/git/commits", 201, $$$"""{"sha":"{{{Undo}}}"}""")
            .On(Get, "~/git/ref/heads/main", 200, $$$"""{"object":{"sha":"{{{BaseHead}}}"}}""")
            .On(Post, "~/git/refs", 201, "{}")
            .On(Post, "~/merges", mergeStatus, mergeStatus == 201 ? "{}" : """{"message":"Merge conflict"}""")
            .On(Delete, "~/git/refs/heads/", 204)
            .On(Post, "~/pulls", 201, """{"number":9}""");

    [Fact]
    public async Task An_engine_squash_is_reverted_from_its_squash_commit()
    {
        var (host, github, _) = Host();
        SquashMerged(github);

        var revert = (await host.CreateRevertPullRequestAsync(new PullRequestRef(Repo, 7), "key-revert", Ct)).Unwrap();

        Assert.Equal(9, revert.Number);
        Assert.Empty(github.Called(Post, "/graphql"));
        var undo = JsonNode.Parse(Assert.Single(github.Called(Post, "~/git/commits")).Body)!;
        Assert.Equal(("tree-parent", Squash), (undo["tree"]!.GetValue<string>(), undo["parents"]![0]!.GetValue<string>()));
        var branch = JsonNode.Parse(Assert.Single(github.Called(Post, "~/git/refs")).Body)!;
        Assert.Equal(("refs/heads/revert-7-ff/work", BaseHead), (branch["ref"]!.GetValue<string>(), branch["sha"]!.GetValue<string>()));
        var merge = JsonNode.Parse(Assert.Single(github.Called(Post, "~/merges")).Body)!;
        Assert.Equal(("revert-7-ff/work", Undo), (merge["base"]!.GetValue<string>(), merge["head"]!.GetValue<string>()));
        var pull = JsonNode.Parse(Assert.Single(github.Called(Post, "~/pulls")).Body)!;
        Assert.Equal(("revert-7-ff/work", "main"), (pull["head"]!.GetValue<string>(), pull["base"]!.GetValue<string>()));
        Assert.Contains("feature-flow:key-revert", pull["body"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_closed_pull_request_with_no_engine_squash_is_not_merged()
    {
        var (host, github, _) = Host();
        SquashMerged(github, withSquash: false);

        Assert.Equal("validation_failed", (await host.CreateRevertPullRequestAsync(new PullRequestRef(Repo, 7), "k", Ct)).Error?.Reason);
        Assert.Empty(github.Called(Post, "~/"));
    }

    [Fact]
    public async Task A_squash_revert_that_conflicts_is_a_conflict_and_leaves_no_branch()
    {
        var (host, github, _) = Host();
        SquashMerged(github, mergeStatus: 409);

        Assert.Equal("conflict", (await host.CreateRevertPullRequestAsync(new PullRequestRef(Repo, 7), "k", Ct)).Error?.Reason);
        Assert.Single(github.Called(Delete, "~/git/refs/heads/revert-7-ff/work"));
        Assert.Empty(github.Called(Post, "~/pulls"));
    }

    [Fact]
    public async Task Checks_name_their_workflow_and_include_commit_statuses()
    {
        var (host, github, _) = Host();
        var sha = new string('d', 40);
        github.On(Get, $"~/commits/{sha}/check-runs", 200, """
                {"total_count":3,"check_runs":[
                  {"name":"unit","status":"completed","conclusion":"success","app":{"slug":"github-actions"},"check_suite":{"id":11}},
                  {"name":"integration","status":"in_progress","conclusion":null,"app":{"slug":"github-actions"},"check_suite":{"id":12}},
                  {"name":"SonarCloud","status":"completed","conclusion":"startup_failure","app":{"slug":"sonarcloud"},"check_suite":{"id":13}}]}
                """)
            .On(Get, $"~/commits/{sha}/status", 200, """{"statuses":[{"context":"legacy/ci","state":"pending"},{"context":"other","state":"error"}]}""")
            .On(Get, $"~/actions/runs?head_sha={sha}", 200, """
                {"workflow_runs":[{"check_suite_id":11,"path":".github/workflows/unit.yml"},{"check_suite_id":12,"path":".github/workflows/integration.yml"}]}
                """);

        var status = (await host.GetCheckStatusAsync(Repo, sha, Ct)).Unwrap();

        Assert.Equal(
            [
                new CheckRun("unit", CheckStates.Completed, Conclusions.Success, "unit.yml"),
                new CheckRun("integration", CheckStates.InProgress, null, "integration.yml"),
                new CheckRun("SonarCloud", CheckStates.Completed, Conclusions.Failure, null),
                new CheckRun("legacy/ci", CheckStates.InProgress, null, null),
                new CheckRun("other", CheckStates.Completed, Conclusions.Failure, null),
            ], status.Checks);
    }

    [Fact]
    public async Task Checks_of_an_unknown_commit_are_not_found()
    {
        var (host, github, _) = Host();
        var unknown = new string('e', 40);
        github.On(Get, $"~/commits/{unknown}/check-runs", 422, """{"message":"No commit found for SHA"}""");
        Assert.Equal("not_found", (await host.GetCheckStatusAsync(Repo, unknown, Ct)).Error?.Reason);
    }
}
