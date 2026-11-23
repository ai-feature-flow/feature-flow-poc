using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The decision popups as popups (design-v2 s4.5): titled per the table, answers at the top, and closed without answering
/// by Escape or a click outside, while a click inside leaves them open.
/// </summary>
public sealed class PopupTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Dialog => Page.GetByRole(AriaRole.Dialog);

    [Fact]
    public async Task A_decision_closes_without_an_answer_on_escape_or_a_click_outside_and_stays_open_on_a_click_inside()
    {
        var s = await Engine.DecisionsAsync();
        var wait = s.Tree.Intervention;
        await OpenAsync($"#/runs/{s.Tree.Root}/waits/{wait.RunId}/{wait.WaitId}");
        await Expect(Dialog.Locator(".ff-dec")).ToBeVisibleAsync();

        // A click inside, on the body and on an answer that opens its form, leaves it open.
        await Dialog.GetByRole(AriaRole.Heading, new() { Name = "Why it stopped" }).ClickAsync();
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Grant attempts…" }).ClickAsync();
        await Expect(Dialog.GetByLabel("Grant")).ToBeVisibleAsync();

        await Page.Keyboard.PressAsync("Escape");
        await Expect(Dialog).ToHaveCountAsync(0);

        await GoAsync("#/runs");
        await GoAsync($"#/runs/{s.Tree.Root}/waits/{wait.RunId}/{wait.WaitId}");
        await Expect(Dialog.Locator(".ff-dec")).ToBeVisibleAsync();
        await Page.Mouse.ClickAsync(5, 300);
        await Expect(Dialog).ToHaveCountAsync(0);
        Assert.Empty(Engine.Answers);
    }

    [Fact]
    public async Task UseDecision_titles_each_popup_by_the_design_table()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync($"#/runs/{s.Tree.Root}");
        // The workspace opens decisions through useDecision(); open each from the page's own module, as a screen would.
        var titles = new Dictionary<ScenarioWait, string>
        {
            [s.Tree.Intervention] = "implement has used all its attempts",
            [s.Tree.Breakpoint] = "Paused before verify.tests",
            [s.Tree.Question] = "implement asks",
            [s.Patch] = "tickets.plan proposes a plan change",
            [s.Tree.Merge] = "Merge acme/api#201 into main?",
        };
        foreach (var (wait, title) in titles)
        {
            await GoAsync($"#/runs/{s.Tree.Root}/waits/{wait.RunId}/{wait.WaitId}");
            await Expect(Dialog.Locator(".ff-dec")).ToBeVisibleAsync();
            await Expect(Dialog.GetByRole(AriaRole.Heading, new() { Level = 2 })).ToHaveTextAsync(title);
            await Page.Keyboard.PressAsync("Escape");
            await GoAsync($"#/runs/{s.Tree.Root}");
        }
    }

    /// <summary>
    /// Not a check: saves a screenshot of every popup, dark, at 1440 × 900, to <c>FF_SCREENS</c> (the package's report
    /// folder). Run it on its own: <c>FF_SCREENS=… dotnet test --filter Screenshots_of_every_popup</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Screenshots_of_every_popup()
    {
        var folder = Environment.GetEnvironmentVariable("FF_SCREENS") ?? Path.Combine(Path.GetTempPath(), "ff-screens");
        Directory.CreateDirectory(folder);
        var s = await Engine.DecisionsAsync();
        await OpenAsync($"#/runs/{s.Tree.Root}");
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Dark theme" }).ClickAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        var shots = new (string Name, ScenarioWait Wait)[]
        {
            ("intervention", s.Tree.Intervention), ("triage", s.Tree.Triage), ("spec-approval", s.Spec), ("merge", s.Tree.Merge),
            ("patch-approval", s.Patch), ("breakpoint", s.Tree.Breakpoint), ("question", s.Tree.Question), ("session", s.Tree.Session),
        };
        foreach (var (name, wait) in shots)
        {
            await GoAsync($"#/runs/{s.Tree.Root}/waits/{wait.RunId}/{wait.WaitId}");
            await Expect(Dialog.Locator(".ff-dec-body")).ToBeVisibleAsync();
            await Page.WaitForTimeoutAsync(600);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, $"{name}.png") });
            await Page.Keyboard.PressAsync("Escape");
            await GoAsync($"#/runs/{s.Tree.Root}");
        }
    }
}
