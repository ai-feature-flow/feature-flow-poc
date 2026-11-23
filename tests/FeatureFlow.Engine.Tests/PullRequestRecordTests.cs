using FeatureFlow.Engine.Actions;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// The run's pull requests as its steps read them (A5): a record on open, on checks and on each merge-action poll that
/// changed something; and the title a pull request is opened with when the step gives none (E1).
/// </summary>
public sealed class PullRequestRecordTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<PullRequestRecord> RecordAsync(ActionWorld w) =>
        Assert.Single((await w.Records.QueryAsync<PullRequestRecord>(Queries.PullRequests, new Dictionary<string, string> { ["run_id"] = w.RunId }, Ct)).Unwrap());

    static async Task<string> TitleAsync(ActionWorld w, string? inputs = null)
    {
        w.CommitWork("a.txt", "work\n");
        var opened = await w.RunAsync("scm.pull_request", inputs ?? "{ body: B }", "open_pr");
        var pr = ActionRunAccess.Pr((string)YamlWriter.ReadPlain(opened.Report!.Outputs["pull_request"])!);
        return (await new InMemoryCodeHost(w.Forge).GetPullRequestAsync(pr, Ct)).Unwrap().Title;
    }

    [Fact]
    public async Task Opening_a_pull_request_records_it_with_its_files_under_the_opening_step()
    {
        await using var w = new ActionWorld(new Dictionary<string, string> { ["README.md"] = "# api\nold\n" });
        w.CommitWork("a.txt", "one\ntwo\n");
        w.Forge.Commit(w.Repo, "refs/heads/" + ActionWorld.WorkBranch, new Dictionary<string, string?> { ["README.md"] = "# api\nnew\n" }, "edit");

        await w.RunAsync("scm.pull_request", "{ title: T }", "open_pr");

        var record = await RecordAsync(w);
        Assert.Equal(("acme/api", 1, "open_pr", PullRequestStates.Open, w.Work), (record.Repository, record.Number, record.StepId, record.State.State, record.State.HeadRevision));
        Assert.Equal([new PullRequestFile("README.md", "modified", 1, 1), new PullRequestFile("a.txt", "added", 2, 0)], record.Files);
        Assert.Equal((3, 1, 2), (record.State.Additions, record.State.Deletions, record.State.ChangedFiles));
        Assert.Null(record.Checks);
    }

    [Fact]
    public async Task Checks_are_recorded_with_the_pull_request_and_kept_by_a_later_read_of_the_same_head()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "work\n");
        await w.RunAsync("scm.pull_request", "{ title: T }", "open_pr");

        Assert.Equal("passed", (await w.RunAsync("ci.checks", null, "checks")).Report?.Outcome);
        var checkedRecord = await RecordAsync(w);
        Assert.Equal(("checks", w.Work), (checkedRecord.StepId, checkedRecord.Checks?.Revision));

        // A merge-action read fetches no checks: the checks of the same head stay.
        await w.RunAsync("scm.merge", "{ engine_can_merge: false, require_tested: false }", "merge");
        var merged = await RecordAsync(w);
        Assert.Equal("merge", merged.StepId);
        Assert.Equal(checkedRecord.Checks!.Checks.Select(c => c.Name), merged.Checks?.Checks.Select(c => c.Name));

        // A new head drops them and lists the files again.
        w.CommitWork("b.txt", "more\n");
        await w.RunAsync("wait.condition", "{ pull_request: approved }", "approval", waits: 1);
        var moved = await RecordAsync(w);
        Assert.Equal((w.Work, (CheckStatus?)null), (moved.State.HeadRevision, moved.Checks));
        Assert.Equal(["a.txt", "b.txt"], moved.Files!.Select(f => f.Path));
    }

    [Fact]
    public async Task A_merge_action_poll_records_only_when_the_pull_request_changed()
    {
        await using var w = new ActionWorld();
        w.CommitWork("a.txt", "work\n");
        await w.RunAsync("scm.pull_request", "{ title: T }", "open_pr");
        await w.RunAsync("ci.checks", null, "checks");
        const string inputs = "{ engine_can_merge: false, require_tested: false }";
        await w.RunAsync("scm.merge", inputs, "merge");
        var first = await RecordAsync(w);

        w.Decision = new StepDecision("ticket.merge_action", StepDecision.Open);
        Assert.Equal(ActionResult.Wait, (await w.RunAsync("scm.merge", inputs, "merge", waits: 3)).Kind);
        Assert.Equal(first.ReadAt, (await RecordAsync(w)).ReadAt);

        w.Forge.MergePullRequest(ActionRunAccess.Pr(w.PullRequests[0]), "alice");
        Assert.Equal("merged", (await w.RunAsync("scm.merge", inputs, "merge")).Report?.Outcome);
        var merged = await RecordAsync(w);
        Assert.Equal((PullRequestStates.Merged, "alice"), (merged.State.State, merged.State.MergedBy));
        Assert.True(merged.ReadAt > first.ReadAt);
    }

    [Theory]
    [InlineData("acme/api#200", "Add DELETE /widgets/{id} (#200)")]
    [InlineData("ACME-12", "Add DELETE /widgets/{id} (ACME-12)")]
    public async Task With_no_title_a_pull_request_is_titled_after_the_runs_tracker_issue(string key, string title)
    {
        await using var w = new ActionWorld();
        w.Tracker.Add(key, new IssueSpec("Add DELETE /widgets/{id}"));
        w.TrackerIssue = key;

        Assert.Equal(title, await TitleAsync(w));
    }

    [Fact]
    public async Task A_title_the_step_gives_is_kept()
    {
        await using var w = new ActionWorld();
        w.Tracker.Add("ACME-12", new IssueSpec("Add DELETE /widgets/{id}"));
        w.TrackerIssue = "ACME-12";

        Assert.Equal("Given", await TitleAsync(w, "{ title: Given }"));
    }

    [Fact]
    public async Task A_tracker_issue_that_does_not_read_titles_the_pull_request_by_the_runs_level()
    {
        await using var w = new ActionWorld();
        w.TrackerIssue = "epic-work-list";
        (await w.Records.AppendAsync(new RecordBatch(Runs:
            [new RunRecord(w.RunId, null, "std:templates/ticket_delivery", "running", "ann", ["acme/api"], false, DateTimeOffset.UtcNow, null, Level: "ticket")]), Ct)).Unwrap();

        Assert.Equal($"ticket: {w.RunId}", await TitleAsync(w));
    }
}
