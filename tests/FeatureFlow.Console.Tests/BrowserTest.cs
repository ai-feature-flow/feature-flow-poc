using Microsoft.Playwright;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// A test of the console in a browser: a <see cref="ConsoleWorld"/> (by default with no sign-in; call <see cref="UseWorld"/>
/// first for another mode) and a page in a fresh browser context. The test fails on any page error, console error or
/// <c>securitypolicyviolation</c> event, checked when it ends. The browser's own report of an API refusal
/// ("Failed to load resource: the server responded with a status of …") is not an error here: refusals are part of what the
/// console handles, and each test asserts what the page shows of them; <see cref="ExpectNoRefusal"/> makes it one.
/// <see cref="Allow"/> admits another expected message.
/// </summary>
public abstract class BrowserTest(BrowserFixture browser) : IAsyncLifetime
{
    /// <summary>How the page reports a policy violation, so it is caught with the console's errors.</summary>
    const string Violation = "securitypolicyviolation";

    /// <summary>How the browser reports an API refusal in the console.</summary>
    const string Refusal = "Failed to load resource: the server responded with a status of";

    readonly List<string> problems = [];
    readonly List<string> allowed = [Refusal];
    ConsoleWorld? world;
    IBrowserContext? context;
    IPage? page;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    protected ConsoleWorld World => world ??= new ConsoleWorld();

    protected FakeEngine Engine => World.Engine;

    /// <summary>The page; its first use opens it.</summary>
    protected IPage Page => page ?? throw new InvalidOperationException("open the console first (OpenAsync)");

    /// <summary>A world under <paramref name="authMode"/>; call it before anything else uses <see cref="World"/>.</summary>
    protected ConsoleWorld UseWorld(string authMode, IReadOnlyList<FeatureFlow.Model.ApprovalRule>? approval = null,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? overrides = null, IReadOnlyList<string>? roles = null)
    {
        Assert.Null(world);
        return world = new ConsoleWorld(authMode, approval, overrides, roles);
    }

    /// <summary>Admits console errors containing <paramref name="fragment"/>, such as a refused connection while the API restarts.</summary>
    protected void Allow(string fragment) => allowed.Add(fragment);

    /// <summary>Makes the browser's report of an API refusal an error too, for a page that should be refused nothing.</summary>
    protected void ExpectNoRefusal() => allowed.Remove(Refusal);

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    /// <summary>Opens the console at <paramref name="hash"/> (such as <c>#/runs/run-1</c>; empty for Needs me).</summary>
    protected async Task<IPage> OpenAsync(string hash = "")
    {
        await OpenAtAsync(World.ConsoleUrl + hash);
        return Page;
    }

    /// <summary>Opens <paramref name="url"/>, such as a link from a notification.</summary>
    protected async Task OpenAtAsync(string url)
    {
        if (page is null)
        {
            context = await browser.Browser.NewContextAsync();
            context.SetDefaultTimeout(10_000);
            page = await context.NewPageAsync();
            page.PageError += (_, error) => Problem($"page error: {error}");
            page.Console += (_, message) =>
            {
                if (message.Type == "error" && !allowed.Any(a => message.Text.Contains(a, StringComparison.Ordinal)))
                    Problem($"console error: {message.Text}");
            };
            // Before any page script: a violation of the content security policy, Trusted Types included, is reported as an error.
            await page.AddInitScriptAsync($$"""
                document.addEventListener("{{Violation}}", e => console.error("{{Violation}}: " + e.violatedDirective + " " + e.blockedURI + " " + e.sample));
                """);
        }

        await page.GotoAsync(url);
        if (World.Auth.Mode == "none")
            await LiveAsync();
    }

    /// <summary>
    /// Waits until the console follows the change stream and its first re-read has settled, so a record appended after this
    /// reaches the page only over <c>GET /events</c>. <see cref="OpenAtAsync"/> waits for it with no sign-in; after a sign-in, call it.
    /// </summary>
    protected Task LiveAsync() => Assertions.Expect(Page.Locator("html")).ToHaveAttributeAsync("data-live", "connected");

    /// <summary>Moves the open console to <paramref name="hash"/> (such as <c>#/runs/run-1</c>) as a link inside it does: a hash change, no reload.</summary>
    protected Task GoAsync(string hash) => Page.EvaluateAsync("hash => { location.hash = hash; }", hash);

    /// <summary>A popup (app/popup.tsx), by its title.</summary>
    protected ILocator Popup(string label) => Page.GetByRole(AriaRole.Dialog, new() { Name = label, Exact = true });

    void Problem(string text)
    {
        lock (problems)
            problems.Add(text);
    }

    /// <summary>The page's own JavaScript value of <paramref name="expression"/>.</summary>
    protected Task<T> EvaluateAsync<T>(string expression) => Page.EvaluateAsync<T>(expression);

    public async ValueTask DisposeAsync()
    {
        if (context is not null)
            await context.CloseAsync();
        if (world is not null)
            await world.DisposeAsync();
        lock (problems)
            Assert.True(problems.Count == 0, "The page reported problems:\n" + string.Join("\n", problems));
    }
}
