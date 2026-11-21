using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>The run list (<c>#/runs</c>): every run the caller may read, each leading to its page.</summary>
public sealed class RunListTests(BrowserFixture browser) : BrowserTest(browser)
{
    [Fact]
    public async Task The_run_list_links_each_run()
    {
        await Engine.RunAsync("run-a", requester: "ana");
        await Engine.RunAsync("run b", "m7s:templates/other", "completed", "ben");

        var page = await OpenAsync("#/runs");

        var a = page.Locator("tr[data-run='run-a']");
        var b = page.Locator("tr[data-run='run b']");
        await Expect(a).ToContainTextAsync("m7s:templates/decide");
        await Expect(a).ToContainTextAsync("running");
        await Expect(a).ToContainTextAsync("ana");
        await Expect(b).ToContainTextAsync("m7s:templates/other");
        await Expect(b).ToContainTextAsync("completed");
        await Expect(b).ToContainTextAsync("ben");

        await b.GetByRole(AriaRole.Link, new() { Name = "run b" }).ClickAsync();
        await Expect(page.Locator(".ff-run-id")).ToHaveTextAsync("run b");
        Assert.Equal(World.ConsoleUrl + "#/runs/run%20b", page.Url);

        // A run started now reaches the list over GET /events.
        await page.GoBackAsync();
        await Engine.RunAsync("run-new");
        await Expect(page.Locator("tr[data-run='run-new']")).ToBeVisibleAsync(new() { Timeout = 5_000 });
    }
}
