using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// Sign-in (LOG 458): the console asks <c>GET /me</c> without a token whether to show the token form. Under
/// <c>local_token</c> it does, and every request after carries the token, the change stream's included (LOG 459).
/// </summary>
public sealed class SignInTests(BrowserFixture browser) : BrowserTest(browser)
{
    [Fact]
    public async Task With_the_local_token_the_console_asks_for_it_and_receives_events()
    {
        UseWorld("local_token");
        var page = await OpenAsync();
        var token = page.GetByLabel("API token");
        var signIn = page.GetByRole(AriaRole.Button, new() { Name = "Sign in" });

        await token.FillAsync("wrong");
        await signIn.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("That token was refused.");

        await token.FillAsync(ConsoleWorld.Token);
        await signIn.ClickAsync();
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Needs me" })).ToBeVisibleAsync();
        await Expect(page.GetByText("Nothing needs you now.")).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out" })).ToBeVisibleAsync();
        await LiveAsync();

        // Appended now, so it reaches the page only over GET /events, which must carry the token.
        await World.SeedRunWithWaitAsync("run-signed-in");
        await Expect(page.Locator("[data-run='run-signed-in'] tr[data-wait='gate-1']")).ToBeVisibleAsync(new() { Timeout = 5_000 });
    }

    [Fact]
    public async Task With_no_sign_in_the_console_asks_for_no_token()
    {
        var page = await OpenAsync();

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Needs me" })).ToBeVisibleAsync();
        await Expect(page.Locator(".ff-actor")).ToHaveTextAsync($"{ConsoleWorld.Actor} (no sign-in)");
        await Expect(page.GetByLabel("API token")).ToHaveCountAsync(0);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Sign out" })).ToHaveCountAsync(0);
    }
}
