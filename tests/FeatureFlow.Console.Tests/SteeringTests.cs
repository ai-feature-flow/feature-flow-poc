using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// A step's Steer ▾ and Edit step (design-v2 s4.4; plan package e), in the step's detail: each opens a form that previews
/// its consequence and needs a reason, and is applied with POST /runs/{id}/overrides; go to a step and re-plan are disabled
/// in B1 with their reason; what the core would refuse on the step now is disabled with why.
/// </summary>
public sealed class SteeringTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Dialog => Page.GetByRole(AriaRole.Dialog);

    ILocator Steer => Page.GetByRole(AriaRole.Button, new() { Name = "Steer ▾", Exact = true });

    ILocator Item(string name) => Page.GetByRole(AriaRole.Menu, new() { Name = "Steer" }).GetByRole(AriaRole.Menuitem, new() { Name = name });

    ILocator Apply => Dialog.GetByRole(AriaRole.Button, new() { Name = "Apply", Exact = true });

    /// <summary>The scenario, open at a step of one of its runs.</summary>
    async Task<ProjectScenario> OpenStepAsync(Func<ProjectScenario, string> run, string stepId)
    {
        var s = await Engine.ProjectTreeAsync();
        await OpenAsync($"#/runs/{s.Root}/steps/{run(s)}/{stepId}");
        await Expect(Steer).ToBeVisibleAsync();
        return s;
    }

    [Fact]
    public async Task Pause_before_previews_its_consequence_and_is_applied_only_with_a_reason()
    {
        await OpenStepAsync(s => s.Tickets[0], "implement");

        await Steer.ClickAsync();
        await Item("Pause before").ClickAsync();
        await Expect(Dialog.GetByRole(AriaRole.Note, new() { Name = "Consequence" })).ToContainTextAsync("The run pauses before implement starts");
        await Expect(Apply).ToBeDisabledAsync();
        await Dialog.GetByLabel("Reason (required)").FillAsync("   ");
        await Expect(Apply).ToBeDisabledAsync();
        await Dialog.GetByLabel("Reason (required)").FillAsync("look before it goes");
        await Apply.ClickAsync();

        await Expect(Dialog).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("breakpoint", "implement", "look before it goes", "true"), (o.Key, o.StepId, o.Reason, o.Change["set"]));
    }

    [Fact]
    public async Task Go_to_a_step_and_re_plan_are_disabled_with_their_reason_and_a_decision_takes_no_attempts()
    {
        await OpenStepAsync(s => s.Tickets[0], "approve_spec");

        await Steer.ClickAsync();
        await Expect(Item("Go to a step")).ToBeDisabledAsync();
        await Expect(Item("Go to a step")).ToContainTextAsync("only at an intervention (B2)");
        await Expect(Item("Re-plan")).ToBeDisabledAsync();
        await Expect(Item("Grant attempts")).ToBeDisabledAsync();
        await Expect(Item("Grant attempts")).ToContainTextAsync("a decision uses no attempts");
        // Waiting at its gate: no breakpoint now.
        await Expect(Item("Pause before")).ToBeDisabledAsync();
    }

    [Fact]
    public async Task Skip_says_where_the_run_goes_and_grant_attempts_what_the_budget_becomes()
    {
        await OpenStepAsync(s => s.Tickets[0], "implement");

        await Steer.ClickAsync();
        await Item("Skip").ClickAsync();
        await Dialog.GetByLabel("Outcome").SelectOptionAsync("implemented");
        await Expect(Dialog.GetByRole(AriaRole.Note, new() { Name = "Consequence" })).ToContainTextAsync("ending implemented, and the run goes on to verify.tests");
        await Dialog.GetByLabel("Reason (required)").FillAsync("done by hand");
        await Apply.ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0, new() { Timeout = 5_000 });

        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("skip", "implement", "done by hand", "implemented"), (o.Key, o.StepId, o.Reason, o.Change["outcome"]));
    }

    [Fact]
    public async Task Grant_attempts_shows_the_budget_it_leaves()
    {
        await OpenStepAsync(s => s.Tickets[2], "verify.fix.verify_fix");

        await Steer.ClickAsync();
        await Item("Grant attempts").ClickAsync();
        await Dialog.GetByLabel("Attempts").FillAsync("2");
        await Expect(Dialog.GetByRole(AriaRole.Note, new() { Name = "Consequence" })).ToContainTextAsync("3 of 3 used, 5 allowed after this");
    }

    [Fact]
    public async Task Edit_step_starts_from_the_steps_limits_so_raising_one_keeps_the_others()
    {
        await OpenStepAsync(s => s.Tickets[0], "implement");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Edit step", Exact = true }).ClickAsync();
        // Prefilled as the plan has them (implement's parameters, then its limits), so a set keeps what is not changed.
        await Expect(Dialog.GetByLabel("Parameters, as YAML")).ToHaveValueAsync("ticket_id:\n  expr: params.ticket_id");
        await Dialog.GetByLabel("Change").SelectOptionAsync("limits");
        var limits = Dialog.GetByLabel("Limits, as YAML");
        await Expect(limits).ToHaveValueAsync("questions:\n  max: 3\n  timeout: \"24h\"");
        await Expect(Apply).ToBeDisabledAsync();
        await limits.FillAsync(await limits.InputValueAsync() + "\nattempts: 4");
        await Expect(Dialog.GetByLabel("Patch")).ToContainTextAsync("op: set");
        await Expect(Dialog.GetByRole(AriaRole.Note, new() { Name = "Consequence" })).ToContainTextAsync("From its next attempt, implement's limits become exactly what is below");
        await Expect(Dialog.GetByLabel("Now", new() { Exact = true })).ToHaveTextAsync("questions:\n  max: 3\n  timeout: \"24h\"");
        await Expect(Dialog.GetByLabel("After", new() { Exact = true })).ToHaveTextAsync("questions:\n  max: 3\n  timeout: \"24h\"\nattempts: 4");
        // The prompt comes from the step's role and task: an amend cannot set it.
        await Expect(Dialog.GetByLabel("Change").Locator("option[value='prompt']")).ToBeDisabledAsync();
        await Dialog.GetByLabel("Reason (required)").FillAsync("it needs more tries");
        await Apply.ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0, new() { Timeout = 5_000 });

        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("amend", null, "it needs more tries"), (o.Key, o.StepId, o.Reason));
        Assert.Contains("    target: implement\n    field: limits\n    value:\n      questions:\n        max: 3\n        timeout: \"24h\"\n      attempts: 4\n", o.Change["patch"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_steer_the_engine_refuses_shows_why_in_place()
    {
        var s = await OpenStepAsync(s => s.Tickets[0], "implement");

        await Steer.ClickAsync();
        await Item("Pause before").ClickAsync();
        // The run ends while the form is open: the engine refuses what was valid when it opened.
        await Engine.RunWithAsync(s.Tickets[0], status: "completed", parentRunId: s.Epics[0], level: "ticket", parentStepId: "tickets.children_0");
        await Dialog.GetByLabel("Reason (required)").FillAsync("try it");
        await Apply.ClickAsync();

        await Expect(Dialog.GetByRole(AriaRole.Alert)).ToContainTextAsync("the run has ended", new() { Timeout = 5_000 });
        await Expect(Dialog).ToBeVisibleAsync();
    }
}
