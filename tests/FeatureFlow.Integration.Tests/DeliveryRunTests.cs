using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M6 delivery through the engine: the work-item budget (F11) at <c>run.child</c> and <c>plan.expand</c>, across a run
/// tree and within the organisation's ceiling, and declarative tracker updates on the run's tracker issue. Test library
/// <c>libraries/delivery</c>; the stand-in serves the actions.
/// </summary>
[Trait("Category", "Integration")]
public class DeliveryRunTests
{
    static readonly string[] Delivery = [Path.Combine(Harness.TestLibraries, "delivery")];

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The run's open wait, its reason, once there is one.</summary>
    static async Task<string> WaitReasonAsync(Harness h, string runId)
    {
        Assert.NotNull(await h.WaitForWaitAsync(runId, TimeSpan.FromMinutes(2)));
        return (await h.ShowAsync(runId))["waits"]!.AsArray().Single()!["reason"]!.ToString();
    }

    [Fact]
    public async Task The_roots_budget_counts_every_descendant_and_a_childs_own_budget_does_not_count()
    {
        await using var h = await Harness.StartAsync("budget-tree", libraries: Delivery);
        var root = h.Start("delivery:templates/budget_tree");

        // The root's child is one work item and its first child the second; the child's second child is refused.
        var middle = await ChildAsync(h, root);
        var reason = await WaitReasonAsync(h, middle);

        Assert.Contains("F11", reason, StringComparison.Ordinal);
        var children = (await h.ShowAsync(middle))["children"]!.AsArray();
        Assert.Single(children);
        Assert.Contains((await h.ShowAsync(middle))["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "completed" && e["stepId"]!.ToString() == "first");
        await h.RecordHistoryAsync(middle, "budget_middle");
    }

    [Fact]
    public async Task Children_started_together_are_counted_as_each_is_allowed()
    {
        await using var h = await Harness.StartAsync("budget-parallel", libraries: Delivery);
        var id = h.Start("delivery:templates/parallel_children");

        // The first child is recorded when the budget allows it, before it can record itself, so the second is refused.
        var reason = await WaitReasonAsync(h, id);

        Assert.Contains("F11", reason, StringComparison.Ordinal);
        Assert.Single((await h.ShowAsync(id))["children"]!.AsArray());
    }

    [Fact]
    public async Task A_plan_expand_past_the_budget_is_refused_before_any_child_starts()
    {
        const string script = """
            {
              "expand_budget:plan": [{
                outcome: planned,
                artifacts: { work_list: { schema: 1, level: ticket, items: [
                  { name: a, title: A, repositories: [acme/api], boundary: a, owns: [R1] },
                  { name: b, title: B, repositories: [acme/api], boundary: b, owns: [R2] },
                  { name: c, title: C, repositories: [acme/api], boundary: c, owns: [R3] } ] } }
              }]
            }
            """;
        await using var h = await Harness.StartAsync("budget-expand", script, libraries: Delivery);
        var id = h.Start("delivery:templates/expand_budget", "--repo", "acme/api");

        var reason = await WaitReasonAsync(h, id);

        Assert.Contains("F11", reason, StringComparison.Ordinal);
        Assert.Contains("would add 3", reason, StringComparison.Ordinal);
        Assert.Empty((await h.ShowAsync(id))["children"]!.AsArray());
    }

    [Fact]
    public async Task The_organisations_ceiling_is_the_budget_of_a_tree_that_sets_none_and_refuses_a_template_asking_more()
    {
        await using var h = await Harness.StartAsync("budget-ceiling", libraries: Delivery, policy: "  budgets: { max_work_items: { ceiling: 1 } }");

        var capped = h.Start("delivery:templates/two_children");
        var reason = await WaitReasonAsync(h, capped);
        Assert.Contains("may create 1 descendant run", reason, StringComparison.Ordinal);
        Assert.Single((await h.ShowAsync(capped))["children"]!.AsArray());

        // budget_tree asks for 2, above the ceiling: check 22 refuses it at run start.
        var asking = h.Start("delivery:templates/budget_tree");
        var (_, end, _) = h.Cli("wait", asking, "--timeout", "120");
        Assert.Equal($"run {asking} failed", end.Trim());
        Assert.Contains("max_work_items", (await h.ShowAsync(asking))["run"]!["detail"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declarative_updates_move_the_runs_tracker_issue_as_steps_start_and_a_fragment_leaves()
    {
        var world = "tracked-" + Guid.NewGuid().ToString("N")[..8];
        var tracker = InMemoryTracker.Named(world);
        var issue = (await tracker.CreateAsync(new IssueSpec("The ticket"), "seed", Ct)).Unwrap();
        await using var h = await Harness.StartAsync("tracked", autoApprove: true, libraries: Delivery, omitTypes: ["tracker"],
            providers: $"  tracker: {{ instances: {{ memory: {{ world: {world} }} }} }}");

        var id = h.Start("delivery:templates/tracked", "--tracker-issue", issue.Key);
        var (_, end, _) = h.Cli("wait", id, "--timeout", "120");

        Assert.Equal($"run {id} completed", end.Trim());
        Assert.Equal("done", (await tracker.GetIssueAsync(issue, Ct)).Unwrap().Status);
        var moves = (await h.ShowAsync(id))["timeline"]!.AsArray().Where(e => e!["kind"]!.ToString() == "tracker").Select(e => e!["detail"]!.ToString()).ToList();
        Assert.Equal(3, moves.Count);
        Assert.EndsWith($"moved {issue.Key} to in_progress", moves[0], StringComparison.Ordinal);
        Assert.EndsWith($"moved {issue.Key} to code_review", moves[1], StringComparison.Ordinal);
        Assert.EndsWith($"moved {issue.Key} to done", moves[2], StringComparison.Ordinal);
        await h.RecordHistoryAsync(id, "delivery_tracked");
    }

    [Fact]
    public async Task A_tracker_update_that_fails_is_noted_and_the_run_carries_on()
    {
        var world = "tracked-" + Guid.NewGuid().ToString("N")[..8];
        await using var h = await Harness.StartAsync("tracked-missing", autoApprove: true, libraries: Delivery, omitTypes: ["tracker"],
            providers: $"  tracker: {{ instances: {{ memory: {{ world: {world} }} }} }}");

        var id = h.Start("delivery:templates/tracked", "--tracker-issue", "MEM-404");
        var (_, end, _) = h.Cli("wait", id, "--timeout", "120");

        Assert.Equal($"run {id} completed", end.Trim());
        var notes = (await h.ShowAsync(id))["timeline"]!.AsArray().Where(e => e!["kind"]!.ToString() == "tracker").Select(e => e!["detail"]!.ToString()).ToList();
        Assert.Equal(3, notes.Count);
        Assert.All(notes, n => Assert.Contains("could not move MEM-404", n, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_childs_own_budget_above_the_ceiling_is_not_checked_only_the_roots_counts()
    {
        // budget_middle asks for 50, above the ceiling of 10; as a child its budget is not read, so it starts.
        await using var h = await Harness.StartAsync("budget-child-cap", libraries: Delivery, policy: "  budgets: { max_work_items: { ceiling: 10 } }");
        var root = h.Start("delivery:templates/budget_tree");

        var middle = await ChildAsync(h, root);
        var reason = await WaitReasonAsync(h, middle);

        Assert.Contains("may create 2 descendant run", reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tracker_outage_is_given_up_after_a_few_tries_and_the_run_carries_on()
    {
        var world = "tracked-" + Guid.NewGuid().ToString("N")[..8];
        var tracker = InMemoryTracker.Named(world);
        var issue = (await tracker.CreateAsync(new IssueSpec("The ticket"), "seed", Ct)).Unwrap();
        // Every try of the first update fails as an outage would; the later updates go through.
        tracker.Fail("Transition", new ProviderError("unreachable", "the tracker is down"), times: 5);
        await using var h = await Harness.StartAsync("tracked-outage", autoApprove: true, libraries: Delivery, omitTypes: ["tracker"],
            providers: $"  tracker: {{ instances: {{ memory: {{ world: {world} }} }} }}");

        var id = h.Start("delivery:templates/tracked", "--tracker-issue", issue.Key);
        var (_, end, _) = h.Cli("wait", id, "--timeout", "180");

        Assert.Equal($"run {id} completed", end.Trim());
        var notes = (await h.ShowAsync(id))["timeline"]!.AsArray().Where(e => e!["kind"]!.ToString() == "tracker").Select(e => e!["detail"]!.ToString()).ToList();
        Assert.Contains("did not answer in 5 tries", notes[0], StringComparison.Ordinal);
        Assert.Equal("done", (await tracker.GetIssueAsync(issue, Ct)).Unwrap().Status);
    }

    static async Task<string> ChildAsync(Harness h, string runId)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            if ((await h.ShowAsync(runId))["children"]!.AsArray().FirstOrDefault() is { } child)
                return child["runId"]!.ToString();
            await Task.Delay(200, Ct);
        }

        throw new TimeoutException($"{runId} started no child");
    }
}
