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

    [Fact]
    public async Task An_artifact_popup_fills_the_window_and_follows_its_size_until_toggled_back()
    {
        await Engine.RunAsync("run-1");
        // Longer than the window, so the popup must stay the window's size and scroll inside.
        var report = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line {i}"));
        var reference = await Engine.ProduceAsync("run-1", "build", "report", report);
        await OpenAsync("#/runs/run-1/artifacts/" + Uri.EscapeDataString(reference));
        await Page.SetViewportSizeAsync(1280, 800);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Open full size" }).ClickAsync();
        var popup = Dialog;
        var fill = popup.GetByRole(AriaRole.Button, new() { Name = "Fill the window" });
        await Expect(fill).ToHaveAttributeAsync("aria-pressed", "false");
        await Expect(fill).ToHaveAttributeAsync("title", "Fill the window");
        var own = (await popup.BoundingBoxAsync())!;
        Assert.True(own.Width < 1280 && own.Height < 800);

        // From the keyboard, as well as by a click.
        await fill.FocusAsync();
        await Page.Keyboard.PressAsync("Enter");
        await Expect(fill).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(fill).ToHaveAttributeAsync("title", "Back to its own size");
        Assert.Equal((0, 0, 1280, 800), Box(await popup.BoundingBoxAsync()));
        await Page.SetViewportSizeAsync(1000, 600);
        Assert.Equal((0, 0, 1000, 600), Box(await popup.BoundingBoxAsync()));
        await popup.GetByText("line 300").ScrollIntoViewIfNeededAsync();
        await Expect(popup.GetByText("line 300")).ToBeInViewportAsync();

        await fill.ClickAsync();
        await Expect(fill).ToHaveAttributeAsync("aria-pressed", "false");
        var back = (await popup.BoundingBoxAsync())!;
        Assert.True(back.Width < 1000 && back.Height < 600);
    }

    static (int, int, int, int) Box(LocatorBoundingBoxResult? box) => ((int)box!.X, (int)box.Y, (int)Math.Round(box.Width), (int)Math.Round(box.Height));

    /// <summary>
    /// Not a check: screenshots of the tweaks after first use (docs/build/console-b1-results.md), dark at 1440 × 900, to
    /// <c>FF_SCREENS</c>: a real run's Markdown prompt formatted, in the viewer and filling the window; its source with its
    /// download; the pop-out icons; and a folded branch that held the selection.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Screenshots_of_the_tweaks_after_first_use()
    {
        var folder = Environment.GetEnvironmentVariable("FF_SCREENS") ?? Path.Combine(Path.GetTempPath(), "ff-screens");
        Directory.CreateDirectory(folder);
        var s = await Engine.DecisionsAsync();
        var prompt = File.ReadAllText(Path.Combine(Repository.Root, "tests", "FeatureFlow.Console.Tests", "data", "plan-prompt.md"));
        await Engine.ProduceAsync(s.Tree.Tickets[0], "plan.plan", "prompt:1", prompt, type: "prompt", execution: 2);
        await OpenAsync($"#/runs/{s.Tree.Root}/steps/{s.Tree.Tickets[0]}/plan.plan");
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Dark theme" }).ClickAsync();
        async Task Shot(string name)
        {
            await Page.WaitForTimeoutAsync(800);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, $"{name}.png") });
        }

        await Page.GetByRole(AriaRole.Button, new() { Name = "prompt:1", Exact = true }).ClickAsync();
        await Expect(Page.Locator(".ff-md h1").First).ToBeVisibleAsync();
        await Shot("markdown-prompt-viewer");
        await Page.GetByRole(AriaRole.Complementary, new() { Name = "Viewer" }).GetByRole(AriaRole.Button, new() { Name = "Source" }).ClickAsync();
        await Shot("markdown-prompt-source-download");
        await Page.GetByRole(AriaRole.Complementary, new() { Name = "Viewer" }).GetByRole(AriaRole.Button, new() { Name = "Formatted" }).ClickAsync();

        await Page.GetByRole(AriaRole.Button, new() { Name = "Open prompt:1 · exec 2 full size" }).ClickAsync();
        await Expect(Dialog.Locator(".ff-md h1").First).ToBeVisibleAsync();
        await Shot("popout-prompt-popup");
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Fill the window" }).ClickAsync();
        await Dialog.Locator(".ff-popup-body").EvaluateAsync("e => { e.scrollTop = e.scrollHeight; }");
        await Shot("popup-filling-window");
        await Page.Keyboard.PressAsync("Escape");

        await Page.GetByRole(AriaRole.Tab, new() { Name = "Artifacts" }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Open requirements · exec 2 full size" }).HoverAsync();
        await Shot("popout-icons-artifacts");

        await GoAsync($"#/runs/{s.Tree.Root}/waits/{s.Spec.RunId}/{s.Spec.WaitId}");
        await Expect(Dialog.Locator(".ff-dec-body")).ToBeVisibleAsync();
        await Expect(Dialog.GetByRole(AriaRole.Table, new() { Name = "Acceptance criteria" })).ToBeVisibleAsync();
        await Page.WaitForTimeoutAsync(500);
        await Dialog.GetByRole(AriaRole.List, new() { Name = "Consumed" }).EvaluateAsync("e => e.scrollIntoView({ block: 'center' })");
        await Shot("popout-icons-decision");
        await Page.Keyboard.PressAsync("Escape");

        await GoAsync($"#/runs/{s.Tree.Root}/steps/{s.Tree.Tickets[0]}/plan.plan");
        await Page.Locator($".ff-row-group[data-run='{s.Tree.Tickets[0]}']").Filter(new() { HasText = "plan" }).First
            .GetByRole(AriaRole.Button, new() { Name = "Fold plan" }).ClickAsync();
        await Shot("tree-folded-selection");
    }

    /// <summary>
    /// Not a check: saves a screenshot of every screen and popup, dark, at 1440 × 900, and the workspace and Needs me light,
    /// to <c>FF_SCREENS</c> (the package's report folder). Run it on its own:
    /// <c>FF_SCREENS=… dotnet test --filter Screenshots_of_every_screen_and_popup</c>.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Screenshots_of_every_screen_and_popup()
    {
        var folder = Environment.GetEnvironmentVariable("FF_SCREENS") ?? Path.Combine(Path.GetTempPath(), "ff-screens");
        Directory.CreateDirectory(folder);
        var s = await Engine.DecisionsAsync();
        await Engine.TraceAsync(s.Tree.Tickets[1], "started", "verify.analysis");
        await OpenAsync("#/needs");
        await Page.SetViewportSizeAsync(1440, 900);
        async Task Shot(string name)
        {
            await Page.WaitForTimeoutAsync(800);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, $"{name}.png") });
        }

        await Expect(Page.Locator("article.ff-item").First).ToBeVisibleAsync();
        await Shot("needs-me-light");
        await GoAsync($"#/runs/{s.Tree.Root}");
        await Expect(Page.Locator(".ff-run-id")).ToHaveTextAsync(s.Tree.Root);
        await Shot("workspace-root-light");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Dark theme" }).ClickAsync();
        await Expect(Page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
        await Shot("workspace-root");
        await GoAsync("#/needs");
        await Expect(Page.Locator("article.ff-item").First).ToBeVisibleAsync();
        await Shot("needs-me");
        await GoAsync("#/runs");
        await Expect(Page.Locator("tr[data-run]").First).ToBeVisibleAsync();
        await Shot("runs");

        var shots = new (string Name, ScenarioWait Wait)[]
        {
            ("intervention", s.Tree.Intervention), ("triage", s.Tree.Triage), ("spec-approval", s.Spec), ("merge", s.Tree.Merge),
            ("patch-approval", s.Patch), ("breakpoint", s.Tree.Breakpoint), ("question", s.Tree.Question), ("session", s.Tree.Session),
        };
        await GoAsync("#/needs");
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
            await Shot(name);
            await Page.Keyboard.PressAsync("Escape");
            await GoAsync("#/needs");
        }

        // A step waiting on you, and one running.
        await GoAsync($"#/runs/{s.Tree.Root}/steps/{s.Tree.Intervention.RunId}/{s.Tree.Intervention.StepId}");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Needs you" })).ToBeVisibleAsync();
        await Shot("step-waiting");
        await GoAsync($"#/runs/{s.Tree.Root}/steps/{s.Tree.Tickets[1]}/verify.analysis");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Running now" })).ToBeVisibleAsync();
        await Shot("step-running");

        // The viewer: a pinned tab and a preview tab.
        await GoAsync($"#/runs/{s.Tree.Root}/steps/{s.Tree.Tickets[0]}/plan.plan");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Artifacts" }).ClickAsync();
        var artifacts = Page.GetByRole(AriaRole.Table, new() { Name = "Artifacts" }).Locator("tbody tr");
        await artifacts.Filter(new() { HasText = "requirements" }).First.GetByRole(AriaRole.Button, new() { Name = "requirements", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Button, new() { Name = "Pin requirements · exec 2" }).ClickAsync();
        await artifacts.Filter(new() { HasText = "delivery_plan" }).First.GetByRole(AriaRole.Button, new() { Name = "delivery_plan", Exact = true }).ClickAsync();
        await Shot("viewer");

        // Steer, Edit step and the margin's breakpoint, from a step not yet started.
        await GoAsync($"#/runs/{s.Tree.Root}/steps/{s.Tree.Tickets[0]}/implement");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Steer ▾", Exact = true }).ClickAsync();
        await Page.GetByRole(AriaRole.Menuitem, new() { Name = "Pause before" }).ClickAsync();
        await Expect(Dialog).ToBeVisibleAsync();
        await Shot("steer");
        await Page.Keyboard.PressAsync("Escape");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Edit step", Exact = true }).ClickAsync();
        await Dialog.GetByLabel("Change").SelectOptionAsync("limits");
        var limits = Dialog.GetByLabel("Limits, as YAML");
        await limits.FillAsync(await limits.InputValueAsync() + "\nattempts: 4");
        await Shot("edit-step");
        await Page.Keyboard.PressAsync("Escape");
        await Page.Locator(".ff-tree .ff-row-step[data-step='implement'][data-run='" + s.Tree.Tickets[0] + "'] .ff-bp").ClickAsync();
        await Expect(Page.GetByRole(AriaRole.Form, new() { Name = "Pause before implement" })).ToBeVisibleAsync();
        await Shot("breakpoint-margin");
    }
}
