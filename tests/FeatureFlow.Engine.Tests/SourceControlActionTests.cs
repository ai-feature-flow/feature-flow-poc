using System.Text.Json;
using FeatureFlow.Engine.Actions;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Testing;

namespace FeatureFlow.Engine.Tests;

/// <summary>The source control actions (docs/04 section 7) over the in-memory world.</summary>
public sealed class SourceControlActionTests
{
    static string Outcome(ActionResult r) => r.Report?.Outcome ?? $"{r.Kind}: {r.Reason}";

    static T Output<T>(ActionResult r, string name) => (T)YamlWriter.ReadPlain(r.Report!.Outputs[name])!;

    // ---- scm.catch_up ----

    [Fact]
    public async Task Catch_up_merges_a_moved_parent_into_the_work_branch_and_reports_the_change()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "work");
        var parent = w.CommitMain("b.txt", "parent");

        var result = await w.RunAsync("scm.catch_up");

        Assert.Equal("updated", Outcome(result));
        Assert.True(Output<bool>(result, "changed"));
        Assert.True(w.Forge.Contains(w.Repo, w.Work!, parent));
    }

    [Fact]
    public async Task Catch_up_with_nothing_to_merge_is_updated_unchanged_and_moves_nothing()
    {
        await using var w = new ActionWorld();
        var head = w.CommitWork("a.txt", "work");

        var result = await w.RunAsync("scm.catch_up");

        Assert.Equal("updated", Outcome(result));
        Assert.False(Output<bool>(result, "changed"));
        Assert.Equal(head, w.Work);
    }

    [Fact]
    public async Task Catch_up_with_conflicts_reports_conflict_and_pushes_nothing()
    {
        await using var w = new ActionWorld();
        var head = w.CommitWork("README.md", "ours");
        w.CommitMain("README.md", "theirs");

        var result = await w.RunAsync("scm.catch_up");

        Assert.Equal("conflict", Outcome(result));
        Assert.Equal(head, w.Work);
        Assert.Contains(result.Events, e => e.Contains("README.md", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Catch_up_reads_a_child_runs_parent_branch_not_the_default()
    {
        await using var w = new ActionWorld();
        var epic = w.Forge.Commit(w.Repo, "refs/heads/ff/epic/E-1", new Dictionary<string, string?> { ["epic.txt"] = "epic" });
        w.CommitMain("main.txt", "main moved");
        w.ParentBranch = "ff/epic/E-1";

        var result = await w.RunAsync("scm.catch_up");

        // Created from the epic's branch, so nothing to merge, and main's later commit is not taken in.
        Assert.False(Output<bool>(result, "changed"));
        Assert.Equal(epic, w.Work);
        Assert.Null(w.Forge.ReadFile(w.Repo, w.Work!, "main.txt"));
    }

    // ---- scm.pull_request ----

    [Fact]
    public async Task A_pull_request_is_opened_into_the_default_branch_read_from_the_code_host_and_adopted_on_a_rerun()
    {
        await using var w = new ActionWorld();
        w.Forge.SetReference(w.Repo, "trunk", w.Main);
        w.Forge.SetDefaultBranch(w.Repo, "trunk");
        w.CommitWork("a.txt", "work");

        var first = await w.RunAsync("scm.pull_request", "{ title: Ticket T-1, body: The body }", "open_pr");
        var second = await w.RunAsync("scm.pull_request", "{ title: Retitled, body: The body }", "open_pr");

        Assert.Equal(Output<string>(first, "pull_request"), Output<string>(second, "pull_request"));
        var pr = ActionRunAccess.Pr(Output<string>(first, "pull_request"));
        var state = (await new InMemoryCodeHost(w.Forge).GetPullRequestAsync(pr, TestContext.Current.CancellationToken)).Unwrap();
        Assert.Equal(("trunk", ActionWorld.WorkBranch, "Retitled"), (state.Base, state.Head, state.Title));
    }

    // ---- scm.pull_request: the review summary (E3) ----

    static ReportedFinding Blocking() =>
        new("null_dereference", "Find may dereference null", "d", "high", true, FindingBases.Demonstrated, new FindingLocation("acme/api", "a.txt", "A.Find", "work"), "c");

    /// <summary>A reviewer's report as the workflow lists it: produced by an agent step, so no action.</summary>
    static async Task ReviewedAsync(ActionWorld w, string stepId, FindingsReport report)
    {
        var reference = (await w.Store.PutAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(ArtifactShapes.WriteLibrary(report))), TestContext.Current.CancellationToken)).Unwrap().Id;
        w.Artifacts.RemoveAll(a => a.StepId == stepId && a.Name == "findings_report");
        w.Artifacts.Add(new ArtifactEntry(stepId, "findings_report", "std:artifact_types/findings_report", reference));
    }

    static async Task<ActionWorld> ReviewedWorldAsync(Func<ICodeHostProvider, ICodeHostProvider>? codeHost = null)
    {
        var w = new ActionWorld(codeHost: codeHost);
        w.CommitWork("a.txt", "work");
        await ReviewedAsync(w, "verify.review.review_0.assess", new FindingsReport(1, [Blocking()], "correctness",
            new FindingsSummary("Find handles `null` | mostly.\n@team should look", ["a.txt and its callers", "ran `dotnet test`"])));
        await ReviewedAsync(w, "verify.review.review_1.assess", new FindingsReport(1, [], "security"));
        Assert.Equal("recorded", Outcome(await w.RunAsync("findings.record", "{ source: findings_report }", "verify.review.review_0.record")));
        return w;
    }

    [Fact]
    public async Task A_pull_request_posts_the_review_summary_once_per_pass_with_the_reviewers_text_as_code()
    {
        await using var w = await ReviewedWorldAsync();

        var first = await w.RunAsync("scm.pull_request", "{ title: T-1 }", "deliver.open_pr");
        await w.RunAsync("scm.pull_request", "{ title: T-1 }", "deliver.open_pr");   // a retry or re-entry with the same reports

        var pr = ActionRunAccess.Pr(Output<string>(first, "pull_request"));
        var comment = Assert.Single(w.Forge.Comments(pr));
        Assert.StartsWith("## Feature Flow review\n\n| Angle | Findings | Blocking |\n|---|---|---|\n| ` correctness ` | 1 | 1 |\n| ` security ` | 0 | 0 |\n", comment, StringComparison.Ordinal);
        Assert.Contains("### ` correctness `\n\n`` Find handles `null` | mostly. ``  \n` @team should look `  \n", comment, StringComparison.Ordinal);
        Assert.Contains("Checked:\n\n- ` a.txt and its callers `\n- `` ran `dotnet test` ``\n", comment, StringComparison.Ordinal);
        Assert.Contains("### ` security `\n\nNo verdict recorded.\n", comment, StringComparison.Ordinal);
        Assert.Contains("Ledger: 1 open (1 blocking), 0 fixed, 0 declined, 0 escalated.", comment, StringComparison.Ordinal);
        Assert.Contains($"review report of run {w.RunId}", comment, StringComparison.Ordinal);
        Assert.Contains(first.Events, e => e.StartsWith("the review summary of 2 report(s) is posted on", StringComparison.Ordinal));

        // A later pass: a reviewer's report is new, so the summary is posted again; the first comment stays. Its angle, a
        // free string, has a line break: it stays on one line, in its code span, in the table row and the heading.
        await ReviewedAsync(w, "verify.review.review_1.assess",
            new FindingsReport(1, [], "security\n@acme/admins # approved", new FindingsSummary("No security concern.", ["a.txt"])));
        await w.RunAsync("scm.pull_request", "{ title: T-1 }", "deliver.open_pr");
        Assert.Equal(2, w.Forge.Comments(pr).Count);
        var second = w.Forge.Comments(pr)[1];
        Assert.Contains("` No security concern. `", second, StringComparison.Ordinal);
        Assert.Contains("| ` security @acme/admins # approved ` | 0 | 0 |\n", second, StringComparison.Ordinal);
        Assert.Contains("### ` security @acme/admins # approved `\n", second, StringComparison.Ordinal);
        Assert.DoesNotContain("\n@acme", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pull_request_with_post_review_false_or_no_review_posts_no_summary()
    {
        await using var w = await ReviewedWorldAsync();
        var quiet = await w.RunAsync("scm.pull_request", "{ title: T-1, post_review: false }", "deliver.open_pr");
        Assert.Empty(w.Forge.Comments(ActionRunAccess.Pr(Output<string>(quiet, "pull_request"))));

        await using var unreviewed = new ActionWorld();
        unreviewed.CommitWork("a.txt", "work");
        var opened = await unreviewed.RunAsync("scm.pull_request", "{ title: T-1 }", "deliver.open_pr");
        Assert.Empty(unreviewed.Forge.Comments(ActionRunAccess.Pr(Output<string>(opened, "pull_request"))));
        Assert.Contains(opened.Events, e => e.Contains("no review summary is posted", StringComparison.Ordinal));
    }

    /// <summary>The same summary through the GitHub code host over the GitHub test double: posted once with its key's marker, adopted on a retry.</summary>
    [Fact]
    public async Task The_review_summary_is_posted_through_the_GitHub_code_host_once()
    {
        var github = new FeatureFlow.Providers.GitHub.Tests.FakeGitHub();
        var posted = new List<string>();
        github.On(HttpMethod.Get, "/repos/acme/api/issues/1/comments", _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(new System.Text.Json.Nodes.JsonArray(posted.Select((b, i) => (System.Text.Json.Nodes.JsonNode)new System.Text.Json.Nodes.JsonObject { ["id"] = i + 1, ["body"] = b }).ToArray()).ToJsonString()),
            })
            .On(HttpMethod.Post, "/repos/acme/api/issues/1/comments", body =>
            {
                posted.Add(System.Text.Json.Nodes.JsonNode.Parse(body)!["body"]!.GetValue<string>());
                return new HttpResponseMessage(System.Net.HttpStatusCode.Created) { Content = new StringContent($$"""{"id":{{posted.Count}}}""") };
            });
        var gitHub = new FeatureFlow.Providers.GitHub.GitHubCodeHost(github.Api());
        await using var w = await ReviewedWorldAsync(memory => new CommentsThrough(memory, gitHub));

        var first = await w.RunAsync("scm.pull_request", "{ title: T-1 }", "deliver.open_pr");
        await w.RunAsync("scm.pull_request", "{ title: T-1 }", "deliver.open_pr");

        Assert.Equal("acme/api#1", Output<string>(first, "pull_request"));
        var body = Assert.Single(posted);
        Assert.StartsWith("## Feature Flow review\n", body, StringComparison.Ordinal);
        Assert.Matches($@"<!-- feature-flow:review/{w.RunId}/[0-9a-f]{{16}} -->$", body);
    }

    /// <summary>The in-memory code host for everything but comments, which go to <paramref name="comments"/>.</summary>
    sealed class CommentsThrough(ICodeHostProvider inner, ICodeHostProvider comments) : ICodeHostProvider
    {
        public ProviderDescription Describe() => inner.Describe();
        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);
        public Task<ProviderResult<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken cancellationToken) => inner.GetDefaultBranchAsync(repo, cancellationToken);
        public Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken cancellationToken) => inner.GetBranchProtectionAsync(repo, branch, cancellationToken);
        public Task<ProviderResult<RepoFile?>> ReadFileAsync(RepoRef repo, string path, string reference, CancellationToken cancellationToken) => inner.ReadFileAsync(repo, path, reference, cancellationToken);
        public Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key, CancellationToken cancellationToken) => inner.OpenPullRequestAsync(repo, spec, key, cancellationToken);
        public Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestSpec spec, CancellationToken cancellationToken) => inner.UpdatePullRequestAsync(pullRequest, spec, cancellationToken);
        public Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) => inner.GetPullRequestAsync(pullRequest, cancellationToken);
        public Task<ProviderResult<IReadOnlyList<PullRequestFile>>> ListPullRequestFilesAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) => inner.ListPullRequestFilesAsync(pullRequest, cancellationToken);
        public Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pullRequest, string? comment, CancellationToken cancellationToken) => inner.ClosePullRequestAsync(pullRequest, comment, cancellationToken);
        public Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pullRequest, string key, CancellationToken cancellationToken) => inner.CreateRevertPullRequestAsync(pullRequest, key, cancellationToken);
        public Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(RepoRef repo, string revision, CancellationToken cancellationToken) => inner.GetCheckStatusAsync(repo, revision, cancellationToken);
        public Task<ProviderResult<CommentRef>> AddCommentAsync(PullRequestRef pullRequest, string body, string key, CancellationToken cancellationToken) =>
            comments.AddCommentAsync(pullRequest, body, key, cancellationToken);
    }

    // ---- scm.merge ----

    /// <summary>The evidence a merge needs, for the work branch's current head: a passing test.run and ci.checks.</summary>
    static async Task TestedAndCheckedAsync(ActionWorld w)
    {
        await w.RunAsync("scm.pull_request", "{ title: T-1 }", "open_pr");
        w.Artifacts.Add(new ArtifactEntry("tests", "test_results", "std:artifact_types/test_results",
            await Put(w, new TestEvidence(w.Repo.FullName, w.Work!, false, true, [new TestResults(w.Work!, "unit", 3, 0, 0, [])])), "test.run", true));
        Assert.Equal("passed", Outcome(await w.RunAsync("ci.checks", null, "checks")));
    }

    static async Task<string> Put(ActionWorld w, object value) =>
        (await w.Store.PutAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions(JsonSerializerDefaults.Web))), TestContext.Current.CancellationToken)).Unwrap().Id;

    [Fact]
    public async Task The_engine_merges_exactly_the_tested_head_and_records_it()
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/*");
        var head = w.CommitWork("a.txt", "work");
        var before = w.Main;
        await TestedAndCheckedAsync(w);

        var result = await w.RunAsync("scm.merge", "{ squash: true }", "merge");

        Assert.Equal("merged", Outcome(result));
        var merged = w.Main;
        Assert.Equal(w.Forge.GetCommit(w.Repo, head).Files, w.Forge.GetCommit(w.Repo, merged).Files);
        Assert.Equal([before], w.Forge.GetCommit(w.Repo, merged).Parents);
        var record = JsonSerializer.Deserialize<MergeEvidence>(await w.ReadAsync(result.Report!.Artifacts["evidence"]), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal((head, before, merged, "squash"), (record.Head, record.TargetBefore, record.Revision, record.Method));
        // After a squash the pull request is closed with a link to the commit, since the code host does not show it merged.
        var pr = ActionRunAccess.Pr(w.PullRequests[0]);
        Assert.Contains(merged, w.Forge.Comments(pr).Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_merge_retried_after_it_landed_is_merged_without_a_second_commit()
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/api");
        w.CommitWork("a.txt", "work");
        await TestedAndCheckedAsync(w);
        Assert.Equal("merged", Outcome(await w.RunAsync("scm.merge", null, "merge")));
        var landed = w.Main;

        var again = await w.RunAsync("scm.merge", null, "merge");

        Assert.Equal("merged", Outcome(again));
        Assert.Equal(landed, w.Main);
    }

    [Fact]
    public async Task A_head_without_passing_test_evidence_is_not_merged()
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/api");
        w.CommitWork("a.txt", "work");
        await TestedAndCheckedAsync(w);
        var before = w.Main;
        w.CommitWork("b.txt", "untested");                 // the head moved past the evidence

        var result = await w.RunAsync("scm.merge", null, "merge");

        Assert.Equal("checks_failed", Outcome(result));
        Assert.Equal(before, w.Main);
        Assert.Contains(result.Events, e => e.StartsWith("require_tested", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_head_with_failed_checks_is_not_merged_and_require_checks_false_lets_it_through()
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/api");
        var head = w.CommitWork("a.txt", "work");
        await w.RunAsync("scm.pull_request", "{ title: T-1 }", "open_pr");
        w.Artifacts.Add(new ArtifactEntry("tests", "test_results", "std:artifact_types/test_results",
            await Put(w, new TestEvidence(w.Repo.FullName, head, false, true, [])), "test.run", true));
        w.Forge.ReportCheck(w.Repo, head, new CheckRun("lint", CheckStates.Completed, Conclusions.Failure, null));
        Assert.Equal("failed", Outcome(await w.RunAsync("ci.checks", null, "checks")));

        Assert.Equal("checks_failed", Outcome(await w.RunAsync("scm.merge", null, "merge")));
        Assert.Equal("merged", Outcome(await w.RunAsync("scm.merge", "{ require_checks: false }", "merge")));
    }

    /// <summary>Evidence for the head as an agent could write it: the right shape, the right revision, "passed".</summary>
    static async Task<(string Tests, string Checks)> ForgedAsync(ActionWorld w) =>
        (await Put(w, new TestEvidence(w.Repo.FullName, w.Work!, false, true, [new TestResults(w.Work!, "unit", 9, 0, 0, [])])),
         await Put(w, new CheckEvidence(w.Repo.FullName, w.Work!, null, true, 0, [])));

    [Theory]
    [InlineData(null, true, "an agent step's artifacts of the evidence types")]
    [InlineData("test.run", false, "artifacts of a library's own types named test_results and check_results")]
    [InlineData("artifact.compose", true, "evidence types made by an action that does not test")]
    public async Task Evidence_that_no_testing_action_produced_is_not_merged(string? producer, bool evidenceType, string what)
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/api");
        w.CommitWork("a.txt", "work");
        await w.RunAsync("scm.pull_request", "{ title: T-1 }", "open_pr");
        var (tests, checks) = await ForgedAsync(w);
        var library = evidenceType ? "std" : "acme_repo";
        w.Artifacts.Add(new ArtifactEntry("write", "test_results", $"{library}:artifact_types/test_results", tests, producer, evidenceType));
        w.Artifacts.Add(new ArtifactEntry("write", "check_results", $"{library}:artifact_types/check_results", checks, producer == "test.run" ? "ci.checks" : producer, evidenceType));
        var before = w.Main;

        var result = await w.RunAsync("scm.merge", null, "merge");

        Assert.True(Outcome(result) == "checks_failed", what);
        Assert.Equal(before, w.Main);
    }

    [Fact]
    public async Task Test_integration_results_do_not_satisfy_require_tested()
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/api");
        w.CommitWork("a.txt", "work");
        await TestedAndCheckedAsync(w);
        var integration = w.Artifacts.Single(a => a.Name == "test_results") with { Action = "test.integration" };
        w.Artifacts.RemoveAll(a => a.Name == "test_results");
        w.Artifacts.Add(integration);

        Assert.Equal("checks_failed", Outcome(await w.RunAsync("scm.merge", null, "merge")));
    }

    [Fact]
    public async Task Evidence_for_another_repository_at_the_same_revision_is_not_merged()
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/api");
        w.CommitWork("a.txt", "work");
        await TestedAndCheckedAsync(w);
        var other = await Put(w, new TestEvidence("acme/web", w.Work!, false, true, [new TestResults(w.Work!, "unit", 3, 0, 0, [])]));
        w.Artifacts.RemoveAll(a => a.Name == "test_results");
        w.Artifacts.Add(new ArtifactEntry("tests", "test_results", "std:artifact_types/test_results", other, "test.run", true));

        Assert.Equal("checks_failed", Outcome(await w.RunAsync("scm.merge", null, "merge")));
    }

    [Fact]
    public async Task A_parent_that_moved_since_the_catch_up_is_base_moved_and_nothing_lands()
    {
        await using var w = new ActionWorld();
        w.EngineMayMerge.Add("acme/api");
        w.CommitWork("a.txt", "work");
        await TestedAndCheckedAsync(w);
        var sibling = w.CommitMain("sibling.txt", "a sibling merged first");

        var result = await w.RunAsync("scm.merge", null, "merge");

        Assert.Equal("base_moved", Outcome(result));
        Assert.Equal(sibling, w.Main);
    }

    [Fact]
    public async Task F1_at_merge_time_turns_a_default_branch_merge_the_organisation_forbids_into_an_action_request()
    {
        await using var w = new ActionWorld();          // EngineMayMerge is empty: the default
        var head = w.CommitWork("a.txt", "work");
        await TestedAndCheckedAsync(w);
        var before = w.Main;

        var waiting = await w.RunAsync("scm.merge", null, "merge", waits: 1);

        // A person must merge: the step asks the run to open its merge_action decision first (LOG 331).
        Assert.Equal(StepOutcome.Decide, waiting.Kind);
        Assert.Equal(before, w.Main);
        Assert.Contains(waiting.Events, e => e.StartsWith("F1 at merge time", StringComparison.Ordinal));

        // Approved, a person merges on the code host: the step sees it and records them.
        w.Decision = new StepDecision("ticket.merge_action", "approve");
        var pr = ActionRunAccess.Pr(w.PullRequests[0]);
        var merged = await w.RunAsync("scm.merge", null, "merge", beforeEachRead: () =>
        {
            if (w.Main == before)
                w.Forge.MergePullRequest(pr, "alice");
        });
        Assert.Equal("merged", Outcome(merged));
        var record = JsonSerializer.Deserialize<MergeEvidence>(await w.ReadAsync(merged.Report!.Artifacts["evidence"]), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(("alice", head), (record.MergedBy, record.Head));
    }

    [Fact]
    public async Task An_action_request_whose_pull_request_is_closed_is_declined()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "work");
        await TestedAndCheckedAsync(w);
        var pr = ActionRunAccess.Pr(w.PullRequests[0]);

        var result = await w.RunAsync("scm.merge", "{ engine_can_merge: false }", "merge", beforeEachRead: () =>
            new InMemoryCodeHost(w.Forge).ClosePullRequestAsync(pr, "not wanted", default).GetAwaiter().GetResult());

        Assert.Equal("declined", Outcome(result));
    }

    [Fact]
    public async Task An_action_request_raises_its_merge_action_decision_and_asks_a_person_to_merge_only_once_it_is_approved()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "work");
        await TestedAndCheckedAsync(w);
        var before = w.Main;

        var decide = await w.RunAsync("scm.merge", "{ engine_can_merge: false }", "merge");
        Assert.Equal(StepOutcome.Decide, decide.Kind);
        Assert.Contains("acme/api#1", decide.Reason, StringComparison.Ordinal);

        // While the decision is open the step watches the pull request, and has asked no one to merge yet.
        w.Decision = new StepDecision("ticket.merge_action", StepDecision.Open);
        var open = await w.RunAsync("scm.merge", "{ engine_can_merge: false }", "merge", waits: 1);
        Assert.Equal(ActionResult.Wait, open.Kind);
        Assert.Contains("ticket.merge_action", open.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(await w.EventsAsync("merge", 2), e => e.Kind == "action_request");

        // Approved: the step asks a person to merge, and waits for them.
        w.Decision = new StepDecision("ticket.merge_action", "approve");
        var asked = await w.RunAsync("scm.merge", "{ engine_can_merge: false }", "merge", waits: 1);
        Assert.Equal(ActionResult.Wait, asked.Kind);
        Assert.Contains(await w.EventsAsync("merge", 3), e => e is { Kind: "action_request", Detail: "a person merges acme/api#1 into main, or closes it to decline" });
        Assert.Equal(before, w.Main);
    }

    [Fact]
    public async Task A_merge_on_the_code_host_while_the_decision_is_open_is_merged_and_says_who_merged_and_that_the_decision_was_bypassed()
    {
        await using var w = new ActionWorld();
        var head = w.CommitWork("a.txt", "work");
        await TestedAndCheckedAsync(w);
        var pr = ActionRunAccess.Pr(w.PullRequests[0]);
        w.Decision = new StepDecision("ticket.merge_action", StepDecision.Open);
        w.Forge.MergePullRequest(pr, "bob");

        var merged = await w.RunAsync("scm.merge", "{ engine_can_merge: false }", "merge");

        Assert.Equal("merged", Outcome(merged));
        Assert.Contains("bob merged acme/api#1 on the code host before ticket.merge_action was answered: the decision was not given through Feature Flow", merged.Events);
        var record = JsonSerializer.Deserialize<MergeEvidence>(await w.ReadAsync(merged.Report!.Artifacts["evidence"]), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(("bob", head), (record.MergedBy, record.Head));
    }

    [Fact]
    public async Task A_merge_into_a_child_runs_parent_branch_needs_no_F1_leave()
    {
        await using var w = new ActionWorld();
        w.Forge.Commit(w.Repo, "refs/heads/ff/epic/E-1", new Dictionary<string, string?> { ["epic.txt"] = "epic" });
        w.ParentBranch = "ff/epic/E-1";
        w.Forge.Commit(w.Repo, "refs/heads/" + ActionWorld.WorkBranch, new Dictionary<string, string?> { ["a.txt"] = "work" }, from: "ff/epic/E-1");
        await TestedAndCheckedAsync(w);

        Assert.Equal("merged", Outcome(await w.RunAsync("scm.merge", null, "merge")));
        Assert.Equal("work", w.Forge.ReadFile(w.Repo, "ff/epic/E-1", "a.txt"));
    }

    // ---- scm.revert ----

    [Fact]
    public async Task A_revert_pull_request_is_opened_for_the_runs_merged_pull_request_and_refused_before_it_merges()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "work");
        await w.RunAsync("scm.pull_request", "{ title: T-1 }", "open_pr");

        Assert.Equal(StepOutcome.WorkFailed, (await w.RunAsync("scm.revert", null, "revert")).Kind);

        w.Forge.MergePullRequest(ActionRunAccess.Pr(w.PullRequests[0]));
        var result = await w.RunAsync("scm.revert", null, "revert");
        Assert.Equal("opened", Outcome(result));
        Assert.Contains(result.Events, e => e.Contains("reverting acme/api#1", StringComparison.Ordinal));
    }

    // ---- clean_up ----

    [Fact]
    public async Task A_clean_up_resets_the_work_branch_to_where_the_step_started_and_keeps_what_it_removes()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "plan");
        Assert.Equal("updated", Outcome(await w.RunAsync("scm.catch_up", null, "plan")));   // records where "plan" started
        var start = w.Work!;
        var discarded = w.CommitWork("b.txt", "work to discard");
        var hidden = $"refs/feature-flow/wip/{WorkRefs.Safe(w.RunId)}/implement";
        w.Forge.SetReference(w.Repo, hidden, discarded);

        var events = (await w.Actions.CleanUpAsync(w.RunId, "plan", 1, [w.Repo.FullName], ActionWorld.WorkBranch, ["plan", "implement"], TestContext.Current.CancellationToken)).Events;

        Assert.Equal(start, w.Work);
        Assert.Equal(start, w.Forge.Resolve(w.Repo, hidden));
        Assert.Equal(discarded, w.Forge.Resolve(w.Repo, $"refs/feature-flow/superseded/{WorkRefs.Safe(w.RunId)}/plan/{discarded[..12]}"));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task A_clean_up_resets_to_where_the_latest_execution_of_the_step_started()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "one");
        await w.RunAsync("scm.catch_up", null, "plan");                 // execution 1 starts here
        w.CommitWork("b.txt", "kept: before execution 2");
        await w.RunAsync("scm.catch_up", null, "plan");                 // execution 2 starts here
        var second = w.Work!;
        w.CommitWork("c.txt", "discarded");

        var result = await w.Actions.CleanUpAsync(w.RunId, "plan", 2, [w.Repo.FullName], ActionWorld.WorkBranch, ["plan"], TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal(second, w.Work);
    }

    [Fact]
    public async Task A_clean_up_whose_push_is_refused_returns_the_error_and_moves_nothing()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "plan");
        await w.RunAsync("scm.catch_up", null, "plan");
        var discarded = w.CommitWork("b.txt", "work to discard");
        w.Forge.Fail("vcs.Push", new ProviderError("validation_failed", "the remote refuses the push"), times: 5);

        var result = await w.Actions.CleanUpAsync(w.RunId, "plan", 1, [w.Repo.FullName], ActionWorld.WorkBranch, ["plan"], TestContext.Current.CancellationToken);

        Assert.Contains("the remote refuses the push", result.Error, StringComparison.Ordinal);
        Assert.Equal(discarded, w.Work);
    }

    [Fact]
    public async Task A_clean_up_whose_remote_is_unreachable_is_retried()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "plan");
        await w.RunAsync("scm.catch_up", null, "plan");
        w.CommitWork("b.txt", "work to discard");
        w.Forge.Fail("vcs.Resolve", new ProviderError("unreachable", "down"));

        await Assert.ThrowsAsync<ActionInfrastructureException>(() =>
            w.Actions.CleanUpAsync(w.RunId, "plan", 1, [w.Repo.FullName], ActionWorld.WorkBranch, ["plan"], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_clean_up_for_a_step_that_never_touched_the_repository_resets_nothing()
    {
        await using var w = new ActionWorld();
        var head = w.CommitWork("a.txt", "work");

        var events = (await w.Actions.CleanUpAsync(w.RunId, "approve_spec", 1, [w.Repo.FullName], ActionWorld.WorkBranch, [], TestContext.Current.CancellationToken)).Events;

        Assert.Equal(head, w.Work);
        Assert.Contains("recorded no start revision", events.Single(), StringComparison.Ordinal);
    }
}

static class ActionRunAccess
{
    public static PullRequestRef Pr(string text) => new(new RepoRef(text[..text.LastIndexOf('#')]), int.Parse(text[(text.LastIndexOf('#') + 1)..]));
}
