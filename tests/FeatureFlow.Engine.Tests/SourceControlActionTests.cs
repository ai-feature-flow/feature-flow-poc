using System.Text.Json;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

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

        Assert.Equal(ActionResult.Wait, waiting.Kind);
        Assert.Equal(before, w.Main);
        Assert.Contains(waiting.Events, e => e.StartsWith("F1 at merge time", StringComparison.Ordinal));

        // A person merges on the code host: the step sees it and records them.
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
        var hidden = $"refs/feature-flow/wip/{EngineActions.Safe(w.RunId)}/implement";
        w.Forge.SetReference(w.Repo, hidden, discarded);

        var events = await w.Actions.CleanUpAsync(w.RunId, "plan", 1, [w.Repo.FullName], ActionWorld.WorkBranch, ["plan", "implement"], TestContext.Current.CancellationToken);

        Assert.Equal(start, w.Work);
        Assert.Equal(start, w.Forge.Resolve(w.Repo, hidden));
        Assert.Equal(discarded, w.Forge.Resolve(w.Repo, $"refs/feature-flow/superseded/{EngineActions.Safe(w.RunId)}/plan/{EngineActions.Short(discarded)}"));
        Assert.Equal(2, events.Count);
    }

    [Fact]
    public async Task A_clean_up_for_a_step_that_never_touched_the_repository_resets_nothing()
    {
        await using var w = new ActionWorld();
        var head = w.CommitWork("a.txt", "work");

        var events = await w.Actions.CleanUpAsync(w.RunId, "approve_spec", 1, [w.Repo.FullName], ActionWorld.WorkBranch, [], TestContext.Current.CancellationToken);

        Assert.Equal(head, w.Work);
        Assert.Contains("recorded no start revision", events.Single(), StringComparison.Ordinal);
    }
}

static class ActionRunAccess
{
    public static PullRequestRef Pr(string text) => new(new RepoRef(text[..text.LastIndexOf('#')]), int.Parse(text[(text.LastIndexOf('#') + 1)..]));
}
