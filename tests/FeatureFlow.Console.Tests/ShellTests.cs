using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The console's shell (plan s5): every route of s5.2 resolves to its screen, the theme toggles and is remembered, and a
/// popup closes on Escape and on a click outside it. The run tree is <see cref="FakeEngine.ProjectTreeAsync"/>'s.
/// </summary>
public sealed class ShellTests(BrowserFixture browser) : BrowserTest(browser)
{
    static string P(string part) => Uri.EscapeDataString(part);

    [Fact]
    public async Task Every_route_resolves_to_its_screen()
    {
        var tree = await Engine.ProjectTreeAsync();
        var ticket = tree.Tickets[0];
        var requirements = Assert.Single(await ArtifactsAsync(ticket), a => a!["name"]!.GetValue<string>() == "requirements")!["ref"]!.GetValue<string>();
        var page = await OpenAsync();
        var runId = page.Locator(".ff-run-id");
        var nowhere = page.GetByText("There is no such page.");

        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Needs me" })).ToBeVisibleAsync();
        await GoAsync("#/needs");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Needs me" })).ToBeVisibleAsync();
        await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Needs me" })).ToHaveAttributeAsync("aria-current", "page");
        await GoAsync("#/runs");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Runs" })).ToBeVisibleAsync();
        await GoAsync("#/start");
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Start a run" })).ToBeVisibleAsync();

        await GoAsync($"#/runs/{P(tree.Root)}");
        await Expect(runId).ToHaveTextAsync(tree.Root);
        await GoAsync($"#/runs/{P(tree.Root)}/steps/{P(ticket)}/approve_spec");
        await Expect(runId).ToHaveTextAsync(ticket);
        await GoAsync($"#/runs/{P(tree.Root)}/waits/{P(tree.Merge.RunId)}/{P(tree.Merge.WaitId)}");
        await Expect(runId).ToHaveTextAsync(tree.Merge.RunId);
        await Expect(Popup("Merge acme/api#201 into main?")).ToBeVisibleAsync();
        await GoAsync($"#/runs/{P(tree.Root)}/artifacts/{P(ticket)}/{P(requirements)}");
        await Expect(page.Locator(".ff-viewer-name")).ToHaveTextAsync("requirements");
        await Expect(Popup("Merge acme/api#201 into main?")).ToHaveCountAsync(0);
        // Slice 1's artifact link keeps working.
        await GoAsync($"#/runs/{P(ticket)}/artifacts/{P(requirements)}");
        await Expect(page.Locator(".ff-viewer-name")).ToHaveTextAsync("requirements");
        await Expect(nowhere).ToHaveCountAsync(0);

        foreach (var bad in new[] { "#/nowhere", $"#/runs/{P(tree.Root)}/steps/{P(ticket)}", $"#/runs/{P(tree.Root)}/other/{P(ticket)}/x", "#/runs/%E0%A4%A" })
        {
            await GoAsync(bad);
            await Expect(nowhere).ToBeVisibleAsync();
        }
    }

    [Fact]
    public async Task The_scenario_tree_has_plans_with_its_children_and_a_wait_of_every_kind()
    {
        var tree = await Engine.ProjectTreeAsync();

        Assert.Contains("epics.children_1", await StepIdsAsync(tree.Root));
        Assert.Contains("tickets.children_1", await StepIdsAsync(tree.Epics[0]));
        Assert.Contains("approve_spec", await StepIdsAsync(tree.Tickets[3]));
        var kinds = new List<string>();
        foreach (var wait in new[] { tree.Session, tree.PatchApproval, tree.SpecApproval, tree.Merge, tree.Breakpoint, tree.Intervention, tree.Triage, tree.Question })
        {
            var open = Assert.Single(await Engine.OpenWaitsAsync(wait.RunId), w => w.WaitId == wait.WaitId);
            Assert.Equal(wait.StepId, open.StepId);
            kinds.Add(open.Kind == "gate" ? open.DecisionType : open.Kind);
        }

        Assert.Equal(["project.requirements_session", "patch_approval", "ticket.spec_approval", "ticket.merge_action", "breakpoint", "intervention",
            "findings.triage", "question"], kinds);
    }

    [Fact]
    public async Task The_theme_toggles_and_is_remembered_across_a_reload()
    {
        var page = await OpenAsync();
        var html = page.Locator("html");
        var toggle = page.GetByRole(AriaRole.Button, new() { Name = "Dark theme" });
        var first = await html.GetAttributeAsync("data-theme");
        Assert.Contains(first, new[] { "light", "dark" });
        var other = first == "dark" ? "light" : "dark";

        await toggle.ClickAsync();
        await Expect(html).ToHaveAttributeAsync("data-theme", other);
        await Expect(toggle).ToHaveAttributeAsync("aria-pressed", other == "dark" ? "true" : "false");
        Assert.Equal(other == "dark" ? "#2e3440" : "#f2f3f3", (await EvaluateAsync<string>("getComputedStyle(document.body).getPropertyValue('--bg')")).Trim());

        await page.ReloadAsync();
        await LiveAsync();
        await Expect(html).ToHaveAttributeAsync("data-theme", other);

        await toggle.ClickAsync();
        await page.ReloadAsync();
        await LiveAsync();
        await Expect(html).ToHaveAttributeAsync("data-theme", first!);
    }

    [Fact]
    public async Task A_popup_closes_on_escape_and_on_a_click_outside_it_but_not_inside()
    {
        var tree = await Engine.ProjectTreeAsync();
        var route = $"#/runs/{P(tree.Root)}/waits/{P(tree.Question.RunId)}/{P(tree.Question.WaitId)}";
        var page = await OpenAsync(route);
        var popup = Popup("implement asks");

        await Expect(popup).ToBeVisibleAsync();
        await Expect(popup).ToContainTextAsync("Are XLSX exports in scope, or CSV only?");
        await popup.GetByText("Are XLSX exports in scope, or CSV only?").ClickAsync();
        await Expect(popup).ToBeVisibleAsync();
        await page.Keyboard.PressAsync("Escape");
        await Expect(popup).ToHaveCountAsync(0);

        await GoAsync($"#/runs/{P(tree.Root)}");
        await GoAsync(route);
        await Expect(popup).ToBeVisibleAsync();
        await page.Mouse.ClickAsync(5, 200);
        await Expect(popup).ToHaveCountAsync(0);
    }

    async Task<IReadOnlyList<string>> StepIdsAsync(string runId)
    {
        using var http = World.Http();
        using var response = await http.GetAsync($"runs/{P(runId)}/plan", Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var plan = JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!;
        return plan["steps"]!.AsArray().Select(s => s!["id"]!.GetValue<string>()).ToList();
    }

    async Task<JsonArray> ArtifactsAsync(string runId)
    {
        using var http = World.Http();
        var body = await http.GetStringAsync($"runs/{P(runId)}/artifacts", Ct);
        return JsonNode.Parse(body)!["artifacts"]!.AsArray();
    }
}
