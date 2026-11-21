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

    [Fact]
    public async Task A_wait_found_in_needs_me_is_answered_with_a_reason()
    {
        var runId = await World.StartRunAsync();
        var page = await OpenAsync();
        await page.Locator($"[data-run='{runId}'] tr[data-wait='gate-1']").GetByRole(AriaRole.Link).ClickAsync();
        await EvaluateAsync<int>("window.ffMarker = 1");

        var form = page.GetByRole(AriaRole.Region, new() { Name = "Wait gate-1" });
        var approved = form.GetByRole(AriaRole.Button, new() { Name = "approved", Exact = true });
        await Expect(approved).ToBeDisabledAsync();
        await form.GetByLabel("Reason").FillAsync("the tests pass");
        await approved.ClickAsync();

        await Expect(form).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        await page.GetByRole(AriaRole.Link, new() { Name = "Needs me" }).ClickAsync();
        await Expect(page.GetByText("Nothing needs you now.")).ToBeVisibleAsync();
        Assert.Equal(1, await EvaluateAsync<int>("window.ffMarker"));
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal(("gate-1", "approved", "the tests pass"), (answer.WaitId, answer.Outcome, answer.Reason));
    }
}
