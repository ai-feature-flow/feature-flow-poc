using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// A step's Steer ▾ and Edit step (design-v2 s4.4; plan package e), in the step's detail: each opens a form that previews
/// its consequence and needs a reason, and is applied with POST /runs/{id}/overrides; go to a step and re-plan list what they
/// cancel (build-console-b2 plan s1.2); Edit step adds instructions to the next prompt (s1.3); what the core would refuse on
/// the step now is disabled with why.
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
    public async Task Go_to_a_step_lists_what_it_cancels_and_sends_the_target_with_clean_up()
    {
        await OpenStepAsync(s => s.Tickets[3], "implement");

        await Steer.ClickAsync();
        await Item("Go to a step…").ClickAsync();
        var target = Dialog.GetByLabel("Continue from");
        await Expect(target).ToHaveValueAsync("implement");
        var consequence = Dialog.GetByRole(AriaRole.Note, new() { Name = "Consequence" });
        await Expect(consequence).ToContainTextAsync("The run continues at implement");
        // Ticket 4's implementer runs with a question open, and a triage waits on a person: both are cancelled.
        var cancelled = consequence.GetByRole(AriaRole.List, new() { Name = "Cancelled" });
        await Expect(cancelled).ToContainTextAsync("implement (waiting: question)");
        await Expect(cancelled).ToContainTextAsync("verify.fix.person_triage (waiting: gate)");

        await target.SelectOptionAsync("plan.plan");
        await Dialog.GetByLabel("Clean up: reset the branch first").CheckAsync();
        await Expect(consequence).ToContainTextAsync("The run continues at plan.plan, after the branch is reset to where that step started");
        await Expect(Apply).ToBeDisabledAsync();
        await Dialog.GetByLabel("Reason (required)").FillAsync("the plan missed the export");
        await Apply.ClickAsync();

        // The engine's reply names what it cancelled; the popup stays until closed.
        await Expect(Dialog.GetByRole(AriaRole.Status)).ToContainTextAsync("applied: continues at plan.plan; cancelled", new() { Timeout = 5_000 });
        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("go_to", "plan.plan", "the plan missed the export", "true"), (o.Key, o.StepId, o.Reason, o.Change["clean_up"]));
        await Dialog.GetByRole(AriaRole.Button, new() { Name = "Done", Exact = true }).ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Re_plan_offers_agent_steps_only_and_a_decision_takes_no_attempts()
    {
        await OpenStepAsync(s => s.Tickets[0], "approve_spec");

        await Steer.ClickAsync();
        await Expect(Item("Grant attempts")).ToBeDisabledAsync();
        await Expect(Item("Grant attempts")).ToContainTextAsync("a decision uses no attempts");
        // Waiting at its gate: no breakpoint now.
        await Expect(Item("Pause before")).ToBeDisabledAsync();
        await Item("Re-plan…").ClickAsync();

        var target = Dialog.GetByLabel("Re-plan from");
        // Opened on a decision: the planner is offered first, and no decision is offered at all.
        await Expect(target).ToHaveValueAsync("plan.plan");
        await Expect(target.Locator("option[value='approve_spec']")).ToHaveCountAsync(0);
        await Expect(Dialog.GetByRole(AriaRole.Note, new() { Name = "Consequence" })).ToContainTextAsync("The run goes back to plan.plan to plan again");
        await Expect(Dialog.GetByRole(AriaRole.Note, new() { Name = "Consequence" })).ToContainTextAsync("approve_spec (waiting: gate)");
        await Dialog.GetByLabel("Reason (required)").FillAsync("split the export into its own ticket");
        await Apply.ClickAsync();

        await Expect(Dialog.GetByRole(AriaRole.Status)).ToContainTextAsync("applied: continues at plan.plan", new() { Timeout = 5_000 });
        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("replan", "plan.plan", "split the export into its own ticket", "false"), (o.Key, o.StepId, o.Reason, o.Change["clean_up"]));
    }

    [Fact]
    public async Task Instructions_for_the_next_attempt_show_where_they_go_and_send_their_text()
    {
        await OpenStepAsync(s => s.Tickets[0], "implement");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Edit step", Exact = true }).ClickAsync();
        await Dialog.GetByLabel("Change").SelectOptionAsync("instructions");
        await Dialog.GetByLabel("Instructions for the next attempt").FillAsync("Keep <b>the</b> public API unchanged.");
        // Shown as text, where the prompt has it: after why the step runs again, before the run context and task.
        var preview = Dialog.GetByLabel("Where it goes in the prompt");
        await Expect(preview).ToContainTextAsync("## A person's instructions for this step\n\nKeep <b>the</b> public API unchanged.");
        await Expect(Dialog.GetByRole(AriaRole.Note, new() { Name = "Consequence" })).ToContainTextAsync("until the step next succeeds. The plan does not change");
        // implement is not protected.
        await Expect(Dialog.GetByRole(AriaRole.Note, new() { Name = "Protected step" })).ToHaveCountAsync(0);
        await Dialog.GetByLabel("Reason (required)").FillAsync("the last attempt renamed a route");
        await Apply.ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0, new() { Timeout = 5_000 });

        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("instruct", "implement", "the last attempt renamed a route", "Keep <b>the</b> public API unchanged."), (o.Key, o.StepId, o.Reason, o.Change["text"]));
    }

    [Fact]
    public async Task Instructions_to_a_protected_step_say_so_and_carry_the_protected_variant()
    {
        await OpenStepAsync(s => s.Tickets[2], "verify.fix.verify_fix");

        await Page.GetByRole(AriaRole.Button, new() { Name = "Edit step", Exact = true }).ClickAsync();
        await Dialog.GetByLabel("Change").SelectOptionAsync("instructions");
        await Expect(Dialog.GetByRole(AriaRole.Note, new() { Name = "Protected step" })).ToContainTextAsync("verify.fix.verify_fix is protected");
        await Dialog.GetByLabel("Instructions for the next attempt").FillAsync("F-1 is fixed by the retry in InvoiceClient.");
        await Dialog.GetByLabel("Reason (required)").FillAsync("the verifier keeps declining a fixed finding");
        await Apply.ClickAsync();
        await Expect(Dialog).ToHaveCountAsync(0, new() { Timeout = 5_000 });

        // The console sends instruct; the API adds the variant the caller holds, which the core needs for a protected step.
        var o = Assert.Single(Engine.Overrides);
        Assert.Equal("instruct", o.Key);
        Assert.Contains(Model.Overrides.InstructProtected, o.Variants ?? []);
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
