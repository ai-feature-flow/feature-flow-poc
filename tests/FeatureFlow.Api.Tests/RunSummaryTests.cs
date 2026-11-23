using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// A run as the run list and its page show it (B1 A1, A5): for the root of an active tree, the steps current across the tree,
/// how many run and how many waits need the caller; each run's pull requests; and on its page, its pull requests in full, its
/// inputs and the steps current in it and below it. Runs recorded before these fields read with them empty.
/// </summary>
public sealed class RunSummaryTests : IAsyncDisposable
{
    static readonly string[] Api = ["acme/api"];

    readonly ReadsWorld world = new();

    public ValueTask DisposeAsync() => world.DisposeAsync();

    static PullRequestRecord PullRequest(string run, int number, string state) =>
        new(run, "acme/api", number, "deliver.open_pr",
            new PullRequestState(new PullRequestRef(new RepoRef("acme/api"), number), state, "Add widgets (#200)", "", "ff/ticket/t0", "abc123", "ff/epic/e0", false, true,
                ["ann"], [], null, null, $"https://github.com/acme/api/pull/{number}", 12, 3, 2, "clean"),
            [new PullRequestFile("src/Widgets.cs", "modified", 10, 3), new PullRequestFile("tests/WidgetTests.cs", "added", 2, 0)],
            new CheckStatus("abc123", [new CheckRun("build", "completed", "success", null, "https://ci.example/1")]), DateTimeOffset.UtcNow);

    /// <summary>
    /// A project with two epics of one ticket each: the project and the first epic run their children; the first epic is
    /// stalled; its ticket runs <c>implement</c> while its spec waits on a person; the second epic and its ticket have ended.
    /// </summary>
    async Task<ReadsWorld.ProjectTree> TreeAsync()
    {
        var tree = await world.ProjectAsync(epics: 2, tickets: 1);
        var (project, e0, e1, t0, t1) = (tree.Project, tree.Epics[0], tree.Epics[1], tree.Tickets[0], tree.Tickets[1]);
        var now = DateTimeOffset.UtcNow;
        await world.AppendAsync(new RecordBatch(
            [
                ReadsWorld.Run(e1, project, "std:templates/epic_delivery", "epic", "epics.children_1", Api, "completed"),
                ReadsWorld.Run(t1, e1, "std:templates/ticket_delivery", "ticket", "tickets.children_0", Api, "completed"),
            ],
            [
                new WaitRecord(e0, "stalled-1", "stalled", "tickets.children", "", "no progress for 2h", [], true, now),
                new WaitRecord(t0, "gate-1", "gate", "approve_spec", "ticket.spec_approval", "Approve?", ["approved", "rejected"], true, now),
                new WaitRecord(t1, "gate-1", "gate", "approve_spec", "ticket.spec_approval", "Approve?", ["approved", "rejected"], true, now),
            ],
            [
                new TimelineRecord(project, 1, "started", "epics.children", null, now),
                new TimelineRecord(e0, 1, "started", "tickets.children", null, now),
                new TimelineRecord(t0, 1, "started", "plan.plan", null, now),
                new TimelineRecord(t0, 2, "completed", "plan.plan", "planned", now),
                new TimelineRecord(t0, 3, "started", "implement", null, now),
                new TimelineRecord(e1, 1, "started", "tickets.children", null, now),
            ],
            PullRequests: [PullRequest(t0, 7, "open"), PullRequest(t1, 6, "merged")]));
        return tree;
    }

    [Fact]
    public async Task An_active_trees_root_counts_what_needs_the_caller_and_what_runs_across_the_tree()
    {
        var tree = await TreeAsync();
        var (project, e0, t0) = (tree.Project, tree.Epics[0], tree.Tickets[0]);

        var rows = (await world.GetAsync<RunList>("runs")).Runs.ToDictionary(r => r.RunId);

        var root = rows[project];
        // Container steps (epics.children, tickets.children) are not current: the console's tree shows their branches in their place.
        Assert.Equal([new CurrentStep(t0, "implement", "running"), new CurrentStep(t0, "approve_spec", "waiting(gate)")], root.CurrentSteps!);
        // The ended epic's ticket waits too, but an ended run needs no one.
        Assert.Equal((1, 1), (root.NeedsYou, root.Running));
        // Only a root carries the tree's counts.
        Assert.Equal(((IReadOnlyList<CurrentStep>?)null, 0, 0), (rows[e0].CurrentSteps, rows[e0].NeedsYou, rows[e0].Running));
        Assert.Equal([new PullRequestLink("acme/api", 7, "https://github.com/acme/api/pull/7", "open")], rows[t0].PullRequests!);
        Assert.Empty(root.PullRequests!);
    }

    [Fact]
    public async Task An_ended_tree_has_nothing_current()
    {
        var tree = await TreeAsync();
        await world.AppendAsync(new RecordBatch([ReadsWorld.Run(tree.Project, null, "std:templates/project_delivery", "project", null, Api, "completed")]));

        var root = (await world.GetAsync<RunList>("runs")).Runs.Single(r => r.RunId == tree.Project);

        Assert.Equal(((IReadOnlyList<CurrentStep>?)null, 0, 0), (root.CurrentSteps, root.NeedsYou, root.Running));
    }

    [Fact]
    public async Task A_runs_page_has_its_pull_requests_in_full_its_inputs_and_whats_current_in_and_below_it()
    {
        var tree = await TreeAsync();
        var (e0, t0) = (tree.Epics[0], tree.Tickets[0]);

        var epic = await world.GetAsync<RunDetail>($"runs/{e0}");
        var ticket = await world.GetAsync<RunDetail>($"runs/{t0}");

        Assert.Equal([new CurrentStep(t0, "implement", "running"), new CurrentStep(t0, "approve_spec", "waiting(gate)")], epic.CurrentSteps!);
        Assert.Empty(epic.PullRequests!);
        Assert.Equal("\"epic_0\"", epic.Inputs!["epic_name"]);
        var pr = Assert.Single(ticket.PullRequests!);
        Assert.Equal((7, "open", "clean", 12, 3, 2, "deliver.open_pr"), (pr.Number, pr.State, pr.MergeableState, pr.Additions, pr.Deletions, pr.ChangedFiles, pr.StepId));
        Assert.Equal(["src/Widgets.cs", "tests/WidgetTests.cs"], pr.Files.Select(f => f.Path));
        Assert.Equal([new CheckView("build", "completed", "success", "https://ci.example/1")], pr.Checks);
        Assert.Equal([new PullRequestLink("acme/api", 7, "https://github.com/acme/api/pull/7", "open")], ticket.Run.PullRequests!);
        Assert.Equal("\"ticket_0_0\"", ticket.Inputs!["ticket_id"]);
    }

    [Fact]
    public async Task A_run_recorded_before_these_fields_reads_with_them_empty()
    {
        await world.AppendAsync(new RecordBatch([new RunRecord("old-1", null, "std:templates/ticket_delivery", "running", "x", Api, false, DateTimeOffset.UtcNow, null)]));

        var row = (await world.GetAsync<RunList>("runs")).Runs.Single();
        var page = await world.GetAsync<RunDetail>("runs/old-1");
        var tree = await world.GetAsync<RunTreeView>("runs/old-1/tree");

        Assert.Equal(((string?)null, (TrackerIssueView?)null, (string?)null, (DateTimeOffset?)null, 0), (row.Level, row.TrackerIssue, row.Title, row.StartedAt, row.NeedsYou));
        Assert.Empty(row.PullRequests!);
        Assert.Empty(row.CurrentSteps!);
        Assert.Equal(((IReadOnlyDictionary<string, string>?)null, 0, 0), (page.Inputs, page.PullRequests!.Count, page.CurrentSteps!.Count));
        Assert.Equal(("old-1", (PlanView?)null, "run old-1 has no plan yet"), (tree.Root.Run.RunId, tree.Root.Plan, tree.Root.PlanNote));
    }
}
