using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The plan panel's steering (F18, G11): breakpoint, skip, grant attempts and amend, each through
/// <c>POST /runs/{id}/overrides</c> with an optional comment, offered only where the caller may use it and the core accepts
/// it, in a form that opens where it was chosen.
/// </summary>
public sealed class SteeringTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Plan => Page.GetByRole(AriaRole.Region, new() { Name = "Plan", Exact = true });

    ILocator Row(string stepId) => Plan.Locator($"tr[data-step='{stepId}']");

    /// <summary>The steering form or reply under a step's row.</summary>
    ILocator Under(string stepId) => Plan.Locator($"tr[data-step='{stepId}'] + tr.ff-plan-steer-row");

    ILocator Button(ILocator within, string name) => within.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    /// <summary>Fills the open steering form's comment and applies it.</summary>
    async Task ApplyAsync(string reason)
    {
        await Plan.GetByLabel("Comment").FillAsync(reason);
        await Button(Plan, "Apply").ClickAsync();
    }

    /// <summary>
    /// A <c>std:templates/ticket_delivery</c> run started through the API, open on its page. Every step is pending: the fake
    /// starts none of them until a test moves the run on.
    /// </summary>
    async Task<string> OpenTicketRunAsync()
    {
        var runId = await World.StartRunAsync("std:templates/ticket_delivery", new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" }, ["acme/api"]);
        await OpenAsync("#/runs/" + runId);
        await Expect(Row("implement")).ToBeVisibleAsync();
        return runId;
    }

    [Fact]
    public async Task A_breakpoint_set_from_the_plan_pauses_the_run_and_continue_resumes_it()
    {
        var runId = await OpenTicketRunAsync();

        await Button(Row("implement"), "Set breakpoint").ClickAsync();
        await ApplyAsync("look before it goes");

        await Expect(Under("implement").GetByRole(AriaRole.Status)).ToHaveTextAsync("Set breakpoint on implement: applied");
        await Expect(Row("implement")).ToContainTextAsync("breakpoint set");
        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("breakpoint", "implement", "look before it goes", "true"), (o.Key, o.StepId, o.Reason, o.Change["set"]));
        // The run pauses only when it reaches the step.
        Assert.DoesNotContain(await Engine.OpenWaitsAsync(runId), w => w.Kind == "breakpoint");

        var waitId = await Engine.ReachAsync(runId, "implement");

        // Without a reload: the breakpoint's form under Open waits.
        var form = Page.GetByRole(AriaRole.Region, new() { Name = "Open waits" }).GetByRole(AriaRole.Region, new() { Name = "Wait " + waitId });
        await Expect(form).ToBeVisibleAsync(new() { Timeout = 5_000 });
        await Expect(Row("implement")).ToContainTextAsync("waiting(breakpoint)");
        await form.GetByLabel("Recovery").SelectOptionAsync("continue");
        await form.GetByLabel("Comment").FillAsync("looks right");
        await Button(form, "Send").ClickAsync();

        await Expect(form).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        await Expect(Page.GetByRole(AriaRole.Region, new() { Name = "Closed waits" })).ToContainTextAsync("continue");
        // The run resumed: the step started, as the core traces a continue.
        await Expect(Row("implement").Locator(".ff-status")).ToHaveTextAsync("running");
        var timeline = Page.GetByRole(AriaRole.Region, new() { Name = "Timeline" }).Locator("tbody tr td:nth-child(2)");
        await Expect(timeline).ToHaveTextAsync(["breakpoint_set", "override", "paused", "recovered", "answered", "started"]);
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal((waitId, "continue", "looks right"), (answer.WaitId, answer.Action, answer.Reason));
    }

    [Fact]
    public async Task Each_step_offers_only_the_steering_the_engine_accepts_there()
    {
        var runId = await World.StartRunAsync("std:templates/ticket_delivery", new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" }, ["acme/api"]);
        using (var http = World.Http())
        using (var set = await http.PostAsync($"runs/{runId}/overrides", Json(new OverrideRequest("breakpoint", "watch it", "baseline.tests", new Dictionary<string, string> { ["set"] = "true" })), Ct))
            Assert.True(set.IsSuccessStatusCode);
        await Engine.ReachAsync(runId, "plan.plan");
        await Engine.TraceAsync(runId, "started", "baseline.tests", "execution 1");
        await Engine.OpenWaitAsync(runId, "gate", "approve_spec", "ticket.spec_approval", "Approve the spec?");
        await OpenAsync("#/runs/" + runId);

        // Pending: a breakpoint, a skip, and attempts for a step that uses them (an agent or an action).
        await Expect(Row("implement").GetByRole(AriaRole.Button)).ToHaveTextAsync(["Set breakpoint", "Skip", "Grant attempts"]);
        await Expect(Row("verify.review.review").GetByRole(AriaRole.Button)).ToHaveTextAsync(["Set breakpoint", "Skip"]);
        // Running: no new breakpoint and no skip.
        await Expect(Row("plan.plan").GetByRole(AriaRole.Button)).ToHaveTextAsync(["Grant attempts"]);
        // A breakpoint set before the step started is cleared at any time.
        await Expect(Row("baseline.tests").GetByRole(AriaRole.Button)).ToHaveTextAsync(["Clear breakpoint", "Grant attempts"]);
        // Waiting at its gate: skipped at once, but no breakpoint, and a decision uses no attempts.
        await Expect(Row("approve_spec").GetByRole(AriaRole.Button)).ToHaveTextAsync(["Skip"]);
    }

    [Fact]
    public async Task A_protected_step_is_skipped_only_with_skip_protected()
    {
        UseWorld("none", overrides: new Dictionary<string, IReadOnlyList<string>> { ["skip_protected"] = [] });
        await OpenTicketRunAsync();

        await Expect(Button(Row("implement"), "Skip")).ToBeVisibleAsync();
        await Expect(Button(Row("verify.tests"), "Set breakpoint")).ToBeVisibleAsync();
        await Expect(Button(Row("verify.tests"), "Skip")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task The_form_opens_under_the_step_with_its_first_field_ready()
    {
        await OpenTicketRunAsync();

        await Button(Row("deliver.merge"), "Skip").ClickAsync();

        var form = Under("deliver.merge").Locator("form");
        await Expect(form.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Skip deliver.merge");
        await Expect(form.GetByLabel("Outcome")).ToBeFocusedAsync();
        await Expect(form).ToBeInViewportAsync();
        await Expect(form.GetByLabel("Outcome").Locator("option")).ToHaveTextAsync(["merged", "base_moved", "checks_failed", "declined"]);
        await Expect(Plan.Locator("form")).ToHaveCountAsync(1);
    }

    [Fact]
    public async Task Skip_and_grant_attempts_send_their_arguments_and_the_plan_shows_them()
    {
        await OpenTicketRunAsync();

        await Button(Row("implement"), "Skip").ClickAsync();
        await Plan.GetByLabel("Outcome").SelectOptionAsync("blocked");
        await ApplyAsync("not needed");
        await Expect(Row("implement")).ToContainTextAsync("skipped with blocked");

        await Button(Row("implement"), "Grant attempts").ClickAsync();
        await Plan.GetByLabel("Attempts").FillAsync("2");
        await ApplyAsync("one more go");
        await Expect(Under("implement").GetByRole(AriaRole.Status)).ToHaveTextAsync("Grant attempts to implement: applied");

        Assert.Collection(Engine.Overrides,
            o => Assert.Equal(("skip", "implement", "not needed", "blocked"), (o.Key, o.StepId, o.Reason, o.Change["outcome"])),
            o => Assert.Equal(("grant_attempts", "implement", "one more go", "2"), (o.Key, o.StepId, o.Reason, o.Change["attempts"])));
    }

    [Fact]
    public async Task An_amendment_is_sent_against_the_current_revision()
    {
        var runId = await World.StartRunAsync();
        await OpenAsync("#/runs/" + runId);
        await Expect(Plan).ToContainTextAsync("Revision 1");

        await Button(Plan, "Amend plan").ClickAsync();
        var patch = Plan.GetByLabel("Patch");
        await Expect(patch).ToBeFocusedAsync();
        await Expect(patch).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^api_version: feature-flow/v1\nkind: patch\nbase_revision: 1\nresume_at:\nchanges:\n"));
        await patch.FillAsync((await patch.InputValueAsync()) + "  - op: remove\n    target: go_ahead\n");
        await ApplyAsync("drop the gate");
        await Expect(Plan.GetByRole(AriaRole.Status)).ToHaveTextAsync("Amend the plan: applied: plan revision 2");

        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("amend", null, "drop the gate"), (o.Key, o.StepId, o.Reason));
        Assert.Contains("base_revision: 1\n", o.Change["patch"]);
        Assert.Contains("op: remove", o.Change["patch"]);
        await Expect(Plan).ToContainTextAsync("Revision 2");

        await Button(Plan, "Amend plan").ClickAsync();
        await Expect(Plan.GetByLabel("Patch")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("base_revision: 2\n"));
    }

    [Fact]
    public async Task Controls_the_caller_may_not_use_are_not_shown()
    {
        UseWorld("none", overrides: new Dictionary<string, IReadOnlyList<string>> { ["skip"] = [], ["amend"] = [] });
        await OpenTicketRunAsync();

        await Expect(Row("implement").GetByRole(AriaRole.Button)).ToHaveTextAsync(["Set breakpoint", "Grant attempts"]);
        await Expect(Button(Plan, "Skip")).ToHaveCountAsync(0);
        await Expect(Button(Plan, "Amend plan")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task No_override_needs_a_comment()
    {
        await OpenTicketRunAsync();
        var apply = Button(Plan, "Apply");

        foreach (var open in new[] { Button(Row("implement"), "Set breakpoint"), Button(Row("implement"), "Skip"), Button(Row("implement"), "Grant attempts"), Button(Plan, "Amend plan") })
        {
            await open.ClickAsync();
            if (await Plan.GetByLabel("Attempts").CountAsync() > 0)
                await Plan.GetByLabel("Attempts").FillAsync("1");
            await Expect(Plan.GetByText("optional; shown to the next agent")).ToBeVisibleAsync();
            await Expect(apply).ToBeEnabledAsync();
            await Button(Plan, "Cancel").ClickAsync();
        }

        Assert.Empty(Engine.Overrides);
        await Button(Row("implement"), "Set breakpoint").ClickAsync();
        await apply.ClickAsync();
        await Expect(Under("implement").GetByRole(AriaRole.Status)).ToHaveTextAsync("Set breakpoint on implement: applied");
        Assert.Null(Assert.Single(Engine.Overrides).Reason);
    }

    [Fact]
    public async Task The_engine_refuses_a_breakpoint_on_a_step_that_is_waiting()
    {
        // FakeEngine refuses as the core does (Steering.cs, BreakpointError): decide's go_ahead is at its gate.
        var runId = await World.StartRunAsync();
        using var http = World.Http();
        using var refused = await http.PostAsync($"runs/{runId}/overrides",
            Json(new OverrideRequest("breakpoint", "too late", "go_ahead", new Dictionary<string, string> { ["set"] = "true" })), Ct);

        Assert.Equal(System.Net.HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("go_ahead is running or waiting; a breakpoint is set on a step before it starts", await refused.Content.ReadAsStringAsync(Ct));
        Assert.DoesNotContain(await Engine.OpenWaitsAsync(runId), w => w.Kind == "breakpoint");
    }

    static StringContent Json(object body) => new(JsonSerializer.Serialize(body, ApiJson.Options), Encoding.UTF8, "application/json");
}
