using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The workspace's panes (design-v2 s4.4, s5): both side panes resized by dragging their inner border, the right one
/// collapsible from the top bar or its own icon, all remembered across a reload; below about 1,000 px the right pane closes
/// and the tree becomes a drawer.
/// </summary>
public sealed class PaneTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Left => Page.GetByRole(AriaRole.Complementary, new() { Name = "Tree" });

    ILocator Right => Page.GetByRole(AriaRole.Complementary, new() { Name = "Viewer" });

    Task<double> WidthAsync(ILocator pane) => pane.EvaluateAsync<double>("e => e.getBoundingClientRect().width");

    async Task DragAsync(string splitter, double by)
    {
        var handle = Page.GetByRole(AriaRole.Separator, new() { Name = splitter });
        var box = (await handle.BoundingBoxAsync())!;
        await Page.Mouse.MoveAsync(box.X + box.Width / 2, box.Y + 200);
        await Page.Mouse.DownAsync();
        await Page.Mouse.MoveAsync(box.X + box.Width / 2 + (float)by, box.Y + 200, new() { Steps = 5 });
        await Page.Mouse.UpAsync();
    }

    [Fact]
    public async Task Both_side_panes_resize_by_dragging_their_inner_border_and_keep_their_widths_across_a_reload()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync("#/runs/" + tree.Root);
        await Page.SetViewportSizeAsync(1440, 900);
        Assert.Equal(360, await WidthAsync(Left), 1);
        Assert.Equal(420, await WidthAsync(Right), 1);

        await DragAsync("Resize the tree", 100);
        await DragAsync("Resize the viewer", -80);
        Assert.Equal(460, await WidthAsync(Left), 2);
        Assert.Equal(500, await WidthAsync(Right), 2);

        await Page.ReloadAsync();
        await LiveAsync();
        await Expect(Left).ToBeVisibleAsync();
        Assert.Equal(460, await WidthAsync(Left), 2);
        Assert.Equal(500, await WidthAsync(Right), 2);
    }

    [Fact]
    public async Task The_right_pane_collapses_from_the_top_bar_or_its_own_icon_and_stays_so_across_a_reload()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync("#/runs/" + tree.Root);
        var toggle = Page.GetByRole(AriaRole.Button, new() { Name = "Right pane" });
        await Expect(Right).ToBeVisibleAsync();

        await toggle.ClickAsync();
        await Expect(Right).ToHaveCountAsync(0);
        await Expect(toggle).ToHaveAttributeAsync("aria-pressed", "false");
        await Page.ReloadAsync();
        await LiveAsync();
        await Expect(Page.GetByRole(AriaRole.Tree)).ToBeVisibleAsync();
        await Expect(Right).ToHaveCountAsync(0);

        await toggle.ClickAsync();
        await Expect(Right).ToBeVisibleAsync();
        await Right.GetByRole(AriaRole.Button, new() { Name = "Close the viewer" }).ClickAsync();
        await Expect(Right).ToHaveCountAsync(0);
        await Expect(toggle).ToHaveAttributeAsync("aria-pressed", "false");
    }

    [Fact]
    public async Task In_a_narrow_window_the_right_pane_closes_and_the_tree_is_a_drawer()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync("#/runs/" + tree.Root);
        await Page.SetViewportSizeAsync(900, 800);

        await Expect(Right).ToHaveCountAsync(0);
        await Expect(Left).ToBeHiddenAsync();
        var drawer = Page.GetByRole(AriaRole.Button, new() { Name = "Tree", Exact = true });
        await drawer.ClickAsync();
        await Expect(Left).ToBeVisibleAsync();
        await Expect(drawer).ToHaveAttributeAsync("aria-expanded", "true");

        // Choosing a row closes the drawer and shows it.
        await Page.Locator($".ff-row-run[data-run='{tree.Epics[0]}']").ClickAsync();
        await Expect(Left).ToBeHiddenAsync();
        await Expect(Page.Locator(".ff-run-id")).ToHaveTextAsync(tree.Epics[0]);

        // Wide again: three panes; the width the window had before is not a preference, so nothing was remembered.
        await Page.SetViewportSizeAsync(1440, 900);
        await Expect(Left).ToBeVisibleAsync();
        await Expect(Right).ToBeVisibleAsync();
    }
}
