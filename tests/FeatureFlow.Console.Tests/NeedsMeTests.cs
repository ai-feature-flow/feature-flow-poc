using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>Needs me (<c>#/</c>, LOG 463): the open waits the caller may answer, grouped by run, each leading to its run.</summary>
public sealed class NeedsMeTests(BrowserFixture browser) : BrowserTest(browser)
{
    [Fact]
    public async Task A_wait_in_needs_me_links_to_its_run()
    {
        var runId = await World.StartRunAsync();
        var page = await OpenAsync();

        var row = page.Locator($"[data-run='{runId}'] tr[data-wait='gate-1']");
        await Expect(row).ToContainTextAsync("gate: m7s.go_ahead");
        await Expect(row).ToContainTextAsync("go_ahead");
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Needs me (1)" })).ToBeVisibleAsync();
        await row.GetByRole(AriaRole.Link).ClickAsync();

        await Expect(page).ToHaveURLAsync(World.ConsoleUrl + "#/runs/" + runId);
        await Expect(page.Locator(".ff-run-id")).ToHaveTextAsync(runId);
        await Expect(page.Locator(".ff-run-template")).ToHaveTextAsync("m7s:templates/decide");
    }
}
