using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// Runs (<c>#/runs</c>, design-v2 s4.3, mock-up 06): a row per run tree, filtered by search, Mine / Everyone and Active /
/// Finished / All, with child runs listed on request; a row opens its tree's workspace.
/// </summary>
public sealed class RunListTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Row(string runId) => Page.Locator($"tr[data-run='{runId}']");

    ILocator Choice(string group, string name) =>
        Page.GetByRole(AriaRole.Group, new() { Name = group }).GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    [Fact]
    public async Task Run_trees_are_filtered_searched_and_list_their_children_on_request()
    {
        var tree = await Engine.ProjectTreeAsync();
        await Engine.RunWithAsync("done-1", status: "completed", repositories: ["acme/web"], level: "ticket", trackerIssue: "acme/web#200",
            trackerTitle: "Add DELETE /widgets/{id}", startedAt: DateTimeOffset.UtcNow.AddMinutes(-50));
        await Engine.RunWithAsync("ana-1", requester: "ana", level: "ticket", trackerIssue: "ANA-1", trackerTitle: "Someone else's");
        var page = await OpenAsync("#/runs");
        var rows = page.Locator("tr[data-run]");

        // Mine and Active: the project tree only, its children inside it.
        await Expect(Choice("Whose", "Mine")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(Choice("State", "Active")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(rows).ToHaveCountAsync(1);
        var root = Row(tree.Root);
        await Expect(root).ToContainTextAsync("project#100 Billing");
        await Expect(root).ToContainTextAsync("2 epics, 4 tickets so far");
        await Expect(root).ToContainTextAsync("needs you");
        await Expect(root).ToContainTextAsync(FakeEngine.ScenarioRepository);

        await Choice("Whose", "Everyone").ClickAsync();
        await Expect(rows).ToHaveCountAsync(2);
        await Expect(Row("ana-1")).ToContainTextAsync("ticketANA-1 Someone else's");

        await Choice("State", "Finished").ClickAsync();
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(Row("done-1")).ToContainTextAsync("ticket#200 Add DELETE /widgets/{id}");
        await Expect(Row("done-1")).ToContainTextAsync("completed");
        await Expect(Row("done-1")).ToContainTextAsync("50 m");

        await Choice("State", "All").ClickAsync();
        await Expect(rows).ToHaveCountAsync(3);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Finished" })).ToBeVisibleAsync();

        // A search finds a tree by any run in it, by ticket, title, repository or id.
        var search = page.GetByRole(AriaRole.Searchbox, new() { Name = "Search runs" });
        await search.FillAsync("pay-2");
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(root).ToBeVisibleAsync();
        await search.FillAsync("acme/web");
        await Expect(rows).ToHaveCountAsync(1);
        await Expect(Row("done-1")).ToBeVisibleAsync();
        await search.FillAsync("nothing like it");
        await Expect(rows).ToHaveCountAsync(0);
        await search.FillAsync("");

        await page.GetByLabel("list child runs too").CheckAsync();
        await Expect(rows).ToHaveCountAsync(3 + 2 + 4);
        await Expect(Row(tree.Epics[1])).ToContainTextAsync("epicpayments");
        await Expect(Row(tree.Tickets[3])).ToContainTextAsync("ticketPAY-2 Ticket PAY-2");

        await root.Locator("td").Nth(1).ClickAsync();
        await Expect(page).ToHaveURLAsync(World.ConsoleUrl + $"#/runs/{tree.Root}");
    }

    [Fact]
    public async Task Runs_counts_the_same_running_steps_and_waits_as_the_trees_pills()
    {
        // Containers (epics.children, tickets.children) and run.child steps are "running" while their branches run, but the
        // tree shows their branches and child runs in their place: neither screen counts them.
        var tree = await Engine.ProjectTreeAsync();
        await Engine.TraceAsync(tree.Tickets[1], "started", "verify.analysis");
        var page = await OpenAsync("#/runs");
        var root = Row(tree.Root);
        await Expect(root.Locator("td").Nth(2)).ToHaveTextAsync("8");
        await Expect(root.Locator("td").Nth(3)).ToHaveTextAsync("1");
        await Expect(root.Locator("td").Nth(4)).Not.ToContainTextAsync("children");

        await root.Locator("td").Nth(1).ClickAsync();
        var pills = page.GetByRole(AriaRole.Toolbar, new() { Name = "Go to" });
        await Expect(pills.GetByRole(AriaRole.Button).Filter(new() { HasText = "Needs you" })).ToHaveTextAsync("⚠ Needs you (8)");
        await Expect(pills.GetByRole(AriaRole.Button).Filter(new() { HasText = "Running" })).ToHaveTextAsync("➜ Running (1)");
    }

    [Fact]
    public async Task A_run_started_now_reaches_the_list()
    {
        await Engine.RunAsync("run-a");
        var page = await OpenAsync("#/runs");
        await Expect(Row("run-a")).ToContainTextAsync("run-a");

        await Engine.RunAsync("run-new");

        await Expect(Row("run-new")).ToBeVisibleAsync(new() { Timeout = 5_000 });
        await Row("run-new").GetByRole(AriaRole.Link).ClickAsync();
        await Expect(page).ToHaveURLAsync(World.ConsoleUrl + "#/runs/run-new");
    }
}
