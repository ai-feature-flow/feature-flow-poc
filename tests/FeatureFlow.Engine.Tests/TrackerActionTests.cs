using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Tests;

/// <summary><c>tracker.update</c> (docs/04 section 12) over the in-memory tracker.</summary>
public sealed class TrackerActionTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string Outcome(ActionResult r) => r.Report?.Outcome ?? $"{r.Kind}: {r.Reason}";

    static string Issue(ActionResult r) => (string)YamlWriter.ReadPlain(r.Report!.Outputs["issue"])!;

    [Fact]
    public async Task Create_makes_one_issue_and_a_retry_of_the_same_step_adopts_it()
    {
        await using var w = new ActionWorld();
        var call = w.Call("tracker.update", "create", 1, "{ op: create, title: A ticket, body: The body., labels: [ff] }");

        var first = await w.Actions.RunAsync(call, w.Scope, () => { }, Ct);
        var retried = await w.Actions.RunAsync(call, w.Scope, () => { }, Ct);

        Assert.Equal("done", Outcome(first));
        Assert.Equal(Issue(first), Issue(retried));
        var issue = (await w.Tracker.GetIssueAsync(new IssueRef(Issue(first)), Ct)).Unwrap();
        Assert.Equal(("A ticket", "The body."), (issue.Title, issue.Body));
        Assert.Single((await w.Tracker.SearchAsync(new IssueQuery(Label: "ff"), Ct)).Unwrap());
    }

    [Fact]
    public async Task Transition_comment_and_update_act_on_the_runs_issue_by_default()
    {
        await using var w = new ActionWorld();
        w.TrackerIssue = (await w.Tracker.CreateAsync(new IssueSpec("Ticket", "Body"), "seed", Ct)).Unwrap().Key;

        Assert.Equal("done", Outcome(await w.RunAsync("tracker.update", "{ op: transition, status: code_review }")));
        Assert.Equal("done", Outcome(await w.RunAsync("tracker.update", "{ op: comment, body: Ready for review. }", "say")));
        Assert.Equal("done", Outcome(await w.RunAsync("tracker.update", "{ op: update, title: Renamed }", "rename")));

        var issue = (await w.Tracker.GetIssueAsync(new IssueRef(w.TrackerIssue), Ct)).Unwrap();
        Assert.Equal(("code_review", "Renamed", "Body"), (issue.Status, issue.Title, issue.Body));
        Assert.Equal(["Ready for review."], w.Tracker.Comments(new IssueRef(w.TrackerIssue)));
    }

    [Fact]
    public async Task A_comment_retried_by_the_same_execution_is_posted_once()
    {
        await using var w = new ActionWorld();
        w.TrackerIssue = (await w.Tracker.CreateAsync(new IssueSpec("Ticket"), "seed", Ct)).Unwrap().Key;
        var call = w.Call("tracker.update", "say", 1, "{ op: comment, body: Started. }");

        await w.Actions.RunAsync(call, w.Scope, () => { }, Ct);
        await w.Actions.RunAsync(call, w.Scope, () => { }, Ct);

        Assert.Single(w.Tracker.Comments(new IssueRef(w.TrackerIssue)));
    }

    [Fact]
    public async Task Link_joins_two_issues()
    {
        await using var w = new ActionWorld();
        var a = (await w.Tracker.CreateAsync(new IssueSpec("A"), "a", Ct)).Unwrap();
        var b = (await w.Tracker.CreateAsync(new IssueSpec("B"), "b", Ct)).Unwrap();

        Assert.Equal("done", Outcome(await w.RunAsync("tracker.update", $"{{ op: link, issue: {a.Key}, to: {b.Key}, link_type: blocks }}")));

        Assert.Equal([(b.Key, "blocks")], w.Tracker.Links(a));
    }

    [Fact]
    public async Task An_issue_that_does_not_exist_is_not_found()
    {
        await using var w = new ActionWorld();

        Assert.Equal("not_found", Outcome(await w.RunAsync("tracker.update", "{ op: transition, issue: MEM-404, status: done }")));
    }

    [Fact]
    public async Task A_status_the_tracker_does_not_map_stops_for_a_person()
    {
        await using var w = new ActionWorld();
        w.TrackerIssue = (await w.Tracker.CreateAsync(new IssueSpec("Ticket"), "seed", Ct)).Unwrap().Key;

        var result = await w.RunAsync("tracker.update", "{ op: transition, status: shipped }");

        Assert.Equal("cannot_answer", result.Kind);
        Assert.Contains("validation_failed", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_issue_named_and_none_on_the_run_stops_for_a_person()
    {
        await using var w = new ActionWorld();

        var result = await w.RunAsync("tracker.update", "{ op: comment, body: Hello }");

        Assert.Equal("cannot_answer", result.Kind);
        Assert.Contains("no tracker issue", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unknown_op_is_refused_by_check_31()
    {
        await using var w = new ActionWorld();

        var result = await w.RunAsync("tracker.update", "{ op: delete, issue: MEM-1 }");

        Assert.Equal("cannot_answer", result.Kind);
        Assert.Contains("check 31", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tracker_outage_is_retried_not_a_step_failure()
    {
        await using var w = new ActionWorld();
        w.TrackerIssue = (await w.Tracker.CreateAsync(new IssueSpec("Ticket"), "seed", Ct)).Unwrap().Key;
        w.Tracker.Fail("Transition", new ProviderError("unreachable", "down"));

        await Assert.ThrowsAsync<ActionInfrastructureException>(() => w.RunAsync("tracker.update", "{ op: transition, status: done }"));
    }
}
