using FeatureFlow.Providers.Abstractions;
using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// A step's Findings tab (design-v2 s4.4; mock-up 02), from <c>GET /runs/{id}/steps/{step}</c>: the findings its finding
/// steps recorded, each with its ledger state and the last thing done to it here; a link to the run's ledger, the review
/// report; and a finding event written while the tab is open reaching it over the change stream, with no reload.
/// </summary>
public sealed class FindingsTabTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Middle => Page.GetByRole(AriaRole.Region, new() { Name = "Selected" });

    ILocator Rows => Middle.GetByRole(AriaRole.Table, new() { Name = "Findings" }).Locator("tbody tr");

    async Task OpenFindingsAsync(string runId, string stepId)
    {
        await OpenAsync($"#/runs/{runId}/steps/{runId}/{stepId}");
        await Middle.GetByRole(AriaRole.Tab, new() { Name = "Findings" }).ClickAsync();
    }

    [Fact]
    public async Task A_reviewers_findings_show_with_their_ledger_state_and_the_ledger_opens_the_review_report()
    {
        var s = await Engine.ReviewScenarioAsync();
        await OpenFindingsAsync(s.RunId, s.ReviewerSteps[0]);

        // The correctness reviewer raised one finding, recorded by its record step; the fix loop fixed it.
        await Expect(Rows).ToHaveCountAsync(1);
        var row = Rows.First;
        await Expect(row).ToHaveAttributeAsync("data-finding", s.Fixed);
        await Expect(row).ToContainTextAsync("Page size is not applied");
        await Expect(row).ToContainTextAsync("high, blocking");
        await Expect(row.Locator("td").Nth(3)).ToHaveTextAsync("fixed");
        await Expect(Middle.GetByRole(AriaRole.Tab, new() { Name = "Findings (1)" })).ToBeVisibleAsync();

        // The ledger is the review report, in a window of its own.
        var opened = Page.Context.WaitForPageAsync();
        await Middle.GetByRole(AriaRole.Link, new() { Name = "The run's full ledger ⧉" }).ClickAsync();
        var report = await opened;
        await Expect(report.GetByRole(AriaRole.Region, new() { Name = "Findings" })).ToContainTextAsync("Page size is not applied");
        Assert.EndsWith($"#/runs/{s.RunId}/review", report.Url);
    }

    [Fact]
    public async Task A_finding_event_written_while_the_tab_is_open_appears_without_a_reload()
    {
        var s = await Engine.ReviewScenarioAsync();
        await OpenFindingsAsync(s.RunId, s.ReviewerSteps[1]);
        await Expect(Middle).ToContainTextAsync("The step has no findings.");
        await Page.EvaluateAsync("() => { window.notReloaded = true; }");

        // The design reviewer raises a finding in a third pass, recorded by its record step: a step_changed for a finding step.
        var raised = new ReportedFinding("design", "The handler mixes paging and filtering", "Paging and filtering are one method.", "low", false, "reasoned",
            new FindingLocation(FakeEngine.ScenarioRepository, "src/Invoices/InvoiceEndpoints.cs", "ListInvoices", null, null), null, null);
        var id = Fingerprints.LedgerId(s.RunId, Fingerprints.Of(raised));
        await Engine.FindingEventAsync(s.RunId, id, FindingEventKinds.Raised, FindingStates.Open, s.ReviewerSteps[1], FakeEngine.RecordStep(1),
            execution: 3, raised: raised);

        await Expect(Rows).ToHaveCountAsync(1, new() { Timeout = 5_000 });
        await Expect(Rows.First).ToContainTextAsync("The handler mixes paging and filtering");
        await Expect(Rows.First.Locator("td").Nth(3)).ToHaveTextAsync("open");
        await Expect(Rows.First.Locator("td").Nth(4)).ToContainTextAsync("raised");
        Assert.True(await EvaluateAsync<bool>("window.notReloaded === true"), "the page should not have reloaded");

        // A later event on it, a triage, changes its state in place.
        await Engine.FindingEventAsync(s.RunId, id, FindingEventKinds.Triaged, FindingStates.Declined, "verify.fix.triage", FakeEngine.RecordStep(1),
            "Splitting it is a refactor for another ticket", execution: 3);
        await Expect(Rows.First.Locator("td").Nth(3)).ToHaveTextAsync("declined", new() { Timeout = 5_000 });
    }
}
