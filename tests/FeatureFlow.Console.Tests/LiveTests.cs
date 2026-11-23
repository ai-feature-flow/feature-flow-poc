using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// Live updates (LOG 459): the console follows <c>GET /events</c> and reads again what a change touches, in place, with
/// no reload; a <c>reset</c> (the server no longer holds the changes since the console's last) reads everything again.
/// </summary>
public sealed class LiveTests(BrowserFixture browser) : BrowserTest(browser)
{
    static string Row(string runId) => $"article[data-run='{runId}'][data-wait='gate-1']";

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
