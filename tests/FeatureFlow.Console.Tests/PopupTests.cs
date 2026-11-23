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
    public async Task Each_popup_opened_through_useDecision_is_titled_by_the_design_table()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync("#/needs");
        // Needs me opens each decision through useDecision() (Review and answer).
        var titles = new Dictionary<ScenarioWait, string>
        {
            [s.Tree.Intervention] = "verify.fix.verify_fix has used all its attempts",
            [s.Tree.Breakpoint] = "Paused before verify.tests",
            [s.Patch] = "tickets.plan proposes a plan change",
            [s.Tree.Merge] = "Merge acme/api#201 into main?",
            [s.Tree.Triage] = "Triage 1 finding the triager could not clear",
        };
        foreach (var (wait, title) in titles)
        {
            var item = Page.Locator($"article.ff-item[data-run='{wait.RunId}'][data-wait='{wait.WaitId}']");
            await item.GetByRole(AriaRole.Button, new() { Name = "Review and answer" }).ClickAsync();
            await Expect(Dialog.Locator(".ff-dec")).ToBeVisibleAsync();
            await Expect(Dialog.GetByRole(AriaRole.Heading, new() { Level = 2 })).ToHaveTextAsync(title);
            await Page.Keyboard.PressAsync("Escape");
            await Expect(Dialog).ToHaveCountAsync(0);
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
        await OpenAsync("#/needs");
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
            var item = Page.Locator($"article.ff-item[data-run='{wait.RunId}'][data-wait='{wait.WaitId}']");
            var review = item.GetByRole(AriaRole.Button, new() { Name = "Review and answer" });
            // A question is answered inline in Needs me; its popup opens from its wait's route.
            if (await review.CountAsync() > 0)
                await review.ClickAsync();
            else
                await GoAsync($"#/runs/{s.Tree.Root}/waits/{wait.RunId}/{wait.WaitId}");
            await Expect(Dialog.Locator(".ff-dec-body")).ToBeVisibleAsync();
            await Page.WaitForTimeoutAsync(800);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, $"{name}.png") });
            await Page.Keyboard.PressAsync("Escape");
            await GoAsync("#/needs");
        }

        // A steer, from the step's detail.
        await GoAsync($"#/runs/{s.Tree.Root}/steps/{s.Tree.Tickets[0]}/implement");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Steer ▾", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = "Pause before" }).ClickAsync();
        await Expect(Dialog).ToBeVisibleAsync();
        await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, "steer.png") });
    }
}
