using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The text-only rule in a browser (LOG 460): API data holding markup is shown as its literal text and inserts no element
/// and runs no script, on every screen that shows it. The sources scan (ConsoleTests) and the Trusted Types policy are
/// the other two layers.
/// <para>
/// To add a screen: seeding is shared (<see cref="SeedAsync"/> already puts the markup in the run's template, requester,
/// wait prompt and choices, a timeline entry's detail and an artifact's name and content); add an entry to
/// <see cref="Screens"/> that opens the screen and names how many elements must show the markup as text.
/// </para>
/// </summary>
public sealed class TextOnlyTests(BrowserFixture browser) : BrowserTest(browser)
{
    /// <summary>Markup that, if it were inserted as markup, would add an image, run its handler and add a bold element.</summary>
    public const string Markup = """<img src=x onerror="window.ffx=1"><b class="ffx">x</b>""";

    const string RunId = "run-markup";

    /// <summary>A screen that shows the seeded data: how to open it, and the least number of elements showing the markup.</summary>
    sealed record Screen(Func<TextOnlyTests, Task> Open, int Shown);

    static readonly Dictionary<string, Screen> Screens = new()
    {
        // The run's template, requester and the wait's prompt.
        ["needs me"] = new(t => t.OpenAsync(), 3),
        // The run's template.
        ["run heading"] = new(t => t.OpenAsync("#/runs/" + RunId), 1),
        // A step's status from the timeline (done(<detail>)) in the plan's table and its readable form.
        ["plan"] = new(async t =>
        {
            var runId = await t.World.StartRunAsync();
            await t.Engine.CloseWaitAsync(runId, "gate-1");
            await t.Engine.TraceAsync(runId, "completed", "go_ahead", Markup);
            await t.OpenAsync("#/runs/" + runId);
        }, 2),
        // The wait form: the seeded gate's prompt and choice, and a second gate's prompt, choice and consumed names, linked and not.
        ["wait form"] = new(async t =>
        {
            var reference = await t.Engine.ProduceAsync(RunId, "draft", "design.md", "the design");
            await t.Engine.OpenWaitAsync(RunId, prompt: Markup, choices: [Markup],
                consumed: [new FeatureFlow.Providers.Abstractions.ConsumedRef("a", Markup, reference), new FeatureFlow.Providers.Abstractions.ConsumedRef("b", Markup, null)]);
            await t.OpenAsync("#/runs/" + RunId);
            await Expect(t.Page.GetByRole(Microsoft.Playwright.AriaRole.Region, new() { Name = "Wait gate-2" })).ToBeVisibleAsync();
        }, 6),
        // The run's template and requester.
        ["run list"] = new(t => t.OpenAsync("#/runs"), 2),
        // The template, requester and detail; a status wait's prompt; a closed wait's choice and reason; history (an answer's
        // choice and reason, an override's actor, change and reason); a child's template; timeline details.
        ["run page"] = new(async t => { await t.SeedRunPageAsync(); await t.OpenAsync("#/runs/" + RunId); }, 16),
    };

    /// <summary>The markup in the run page's other fields: its detail, a status wait, an answer, an override and a child.</summary>
    async Task SeedRunPageAsync()
    {
        await Engine.RunAsync(RunId, Markup, requester: Markup, detail: Markup);
        await Engine.OpenWaitAsync(RunId, "stalled", "go_ahead", "ticket.stalled", Markup);
        using var http = World.Http();
        using var answered = await http.PostAsync($"runs/{RunId}/answers",
            new StringContent(JsonSerializer.Serialize(new AnswerRequest("gate-1", Outcome: Markup, Reason: Markup), ApiJson.Options), Encoding.UTF8, "application/json"), Ct);
        Assert.True(answered.IsSuccessStatusCode, await answered.Content.ReadAsStringAsync(Ct));
        await Engine.AppendAsync(new(Overrides: [new(RunId, "o1", "skip", Markup, Markup, "go_ahead", new Dictionary<string, string> { ["outcome"] = Markup }, "applied", Engine.Now())]));
        await Engine.RunAsync("run-markup-child", Markup, parentRunId: RunId);
    }

    public static TheoryData<string> Names => [.. Screens.Keys];

    /// <summary>The markup in every field of API data a screen shows.</summary>
    async Task SeedAsync()
    {
        await World.SeedRunWithWaitAsync(RunId, template: Markup, prompt: Markup, choices: [Markup], requester: Markup);
        await Engine.TraceAsync(RunId, "started", "go_ahead", Markup);
        await Engine.ProduceAsync(RunId, "go_ahead", Markup, Markup);
    }

    [Theory]
    [MemberData(nameof(Names))]
    public async Task Markup_in_api_data_is_shown_as_text_and_inserts_no_element(string screen)
    {
        await SeedAsync();

        await Screens[screen].Open(this);

        var shown = Page.GetByText(Markup);
        await Expect(shown.First).ToBeVisibleAsync();
        Assert.True(await shown.CountAsync() >= Screens[screen].Shown, $"{screen} shows the markup as text in {await shown.CountAsync()} elements, not {Screens[screen].Shown}");
        Assert.Equal(0, await EvaluateAsync<int>("document.querySelectorAll('.ffx, img').length"));
        Assert.True(await EvaluateAsync<bool>("window.ffx === undefined"));
    }
}
