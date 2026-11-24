using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// A run's page in the workspace (<c>#/runs/{id}</c>, design-v2 s4.4): a run with no plan yet, a status wait at its step, and
/// Run actions ▾ (abort with a required comment, offered only while the run runs and to a caller who may; its history).
/// </summary>
public sealed class RunPageTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Middle => Page.GetByRole(AriaRole.Region, new() { Name = "Selected" });

    ILocator RunActions => Middle.GetByRole(AriaRole.Button, new() { Name = "Run actions ▾" });

    ILocator Item(string name) => Page.GetByRole(AriaRole.Menuitem).Filter(new() { HasText = name });

    async Task PostAsync(string path, object body)
    {
        using var http = World.Http();
        using var response = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body, ApiJson.Options), Encoding.UTF8, "application/json"), Ct);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_new_run_with_no_plan_revision_yet_is_refused_nothing()
    {
        // As the workspace reads a run before the engine has indexed revision 1 (LOG 493): no request is refused.
        ExpectNoRefusal();
        await Engine.RunAsync("run-new");

        await OpenAsync("#/runs/run-new");

        await Expect(Middle.Locator(".ff-run-id")).ToHaveTextAsync("run-new");
        await Expect(Page.Locator(".ff-row-ghost[data-run='run-new']")).ToContainTextAsync("has no plan yet");
    }

    [Fact]
    public async Task An_ended_run_took_until_it_ended_and_its_actions_list_what_comes_later()
    {
        var now = DateTimeOffset.UtcNow;
        await Engine.AppendAsync(new FeatureFlow.Providers.Abstractions.RecordBatch([new FeatureFlow.Providers.Abstractions.RunRecord("run-ended", null,
            "std:templates/ticket_delivery", "completed", "local", [], false, now.AddHours(-1), null, "ticket", StartedAt: now.AddHours(-3))]));

        await OpenAsync("#/runs/run-ended");

        // Started three hours ago and ended one hour ago: it took two, not three.
        await Expect(Middle.Locator(".ff-facts")).ToContainTextAsync("by local · 2 h 0 m");
        await RunActions.ClickAsync();
        await Expect(Item("Policy in force")).ToBeEnabledAsync();
        await Expect(Item("Restart from a step…")).ToBeDisabledAsync();
        await Expect(Item("Re-run")).ToBeDisabledAsync();
    }

    [Fact]
    public async Task A_status_wait_shows_at_its_step_as_a_status_with_nothing_to_answer()
    {
        var runId = await World.StartRunAsync();
        await Engine.CloseWaitAsync(runId, "gate-1");
        await Engine.OpenWaitAsync(runId, "stalled", "go_ahead", "ticket.stalled", "no progress for an hour");

        await OpenAsync($"#/runs/{runId}/steps/{runId}/go_ahead");

        var stalled = Middle.GetByRole(AriaRole.Region, new() { Name = "stalled" });
        await Expect(stalled.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Stalled");
        await Expect(stalled.GetByRole(AriaRole.Status)).ToHaveTextAsync("no progress for an hour");
        await Expect(stalled.GetByRole(AriaRole.Button)).ToHaveCountAsync(0);
        await Expect(Middle.GetByRole(AriaRole.Region, new() { Name = "Needs you" })).ToHaveCountAsync(0);
        // In the tree: in trouble, not waiting on you.
        var row = Page.Locator($".ff-row-step[data-step='go_ahead']");
        await Expect(row).ToHaveAttributeAsync("data-state", "problem");
        await Expect(Page.GetByRole(AriaRole.Toolbar, new() { Name = "Go to" })).ToContainTextAsync("Problems (1)");
    }

    [Fact]
    public async Task Abort_needs_a_comment_and_ends_the_run()
    {
        var runId = await World.StartRunAsync();
        await OpenAsync("#/runs/" + runId);

        await RunActions.ClickAsync();
        await Item("Abort…").ClickAsync();
        var popup = Popup($"Abort {runId}");
        var abort = popup.GetByRole(AriaRole.Button, new() { Name = "Abort (comment required)", Exact = true });
        await Expect(abort).ToBeDisabledAsync();
        await popup.GetByLabel("Comment").FillAsync("wrong ticket");
        await popup.GetByLabel("Close its pull requests").CheckAsync();
        await abort.ClickAsync();

        await Expect(popup).ToHaveCountAsync(0);
        var row = Page.Locator($".ff-tree .ff-row-run[data-run='{runId}']");
        await Expect(row).ToContainTextAsync("aborted");
        await Expect(row).ToHaveAttributeAsync("data-state", "problem");
        await Expect(Page.Locator(".ff-tree").GetByRole(AriaRole.Img, new() { Name = "running" })).ToHaveCountAsync(0);
        var aborted = Assert.Single(Engine.Aborts);
        Assert.Equal("wrong ticket", aborted.Reason);
        Assert.True(aborted.ClosePullRequests);
        await RunActions.ClickAsync();
        await Expect(Item("Abort…")).ToBeDisabledAsync();
        await Expect(Item("Abort…")).ToContainTextAsync("the run has ended");
    }

    [Fact]
    public async Task A_run_that_has_ended_offers_no_abort_or_steering()
    {
        // rejected is one of the engine's end statuses (Interpreter/Types.cs, RunStatus); the API refuses any update to it.
        var runId = await World.StartRunAsync();
        await Engine.RunAsync(runId, status: "rejected");

        await OpenAsync($"#/runs/{runId}/steps/{runId}/go_ahead");

        await Expect(Page.Locator($".ff-row-run[data-run='{runId}']")).ToHaveAttributeAsync("data-state", "problem");
        await Expect(Page.Locator($".ff-row-step[data-step='go_ahead']").GetByRole(AriaRole.Button, new() { Name = "Breakpoint before go_ahead" })).ToHaveCountAsync(0);
        await Expect(Middle.GetByRole(AriaRole.Button, new() { Name = "Edit step" })).ToBeDisabledAsync();
        await Middle.GetByRole(AriaRole.Button, new() { Name = "Steer ▾" }).ClickAsync();
        foreach (var name in new[] { "Pause before", "Skip", "Grant attempts" })
            await Expect(Item(name)).ToBeDisabledAsync();

        await Page.Locator($".ff-row-run[data-run='{runId}']").ClickAsync();
        await RunActions.ClickAsync();
        await Expect(Item("Abort…")).ToBeDisabledAsync();
    }

    [Fact]
    public async Task Abort_is_not_offered_to_a_caller_who_may_not_abort()
    {
        UseWorld("none", roles: ["viewer", "approver"]);
        await World.SeedRunWithWaitAsync("run-not-mine-to-abort");

        await OpenAsync("#/runs/run-not-mine-to-abort");

        await Expect(Middle.Locator(".ff-run-id")).ToHaveTextAsync("run-not-mine-to-abort");
        await RunActions.ClickAsync();
        await Expect(Item("Abort…")).ToBeDisabledAsync();
        await Expect(Item("Abort…")).ToContainTextAsync("you may not abort this run");
    }

    [Fact]
    public async Task History_lists_every_answer_and_override_with_who_and_why()
    {
        var runId = await World.StartRunAsync();
        // Clearing a breakpoint that is not set is accepted (and traced only as an override).
        await PostAsync($"runs/{runId}/overrides", new OverrideRequest("breakpoint", "look before it goes", "go_ahead", new Dictionary<string, string> { ["set"] = "false" }));
        await PostAsync($"runs/{runId}/answers", new AnswerRequest("gate-1", Outcome: "rejected", Reason: "not yet"));
        await OpenAsync("#/runs/" + runId);

        await RunActions.ClickAsync();
        await Item("History").ClickAsync();

        var rows = Popup($"History of {runId}").Locator("tbody tr");
        var breakpoint = rows.Filter(new() { HasText = "look before it goes" }).Filter(new() { HasText = "applied" });
        await Expect(breakpoint).ToContainTextAsync("breakpoint");
        await Expect(breakpoint).ToContainTextAsync("set: false");
        await Expect(breakpoint).ToContainTextAsync(ConsoleWorld.Actor);
        await Expect(rows.Filter(new() { HasText = "gate-1: rejected" }).Filter(new() { HasText = "recorded" })).ToContainTextAsync("not yet");
    }
}
