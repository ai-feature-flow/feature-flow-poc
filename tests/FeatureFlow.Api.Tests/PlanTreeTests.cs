using System.Net;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// A run's plan as a tree (B1 A2): its groups (fragment uses, <c>for_each</c> and <c>parallel</c> branches), each step's place
/// in them, its counters and the child run it started; and a patch approval's proposed plan, marked against the current (A8).
/// </summary>
public sealed class PlanTreeTests : IAsyncDisposable
{
    static readonly string[] Api = ["acme/api"];
    static readonly Dictionary<string, string> Ticket = new() { ["ticket_id"] = "\"T-1\"" };

    readonly ReadsWorld world = new();

    public ValueTask DisposeAsync() => world.DisposeAsync();

    static (string? Parent, GroupKind Kind, string Label, string? Item) Group(PlanView plan, string id)
    {
        var g = Assert.Single(plan.Groups!, g => g.Id == id);
        return (g.Parent, g.Kind, g.Label, g.Item);
    }

    static string? ParentOf(PlanView plan, string step) => Assert.Single(plan.Steps, s => s.Id == step).Parent;

    /// <summary>Every group sits in a group listed before it, or in a container step.</summary>
    static void AssertNested(PlanView plan)
    {
        var listed = new HashSet<string>();
        foreach (var g in plan.Groups!)
        {
            Assert.True(g.Parent is null || listed.Contains(g.Parent) || plan.Steps.Any(s => s.Id == g.Parent && s.Form is StepForm.ForEach or StepForm.Parallel or StepForm.Chain),
                $"group {g.Id} sits in {g.Parent}, which is neither a group before it nor a container");
            listed.Add(g.Id);
        }

        Assert.All(plan.Steps, s => Assert.True(s.Parent is null || listed.Contains(s.Parent), $"step {s.Id} sits in {s.Parent}"));
    }

    [Fact]
    public async Task A_tickets_plan_groups_its_fragments_and_review_angles_and_each_step_sits_in_its_innermost_group()
    {
        var run = await world.StartAsync("std:templates/ticket_delivery", "ticket", Ticket, Api);
        await world.IndexAsync(run, "std:templates/ticket_delivery", Api, Ticket);

        var plan = await world.GetAsync<PlanView>($"runs/{run}/plan");

        Assert.Equal(["baseline", "plan", "verify", "verify.review"], plan.Groups!.Take(4).Select(g => g.Id));
        Assert.Equal(((string?)null, GroupKind.Fragment, "verify", (string?)null), Group(plan, "verify"));
        Assert.Equal(("verify", GroupKind.Fragment, "review", (string?)null), Group(plan, "verify.review"));
        Assert.Equal(("verify.review.review", GroupKind.Branch, "design", "design"), Group(plan, "verify.review.review#1"));
        Assert.Equal(("verify", GroupKind.Fragment, "fix", (string?)null), Group(plan, "verify.fix"));
        Assert.Equal(
            [null, "plan", "verify.review", "verify.review.review#1", "verify.review.review#1", "verify.fix", "deliver"],
            ((string[])["approve_spec", "plan.plan", "verify.review.review", "verify.review.review_1.assess", "verify.review.review_1.record", "verify.fix.triage", "deliver.merge"])
            .Select(s => ParentOf(plan, s)));
        AssertNested(plan);
    }

    [Fact]
    public async Task A_projects_plan_groups_a_branch_per_repository_and_child_and_a_fragment_used_as_a_chain_branch()
    {
        string[] repositories = ["acme/api", "acme/ui"];
        var parameters = new Dictionary<string, string> { ["project_name"] = "\"acme\"" };
        var run = await world.StartAsync("std:templates/project_delivery", "project", parameters, repositories);
        await world.IndexAsync(run, "std:templates/project_delivery", repositories, parameters, ReadsWorld.WorkList("epic", 2));

        var plan = await world.GetAsync<PlanView>($"runs/{run}/plan");

        Assert.Equal(2, plan.Revision);
        Assert.Equal(("tests", GroupKind.Branch, "acme/ui", "acme/ui"), Group(plan, "tests#1"));
        Assert.Equal("tests#1", ParentOf(plan, "tests_1"));
        Assert.Equal(("epics.children", GroupKind.Branch, "children_1", (string?)null), Group(plan, "epics.children#1"));
        Assert.Equal("epics.children#1", ParentOf(plan, "epics.children_1"));
        // deliver_in_order's chain uses merge_into_parent for each repository: the fragment's use sits in its branch.
        Assert.Equal(("deliver.deliver", GroupKind.Branch, "acme/ui", "acme/ui"), Group(plan, "deliver.deliver#1"));
        Assert.Equal(("deliver.deliver#1", GroupKind.Fragment, "deliver_1", (string?)null), Group(plan, "deliver.deliver_1"));
        Assert.Equal("deliver.deliver_1", ParentOf(plan, "deliver.deliver_1.merge"));
        AssertNested(plan);
    }

    [Fact]
    public async Task Each_steps_counters_child_run_and_last_change_come_from_the_runs_records()
    {
        var run = await world.StartAsync("std:templates/epic_delivery", "epic", new Dictionary<string, string> { ["epic_name"] = "\"billing\"" }, Api);
        await world.IndexAsync(run, "std:templates/epic_delivery", Api, new Dictionary<string, string> { ["epic_name"] = "\"billing\"" }, ReadsWorld.WorkList("t", 2));
        var at = new DateTimeOffset(2026, 11, 24, 1, 0, 0, TimeSpan.Zero);
        await world.AppendAsync(new RecordBatch(
            [
                ReadsWorld.Run($"{run}.a", run, "std:templates/ticket_delivery", "ticket", "tickets.children_0", Api) with { StartedAt = at },
                // Started again by the same step (a retry): the later child is the one shown.
                ReadsWorld.Run($"{run}.b", run, "std:templates/ticket_delivery", "ticket", "tickets.children_0", Api) with { StartedAt = at.AddMinutes(5) },
                ReadsWorld.Run($"{run}.c", run, "std:templates/ticket_delivery", "ticket", "tickets.children_1", Api) with { StartedAt = at.AddMinutes(1) },
            ],
            Timeline:
            [
                new TimelineRecord(run, 1, "started", "tickets.plan", "execution 1", at),
                new TimelineRecord(run, 2, "retried", "tickets.plan", null, at.AddMinutes(2)),
                new TimelineRecord(run, 3, "started", "tickets.plan", "execution 2", at.AddMinutes(3)),
            ],
            StepStates:
            [
                new StepStateRecord(run, "tickets.plan", 1, 1, 3, 0, at),
                new StepStateRecord(run, "tickets.approve", 1, 0, null, 0, at),
            ]));
        await world.AppendAsync(new RecordBatch(StepStates: [new StepStateRecord(run, "tickets.plan", 2, 2, 5, 2, at.AddMinutes(3))]));

        var steps = (await world.GetAsync<PlanView>($"runs/{run}/plan")).Steps.ToDictionary(s => s.Id);

        Assert.Equal((2, (int?)2, (long?)5, 2L, (DateTimeOffset?)at.AddMinutes(3), "running"),
            (steps["tickets.plan"].Executions, steps["tickets.plan"].AttemptsUsed, steps["tickets.plan"].AttemptsAllowed, steps["tickets.plan"].AttemptsGranted,
                steps["tickets.plan"].ChangedAt, steps["tickets.plan"].Status));
        Assert.Equal((1, (int?)0, (long?)null), (steps["tickets.approve"].Executions, steps["tickets.approve"].AttemptsUsed, steps["tickets.approve"].AttemptsAllowed));
        // A step with no record yet (a run recorded before step states, too): no counters, no change.
        Assert.Equal((0, (int?)null, (long?)null, 0L, (DateTimeOffset?)null), (steps["design"].Executions, steps["design"].AttemptsUsed,
            steps["design"].AttemptsAllowed, steps["design"].AttemptsGranted, steps["design"].ChangedAt));
        Assert.Equal(($"{run}.b", $"{run}.c", (string?)null), (steps["tickets.children_0"].ChildRunId, steps["tickets.children_1"].ChildRunId, steps["design"].ChildRunId));
    }

    const string Lint = """
        { api_version: feature-flow/v1, kind: patch, base_revision: 1, changes: [{ op: insert, after: implement, step: { id: lint, action: analysis.run } }] }
        """;

    async Task<string> PatchApprovalAsync(string document)
    {
        var run = await world.StartAsync("std:templates/ticket_delivery", "ticket", Ticket, Api);
        await world.IndexAsync(run, "std:templates/ticket_delivery", Api, Ticket, patches: [Lint]);
        await world.AppendAsync(new RecordBatch(Waits:
        [
            new WaitRecord(run, "patch_approval-1", "patch_approval", "implement", "", "Approve the patch?", ["approve", "reject"], true, DateTimeOffset.UtcNow,
                Patch: new WaitPatch("agent", "implement", "lint first", ["adds a step"], "implement", 2, false, document)),
        ]));
        return run;
    }

    [Fact]
    public async Task A_proposed_plan_marks_added_removed_and_changed_steps_and_keeps_a_removed_one_in_place()
    {
        var run = await PatchApprovalAsync("""
            { api_version: feature-flow/v1, kind: patch, base_revision: 2, changes: [
              { op: remove, target: lint },
              { op: insert, after: implement, step: { id: format, action: analysis.run } },
              { op: set, target: implement, field: limits, value: { attempts: 9 } }] }
            """);

        var current = await world.GetAsync<PlanView>($"runs/{run}/plan");
        var proposed = await world.GetAsync<PlanView>($"runs/{run}/plan?proposed=patch_approval-1");

        Assert.Equal((2, 3), (current.Revision, proposed.Revision));
        Assert.All(current.Steps, s => Assert.Null(s.Change));
        var ids = proposed.Steps.Select(s => s.Id).ToList();
        Assert.Equal(["implement", "lint", "format", "verify.tests"], ids.Skip(ids.IndexOf("implement")).Take(4));
        // implement is changed as written; lint is gone but shown where it was, after the step it followed.
        Assert.Equal(new Dictionary<string, PlanChange?> { ["implement"] = PlanChange.Changed, ["lint"] = PlanChange.Removed, ["format"] = PlanChange.Added },
            proposed.Steps.Where(s => s.Change is not null).ToDictionary(s => s.Id, s => s.Change));
        Assert.Contains("format", proposed.Readable);
    }

    [Theory]
    [InlineData("""{ api_version: feature-flow/v1, kind: patch, base_revision: 2, changes: [{ op: remove, target: no_such_step }] }""")]
    [InlineData("not: [a patch")]
    public async Task A_patch_that_does_not_apply_is_409_and_an_unknown_wait_404(string document)
    {
        var run = await PatchApprovalAsync(document);
        var ct = TestContext.Current.CancellationToken;

        using var bad = await world.Http.GetAsync($"runs/{run}/plan?proposed=patch_approval-1", ct);
        using var unknown = await world.Http.GetAsync($"runs/{run}/plan?proposed=patch_approval-9", ct);

        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.NotFound), (bad.StatusCode, unknown.StatusCode));
        Assert.Contains("the patch of patch_approval-1 does not apply", await bad.Content.ReadAsStringAsync(ct));
    }
}
