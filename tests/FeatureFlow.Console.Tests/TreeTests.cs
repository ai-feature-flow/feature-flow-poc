using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The workspace's tree (design-v2 s4.4; plan s5.3) over <see cref="FakeEngine.ProjectTreeAsync"/>'s run tree: child runs
/// nested under the steps that started them, roll-ups, placeholders, margin markers, <c>used/budget</c>, sticky enclosing rows,
/// the navigator pills, breakpoints from the margin, and Follow current.
/// </summary>
public sealed class TreeTests(BrowserFixture browser) : BrowserTest(browser)
{
    static string P(string part) => Uri.EscapeDataString(part);

    ILocator Tree => Page.GetByRole(AriaRole.Tree, new() { Name = "Run tree" });

    ILocator Run(string runId) => Tree.Locator($".ff-row-run[data-run='{runId}']");

    ILocator Step(string runId, string stepId) => Tree.Locator($".ff-row-step[data-run='{runId}'][data-step='{stepId}']");

    ILocator Selected => Tree.Locator(".ff-row-selected");

    ILocator Pill(string name) => Page.GetByRole(AriaRole.Toolbar, new() { Name = "Go to" }).GetByRole(AriaRole.Button).Filter(new() { HasText = name });

    [Fact]
    public async Task Child_runs_nest_under_the_steps_that_started_them_with_markers_roll_ups_and_attempts()
    {
        var tree = await Engine.ProjectTreeAsync();
        // A step of ticket 2 running, and ticket 3's implementer out of attempts.
        await Engine.TraceAsync(tree.Tickets[1], "started", "verify.analysis");
        await Engine.StepStateAsync(tree.Tickets[2], "implement", executions: 3, attemptsUsed: 3, attemptsAllowed: 3);
        await OpenAsync("#/runs/" + P(tree.Root));

        // The epics sit in the root's epics fragment, the tickets in each epic's tickets fragment, each run with ↗.
        var epics = Tree.Locator(".ff-row-group").Filter(new() { HasText = "epics" }).First;
        await Expect(epics).ToHaveAttributeAsync("aria-level", "2");
        await Expect(epics).ToContainTextAsync("2 epics: 2 needs you");
        await Expect(Run(tree.Epics[0])).ToHaveAttributeAsync("aria-level", "3");
        await Expect(Run(tree.Epics[0])).ToContainTextAsync("epic");
        await Expect(Run(tree.Epics[0])).ToContainTextAsync("invoices");
        await Expect(Run(tree.Epics[0]).GetByRole(AriaRole.Link, new() { Name = "Open invoices on its own" })).ToHaveAttributeAsync("href", "#/runs/" + P(tree.Epics[0]));
        await Expect(Run(tree.Tickets[0])).ToHaveAttributeAsync("aria-level", "5");
        await Expect(Run(tree.Tickets[0])).ToContainTextAsync("Ticket INV-1");
        await Expect(Tree.Locator(".ff-row-group[data-run='" + tree.Epics[0] + "']").Filter(new() { HasText = "tickets" }).First)
            .ToContainTextAsync("2 tickets: 2 needs you");

        // Waiting on you: the triangle and a yellow row; running: the arrow; done, pending: no marker.
        var waiting = Step(tree.SpecApproval.RunId, "approve_spec");
        await Expect(waiting).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("ff-row-needs"));
        await Expect(waiting.GetByRole(AriaRole.Img, new() { Name = "waiting on you" })).ToBeVisibleAsync();
        var running = Step(tree.Tickets[1], "verify.analysis");
        await Expect(running.GetByRole(AriaRole.Img, new() { Name = "running" })).ToBeVisibleAsync();
        await Expect(running).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("ff-row-needs"));
        await Expect(Step(tree.Tickets[0], "approve_spec").GetByRole(AriaRole.Img)).ToHaveCountAsync(1);
        await Expect(Step(tree.Root, "check_requirements").GetByRole(AriaRole.Img)).ToHaveCountAsync(0);

        // used/budget, red when spent.
        await Expect(Step(tree.Tickets[2], "implement").Locator(".ff-used")).ToHaveTextAsync("3/3");
        await Expect(Step(tree.Tickets[2], "implement").Locator(".ff-used")).ToHaveClassAsync("ff-used ff-used-spent");
        await Tree.Locator($".ff-row-group[data-run='{tree.Tickets[0]}']").Filter(new() { HasText = "plan" }).First.GetByRole(AriaRole.Button, new() { Name = "Open plan" }).ClickAsync();
        await Expect(Step(tree.Tickets[0], "plan.plan").Locator(".ff-used")).ToHaveTextAsync("1/3");
        await Expect(Step(tree.Tickets[0], "plan.plan").Locator(".ff-used")).ToHaveClassAsync("ff-used");

        // A fragment where nothing happens is folded, with a roll-up of its steps; it opens on ▸.
        var deliver = Tree.Locator($".ff-row-group[data-run='{tree.Tickets[0]}']").Filter(new() { HasText = "deliver" }).First;
        await Expect(deliver).ToHaveAttributeAsync("aria-expanded", "false");
        await Expect(deliver).ToContainTextAsync("7 steps");
        await deliver.GetByRole(AriaRole.Button, new() { Name = "Open deliver" }).ClickAsync();
        await Expect(Step(tree.Tickets[0], "deliver.merge")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Parts_not_yet_planned_show_as_grey_placeholders()
    {
        // An epic started through the API has only its first revision: its tickets are not planned yet.
        var epic = await World.StartRunAsync("std:templates/epic_delivery", new Dictionary<string, string> { ["epic_name"] = "\"search\"" }, ["acme/api"]);
        await OpenAsync("#/runs/" + P(epic));
        await Tree.Locator($".ff-row-group[data-run='{epic}']").Filter(new() { HasText = "tickets" }).GetByRole(AriaRole.Button, new() { Name = "Open tickets" }).ClickAsync();

        var ghost = Tree.Locator($".ff-row-ghost[data-run='{epic}']");
        await Expect(ghost).ToHaveTextAsync($"·not planned yet: tickets appear once {epic} plans them");
        await Expect(ghost.Locator(".ff-lbl")).ToHaveCSSAsync("font-style", "italic");
        // A placeholder is not a step: a click selects nothing.
        await ghost.ClickAsync();
        await Expect(Selected).ToHaveAttributeAsync("data-run", epic);
        await Expect(Selected).Not.ToHaveAttributeAsync("data-step", new System.Text.RegularExpressions.Regex("."));
    }

    [Fact]
    public async Task Enclosing_rows_stay_pinned_at_the_top_while_scrolled_up_to_four_deep()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync("#/runs/" + P(tree.Root));
        var box = Page.Locator(".ff-treebox");

        // Scroll until ticket 2's rows are at the top: the root, epics, epic 1 and its tickets stay pinned, ticket 1 (fifth) does not.
        var target = Step(tree.Tickets[1], "approve_spec");
        await target.ScrollIntoViewIfNeededAsync();
        await box.EvaluateAsync("(b, r) => b.scrollTop += r.getBoundingClientRect().top - b.getBoundingClientRect().top - 200", await target.ElementHandleAsync());
        var top = await box.EvaluateAsync<double>("b => b.getBoundingClientRect().top");
        Assert.True(await box.EvaluateAsync<double>("b => b.scrollTop") > 400);
        async Task<double> At(ILocator row) => await row.EvaluateAsync<double>("r => r.getBoundingClientRect().top") - top;
        Assert.Equal(0, await At(Run(tree.Root)), 1);
        Assert.Equal(34, await At(Tree.Locator(".ff-row-group").Filter(new() { HasText = "epics" }).First), 1);
        Assert.Equal(68, await At(Run(tree.Epics[0])), 1);
        Assert.Equal(102, await At(Tree.Locator($".ff-row-group[data-run='{tree.Epics[0]}']").Filter(new() { HasText = "tickets" }).First), 1);
        Assert.True(await At(Run(tree.Tickets[0])) < 0, "a fifth enclosing row is not pinned");

        // A pinned row is still a row: a click selects it.
        await Run(tree.Epics[0]).ClickAsync(new() { Position = new() { X = 200, Y = 17 } });
        await Expect(Selected).ToHaveAttributeAsync("data-run", tree.Epics[0]);
        await Expect(Page.Locator(".ff-run-id")).ToHaveTextAsync(tree.Epics[0]);
    }

    [Fact]
    public async Task The_pills_go_through_their_steps_in_tree_order_and_return_to_counts()
    {
        var tree = await Engine.ProjectTreeAsync();
        await Engine.TraceAsync(tree.Tickets[1], "started", "verify.analysis");
        await OpenAsync("#/runs/" + P(tree.Root));
        // The open decisions in tree order: the project's session, epic 1's patch approval, ticket 1, ticket 2, then in epic 2
        // ticket 3, ticket 4's question and its triage, and last epic 2's own breakpoint before verify (after its tickets).
        (string Run, string Step)[] order =
        [
            (tree.Session.RunId, tree.Session.StepId), (tree.PatchApproval.RunId, tree.PatchApproval.StepId), (tree.SpecApproval.RunId, tree.SpecApproval.StepId),
            (tree.Merge.RunId, tree.Merge.StepId), (tree.Intervention.RunId, tree.Intervention.StepId), (tree.Question.RunId, tree.Question.StepId),
            (tree.Triage.RunId, tree.Triage.StepId), (tree.Breakpoint.RunId, tree.Breakpoint.StepId),
        ];
        var needs = Pill("Needs you");
        await Expect(needs).ToHaveTextAsync($"⚠ Needs you ({order.Length})");

        for (var i = 0; i < order.Length; i++)
        {
            await needs.ClickAsync();
            await Expect(needs).ToHaveTextAsync($"⚠ Needs you ({i + 1}/{order.Length})");
            await Expect(Selected).ToHaveAttributeAsync("data-run", order[i].Run);
            await Expect(Selected).ToHaveAttributeAsync("data-step", order[i].Step);
            await Expect(Selected).ToBeInViewportAsync();
            await Expect(Page.Locator(".ff-run-id")).ToHaveTextAsync(order[i].Run);
        }

        await needs.ClickAsync();
        await Expect(needs).ToHaveTextAsync($"⚠ Needs you (1/{order.Length})");
        await Expect(Selected).ToHaveAttributeAsync("data-step", order[0].Step);

        // Running goes to the one running step; selecting any other row returns the pills to their counts.
        var running = Pill("Running");
        await running.ClickAsync();
        await Expect(running).ToHaveTextAsync("➜ Running (1/1)");
        await Expect(needs).ToHaveTextAsync($"⚠ Needs you ({order.Length})");
        await Expect(Selected).ToHaveAttributeAsync("data-step", "verify.analysis");
        await Step(tree.Root, "approve").ClickAsync();
        await Expect(running).ToHaveTextAsync("➜ Running (1)");

        // Problems: the intervention's step (exhausted, its attempts spent).
        var problems = Pill("Problems");
        await Expect(problems).ToHaveTextAsync("Problems (1)");
        await problems.ClickAsync();
        await Expect(problems).ToHaveTextAsync("Problems (1/1)");
        await Expect(Selected).ToHaveAttributeAsync("data-run", tree.Intervention.RunId);
    }

    [Fact]
    public async Task A_breakpoint_is_set_and_cleared_by_clicking_the_margin()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync("#/runs/" + P(tree.Root));
        var row = Step(tree.Tickets[0], "implement");
        var margin = row.GetByRole(AriaRole.Button, new() { Name = "Breakpoint before implement" });

        await Expect(margin).ToHaveAttributeAsync("aria-pressed", "false");
        await margin.ClickAsync();
        await Expect(margin).ToHaveAttributeAsync("aria-pressed", "true", new() { Timeout = 5_000 });
        await Expect(margin).ToHaveCSSAsync("background-color", new System.Text.RegularExpressions.Regex("rgb\\((191, 97, 106|217, 21, 21)\\)"));
        // The click sets the breakpoint only: the step is not selected.
        await Expect(row).ToHaveAttributeAsync("aria-selected", "false");

        await margin.ClickAsync();
        await Expect(margin).ToHaveAttributeAsync("aria-pressed", "false", new() { Timeout = 5_000 });
        var overrides = Engine.Overrides.Where(o => o.Key == "breakpoint").ToList();
        Assert.Equal(["true", "false"], overrides.Select(o => o.Change["set"]));
        Assert.All(overrides, o => Assert.Equal("implement", o.StepId));

        // A step waiting has no margin to set one: the core refuses a breakpoint on it.
        await Expect(Step(tree.SpecApproval.RunId, "approve_spec").GetByRole(AriaRole.Button, new() { Name = "Breakpoint before approve_spec" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Follow_current_moves_the_selection_to_the_latest_change_and_can_be_turned_off()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync("#/runs/" + P(tree.Root));
        var follow = Page.GetByRole(AriaRole.Button, new() { Name = "Follow current" });
        await Expect(follow).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Selected).ToHaveAttributeAsync("data-run", tree.Root);

        await Engine.TraceAsync(tree.Tickets[1], "started", "verify.analysis");
        await Expect(Selected).ToHaveAttributeAsync("data-step", "verify.analysis", new() { Timeout = 5_000 });
        await Expect(Selected).ToHaveAttributeAsync("data-run", tree.Tickets[1]);
        await Expect(Page.Locator(".ff-run-id")).ToHaveTextAsync(tree.Tickets[1]);

        await follow.ClickAsync();
        await Expect(follow).ToHaveAttributeAsync("aria-pressed", "false");
        await Engine.TraceAsync(tree.Tickets[0], "started", "implement");
        await Expect(Step(tree.Tickets[0], "implement").GetByRole(AriaRole.Img, new() { Name = "running" })).ToBeVisibleAsync(new() { Timeout = 5_000 });
        await Expect(Selected).ToHaveAttributeAsync("data-step", "verify.analysis");
    }
}
