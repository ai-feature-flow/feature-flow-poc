using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The review report page (A14; design-v2 s4.6), <c>#/runs/{tree}/review</c>, from <c>GET /runs/{id}/review</c> over
/// <see cref="FakeEngine.ReviewScenarioAsync"/>: the current pass's five angles with their verdicts and what each checked,
/// the earlier pass folded with "not recorded" for a report written without a summary, the findings with their states and
/// reasons, the external review, the trace and the pull request; opened in a window of its own from the merge popup and
/// from the run header.
/// </summary>
public sealed class ReviewReportTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Box(string name) => Page.GetByRole(AriaRole.Region, new() { Name = name, Exact = true });

    ILocator Angle(ILocator table, string angle) => table.Locator($"tr[data-angle='{angle}']");

    [Fact]
    public async Task The_report_shows_each_angle_of_each_pass_the_findings_with_reasons_the_external_review_and_the_trace()
    {
        var s = await Engine.ReviewScenarioAsync();
        await OpenAsync($"#/runs/{s.RunId}/review");

        await Expect(Page.Locator(".ff-review h1")).ToContainTextAsync("Review:");
        await Expect(Page.GetByRole(AriaRole.Link, new() { Name = "acme/api#207 ↗" })).ToHaveAttributeAsync("href", "https://github.com/acme/api/pull/207");

        // The current review is pass 2: five angles, each with a verdict and what it checked.
        var current = Box("Current review");
        await Expect(current.GetByRole(AriaRole.Heading)).ToHaveTextAsync("Current review: pass 2");
        var angles = current.GetByRole(AriaRole.Table, new() { Name = "Angles of pass 2" });
        await Expect(angles.Locator("tbody tr")).ToHaveCountAsync(5);
        foreach (var angle in s.Angles)
            await Expect(Angle(angles, angle)).Not.ToContainTextAsync("not recorded");
        await Expect(Angle(angles, "correctness")).ToContainTextAsync("page_size is now applied and tested; no other defect found.");
        await Expect(Angle(angles, "correctness").Locator("li")).ToHaveTextAsync(["src/Invoices/InvoiceEndpoints.cs", "AC-001", "dotnet test --filter Invoices"]);
        await Expect(Angle(angles, "security").Locator("td").Nth(3)).ToHaveTextAsync("1");

        // Pass 1, folded: security's report recorded no summary.
        var earlier = Box("Earlier passes");
        await earlier.Locator("summary").Filter(new() { HasText = "Pass 1" }).ClickAsync();
        var first = earlier.GetByRole(AriaRole.Table, new() { Name = "Angles of pass 1" });
        await Expect(Angle(first, "security")).ToContainTextAsync("not recorded");
        await Expect(Angle(first, "correctness")).ToContainTextAsync("The endpoint ignores page_size, so AC-001 is not met.");
        await Expect(Angle(first, "correctness").Locator("td").Nth(3)).ToHaveTextAsync("1 (1 blocking)");

        // The findings, with their states and the latest reason and who gave it.
        var findings = Box("Findings");
        await Expect(findings.Locator("tbody tr")).ToHaveCountAsync(3);
        var person = findings.Locator($"tr[data-finding='{s.DeclinedByPerson}']");
        await Expect(person).ToContainTextAsync("declined");
        await Expect(person).ToContainTextAsync("Covered by InvoicesApiTests.Pages_are_limited; a unit test would repeat it");
        await Expect(person).ToContainTextAsync("local");
        await Expect(person).ToContainTextAsync("pass 1");
        var triager = findings.Locator($"tr[data-finding='{s.DeclinedByTriager}']");
        await Expect(triager).ToContainTextAsync("Ids are scoped to the caller's organisation");
        await Expect(triager).ToContainTextAsync("raised again");
        await Expect(findings.Locator($"tr[data-finding='{s.Fixed}'] .ff-pill")).ToHaveTextAsync("fixed");

        // The external review's finding, open.
        var external = Box("External review");
        await Expect(external.Locator("tbody tr")).ToHaveCountAsync(1);
        await Expect(external.Locator($"tr[data-finding='{s.External}']")).ToContainTextAsync("The page_size parameter is not documented");
        await Expect(external.Locator(".ff-pill")).ToHaveTextAsync("open");

        // The trace: R-001 proved, R-002 with its gap.
        var trace = Box("Trace");
        await Expect(trace.Locator("tbody tr")).ToHaveCountAsync(2);
        await Expect(trace.Locator("tbody tr").Nth(0)).ToContainTextAsync("proved");
        await Expect(trace.Locator("tbody tr").Nth(1)).ToContainTextAsync("AC-002: no test is claimed");
        await Expect(Box("Summary")).ToContainTextAsync("1 of 2 requirements traced");
    }

    [Fact]
    public async Task The_merge_popup_and_the_run_header_open_the_report_in_a_window_of_its_own()
    {
        var s = await Engine.ReviewScenarioAsync();
        await OpenAsync($"#/runs/{s.RunId}/waits/{s.Merge.RunId}/{s.Merge.WaitId}");
        var dialog = Page.GetByRole(AriaRole.Dialog);
        await Expect(dialog.Locator(".ff-dec")).ToBeVisibleAsync();

        var opened = Page.Context.WaitForPageAsync();
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Open review report ⧉" }).ClickAsync();
        var window = await opened;
        Assert.EndsWith($"#/runs/{s.RunId}/review", window.Url);
        await Expect(window.GetByRole(AriaRole.Region, new() { Name = "Current review" })).ToBeVisibleAsync();
        // The merge popup stays open beside it.
        await Expect(dialog).ToBeVisibleAsync();
        await window.CloseAsync();

        await Page.Keyboard.PressAsync("Escape");
        await GoAsync($"#/runs/{s.RunId}");
        opened = Page.Context.WaitForPageAsync();
        await Page.GetByRole(AriaRole.Region, new() { Name = "Selected" }).GetByRole(AriaRole.Button, new() { Name = "Review report ⧉" }).ClickAsync();
        window = await opened;
        Assert.EndsWith($"#/runs/{s.RunId}/review", window.Url);
        await Expect(window.GetByRole(AriaRole.Region, new() { Name = "Findings" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task A_run_with_no_review_says_so()
    {
        await Engine.RunAsync("run-1");
        await OpenAsync("#/runs/run-1/review");

        await Expect(Page.GetByRole(AriaRole.Note)).Not.ToBeEmptyAsync();
        await Expect(Box("Current review")).ToHaveCountAsync(0);
        await Expect(Box("Findings")).ToContainTextAsync("The review raised no findings.");
        await Expect(Box("Trace")).ToContainTextAsync("No trace in this run");
    }
}
