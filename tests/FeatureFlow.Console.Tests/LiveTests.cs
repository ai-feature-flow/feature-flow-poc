using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// Live updates (LOG 459): the console follows <c>GET /events</c> and reads again what a change touches, in place, with
/// no reload; a <c>reset</c> (the server no longer holds the changes since the console's last) reads everything again. In
/// the workspace, a frame naming any run of the tree reads the tree again.
/// </summary>
public sealed class LiveTests(BrowserFixture browser) : BrowserTest(browser)
{
    static string Row(string runId) => $"article[data-run='{runId}'][data-wait='gate-1']";

    static string StepRow(string runId, string stepId) => $".ff-tree .ff-row-step[data-run='{runId}'][data-step='{stepId}']";

    /// <summary>Completes when the page reads a response from a URL containing <paramref name="path"/> whose body holds <paramref name="text"/>; start it before the change.</summary>
    static async Task ReadAgainAsync(Microsoft.Playwright.IPage page, string path, string text)
    {
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async void Seen(object? sender, Microsoft.Playwright.IResponse r)
        {
            try
            {
                if (r.Url.Contains(path, StringComparison.Ordinal) && (await r.TextAsync()).Contains(text, StringComparison.Ordinal))
                    read.TrySetResult();
            }
            catch (Microsoft.Playwright.PlaywrightException)
            {
                // A response whose body is gone (the page moved on) is not the read waited for.
            }
        }

        page.Response += Seen;
        try
        {
            await read.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            page.Response -= Seen;
        }
    }

    [Fact]
    public async Task A_change_to_a_child_run_is_read_again_in_the_workspace_in_place()
    {
        var tree = await Engine.ProjectTreeAsync();
        var page = await OpenAsync("#/runs/" + Uri.EscapeDataString(tree.Root));
        var row = page.Locator(StepRow(tree.Tickets[0], "implement"));
        await Expect(row).ToHaveAttributeAsync("data-state", "pending");
        await Expect(page.GetByRole(Microsoft.Playwright.AriaRole.Region, new() { Name = "Needs you" }).GetByRole(Microsoft.Playwright.AriaRole.Heading)).ToHaveTextAsync("Needs you (8)");
        await EvaluateAsync<int>("window.ffMarker = 1");
        await page.GetByRole(Microsoft.Playwright.AriaRole.Button, new() { Name = "Follow current" }).ClickAsync();

        // A frame for a ticket two levels down: the tree is read again, and the root page's lists with it. Waits on that
        // read (a tree read that holds the new question), not on a time, so a slow machine does not fail it.
        var reread = ReadAgainAsync(page, "/tree", "Which currency?");
        await Engine.OpenWaitAsync(tree.Tickets[0], "question", "implement", "", "Which currency?", ["NZD", "AUD"]);
        await reread;

        await Expect(row).ToHaveAttributeAsync("data-state", "needs");
        await Expect(page.GetByRole(Microsoft.Playwright.AriaRole.Region, new() { Name = "Needs you" }).GetByRole(Microsoft.Playwright.AriaRole.Heading)).ToHaveTextAsync("Needs you (9)");
        Assert.Equal(1, await EvaluateAsync<int>("window.ffMarker"));
    }

    [Fact]
    public async Task After_a_reset_the_workspace_reads_its_tree_again()
    {
        Allow("net::ERR_");
        var tree = await Engine.ProjectTreeAsync();
        var page = await OpenAsync("#/runs/" + Uri.EscapeDataString(tree.Root));
        await Expect(page.Locator(StepRow(tree.Tickets[0], "implement"))).ToHaveAttributeAsync("data-state", "pending");
        await EvaluateAsync<int>("window.ffMarker = 1");
        await Task.Delay(1_000, Ct);

        await World.RestartAsync(() => Engine.TraceAsync(tree.Tickets[0], "started", "implement"));

        await Expect(page.Locator(StepRow(tree.Tickets[0], "implement"))).ToHaveAttributeAsync("data-state", "running", new() { Timeout = 15_000 });
        Assert.Equal(1, await EvaluateAsync<int>("window.ffMarker"));
    }

    [Fact]
    public async Task A_wait_opened_elsewhere_appears_without_a_reload()
    {
        var page = await OpenAsync();
        await Expect(page.GetByText("Nothing needs you now.")).ToBeVisibleAsync();
        await EvaluateAsync<int>("window.ffMarker = 1");

        await World.SeedRunWithWaitAsync("run-elsewhere");

        await Expect(page.Locator(Row("run-elsewhere"))).ToBeVisibleAsync(new() { Timeout = 5_000 });
        Assert.Equal(1, await EvaluateAsync<int>("window.ffMarker"));
    }

    [Fact]
    public async Task After_a_reset_the_page_reads_everything_again()
    {
        // While the API is down, the stream's reconnection is refused.
        Allow("net::ERR_");
        var page = await OpenAsync();
        await Expect(page.GetByText("Nothing needs you now.")).ToBeVisibleAsync();
        await World.SeedRunWithWaitAsync("run-before");
        await Expect(page.Locator(Row("run-before"))).ToBeVisibleAsync(new() { Timeout = 5_000 });
        await EvaluateAsync<int>("window.ffMarker = 1");
        // Let run-before's change land (a 200 ms coalesce, a 100 ms poll), so the console's last event id is that change and
        // no re-read is pending when the API stops.
        await Task.Delay(1_000, Ct);

        // A restart starts a new change feed, which holds nothing older than its start. The console reconnects with the id
        // of the last change it saw; that is older, so the feed sends reset, not the change appended while it was down.
        await World.RestartAsync(() => World.SeedRunWithWaitAsync("run-while-down"));

        await Expect(page.Locator(Row("run-while-down"))).ToBeVisibleAsync(new() { Timeout = 15_000 });
        Assert.Equal(1, await EvaluateAsync<int>("window.ffMarker"));
    }
}
