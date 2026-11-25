using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The staged patch card on a running step (A8; build-console-b2 plan s2.2): the plan patch the agent has staged, from the
/// step detail's <c>staged_patch</c>, with its target, revision and operations and the plan as it would become
/// (<c>GET /runs/{id}/plan?staged=</c>); read again on the step's <c>step_changed</c>, so a patch staged again shows and a
/// withdrawn one disappears without a reload.
/// </summary>
public sealed class StagedPatchTests(BrowserFixture browser) : BrowserTest(browser)
{
    const string Step = FakeEngine.AgentStep;

    ILocator Middle => Page.GetByRole(AriaRole.Region, new() { Name = "Selected" });

    ILocator Card => Middle.GetByRole(AriaRole.Region, new() { Name = "Staged plan change" });

    Task OpenStepAsync(string runId) => OpenAsync($"#/runs/{runId}/steps/{runId}/{Step}");

    [Fact]
    public async Task A_running_steps_staged_patch_shows_its_target_revision_operations_and_the_plan_it_would_make()
    {
        await Engine.AgentStepAsync("run-1");
        await Engine.StagedPatchAsync("run-1", Step);
        await OpenStepAsync("run-1");

        await Expect(Card).ToContainTextAsync("this run's plan");
        await Expect(Card).ToContainTextAsync("The invoices table needs an index before paging is fast enough");
        await Expect(Card.GetByRole(AriaRole.List, new() { Name = "Operations" })).ToHaveTextAsync("insert migration_check after plan.plan");
        await Card.GetByRole(AriaRole.Button, new() { Name = "Show the plan as it would become" }).ClickAsync();
        var plan = Card.GetByRole(AriaRole.Table, new() { Name = "Plan as it would become" });
        await Expect(plan.Locator("tr[data-change='added']")).ToContainTextAsync("plan.migration_check");
    }

    [Fact]
    public async Task A_patch_staged_again_while_open_shows_and_a_withdrawn_one_disappears()
    {
        await Engine.AgentStepAsync("run-1");
        await OpenStepAsync("run-1");
        await Expect(Middle.GetByRole(AriaRole.Region, new() { Name = $"Step {Step}" })).ToBeVisibleAsync();
        await Expect(Card).ToHaveCountAsync(0);

        // Agent events reach the open page as step_changed frames.
        await Engine.StagedPatchAsync("run-1", Step);
        await Expect(Card).ToBeVisibleAsync(new() { Timeout = 5_000 });
        await Expect(Card.GetByRole(AriaRole.Definition).Nth(1)).ToHaveTextAsync("1");
        await Engine.StagedPatchAsync("run-1", Step, revision: 2);
        await Expect(Card.GetByRole(AriaRole.Definition).Nth(1)).ToHaveTextAsync("2", new() { Timeout = 5_000 });

        await Engine.AgentEventAsync("run-1", Step, 1, "patch_withdrawn", "");
        await Expect(Card).ToHaveCountAsync(0, new() { Timeout = 5_000 });
    }

    [Fact]
    public async Task A_burst_of_agent_events_reads_the_step_at_most_once_per_two_seconds_and_the_last_is_read()
    {
        await Engine.AgentStepAsync("run-1");
        await OpenStepAsync("run-1");
        await Expect(Middle.GetByRole(AriaRole.Region, new() { Name = $"Step {Step}" })).ToBeVisibleAsync();
        var reads = 0;
        Page.Request += (_, r) =>
        {
            if (new Uri(r.Url).AbsolutePath.EndsWith($"/runs/run-1/steps/{Step}", StringComparison.Ordinal))
                Interlocked.Increment(ref reads);
        };

        // An agent at work: an event every 300 ms for 2.4 s, each a step_changed frame beyond live.ts's 200 ms coalescing.
        for (var i = 0; i < 8; i++)
        {
            await Engine.AgentEventAsync("run-1", Step, 1, "hook_pre_tool_use", $"Edit allow {i}");
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }

        await Engine.StagedPatchAsync("run-1", Step, revision: 2);
        await Expect(Card.GetByRole(AriaRole.Definition).Nth(1)).ToHaveTextAsync("2", new() { Timeout = 6_000 });
        // About 2.7 s of changes: a read at once, then one at most every 2 s, the last after the staged patch.
        Assert.InRange(reads, 1, 3);
    }

    [Fact]
    public async Task A_change_that_is_not_an_object_is_skipped_and_the_step_page_still_shows()
    {
        await Engine.AgentStepAsync("run-1");
        // Agent-written: entries a patch should not hold, beside one it may.
        await Engine.StagedPatchAsync("run-1", Step, patch: """{"metadata":{"reason":"odd"},"changes":[null,1,{"op":"remove","target":"plan.validate"}]}""");
        await OpenStepAsync("run-1");

        await Expect(Card.GetByRole(AriaRole.List, new() { Name = "Operations" })).ToHaveTextAsync("remove plan.validate");
        await Expect(Middle.GetByRole(AriaRole.Region, new() { Name = $"Step {Step}" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_finished_step_shows_no_staged_patch()
    {
        await Engine.AgentStepAsync("run-1", running: false);
        await Engine.StagedPatchAsync("run-1", Step);
        await OpenStepAsync("run-1");

        await Expect(Middle.GetByRole(AriaRole.Region, new() { Name = $"Step {Step}" })).ToBeVisibleAsync();
        await Expect(Card).ToHaveCountAsync(0);
    }

    /// <summary>Not a check: a screenshot of the card with the plan shown (docs/build/console-b2-results.md), dark at 1440 × 900, to <c>FF_SCREENS</c>.</summary>
    [Fact(Explicit = true)]
    public async Task Screenshot_of_the_staged_patch_card()
    {
        var folder = Environment.GetEnvironmentVariable("FF_SCREENS") ?? Path.Combine(Path.GetTempPath(), "ff-screens");
        Directory.CreateDirectory(folder);
        await Engine.AgentStepAsync("run-1");
        await Engine.StagedPatchAsync("run-1", Step);
        await OpenStepAsync("run-1");
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Dark theme" }).ClickAsync();
        await Card.GetByRole(AriaRole.Button, new() { Name = "Show the plan as it would become" }).ClickAsync();
        await Expect(Card.GetByRole(AriaRole.Table)).ToBeVisibleAsync();
        await Card.EvaluateAsync("e => e.scrollIntoView()");
        await Page.WaitForTimeoutAsync(800);
        await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, "staged-patch.png") });
    }
}
