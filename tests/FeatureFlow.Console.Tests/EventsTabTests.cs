using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// A step's Events tab (design-v2 s4.4; mock-up 02), from <c>GET /runs/{id}/steps/{step}</c>: every event of the step merged
/// from its sources, newest first; filtered by source; an agent event written while it is open arriving over the change stream.
/// </summary>
public sealed class EventsTabTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Middle => Page.GetByRole(AriaRole.Region, new() { Name = "Selected" });

    ILocator Rows => Middle.GetByRole(AriaRole.Table, new() { Name = "Events" }).Locator("tbody tr");

    ILocator Source(string name) => Middle.GetByRole(AriaRole.Group, new() { Name = "Source" }).GetByRole(AriaRole.Button).Filter(new() { HasText = name });

    [Fact]
    public async Task A_steps_events_from_every_source_are_merged_newest_first_and_filtered_by_source()
    {
        var s = await Engine.ReviewScenarioAsync();
        await Engine.AgentEventAsync(s.RunId, s.ReviewerSteps[0], 2, "hook_pre_tool_use", "Edit allow");
        await OpenAsync($"#/runs/{s.RunId}/steps/{s.RunId}/{s.ReviewerSteps[0]}");
        await Middle.GetByRole(AriaRole.Tab, new() { Name = "Events" }).ClickAsync();

        // The timeline (two starts, two completions), the agent's hook call, and the finding events of its record step.
        await Expect(Middle.Locator("tbody tr[data-source='timeline']")).Not.ToHaveCountAsync(0);
        await Expect(Middle.Locator("tbody tr[data-source='agent']")).ToHaveCountAsync(1);
        await Expect(Middle.Locator("tbody tr[data-source='finding']")).Not.ToHaveCountAsync(0);
        // Newest first: the hook call, written last, is the first row.
        await Expect(Rows.First).ToHaveAttributeAsync("data-source", "agent");
        await Expect(Rows.First).ToContainTextAsync("hook_pre_tool_use");
        await Expect(Rows.First).ToContainTextAsync("Edit allow");
        // A kind is one word on one line, never broken mid-word.
        Assert.Equal("nowrap", await Rows.First.Locator("td").Nth(3).EvaluateAsync<string>("td => getComputedStyle(td).whiteSpace"));

        await Source("Findings").ClickAsync();
        await Expect(Source("Findings")).ToHaveAttributeAsync("aria-pressed", "true");
        var findings = await Rows.CountAsync();
        Assert.True(findings > 0);
        for (var i = 0; i < findings; i++)
            await Expect(Rows.Nth(i)).ToHaveAttributeAsync("data-source", "finding");
        await Expect(Rows.Filter(new() { HasText = s.Fixed })).Not.ToHaveCountAsync(0);

        await Source("Overrides").ClickAsync();
        await Expect(Middle).ToContainTextAsync("No events from this source.");

        // An agent event while the tab is open reaches it (step_changed for the step).
        await Source("Agent").ClickAsync();
        await Expect(Rows).ToHaveCountAsync(1);
        await Engine.AgentEventAsync(s.RunId, s.ReviewerSteps[0], 2, "model", "claude-sonnet-5.5");
        await Expect(Rows).ToHaveCountAsync(2, new() { Timeout = 5_000 });
        await Expect(Rows.First).ToContainTextAsync("claude-sonnet-5.5");
    }

    [Fact]
    public async Task Event_text_from_the_api_is_shown_as_text()
    {
        await Engine.AgentStepAsync("run-1");
        await Engine.AgentEventAsync("run-1", FakeEngine.AgentStep, 1, "hook_pre_tool_use", "<img src=x onerror=alert(1)> **bold**");
        await OpenAsync($"#/runs/run-1/steps/run-1/{FakeEngine.AgentStep}");
        await Middle.GetByRole(AriaRole.Tab, new() { Name = "Events" }).ClickAsync();

        var row = Middle.Locator("tbody tr[data-source='agent']");
        await Expect(row).ToContainTextAsync("<img src=x onerror=alert(1)> **bold**");
        await Expect(Middle.Locator("tbody img")).ToHaveCountAsync(0);
    }
}
