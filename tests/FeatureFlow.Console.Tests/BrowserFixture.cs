using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(FeatureFlow.Console.Tests.BrowserFixture))]

namespace FeatureFlow.Console.Tests;

/// <summary>
/// One Chromium for the test assembly (LOG 461); each test opens its own context. Locally the first run downloads
/// Chromium into the user's cache (Playwright's <c>install</c>); in CI the browser step has already installed it, so
/// this does nothing. <c>HEADED=1</c> shows the browser, slowed down, to watch a test.
/// </summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    IPlaywright? playwright;

    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var installed = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (installed != 0)
            throw new InvalidOperationException($"Playwright could not install Chromium (exit {installed}); see its output above");
        playwright = await Playwright.CreateAsync();
        var headed = Environment.GetEnvironmentVariable("HEADED") == "1";
        Browser = await playwright.Chromium.LaunchAsync(new() { Headless = !headed, SlowMo = headed ? 250 : null });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
            await Browser.DisposeAsync();
        playwright?.Dispose();
    }
}
