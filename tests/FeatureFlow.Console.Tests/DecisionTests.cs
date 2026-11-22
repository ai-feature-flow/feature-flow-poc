using System.Net.Http.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using Core = FeatureFlow.Interpreter;
using FeatureFlow.Providers.Abstractions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The decision form (WaitForm, LOG 463): each kind of wait answered as the API takes it (POST /runs/{id}/answers), with
/// an optional comment, which a choice that routes the step as a failure, or ends the run, needs (LOG 502).
/// </summary>
public sealed class DecisionTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Form(string waitId) => Page.GetByRole(AriaRole.Region, new() { Name = $"Wait {waitId}" });

    /// <summary>A select's options, in order, once it has them.</summary>
    static Task ExpectOptionsAsync(ILocator select, IEnumerable<string> options) =>
        Expect(select.Locator("option")).ToHaveTextAsync(options.ToArray());

    /// <summary>A started run (so its plan is indexed) with an open wait of <paramref name="kind"/> on its page.</summary>
    async Task<(string RunId, ILocator Form)> OpenWaitAsync(string kind)
    {
        var runId = await World.StartRunAsync();
        var waitId = await Engine.OpenWaitAsync(runId, kind, prompt: $"A {kind}");
        await OpenAsync("#/runs/" + runId);
        return (runId, Form(waitId));
    }

    [Fact]
    public async Task An_approval_needs_no_comment_and_a_rejection_is_sent_only_with_one()
    {
        var runId = await World.StartRunAsync();
        var approve = await Engine.OpenWaitAsync(runId);
        var reject = await Engine.OpenWaitAsync(runId);
        await OpenAsync("#/runs/" + runId);

        await Expect(Form(approve).GetByText("optional; shown to the next agent")).ToBeVisibleAsync();
        await Form(approve).GetByRole(AriaRole.Button, new() { Name = "approved", Exact = true }).ClickAsync();
        await Expect(Form(approve)).ToHaveCountAsync(0, new() { Timeout = 5_000 });

        var rejected = Form(reject).GetByRole(AriaRole.Button, new() { Name = "rejected (comment required)", Exact = true });
        await Expect(rejected).ToBeDisabledAsync();
        await Form(reject).GetByLabel("Comment").FillAsync("  ");
        await Expect(rejected).ToBeDisabledAsync();
        await Form(reject).GetByLabel("Comment").FillAsync("requirements omit the audit log");
        await rejected.ClickAsync();
        await Expect(Form(reject)).ToHaveCountAsync(0, new() { Timeout = 5_000 });

        Assert.Equal([("approved", null), ("rejected", "requirements omit the audit log")], Engine.Answers.Select(a => (a.Outcome, a.Reason)));
    }

    [Fact]
    public async Task A_patch_approvals_prompt_is_shown_as_text_line_by_line_and_approve_sends_the_action()
    {
        // The core renders the prompt as plain text: the agent's reason, why it needs approval, and the changes as YAML.
        const string prompt = "implement proposes a change to the plan (revision 1).\nReason: <b>orders</b> needs a <script>window.ffx = 1</script> migration\n"
                              + "Needs approval: implement has planning.review: required\nContinues at: migrate\nChanges:\n- op: insert\n  after: implement\n  step: { id: migrate }";
        var runId = await World.StartRunAsync();
        var waitId = await Engine.OpenWaitAsync(runId, "patch_approval", stepId: "go_ahead", prompt: prompt);
        await OpenAsync("#/runs/" + runId);
        var form = Form(waitId);

        var shown = form.Locator(".ff-wait-prompt");
        await Expect(shown).ToBeVisibleAsync();
        Assert.Equal(prompt, await shown.TextContentAsync());
        Assert.Equal("pre-wrap", await shown.EvaluateAsync<string>("e => getComputedStyle(e).whiteSpace"));
        Assert.Equal(0, await shown.Locator("*").CountAsync());
        Assert.True(await EvaluateAsync<bool>("window.ffx === undefined"));

        await form.GetByRole(AriaRole.Button, new() { Name = "approve", Exact = true }).ClickAsync();
        await Expect(form).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        Assert.Equal("approve", Assert.Single(Engine.Answers).Action);
    }

    [Fact]
    public async Task An_intervention_offers_its_recoveries_and_go_to_picks_a_step_from_the_plan()
    {
        var (runId, form) = await OpenWaitAsync("intervention");
        using var http = World.Http();
        var plan = await http.GetFromJsonAsync<PlanView>($"runs/{runId}/plan", ApiJson.Options, Ct);

        var recovery = form.GetByLabel("Recovery");
        await ExpectOptionsAsync(recovery, Core.Recoveries.For(Core.WaitKind.Intervention).Select(r => r == "end" ? "end (comment required)" : r));
        await Expect(form.GetByLabel("Step")).ToHaveCountAsync(0);
        await recovery.SelectOptionAsync("go_to");
        await ExpectOptionsAsync(form.GetByLabel("Step"), plan!.Steps.Select(s => s.Id));
        await form.GetByLabel("Step").SelectOptionAsync("go_ahead");
        await form.GetByLabel("Comment").FillAsync("try the decision again");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();

        await Expect(form).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal(("go_to", "go_ahead", "try the decision again"), (answer.Action, answer.StepId, answer.Reason));
    }

    [Fact]
    public async Task A_breakpoint_offers_continue_which_needs_no_comment()
    {
        var (_, form) = await OpenWaitAsync("breakpoint");

        var recovery = form.GetByLabel("Recovery");
        await ExpectOptionsAsync(recovery, Core.Recoveries.For(Core.WaitKind.Breakpoint).Select(r => r == "end" ? "end (comment required)" : r));
        await Expect(recovery).ToHaveValueAsync("continue");
        await form.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();

        await Expect(form).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        Assert.Equal(("continue", null), (Assert.Single(Engine.Answers).Action, Engine.Answers[0].Reason));
    }

    [Fact]
    public async Task Grant_attempts_and_end_send_their_arguments()
    {
        var runId = await World.StartRunAsync();
        var grant = await Engine.OpenWaitAsync(runId, "intervention");
        var end = await Engine.OpenWaitAsync(runId, "intervention");
        await OpenAsync("#/runs/" + runId);

        await Form(grant).GetByLabel("Recovery").SelectOptionAsync("grant_attempts");
        await Form(grant).GetByLabel("Attempts").FillAsync("3");
        await Form(grant).GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();
        await Expect(Form(grant)).ToHaveCountAsync(0, new() { Timeout = 5_000 });

        await Form(end).GetByLabel("Recovery").SelectOptionAsync("end");
        await Form(end).GetByLabel("Status").SelectOptionAsync("partial");
        var send = Form(end).GetByRole(AriaRole.Button, new() { Name = "Send" });
        await Expect(send).ToBeDisabledAsync();
        await Form(end).GetByLabel("Comment").FillAsync("enough");
        await send.ClickAsync();
        await Expect(Form(end)).ToHaveCountAsync(0, new() { Timeout = 5_000 });

        Assert.Equal([("grant_attempts", 3L, null, null), ("end", 0L, "partial", "enough")], Engine.Answers.Select(a => (a.Action!, a.Attempts, a.Status, a.Reason)));
    }

    [Fact]
    public async Task A_gate_links_the_artifacts_its_step_consumed()
    {
        var runId = await World.StartRunAsync();
        var design = await Engine.ProduceAsync(runId, "draft", "design.md", "the design");
        var waitId = await Engine.OpenWaitAsync(runId, consumed: [new ConsumedRef("design", "design.md", design), new ConsumedRef("notes", "notes.md", null)]);
        await OpenAsync("#/runs/" + runId);

        var form = Form(waitId);
        await Expect(form.GetByRole(AriaRole.Link, new() { Name = "design.md" })).ToHaveAttributeAsync("href", $"#/runs/{runId}/artifacts/{Uri.EscapeDataString(design)}");
        await Expect(form.GetByText("notes.md")).ToBeVisibleAsync();
        await Expect(form.GetByRole(AriaRole.Link, new() { Name = "notes.md" })).ToHaveCountAsync(0);
        // The route the plan gives each outcome is shown beside its button.
        await Expect(form.GetByRole(AriaRole.Listitem).Filter(new() { HasText = "approved" })).ToContainTextAsync("→ end(completed)");
    }

    [Fact]
    public async Task Min_count_progress_is_shown()
    {
        UseWorld("none", [new ApprovalRule("m7s.*", [PeopleRoles.Approver], 2, false, new Cap<bool>(false, false))]);
        var runId = await World.StartRunAsync();
        await OpenAsync("#/runs/" + runId);

        var form = Form("gate-1");
        await Expect(form).ToContainTextAsync("0 of 2 people");
        await form.GetByLabel("Comment").FillAsync("fine by me");
        await form.GetByRole(AriaRole.Button, new() { Name = "approved" }).ClickAsync();

        // The answer is recorded (202) and waits for a second person: shown as pending, and the wait stays open.
        await Expect(form).ToContainTextAsync("Pending: 1 of 2 people have answered this way");
        await Expect(form).ToContainTextAsync("approved: 1 of 2 people (local)", new() { Timeout = 5_000 });
        Assert.Empty(Engine.Answers);
    }

    [Fact]
    public async Task A_question_is_answered_with_text()
    {
        var (_, form) = await OpenWaitAsync("question");

        var send = form.GetByRole(AriaRole.Button, new() { Name = "Send" });
        await Expect(send).ToBeDisabledAsync();
        await form.GetByLabel("Answer").FillAsync("use the existing table");
        await send.ClickAsync();

        await Expect(form).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal(("use the existing table", null), (answer.Outcome, answer.Reason));
    }

    [Fact]
    public async Task A_caller_who_may_not_answer_sees_the_form_read_only()
    {
        // No one answers a wait of a run that is no longer running.
        await Engine.RunAsync("run-done", status: "completed");
        await Engine.OpenWaitAsync("run-done");
        await OpenAsync("#/runs/run-done");

        var form = Form("gate-1");
        await Expect(form).ToContainTextAsync("You may not answer this.");
        await Expect(form.GetByLabel("Comment")).ToBeDisabledAsync();
        await Expect(form.GetByRole(AriaRole.Button, new() { Name = "approved" })).ToBeDisabledAsync();
    }
}
