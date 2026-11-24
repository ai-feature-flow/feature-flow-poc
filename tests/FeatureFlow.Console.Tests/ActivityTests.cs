using Microsoft.Playwright;
using static FeatureFlow.Console.Tests.FakeEngine.ActivityEntry;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// A step's Activity tab (build-activity design s5; design-v2 s4.4): a running agent step's sessions as turns, read again on
/// its <c>step_changed</c>; following new activity at the bottom and holding its place when scrolled up; a finished step's
/// transcript; the execution picker; and the running marker on the current tool call.
/// </summary>
public sealed class ActivityTests(BrowserFixture browser) : BrowserTest(browser)
{
    const string Step = FakeEngine.AgentStep;

    ILocator Middle => Page.GetByRole(AriaRole.Region, new() { Name = "Selected" });

    ILocator Activity => Middle.GetByRole(AriaRole.Region, new() { Name = "Activity" });

    ILocator Turns => Activity.Locator(".ff-tx-turn");

    Task OpenStepAsync(string runId) => OpenAsync($"#/runs/{runId}/steps/{runId}/{Step}");

    [Fact]
    public async Task A_running_agent_steps_activity_opens_first_and_follows_its_activity_with_the_current_tool_call_marked()
    {
        await Engine.AgentStepAsync("run-1");
        await Engine.ActivityAsync("run-1", Step, 1, 1, Said("Reading the **inputs** first."));
        await OpenStepAsync("run-1");

        await Expect(Middle.GetByRole(AriaRole.Tab, new() { Name = "Activity" })).ToHaveAttributeAsync("aria-selected", "true");
        await Expect(Turns).ToHaveCountAsync(1);
        await Expect(Turns.First.Locator(".ff-tx-who")).ToContainTextAsync("Agent");
        await Expect(Turns.First.Locator(".ff-md strong")).ToHaveTextAsync("inputs");
        await Expect(Activity.Locator(".ff-act-divider")).ToContainTextAsync("Session 1");
        await Expect(Activity.Locator(".ff-act-divider")).ToContainTextAsync("live");

        // A tool call started while the page is open arrives over the change stream (step_changed only), marked running.
        await Engine.ActivityAsync("run-1", Step, 1, 1, ToolStarted("c1", "bash", "List the repository", """{"command":"ls"}"""));
        await Expect(Turns).ToHaveCountAsync(2, new() { Timeout = 5_000 });
        await Expect(Turns.Nth(1).Locator(".ff-tx-name")).ToHaveTextAsync("bash");
        await Expect(Turns.Nth(1).Locator(".ff-tx-running")).ToHaveTextAsync("running");
        await Expect(Activity.Locator(".ff-tx-running")).ToHaveCountAsync(1);

        // It finishes, and the agent says more: the marker goes, and the result is under the call.
        await Engine.ActivityAsync("run-1", Step, 1, 1, ToolFinished("c1", "README.md\nsrc"), Said("Now planning."));
        await Expect(Turns).ToHaveCountAsync(3, new() { Timeout = 5_000 });
        await Expect(Activity.Locator(".ff-tx-running")).ToHaveCountAsync(0);
        await Turns.Nth(1).Locator("summary").ClickAsync();
        await Expect(Turns.Nth(1).Locator("pre").Last).ToHaveTextAsync("README.md\nsrc");
        await Expect(Turns.Nth(2)).ToContainTextAsync("Now planning.");
    }

    async Task<bool> AtBottomAsync() =>
        await Activity.EvaluateAsync<bool>("e => e.scrollHeight - e.scrollTop - e.clientHeight <= 2");

    [Fact]
    public async Task New_activity_is_followed_at_the_bottom_and_waits_behind_a_button_when_scrolled_up()
    {
        await Engine.AgentStepAsync("run-1");
        await Engine.ActivityAsync("run-1", Step, 1, 1, Enumerable.Range(1, 30).Select(i => Said($"Step {i} of the plan.\n\nMore about it.")).ToArray());
        await OpenStepAsync("run-1");
        await Page.SetViewportSizeAsync(1440, 900);
        await Expect(Turns).ToHaveCountAsync(30);
        await Activity.ScrollIntoViewIfNeededAsync();
        Assert.True(await Activity.EvaluateAsync<bool>("e => e.scrollHeight > e.clientHeight + 100"), "the activity should overflow its box");
        Assert.True(await AtBottomAsync(), "a live session opens at its newest activity");

        // At the bottom, new activity is followed.
        await Engine.ActivityAsync("run-1", Step, 1, 1, Said("Step 31."));
        await Expect(Turns).ToHaveCountAsync(31, new() { Timeout = 5_000 });
        await Expect(Turns.Last).ToBeInViewportAsync();
        Assert.True(await AtBottomAsync());
        var newActivity = Middle.GetByRole(AriaRole.Button, new() { Name = "New activity ↓" });
        await Expect(newActivity).ToHaveCountAsync(0);

        // Scrolled up, the box keeps its place and offers the new activity.
        await Activity.EvaluateAsync("e => { e.scrollTop = 0; }");
        await Engine.ActivityAsync("run-1", Step, 1, 1, Said("Step 32."));
        await Expect(Turns).ToHaveCountAsync(32, new() { Timeout = 5_000 });
        await Expect(newActivity).ToBeVisibleAsync();
        Assert.Equal(0, await Activity.EvaluateAsync<double>("e => e.scrollTop"));

        await newActivity.ClickAsync();
        await Expect(newActivity).ToHaveCountAsync(0);
        Assert.True(await AtBottomAsync());
        await Expect(Turns.Last).ToBeInViewportAsync();
    }

    [Fact]
    public async Task A_finished_steps_activity_is_its_transcripts_turns_with_a_link_to_the_full_transcript()
    {
        await Engine.AgentStepAsync("run-1", running: false);
        await Engine.ProduceAsync("run-1", Step, "transcript:1", ArtifactTests.RealTranscript, type: "transcript");
        await OpenStepAsync("run-1");

        // Not running: the overview first.
        var tab = Middle.GetByRole(AriaRole.Tab, new() { Name = "Activity" });
        await Expect(Middle.GetByRole(AriaRole.Tab, new() { Name = "Overview" })).ToHaveAttributeAsync("aria-selected", "true");
        await tab.ClickAsync();

        await Expect(Activity.Locator(".ff-act-divider")).ToContainTextAsync("transcript");
        await Expect(Turns).ToHaveCountAsync(13);
        await Expect(Turns.Nth(5).Locator("summary .ff-tx-failed")).ToHaveTextAsync("failed");
        await Expect(Activity.Locator(".ff-tx-running")).ToHaveCountAsync(0);
        Assert.Equal(0, await Activity.EvaluateAsync<double>("e => e.scrollTop"));
        await Expect(Middle.Locator(".ff-act-pick")).ToHaveCountAsync(0);

        await Activity.GetByRole(AriaRole.Button, new() { Name = "Full transcript" }).ClickAsync();
        await Expect(Page.Locator(".ff-right .ff-viewer-name")).ToHaveTextAsync("transcript:1");
        await Expect(Page.Locator(".ff-right .ff-tx-turn")).ToHaveCountAsync(13);
    }

    [Fact]
    public async Task The_execution_picker_follows_the_latest_until_an_older_one_is_picked()
    {
        await Engine.AgentStepAsync("run-1", executions: 2);
        await Engine.ProduceAsync("run-1", Step, "transcript:1", ArtifactTests.RealTranscript, type: "transcript");
        await Engine.ActivityAsync("run-1", Step, 2, 1, Said("Second go."));
        await OpenStepAsync("run-1");

        var picker = Middle.GetByLabel("Execution");
        await Expect(picker).ToHaveValueAsync("2");
        await Expect(Turns).ToHaveCountAsync(1);
        await Expect(Turns.First).ToContainTextAsync("Second go.");

        await picker.SelectOptionAsync("1");
        await Expect(Turns).ToHaveCountAsync(13);
        // The latest's new activity does not take the picker back.
        await Engine.ActivityAsync("run-1", Step, 2, 1, Said("Still going."));
        await Page.WaitForTimeoutAsync(1_000);
        await Expect(picker).ToHaveValueAsync("1");
        await Expect(Turns).ToHaveCountAsync(13);

        // Back on the latest, it follows again.
        await picker.SelectOptionAsync("2");
        await Expect(Turns).ToHaveCountAsync(2);
        await Engine.ActivityAsync("run-1", Step, 2, 1, Said("Done soon."));
        await Expect(Turns).ToHaveCountAsync(3, new() { Timeout = 5_000 });
    }

    /// <summary>
    /// Not a check: screenshots of the Activity tab (docs/build/activity-results.md), dark at 1440 × 900, to <c>FF_SCREENS</c>:
    /// a running step's live session with a tool call running, and a finished step's transcript.
    /// </summary>
    [Fact(Explicit = true)]
    public async Task Screenshots_of_the_activity_tab()
    {
        var folder = Environment.GetEnvironmentVariable("FF_SCREENS") ?? Path.Combine(Path.GetTempPath(), "ff-screens");
        Directory.CreateDirectory(folder);
        await Engine.AgentStepAsync("run-live");
        await Engine.ActivityAsync("run-live", Step, 1, 1,
            Said("I'll start by reading the inputs and the repository's layout."),
            ToolStarted("c1", "bash", "Read inputs", """{"command":"cat /ff/in/inputs.json"}"""),
            ToolFinished("c1", """{ "step": "plan.plan", "ticket": "INV-9" }"""),
            ToolStarted("c2", "view", "View the invoices controller", """{"path":"src/Invoices/InvoicesController.cs"}"""),
            ToolFinished("c2", "public sealed class InvoicesController : ControllerBase\n{\n    // ...\n}"),
            Said("The controller lists invoices but has no export. I'll plan **two requirements**: listing and a CSV export."),
            ToolStarted("c3", "bash", "Run the unit tests", """{"command":"dotnet test --filter Category=Unit"}"""));
        await Engine.AgentStepAsync("run-done", running: false);
        await Engine.ProduceAsync("run-done", Step, "transcript:1", ArtifactTests.RealTranscript, type: "transcript");

        await OpenStepAsync("run-live");
        await Page.SetViewportSizeAsync(1440, 900);
        await Page.GetByRole(AriaRole.Button, new() { Name = "Dark theme" }).ClickAsync();
        async Task Shot(string name)
        {
            await Page.WaitForTimeoutAsync(800);
            await Page.ScreenshotAsync(new() { Path = Path.Combine(folder, $"{name}.png") });
        }

        await Expect(Activity.Locator(".ff-tx-running")).ToBeVisibleAsync();
        await Middle.EvaluateAsync("e => e.querySelector('.ff-detail').scrollIntoView()");
        await Shot("activity-running");

        await GoAsync($"#/runs/run-done/steps/run-done/{Step}");
        await Middle.GetByRole(AriaRole.Tab, new() { Name = "Activity" }).ClickAsync();
        await Expect(Turns).ToHaveCountAsync(13);
        await Turns.Nth(5).Locator("summary").ClickAsync();
        await Middle.EvaluateAsync("e => e.querySelector('.ff-detail').scrollIntoView()");
        await Shot("activity-finished");
    }
}
