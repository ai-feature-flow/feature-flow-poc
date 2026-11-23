using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The workspace's middle pane (design-v2 s4.4; mock-ups 02 and 05) over <see cref="FakeEngine.ProjectTreeAsync"/>'s run tree:
/// the root page (header, facts, Needs you in tree order whose titles select the step, Progress), a child run's page, a step's
/// detail (path, Needs you card, Running now card, four boxes, tabs), and the routes that select into it.
/// </summary>
public sealed class WorkspaceTests(BrowserFixture browser) : BrowserTest(browser)
{
    static string P(string part) => Uri.EscapeDataString(part);

    ILocator Selected => Page.Locator(".ff-tree .ff-row-selected");

    ILocator Middle => Page.GetByRole(AriaRole.Region, new() { Name = "Selected" });

    [Fact]
    public async Task The_root_page_shows_the_trees_facts_needs_in_tree_order_and_progress()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync("#/runs/" + P(tree.Root));

        await Expect(Middle.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToContainTextAsync("Billing");
        await Expect(Middle.Locator(".ff-runhdr .ff-kind")).ToHaveTextAsync("project");
        var facts = Middle.GetByLabel("Facts");
        await Expect(facts.GetByRole(AriaRole.Link, new() { Name = "acme/api#100 ↗" })).ToHaveAttributeAsync("href", "https://github.com/acme/api/issues/100");
        await Expect(facts.Locator(".ff-run-template")).ToHaveTextAsync("std:templates/project_delivery");
        await Expect(facts.Locator(".ff-run-id")).ToHaveTextAsync(tree.Root);
        await Expect(facts).ToContainTextAsync("project_name: \"billing\"");
        await Expect(facts).ToContainTextAsync("0 running · 8 need you");

        var needs = Middle.GetByRole(AriaRole.Region, new() { Name = "Needs you" });
        await Expect(needs.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Needs you (8)");
        var titles = needs.Locator(".ff-needlist-title");
        await Expect(titles).ToHaveCountAsync(8);
        await Expect(titles.Nth(0)).ToHaveTextAsync("Your planning session for acme/api#100 Billing");
        await Expect(titles.Nth(2)).ToHaveTextAsync("Approve the plan for INV-1 Ticket INV-1");
        await Expect(titles.Nth(7)).ToHaveTextAsync("Paused before verify.tests");
        await Expect(needs.Locator(".ff-crumbs").Nth(2)).ToHaveTextAsync("Billing › epics › invoices › tickets › Ticket INV-1 › approve spec");
        await Expect(needs.Locator(".ff-needlist-item").Nth(5).GetByRole(AriaRole.Button)).ToHaveTextAsync("Answer");

        var progress = Middle.GetByRole(AriaRole.Region, new() { Name = "Progress" }).Locator("tbody tr");
        await Expect(progress).ToHaveCountAsync(6);
        await Expect(progress.Nth(0)).ToContainTextAsync("invoices");
        await Expect(progress.Nth(1)).ToContainTextAsync("Ticket INV-1");
        await Expect(progress.Nth(1)).ToContainTextAsync("approve spec");
        await Expect(progress.Nth(1)).ToContainTextAsync("needs you");
        await Expect(progress.Nth(1).Locator("td").First).ToHaveClassAsync("ff-board-indent");
        await Expect(progress.Nth(0).Locator("td").First).Not.ToHaveClassAsync("ff-board-indent");
    }

    [Fact]
    public async Task A_title_under_needs_you_selects_its_step_in_the_tree_and_shows_it()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync("#/runs/" + P(tree.Root));

        await Middle.GetByRole(AriaRole.Link, new() { Name = "Approve the plan for INV-1 Ticket INV-1" }).ClickAsync();

        await Expect(Selected).ToHaveAttributeAsync("data-run", tree.SpecApproval.RunId);
        await Expect(Selected).ToHaveAttributeAsync("data-step", "approve_spec");
        await Expect(Selected).ToBeInViewportAsync();
        await Expect(Middle.Locator(".ff-crumbs")).ToHaveTextAsync("Billing › epics › invoices › tickets › Ticket INV-1");
        await Expect(Middle.GetByRole(AriaRole.Heading, new() { Level = 1 })).ToHaveTextAsync("approve spec");
        // The address names the step, so a reload keeps it; no navigation happened (an open popup would stay open).
        Assert.EndsWith($"#/runs/{P(tree.Root)}/steps/{P(tree.SpecApproval.RunId)}/approve_spec", Page.Url);
        await Page.ReloadAsync();
        await LiveAsync();
        await Expect(Selected).ToHaveAttributeAsync("data-step", "approve_spec");
    }

    [Fact]
    public async Task A_waiting_step_shows_why_first_and_review_and_answer_opens_its_decision()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync($"#/runs/{P(tree.Root)}/steps/{P(tree.Intervention.RunId)}/{P(tree.Intervention.StepId)}");

        // Run #198's shape: the verifier used its attempts, sent back by record_fixes each time; one "Needs you" in the heading.
        var card = Middle.GetByRole(AriaRole.Region, new() { Name = "Needs you" });
        await Expect(card.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Needs you: verify.fix.verify_fix has used all its attempts");
        await Expect(card.Locator(".ff-why")).ToContainTextAsync("Why. attempts exhausted (F-1 declined, as before). It last said: “F-1 declined, as before”. Sent back 3 times");
        await Expect(card.Locator(".ff-why")).ToContainTextAsync("record fixes (invalid): verify_fix proposed state declined for F-1");
        await Expect(Middle.GetByRole(AriaRole.Region, new() { Name = "Running now" })).ToHaveCountAsync(0);

        var step = Middle.GetByRole(AriaRole.Region, new() { Name = "Step verify.fix.verify_fix" });
        await Expect(step.Locator(".ff-glance > div")).ToHaveCountAsync(4);
        await Expect(step.Locator(".ff-glance > div").Nth(0)).ToContainTextAsync("Waiting: intervention");
        await Expect(step.Locator(".ff-glance > div").Nth(1)).ToContainTextAsync("3 / 3");
        await Expect(step.Locator(".ff-glance > div").Nth(3)).ToContainTextAsync("reviewer");
        // Its three executions, from the timeline's starts.
        await Expect(step.Locator("table[aria-label='Executions'] tbody tr")).ToHaveCountAsync(3);
        await Expect(step.GetByRole(AriaRole.Button, new() { Name = "Steer ▾" })).ToBeVisibleAsync();
        await Expect(step.GetByRole(AriaRole.Tab, new() { Name = "Overview" })).ToHaveAttributeAsync("aria-selected", "true");
        foreach (var later in new[] { "Activity", "Findings", "Events" })
        {
            await Expect(step.GetByRole(AriaRole.Tab, new() { Name = later })).ToBeDisabledAsync();
            await Expect(step.GetByRole(AriaRole.Tab, new() { Name = later })).ToHaveAttributeAsync("title", "in B2");
        }

        await card.GetByRole(AriaRole.Button, new() { Name = "Review and answer" }).ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Dialog)).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("verify.fix.verify_fix has used all its attempts");
    }

    [Fact]
    public async Task A_needs_you_card_whose_title_says_it_needs_you_is_not_prefixed_again()
    {
        var tree = await Engine.ProjectTreeAsync();
        await Engine.OpenInterventionAsync(tree.Tickets[0], "implement",
            new FeatureFlow.Providers.Abstractions.WaitCause("implement", "blocked", "blocked escalates: the spec contradicts itself", "the spec contradicts itself", 1, 3, []));
        await OpenAsync($"#/runs/{P(tree.Root)}/steps/{P(tree.Tickets[0])}/implement");

        var heading = Middle.GetByRole(AriaRole.Region, new() { Name = "Needs you" }).GetByRole(AriaRole.Heading);
        await Expect(heading).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^implement needs you"));
    }

    [Fact]
    public async Task A_running_step_shows_running_now_and_its_executions()
    {
        var tree = await Engine.ProjectTreeAsync();
        var reference = await Engine.ProduceAsync(tree.Tickets[1], "verify.analysis", "analysis", "schema: 1\nfindings: []\n");
        await Engine.TraceAsync(tree.Tickets[1], "started", "verify.analysis");
        await OpenAsync($"#/runs/{P(tree.Root)}/steps/{P(tree.Tickets[1])}/verify.analysis");

        await Expect(Middle.GetByRole(AriaRole.Region, new() { Name = "Running now" })).ToContainTextAsync("Action analysis.run, execution 1");
        await Expect(Middle.GetByRole(AriaRole.Region, new() { Name = "Needs you" })).ToHaveCountAsync(0);
        var executions = Middle.GetByRole(AriaRole.Table, new() { Name = "Executions" }).Locator("tbody tr");
        await Expect(executions).ToHaveCountAsync(1);
        await Expect(executions.First).ToContainTextAsync("running");

        // Its output opens in the viewer.
        await executions.First.GetByRole(AriaRole.Button, new() { Name = "output" }).ClickAsync();
        await Expect(Page.Locator(".ff-right .ff-viewer-name")).ToHaveTextAsync("analysis");
        Assert.NotEmpty(reference);
    }

    [Fact]
    public async Task A_child_runs_route_opens_its_roots_tree_with_the_child_selected_and_its_page()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync("#/runs/" + P(tree.Epics[1]));

        await Expect(Page.Locator(".ff-tree .ff-row-run").First).ToHaveAttributeAsync("data-run", tree.Root);
        await Expect(Selected).ToHaveAttributeAsync("data-run", tree.Epics[1]);
        await Expect(Middle.Locator(".ff-run-id")).ToHaveTextAsync(tree.Epics[1]);
        await Expect(Middle.Locator(".ff-run-template")).ToHaveTextAsync("std:templates/epic_delivery");
        // Its own needs, in tree order (its tickets', then its own breakpoint), and its own tickets' progress.
        var needs = Middle.GetByRole(AriaRole.Region, new() { Name = "Needs you" });
        await Expect(needs.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Needs you (4)");
        await Expect(needs.Locator(".ff-needlist-title").Last).ToHaveTextAsync("Paused before verify.tests");
        await Expect(Middle.GetByRole(AriaRole.Region, new() { Name = "Progress" }).Locator("tbody tr")).ToHaveCountAsync(2);

        // A click on the root's row comes back to the root's page.
        await Page.Locator(".ff-tree .ff-row-run").First.ClickAsync();
        await Expect(Middle.Locator(".ff-run-id")).ToHaveTextAsync(tree.Root);
        Assert.EndsWith("#/runs/" + P(tree.Root), Page.Url);
    }

    [Fact]
    public async Task A_waits_route_selects_its_step_and_opens_its_decision()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync($"#/runs/{P(tree.Root)}/waits/{P(tree.Question.RunId)}/{P(tree.Question.WaitId)}");

        await Expect(Page.GetByRole(AriaRole.Dialog)).ToContainTextAsync("Are XLSX exports in scope, or CSV only?");
        await Expect(Selected).ToHaveAttributeAsync("data-run", tree.Question.RunId);
        await Expect(Selected).ToHaveAttributeAsync("data-step", "implement");
    }
}
