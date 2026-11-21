using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The run page (<c>#/runs/{id}</c>): its summary, open waits (a form for each decision, a banner for each status wait),
/// closed waits with the answer that closed them, child runs, history, timeline and abort.
/// </summary>
public sealed class RunPageTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Open => Page.GetByRole(AriaRole.Region, new() { Name = "Open waits" });

    ILocator Closed => Page.GetByRole(AriaRole.Region, new() { Name = "Closed waits" });

    async Task<string> PostAsync(string path, object body)
    {
        using var http = World.Http();
        using var response = await http.PostAsync(path, new StringContent(JsonSerializer.Serialize(body, ApiJson.Options), Encoding.UTF8, "application/json"), Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.IsSuccessStatusCode, text);
        return text;
    }

    [Fact]
    public async Task Stalled_and_stuck_show_as_status_without_a_form()
    {
        await Engine.RunAsync("run-status");
        await Engine.OpenWaitAsync("run-status", "stalled", "build", "ticket.stalled", "no progress for an hour");
        await Engine.OpenWaitAsync("run-status", "stuck", "test", "ticket.stuck", "the same failure three times");

        await OpenAsync("#/runs/run-status");

        var banners = Open.GetByRole(AriaRole.Status);
        await Expect(banners).ToHaveCountAsync(2);
        await Expect(banners.Nth(0)).ToContainTextAsync("stalled at build: no progress for an hour");
        await Expect(banners.Nth(1)).ToContainTextAsync("stuck at test: the same failure three times");
        await Expect(Open.GetByLabel("Reason")).ToHaveCountAsync(0);
        await Expect(Open.GetByRole(AriaRole.Button)).ToHaveCountAsync(0);
        await Expect(Open.Locator("section")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Closed_waits_show_who_answered_with_what_and_why()
    {
        var runId = await World.StartRunAsync();
        await PostAsync($"runs/{runId}/answers", new AnswerRequest("gate-1", Outcome: "approved", Reason: "the plan is sound"));

        await OpenAsync("#/runs/" + runId);

        var row = Closed.Locator("tr[data-wait='gate-1']");
        await Expect(row).ToContainTextAsync("gate: m7s.go_ahead");
        await Expect(row).ToContainTextAsync("go_ahead");
        await Expect(row).ToContainTextAsync(ConsoleWorld.Actor);
        await Expect(row).ToContainTextAsync("approved");
        await Expect(row).ToContainTextAsync("the plan is sound");
        await Expect(Open).ToContainTextAsync("Nothing is waiting.");
        await Expect(Open.Locator("[data-wait='gate-1']")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Children_are_linked_and_marked_when_they_wait_on_you()
    {
        await Engine.RunAsync("run-parent");
        await Engine.RunAsync("run-child-waits", parentRunId: "run-parent");
        await Engine.OpenWaitAsync("run-child-waits");
        await Engine.RunAsync("run-child-quiet", parentRunId: "run-parent");

        await OpenAsync("#/runs/run-parent");

        var children = Page.GetByRole(AriaRole.Region, new() { Name = "Child runs" });
        var waits = children.Locator("tr[data-run='run-child-waits']");
        var quiet = children.Locator("tr[data-run='run-child-quiet']");
        await Expect(waits).ToContainTextAsync("waits on you");
        await Expect(quiet).ToContainTextAsync("running");
        await Expect(quiet).Not.ToContainTextAsync("waits on you");
        await waits.GetByRole(AriaRole.Link, new() { Name = "run-child-waits" }).ClickAsync();

        await Expect(Page).ToHaveURLAsync(World.ConsoleUrl + "#/runs/run-child-waits");
        await Expect(Page.Locator(".ff-run-id")).ToHaveTextAsync("run-child-waits");
        await Expect(Page.Locator(".ff-run-summary")).ToContainTextAsync("Parent run");
        await Expect(Page.Locator(".ff-run-summary").GetByRole(AriaRole.Link, new() { Name = "run-parent" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task The_timeline_and_history_are_shown()
    {
        var runId = await World.StartRunAsync();
        // Clearing a breakpoint that is not set is accepted (and traced only as an override); a set one would be refused, the gate being open.
        await PostAsync($"runs/{runId}/overrides", new OverrideRequest("breakpoint", "look before it goes", "go_ahead", new Dictionary<string, string> { ["set"] = "false" }));
        await PostAsync($"runs/{runId}/answers", new AnswerRequest("gate-1", Outcome: "rejected", Reason: "not yet"));
        await Engine.TraceAsync(runId, "completed", "go_ahead", "the last step");

        await OpenAsync("#/runs/" + runId);

        var history = Page.GetByRole(AriaRole.Region, new() { Name = "History" }).Locator("tbody tr");
        var breakpoint = history.Filter(new() { HasText = "breakpoint" }).Filter(new() { HasText = "applied" });
        await Expect(breakpoint).ToContainTextAsync("look before it goes");
        await Expect(breakpoint).ToContainTextAsync("set: false");
        await Expect(history.Filter(new() { HasText = "gate-1: rejected" }).Filter(new() { HasText = "recorded" })).ToContainTextAsync("not yet");

        var timeline = Page.GetByRole(AriaRole.Region, new() { Name = "Timeline" }).Locator("tbody tr");
        string[] kinds = ["started", "override", "answered", "completed"];
        await Expect(timeline).ToHaveCountAsync(kinds.Length);
        for (var i = 0; i < kinds.Length; i++)
            await Expect(timeline.Nth(i).Locator("td").Nth(1)).ToHaveTextAsync(kinds[i]);
        await Expect(timeline.Nth(3)).ToContainTextAsync("the last step");
    }

    [Fact]
    public async Task Abort_needs_a_reason_and_ends_the_run()
    {
        var runId = await World.StartRunAsync();
        await OpenAsync("#/runs/" + runId);

        await Page.GetByRole(AriaRole.Button, new() { Name = "Abort run" }).ClickAsync();
        var abort = Page.GetByRole(AriaRole.Button, new() { Name = "Abort", Exact = true });
        await Expect(abort).ToBeDisabledAsync();
        await Page.Locator(".ff-run-abort").GetByLabel("Reason").FillAsync("wrong ticket");
        await Page.GetByLabel("Close its pull requests").CheckAsync();
        await abort.ClickAsync();

        await Expect(Page.Locator(".ff-run-heading .ff-status")).ToHaveTextAsync("aborted");
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Abort run" })).ToHaveCountAsync(0);
        var aborted = Assert.Single(Engine.Aborts);
        Assert.Equal("wrong ticket", aborted.Reason);
        Assert.True(aborted.ClosePullRequests);
    }

    [Fact]
    public async Task A_run_that_has_ended_offers_no_abort_or_steering()
    {
        // rejected is one of the engine's end statuses (Interpreter/Types.cs, RunStatus); the API refuses any update to it.
        var runId = await World.StartRunAsync();
        await Engine.RunAsync(runId, status: "rejected");

        await OpenAsync("#/runs/" + runId);

        await Expect(Page.Locator(".ff-run-heading .ff-status")).ToHaveTextAsync("rejected");
        await Expect(Page.Locator(".ff-run-heading .ff-status")).ToHaveClassAsync("ff-status ff-status-failed");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Plan", Exact = true })).ToContainTextAsync("go_ahead");
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Plan", Exact = true }).GetByRole(AriaRole.Button)).ToHaveCountAsync(0);
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Abort run" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Abort_is_not_offered_to_a_caller_who_may_not_abort()
    {
        UseWorld("none", roles: ["viewer", "approver"]);
        var runId = "run-not-mine-to-abort";
        await World.SeedRunWithWaitAsync(runId);

        await OpenAsync("#/runs/" + runId);

        await Expect(Page.Locator(".ff-run-id")).ToHaveTextAsync(runId);
        await Expect(Open.Locator("[data-wait='gate-1']")).ToBeVisibleAsync();
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Abort run" })).ToHaveCountAsync(0);
    }
}
