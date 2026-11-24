using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// Policy in force (design-v2 s4.4, Run actions ▾), a popup over <c>GET /runs/{id}/policy</c>: the run's decision types with
/// their steps and who answers them, the overrides with who may use each and whether you may, and the attempt limits.
/// </summary>
public sealed class PolicyTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Middle => Page.GetByRole(AriaRole.Region, new() { Name = "Selected" });

    [Fact]
    public async Task Policy_in_force_shows_who_answers_each_decision_who_may_override_and_the_attempt_limits()
    {
        var s = await Engine.ReviewScenarioAsync();
        await OpenAsync($"#/runs/{s.RunId}");

        await Middle.GetByRole(AriaRole.Button, new() { Name = "Run actions ▾" }).ClickAsync();
        var item = Page.GetByRole(AriaRole.Menuitem).Filter(new() { HasText = "Policy in force" });
        await Expect(item).ToBeEnabledAsync();
        await item.ClickAsync();

        var popup = Popup($"Policy in force for {s.RunId}");
        await Expect(popup).ToBeVisibleAsync();
        // The merge gate is one of the plan's decisions, with the roles that answer it.
        var merge = popup.Locator("tr[data-decision]").Filter(new() { HasText = "deliver.merge" });
        await Expect(merge).ToHaveCountAsync(1);
        await Expect(merge.Locator("td").Nth(2)).Not.ToBeEmptyAsync();
        // Every override key, with whether you may use it: on loopback with no sign-in you may abort.
        var abort = popup.Locator("tr[data-override='abort']");
        await Expect(abort.Locator("td").Nth(2)).ToHaveTextAsync("may");
        await Expect(popup.Locator("tr[data-override='go_to']")).ToHaveCountAsync(1);
        await Expect(popup.GetByRole(AriaRole.Heading, new() { Name = "Attempts" })).ToBeVisibleAsync();
        await Expect(popup).ToContainTextAsync("A step's default attempts");
    }
}
