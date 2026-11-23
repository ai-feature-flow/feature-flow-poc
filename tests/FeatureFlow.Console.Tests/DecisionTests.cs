using System.Text;
using System.Text.RegularExpressions;
using FeatureFlow.Providers.Abstractions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The decision popups (design-v2 s4.5; plan package e): each kind opened from its wait's route, read from what the API
/// gives it, and answered as the run takes it (POST /runs/{id}/answers, or a session's submit), the popup closing once taken.
/// </summary>
public sealed class DecisionTests(BrowserFixture browser) : BrowserTest(browser)
{
    /// <summary>The open decision popup.</summary>
    ILocator Dialog => Page.GetByRole(AriaRole.Dialog);

    ILocator Answer(string name) => Dialog.GetByRole(AriaRole.Group, new() { Name = "Answers" }).GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    ILocator Button(string name) => Dialog.GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    /// <summary>Opens the console at the wait's route, which opens its popup.</summary>
    async Task OpenAsync(DecisionScenario s, ScenarioWait wait)
    {
        await OpenAsync($"#/runs/{s.Tree.Root}/waits/{wait.RunId}/{wait.WaitId}");
        await Expect(Dialog.Locator(".ff-dec")).ToBeVisibleAsync();
    }

    async Task<string> TextAsync(string reference)
    {
        await using var stream = (await Engine.Artifacts.GetAsync(new ArtifactRef(reference), Ct)).Unwrap();
        return await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync(Ct);
    }

    async Task ClosedAsync() => await Expect(Dialog).ToHaveCountAsync(0, new() { Timeout = 5_000 });

    [Fact]
    public async Task A_spec_approval_shows_what_changed_the_requirements_with_their_cover_and_rejecting_needs_a_reason()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync(s, s.Spec);

        await Expect(Dialog.GetByRole(AriaRole.List, new() { Name = "What changed" }).GetByRole(AriaRole.Listitem))
            .ToHaveTextAsync(["R2 split into R2 and R3", "AC-002 verified by a unit test"]);
        var requirements = Dialog.GetByRole(AriaRole.Table, new() { Name = "Requirements" });
        await Expect(requirements.Locator("tbody tr")).ToHaveCountAsync(2);
        await Expect(requirements.Locator("tbody tr").Nth(0)).ToContainTextAsync("✓ AC-001");
        await Expect(requirements.Locator("tbody tr").Nth(1)).ToContainTextAsync("not covered");
        await Expect(Dialog.GetByRole(AriaRole.Table, new() { Name = "Acceptance criteria" })).ToContainTextAsync("unit tier test in acme/api");
        await Expect(Dialog.GetByRole(AriaRole.Group, new() { Name = "Answers" })).ToContainTextAsync("2 requirements · 1 acceptance criterion · 1 not covered");

        await Answer("Reject…").ClickAsync();
        await Expect(Dialog.Locator(".ff-dec-param")).ToContainTextAsync("work branch is reset to where planning started");
        var reject = Button("Reject");
        await Expect(reject).ToBeDisabledAsync();
        await Dialog.GetByLabel("Reason (required)").FillAsync("R-002 has no criterion");
        await reject.ClickAsync();

        await ClosedAsync();
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal((s.Spec.WaitId, "rejected", "R-002 has no criterion"), (answer.WaitId, answer.Outcome, answer.Reason));
    }

    [Fact]
    public async Task A_triage_is_sent_only_once_every_finding_needing_the_person_is_decided_and_sends_what_it_shows()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync(s, s.Tree.Triage);

        // Only the finding that needs the person is a card; the one a person decided and the rest are folded, read only.
        await Expect(Dialog.GetByRole(AriaRole.Article)).ToHaveCountAsync(1);
        var card = Dialog.GetByRole(AriaRole.Article, new() { Name = "Finding " + s.Needing });
        await Expect(card).ToContainTextAsync("Compilation error in initial commit");
        await Expect(card.GetByRole(AriaRole.List, new() { Name = "History of " + s.Needing })).ToContainTextAsync("declined");
        await Expect(Dialog.GetByRole(AriaRole.Table, new() { Name = "Decided by a person", IncludeHidden = true })).ToContainTextAsync(s.DecidedByPerson);
        await Expect(Dialog.GetByRole(AriaRole.Table, new() { Name = "The rest of the ledger", IncludeHidden = true })).ToContainTextAsync(s.Ledger);
        await Expect(card.GetByRole(AriaRole.Radio, new() { Name = "Mark fixed" })).ToBeDisabledAsync();

        var send = Answer("Send triage");
        await Expect(Dialog.GetByLabel("Tally")).ToContainTextAsync("0 of 1 decided");
        await Expect(send).ToBeDisabledAsync();
        await card.GetByRole(AriaRole.Radio, new() { Name = "Decline" }).ClickAsync();
        await Expect(send).ToBeDisabledAsync();
        await card.GetByLabel("Reason for " + s.Needing).FillAsync("HEAD is correct: \"a process note\"");
        await Expect(Dialog.GetByLabel("Tally")).ToContainTextAsync("1 of 1 decided");

        const string expected = "schema: 1\nentries:\n  - finding: F-E9EA33EE28\n    state: declined\n    reason: \"HEAD is correct: \\\"a process note\\\"\"\n";
        Assert.Equal(expected, await Dialog.GetByLabel("What will be sent", new() { }).TextContentAsync());
        await send.ClickAsync();

        await ClosedAsync();
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal("triaged", answer.Outcome);
        Assert.Equal(expected, await TextAsync(answer.Artifacts!["person_triage"]));
    }

    [Fact]
    public async Task A_triage_is_sent_with_the_outcome_that_produces_the_persons_triage_whatever_the_choices_order()
    {
        var s = await Engine.DecisionsAsync();
        // The same wait, its choices led by an outcome that does not produce person_triage.
        await Engine.OpenWaitAsync(s.Tree.Triage.RunId, "gate", s.Tree.Triage.StepId, "findings.triage", "Triage the findings the agent may not clear",
            ["held", "triaged", "escalate"], s.Tree.Triage.WaitId);
        await OpenAsync(s, s.Tree.Triage);

        var card = Dialog.GetByRole(AriaRole.Article, new() { Name = "Finding " + s.Needing });
        await card.GetByRole(AriaRole.Radio, new() { Name = "Decline" }).ClickAsync();
        await card.GetByLabel("Reason for " + s.Needing).FillAsync("not a defect");
        await Answer("Send triage").ClickAsync();

        await ClosedAsync();
        Assert.Equal("triaged", Assert.Single(Engine.Answers).Outcome);
    }

    [Fact]
    public async Task A_triage_the_api_refuses_shows_its_errors_in_place_and_the_popup_stays()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync(s, s.Tree.Triage);
        // A triage the form cannot build: the request is altered on its way, so the API's own check (A10) refuses it.
        await Page.RouteAsync("**/runs/*/answers", async route =>
        {
            var body = route.Request.PostData!.Replace("state: declined", "state: closed", StringComparison.Ordinal);
            await route.ContinueAsync(new() { PostData = Encoding.UTF8.GetBytes(body) });
        });

        var card = Dialog.GetByRole(AriaRole.Article, new() { Name = "Finding " + s.Needing });
        await card.GetByRole(AriaRole.Radio, new() { Name = "Decline" }).ClickAsync();
        await card.GetByLabel("Reason for " + s.Needing).FillAsync("HEAD is correct");
        await Answer("Send triage").ClickAsync();

        var refused = Dialog.GetByRole(AriaRole.Alert, new() { Name = "Refused" });
        await Expect(refused.GetByRole(AriaRole.Listitem).First).ToContainTextAsync("person_triage");
        await Expect(refused.GetByRole(AriaRole.Listitem).First).ToContainTextAsync("state");
        await Expect(Dialog).ToBeVisibleAsync();
        Assert.Empty(Engine.Answers);
    }

    [Fact]
    public async Task A_merge_shows_its_pull_request_with_files_and_checks_and_approve_merges()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync(s, s.Tree.Merge);

        await Expect(Dialog.Locator(".ff-dec-facts4")).ToContainTextAsync("#201 ↗");
        await Expect(Dialog.Locator(".ff-dec-facts4")).ToContainTextAsync("+12 −3 in 1 file");
        await Expect(Dialog.Locator(".ff-dec-facts4")).Not.ToContainTextAsync("1 files");
        await Expect(Dialog.GetByRole(AriaRole.Table, new() { Name = "Files" })).ToContainTextAsync("src/Widgets.cs");
        var log = Dialog.GetByRole(AriaRole.Table, new() { Name = "Checks" }).GetByRole(AriaRole.Link, new() { Name = "log ↗" });
        await Expect(log).ToHaveAttributeAsync("href", "https://github.com/acme/api/actions/runs/1");
        await Expect(Dialog.GetByRole(AriaRole.Link, new() { Name = "Open pull request ↗" })).ToHaveAttributeAsync("href", "https://github.com/acme/api/pull/201");
        await Expect(Button("Open review report ⧉")).ToBeDisabledAsync();
        await Expect(Dialog.GetByLabel("Ticks")).ToContainTextAsync("✓ CI passed");
        await Expect(Dialog).ToContainTextAsync("the engine may not merge here");
        await Expect(Dialog).ToContainTextAsync("No trace in this run");
        await Expect(Dialog).ToContainTextAsync("No criteria recorded.");
        // The links sit on the answer row, beside Approve and Reject (mock-up 04).
        var row = Dialog.GetByRole(AriaRole.Group, new() { Name = "Answers" });
        await Expect(row.GetByRole(AriaRole.Link, new() { Name = "Open pull request ↗" })).ToBeVisibleAsync();
        Assert.Equal(await Answer("Approve merge").EvaluateAsync<double>("b => b.getBoundingClientRect().top"),
            await row.GetByRole(AriaRole.Link, new() { Name = "Open pull request ↗" }).EvaluateAsync<double>("a => a.getBoundingClientRect().top"), 6);

        await Answer("Approve merge").ClickAsync();
        await ClosedAsync();
        Assert.Equal("approved", Assert.Single(Engine.Answers).Outcome);
    }

    [Fact]
    public async Task An_intervention_says_why_it_stopped_and_grants_attempts()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync(s, s.Tree.Intervention);

        await Expect(Dialog.GetByLabel("Why it stopped")).ToContainTextAsync("3 of 3 attempts used");
        await Expect(Dialog.GetByRole(AriaRole.Table, new() { Name = "Sent back" }).Locator("tbody tr")).ToHaveCountAsync(3);
        await Expect(Dialog.GetByLabel("Why it stopped")).ToContainTextAsync("likely to repeat");
        await Expect(Dialog.GetByRole(AriaRole.Group, new() { Name = "Answers" }).GetByRole(AriaRole.Button))
            .ToHaveTextAsync(["Retry", "Grant attempts…", "Go to a step…", "Re-plan…", "End the run…"]);

        await Answer("Go to a step…").ClickAsync();
        await Expect(Dialog.GetByLabel("Step").Locator("optgroup").First).ToBeAttachedAsync();
        await Answer("Grant attempts…").ClickAsync();
        await Dialog.GetByLabel("Grant").FillAsync("2");
        await Dialog.GetByLabel("Comment").FillAsync("one more go");
        await Button("Grant and retry").ClickAsync();

        await ClosedAsync();
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal(("grant_attempts", 2L, "one more go"), (answer.Action, answer.Attempts, answer.Reason));
    }

    [Fact]
    public async Task A_patch_approval_shows_the_plan_as_it_would_become_with_its_changes_marked()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync(s, s.Patch);

        await Expect(Dialog).ToContainTextAsync("INV-2 needs a migration first");
        var plan = Dialog.GetByRole(AriaRole.Table, new() { Name = "Proposed plan" });
        await Expect(plan.Locator("tr[data-change='added']")).ToContainTextAsync("migrate");
        await Expect(plan.Locator("tr[data-change='added']")).ToContainTextAsync("+ added");
        await Expect(plan.Locator("tr[data-change='changed']", new() { HasText = "tickets.plan" })).ToContainTextAsync("~ changed");

        await Answer("Approve").ClickAsync();
        await ClosedAsync();
        Assert.Equal("approve", Assert.Single(Engine.Answers).Action);
    }

    [Fact]
    public async Task A_question_is_answered_with_an_offered_option_or_written_text()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync(s, s.Tree.Question);

        await Expect(Dialog).ToContainTextAsync("Are XLSX exports in scope, or CSV only?");
        await Expect(Button("Send")).ToBeDisabledAsync();
        await Dialog.GetByLabel("Answer", new() { Exact = true }).FillAsync("CSV only, for now");
        await Button("Send").ClickAsync();

        await ClosedAsync();
        Assert.Equal("CSV only, for now", Assert.Single(Engine.Answers).Outcome);
    }

    [Fact]
    public async Task Clear_and_continue_at_a_breakpoint_clears_it_then_continues()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync(s, s.Tree.Breakpoint);

        await Expect(Dialog.GetByRole(AriaRole.Group, new() { Name = "Answers" }).GetByRole(AriaRole.Button).First).ToHaveTextAsync("Continue");
        await Dialog.GetByLabel("Comment").FillAsync("seen enough");
        await Answer("Clear and continue").ClickAsync();

        await ClosedAsync();
        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("breakpoint", "verify.tests", "false", "seen enough"), (o.Key, o.StepId, o.Change["set"], o.Reason));
        Assert.Equal("continue", Assert.Single(Engine.Answers).Action);
    }

    [Fact]
    public async Task A_session_card_gives_the_command_its_drafts_and_submits_a_commit()
    {
        var s = await Engine.DecisionsAsync();
        // Two checks run on the session's branch: Submit is prefilled from the newer's commit (plan risk 11).
        var sid = $"{s.Tree.Root}-session-1";
        await Engine.AppendAsync(new FeatureFlow.Providers.Abstractions.RecordBatch(SessionChecks:
        [
            new(sid, "check-1", "check_requirements", new string('b', 40), "{}", "passed", null, DateTimeOffset.UtcNow.AddMinutes(-10)),
            new(sid, "check-2", "check_requirements", new string('c', 40), "{}", "passed", null, DateTimeOffset.UtcNow.AddMinutes(-2)),
        ]));
        await OpenAsync(s, s.Tree.Session);

        await Expect(Dialog.GetByLabel("Open command")).ToHaveTextAsync(new Regex("^ff session fetch .+ --out \\.ff-session$"));
        await Expect(Dialog).ToContainTextAsync("ff/session/");
        await Expect(Dialog.Locator("summary", new() { HasText = "requirements.yaml" })).ToBeVisibleAsync();
        await Expect(Dialog.GetByLabel("Check", new() { Exact = true })).ToHaveValueAsync("check_requirements");
        await Expect(Dialog.GetByRole(AriaRole.Table, new() { Name = "Checks" }).Locator("tr")).ToHaveCountAsync(2);

        await Answer("Submit…").ClickAsync();
        var submit = Button("Submit");
        await Expect(Dialog.GetByLabel("Commit to submit")).ToHaveValueAsync(new string('c', 40));
        await Dialog.GetByLabel("Commit to submit").FillAsync("not a commit");
        await Expect(submit).ToBeDisabledAsync();
        await Dialog.GetByLabel("Commit to submit").FillAsync(new string('a', 40));
        await submit.ClickAsync();

        await ClosedAsync();
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal(("submitted", new string('a', 40)), (answer.Outcome, answer.Revision));
    }

    [Fact]
    public async Task A_min_count_decision_shows_its_progress_and_an_answer_that_waits_for_more_people_is_recorded()
    {
        UseWorld("none", [new FeatureFlow.Model.ApprovalRule("m7s.*", [FeatureFlow.Model.PeopleRoles.Approver], 2, false, new FeatureFlow.Model.Cap<bool>(false, false))]);
        var runId = await World.StartRunAsync();
        var route = $"#/runs/{runId}/waits/{runId}/gate-1";
        await OpenAsync(route);
        await Expect(Dialog).ToContainTextAsync("0 of 2 people");

        await Answer("approved").ClickAsync();
        await ClosedAsync();
        Assert.Empty(Engine.Answers);

        await GoAsync("#/runs");
        await GoAsync(route);
        await Expect(Dialog).ToContainTextAsync("approved: 1 of 2 people (local)", new() { Timeout = 5_000 });
    }

    [Fact]
    public async Task A_caller_who_may_not_answer_is_told_so_and_offered_no_answer()
    {
        // No one answers a wait of a run that is no longer running.
        await Engine.RunAsync("run-done", status: "completed");
        await Engine.OpenWaitAsync("run-done");
        await OpenAsync("#/runs/run-done/waits/run-done/gate-1");

        await Expect(Dialog).ToContainTextAsync("You may not answer this.");
        await Expect(Dialog.GetByRole(AriaRole.Group, new() { Name = "Answers" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task A_decision_that_has_closed_says_so_and_offers_no_answer()
    {
        var s = await Engine.DecisionsAsync();
        await OpenAsync(s, s.Tree.Question);
        await Expect(Answer("Write an answer")).ToBeVisibleAsync();

        await Engine.CloseWaitAsync(s.Tree.Question.RunId, s.Tree.Question.WaitId);

        await Expect(Dialog).ToContainTextAsync("This decision is closed.", new() { Timeout = 5_000 });
        await Expect(Dialog.GetByRole(AriaRole.Group, new() { Name = "Answers" })).ToHaveCountAsync(0);
    }
}
