using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>Starting a run (<c>#/start</c>): a template from <c>GET /templates</c>, its parameters by type, and <c>POST /runs</c>.</summary>
public sealed partial class StartRunTests(BrowserFixture browser) : BrowserTest(browser)
{
    [GeneratedRegex(@"#/runs/run-[0-9a-f]{12}$")]
    private static partial Regex RunPage();

    /// <summary>The open gate's form, by the shared accessible name or, before task T5's form, the placeholder's mark.</summary>
    static ILocator WaitOf(IPage page, string waitId) => page.Locator($"section[aria-label='Wait {waitId}'], section[data-wait='{waitId}']");

    [Fact]
    public async Task A_run_started_from_a_template_opens_on_its_page()
    {
        var page = await OpenAsync("#/start");

        await page.GetByLabel("Template").SelectOptionAsync("m7s:templates/decide");
        await page.GetByRole(AriaRole.Button, new() { Name = "Start" }).ClickAsync();

        await Expect(page).ToHaveURLAsync(RunPage());
        var start = Assert.Single(Engine.Starts);
        Assert.Equal("m7s:templates/decide", start.Template);
        Assert.EndsWith("#/runs/" + start.RunId, page.Url);
        await Expect(page.Locator(".ff-run-id")).ToHaveTextAsync(start.RunId);
        await Expect(page.Locator(".ff-run-template")).ToHaveTextAsync("m7s:templates/decide");
        await Expect(WaitOf(page, "gate-1")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Parameters_are_offered_by_type_with_their_defaults()
    {
        var page = await OpenAsync("#/start");
        var template = page.GetByLabel("Template");

        // A default given as an expression is computed at start, so its field starts empty and an empty field is not sent.
        await template.SelectOptionAsync("std:templates/project_delivery");
        var planning = page.GetByLabel("planning_repository");
        await Expect(planning).ToHaveValueAsync("");
        await Expect(planning).ToHaveAttributeAsync("placeholder", "computed at start");
        await Expect(page.Locator("[data-parameter='planning_repository']")).ToContainTextAsync("computed at start from run.repositories[0]");

        // A string is a text field (required here); a boolean is a checkbox holding its default.
        await template.SelectOptionAsync("std:templates/ticket_delivery");
        var ticket = page.GetByLabel("ticket_id");
        var merge = page.GetByLabel("engine_can_merge");
        await Expect(ticket).ToHaveAttributeAsync("type", "text");
        await Expect(ticket).ToHaveAttributeAsync("required", "");
        await Expect(merge).ToHaveAttributeAsync("type", "checkbox");
        await Expect(merge).Not.ToBeCheckedAsync();
        await Expect(page.Locator("[data-parameter='ticket_id']")).ToContainTextAsync("The tracker key");

        await ticket.FillAsync("ACME-12");
        await merge.CheckAsync();
        await page.GetByLabel("Repositories (comma-separated)").FillAsync(" acme/app ,, acme/web");
        await page.GetByLabel("Tracker issue").FillAsync("ACME-12");
        await page.GetByLabel("Dry run").CheckAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Start" }).ClickAsync();

        await Expect(page).ToHaveURLAsync(RunPage());
        var start = Assert.Single(Engine.Starts);
        Assert.Equal("std:templates/ticket_delivery", start.Template);
        // Each value is JSON text, so the string is quoted and the boolean is not.
        Assert.Equal(2, start.Parameters.Count);
        Assert.Equal("\"ACME-12\"", start.Parameters["ticket_id"]);
        Assert.Equal("true", start.Parameters["engine_can_merge"]);
        Assert.Equal(new[] { "acme/app", "acme/web" }, start.Repositories);
        Assert.Equal("ACME-12", start.TrackerIssue);
        Assert.True(start.DryRun);
    }

    [Fact]
    public async Task A_boolean_without_a_literal_default_is_sent_only_when_changed()
    {
        // The libraries here have no such boolean, so the template list is changed on its way to the page: engine_can_merge
        // gets an expression for its default, as a library may declare it.
        var page = await OpenAsync();
        await page.RouteAsync("**/templates", async route =>
        {
            var response = await route.FetchAsync();
            var body = JsonNode.Parse(await response.TextAsync())!;
            foreach (var parameter in body["templates"]!.AsArray().SelectMany(t => t!["parameters"]!.AsArray()))
                if ((string?)parameter!["name"] == "engine_can_merge")
                {
                    parameter["default"] = null;
                    parameter["default_expression"] = "params.ticket_id != ''";
                }

            await route.FulfillAsync(new() { Response = response, Body = body.ToJsonString() });
        });

        foreach (var merge in new[] { false, true })
        {
            await page.EvaluateAsync("() => { location.hash = '#/start'; }");
            await page.GetByLabel("Template").SelectOptionAsync("std:templates/ticket_delivery");
            await page.GetByLabel("ticket_id").FillAsync("ACME-12");
            await Expect(page.GetByLabel("engine_can_merge")).Not.ToBeCheckedAsync();
            if (merge)
                await page.GetByLabel("engine_can_merge").CheckAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Start" }).ClickAsync();
            await Expect(page).ToHaveURLAsync(RunPage());
        }

        Assert.Collection(Engine.Starts,
            untouched => Assert.Equal(["ticket_id"], untouched.Parameters.Keys),
            ticked => Assert.Equal("true", ticked.Parameters["engine_can_merge"]));
    }

    [Fact]
    public async Task A_refused_start_shows_the_reason()
    {
        // The libraries here declare only string and boolean parameters, which the form always sends as valid JSON, so the
        // refusal is of another field: a tracker issue longer than a tracker key may be.
        var page = await OpenAsync("#/start");

        await page.GetByLabel("Template").SelectOptionAsync("m7s:templates/decide");
        await page.GetByLabel("Tracker issue").FillAsync(new string('A', 201));
        await page.GetByRole(AriaRole.Button, new() { Name = "Start" }).ClickAsync();

        await Expect(page.GetByRole(AriaRole.Alert)).ToHaveTextAsync("tracker_issue must be a tracker key, such as ACME-12 or owner/repo#12");
        Assert.EndsWith("#/start", page.Url);
        Assert.Empty(Engine.Starts);
        await Expect(page.GetByRole(AriaRole.Button, new() { Name = "Start" })).ToBeEnabledAsync();
    }
}
