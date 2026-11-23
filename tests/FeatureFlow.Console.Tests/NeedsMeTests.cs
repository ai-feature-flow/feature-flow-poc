using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// Needs me (<c>#/</c>, design-v2 s4.2, mock-up 06): every open wait the caller may answer across the run trees, filtered by
/// kind; a question answered in place, any other wait in its popup; Go to step; the caller's recent answers, folded. The
/// tree is <see cref="FakeEngine.ProjectTreeAsync"/>'s.
/// </summary>
public sealed class NeedsMeTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Item(ScenarioWait wait) => Page.Locator($"article[data-run='{wait.RunId}'][data-wait='{wait.WaitId}']");

    ILocator Kind(string name) => Page.GetByRole(AriaRole.Group, new() { Name = "Kind" }).GetByRole(AriaRole.Button, new() { Name = name, Exact = true });

    [Fact]
    public async Task A_question_is_answered_in_place_and_then_shows_in_the_recent_answers()
    {
        var tree = await Engine.ProjectTreeAsync();
        var page = await OpenAsync();
        var question = Item(tree.Question);

        await Expect(question).ToContainTextAsync("question");
        await Expect(question).ToContainTextAsync("Are XLSX exports in scope, or CSV only?");
        // Its path: the tree's root, the epic and the ticket, each with its kind, and the steps between them.
        await Expect(question.Locator(".ff-item-path")).ToHaveTextAsync(
            "project#100 Billing › epics › epicpayments › tickets › ticketPAY-2 Ticket PAY-2 › implement");
        await Expect(question.GetByRole(AriaRole.Button, new() { Name = "Review and answer" })).ToHaveCountAsync(0);

        await question.GetByRole(AriaRole.Textbox).FillAsync("CSV only for now");
        await question.GetByRole(AriaRole.Button, new() { Name = "Send" }).ClickAsync();

        await Expect(question).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal((tree.Question.WaitId, "CSV only for now", null), (answer.WaitId, answer.Outcome, answer.Reason));
        var recent = page.Locator(".ff-answered");
        await Expect(recent.Locator("summary")).ToHaveTextAsync("Answered by you recently (1)");
        await Expect(recent.Locator($"tr[data-wait='{tree.Question.WaitId}']")).ToContainTextAsync("CSV only for now");
    }

    [Fact]
    public async Task An_offered_option_answers_the_question_with_itself()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync();
        var question = Item(tree.Question);

        await question.GetByRole(AriaRole.Button, new() { Name = "CSV and XLSX", Exact = true }).ClickAsync();

        await Expect(question).ToHaveCountAsync(0, new() { Timeout = 5_000 });
        var answer = Assert.Single(Engine.Answers);
        Assert.Equal((tree.Question.WaitId, "CSV and XLSX"), (answer.WaitId, answer.Outcome));
    }

    [Fact]
    public async Task The_items_are_filtered_by_kind()
    {
        var tree = await Engine.ProjectTreeAsync();
        var page = await OpenAsync();
        var items = page.Locator("article.ff-item");

        await Expect(Kind("All")).ToHaveAttributeAsync("aria-pressed", "true");
        await Expect(items).ToHaveCountAsync(8);
        await Expect(page.GetByRole(AriaRole.Heading, new() { Name = "Needs me 8" })).ToBeVisibleAsync();

        foreach (var (kind, waits) in new (string, ScenarioWait[])[]
                 {
                     ("Approvals", [tree.Session, tree.PatchApproval, tree.SpecApproval, tree.Merge]),
                     ("Interventions", [tree.Breakpoint, tree.Intervention]),
                     ("Questions", [tree.Question]),
                     ("Triage", [tree.Triage]),
                 })
        {
            await Kind(kind).ClickAsync();
            await Expect(Kind(kind)).ToHaveAttributeAsync("aria-pressed", "true");
            await Expect(items).ToHaveCountAsync(waits.Length);
            foreach (var wait in waits)
                await Expect(Item(wait)).ToBeVisibleAsync();
        }

        await Kind("All").ClickAsync();
        await Expect(items).ToHaveCountAsync(8);
    }

    [Fact]
    public async Task Review_and_answer_opens_the_waits_popup()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync();
        var item = Item(tree.Intervention);
        var title = await item.GetAttributeAsync("aria-label");
        Assert.False(string.IsNullOrEmpty(title));
        await Expect(item.Locator(".ff-item-title")).ToHaveTextAsync(title!);

        await item.GetByRole(AriaRole.Button, new() { Name = "Review and answer" }).ClickAsync();

        await Expect(Popup(title!)).ToBeVisibleAsync();
        Assert.Empty(Engine.Answers);
    }

    [Fact]
    public async Task Go_to_step_opens_the_workspace_at_the_waits_step()
    {
        var tree = await Engine.ProjectTreeAsync();
        var page = await OpenAsync();

        await Item(tree.Intervention).GetByRole(AriaRole.Link, new() { Name = "Go to step →" }).ClickAsync();

        await Expect(page).ToHaveURLAsync(World.ConsoleUrl + $"#/runs/{tree.Root}/steps/{tree.Intervention.RunId}/{tree.Intervention.StepId}");
        await Expect(page.Locator("main[data-screen='workspace']")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Recent_answers_are_folded_until_opened()
    {
        var tree = await Engine.ProjectTreeAsync();
        // An earlier answer of this person's, on a plan approval now open again.
        await Engine.AnsweredAsync(tree.SpecApproval.RunId, tree.SpecApproval.WaitId, "approved", "the spec is complete");
        var page = await OpenAsync();
        var recent = page.Locator(".ff-answered");

        await Expect(recent.Locator("summary")).ToHaveTextAsync("Answered by you recently (1)");
        var row = recent.Locator($"tr[data-wait='{tree.SpecApproval.WaitId}']");
        await Expect(row).Not.ToBeVisibleAsync();

        await recent.Locator("summary").ClickAsync();

        await Expect(row).ToBeVisibleAsync();
        await Expect(row).ToContainTextAsync("ticketINV-1 Ticket INV-1 › approve_spec");
        await Expect(row).ToContainTextAsync("approved: “the spec is complete”");
    }

    [Fact]
    public async Task Nothing_needs_you_says_so()
    {
        await OpenAsync();

        await Expect(Page.GetByText("Nothing needs you now.")).ToBeVisibleAsync();
        await Expect(Page.Locator(".ff-answered")).ToHaveCountAsync(0);
    }
}
