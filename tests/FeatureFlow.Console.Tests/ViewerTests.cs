using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The viewer (design-v2 s4.4; A7): an artifact opens in one preview tab that the next replaces; 📌 keeps a tab; a double-click
/// or ⤢ opens it full size; YAML shows as tables and lists, its source and previous revision a click away.
/// </summary>
public sealed class ViewerTests(BrowserFixture browser) : BrowserTest(browser)
{
    static string P(string part) => Uri.EscapeDataString(part);

    ILocator Viewer => Page.GetByRole(AriaRole.Complementary, new() { Name = "Viewer" });

    ILocator Tabs => Viewer.GetByRole(AriaRole.Tab);

    async Task<ProjectScenario> OpenPlanStepAsync()
    {
        var tree = await Engine.ProjectTreeAsync();
        await OpenAsync($"#/runs/{P(tree.Root)}/steps/{P(tree.Tickets[0])}/plan.plan");
        await Page.GetByRole(AriaRole.Tab, new() { Name = "Artifacts" }).ClickAsync();
        return tree;
    }

    /// <summary>Opens the step's artifact named <paramref name="name"/> from its first execution (the table lists the latest first).</summary>
    Task OpenArtifactAsync(string name) =>
        Page.GetByRole(AriaRole.Table, new() { Name = "Artifacts" }).Locator("tbody tr").Filter(new() { HasText = name }).Last
            .GetByRole(AriaRole.Button, new() { Name = name, Exact = true }).ClickAsync();

    [Fact]
    public async Task The_preview_tab_is_replaced_by_the_next_artifact_and_a_pinned_tab_is_kept()
    {
        await OpenPlanStepAsync();

        await OpenArtifactAsync("requirements");
        await Expect(Tabs).ToHaveCountAsync(1);
        await Expect(Tabs.First).ToContainTextAsync("requirements · exec 1");
        await Expect(Tabs.First.Locator(".ff-vtab-name")).ToHaveCSSAsync("font-style", "italic");

        await OpenArtifactAsync("delivery_plan");
        await Expect(Tabs).ToHaveCountAsync(1);
        await Expect(Tabs.First).ToContainTextAsync("delivery_plan · exec 1");
        await Expect(Viewer.Locator(".ff-viewer-name")).ToHaveTextAsync("delivery_plan");

        await Viewer.GetByRole(AriaRole.Button, new() { Name = "Pin delivery_plan · exec 1" }).ClickAsync();
        await Expect(Tabs.First.Locator(".ff-vtab-name")).ToHaveCSSAsync("font-style", "normal");
        await OpenArtifactAsync("requirements");
        await Expect(Tabs).ToHaveCountAsync(2);
        await Expect(Tabs.Nth(0)).ToContainTextAsync("delivery_plan");
        await Expect(Tabs.Nth(1)).ToContainTextAsync("requirements");
        await Expect(Tabs.Nth(1)).ToHaveAttributeAsync("aria-selected", "true");

        // Back to the pinned tab, then closing the preview leaves the pinned one.
        await Tabs.Nth(0).ClickAsync();
        await Expect(Viewer.Locator(".ff-viewer-name")).ToHaveTextAsync("delivery_plan");
        await Viewer.GetByRole(AriaRole.Button, new() { Name = "Close requirements · exec 1" }).ClickAsync();
        await Expect(Tabs).ToHaveCountAsync(1);
        await Expect(Tabs.First).ToContainTextAsync("delivery_plan");
    }

    [Fact]
    public async Task A_double_click_or_the_full_size_button_opens_the_artifact_full_size()
    {
        await OpenPlanStepAsync();
        await OpenArtifactAsync("requirements");

        await Tabs.First.DblClickAsync();
        var full = Page.GetByRole(AriaRole.Dialog, new() { Name = "requirements · exec 1" });
        await Expect(full.Locator(".ff-viewer-name")).ToHaveTextAsync("requirements");
        await Expect(full.Locator(".ff-doc")).ToContainTextAsync("List invoices");
        await Page.Keyboard.PressAsync("Escape");
        await Expect(full).ToHaveCountAsync(0);

        await Viewer.GetByRole(AriaRole.Button, new() { Name = "Open full size" }).ClickAsync();
        await Expect(full).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Yaml_shows_as_tables_and_lists_with_its_source_and_previous_revision_a_click_away()
    {
        await OpenPlanStepAsync();
        // The scenario's spec was put to the person a second time: its requirements of execution 2, with what changed.
        await Expect(Page.GetByRole(AriaRole.Table, new() { Name = "Artifacts" }).Locator("tbody tr")).ToHaveCountAsync(4);
        await Page.GetByRole(AriaRole.Table, new() { Name = "Artifacts" }).Locator("tbody tr").Filter(new() { HasText = "requirements" }).First.GetByRole(AriaRole.Button).ClickAsync();

        var doc = Viewer.Locator(".ff-doc");
        await Expect(Viewer.Locator(".ff-viewer-about")).ToHaveTextAsync("artifact from plan.plan, execution 2, type std:artifact_types/requirements");
        // A list of maps is a table; a list of strings a list; a map key/value rows.
        var requirements = doc.Locator(".ff-doc-table");
        await Expect(requirements.Locator("th")).ToHaveTextAsync(["id", "title", "behaviour"]);
        await Expect(requirements.Locator("tbody tr").First.Locator("td")).ToHaveTextAsync(["R-001", "List invoices", "GET /invoices lists the caller's invoices"]);
        await Expect(doc.Locator(".ff-doc-list li")).ToHaveTextAsync(["R2 split into R2 and R3"]);
        await Expect(doc.Locator(".ff-doc-kv > tbody > tr > th")).ToHaveTextAsync(["schema", "scope", "kind", "id", "changes", "requirements"]);

        await Viewer.GetByRole(AriaRole.Button, new() { Name = "Source" }).ClickAsync();
        await Expect(Viewer.Locator("pre.ff-viewer-text")).ToHaveTextAsync(new System.Text.RegularExpressions.Regex("^schema: 1\\nscope: \\{ kind: ticket, id: INV-1 \\}\\nchanges:"));
        await Viewer.GetByRole(AriaRole.Button, new() { Name = "Formatted" }).ClickAsync();
        await Expect(doc).ToBeVisibleAsync();

        await Viewer.GetByRole(AriaRole.Button, new() { Name = "Previous revision" }).ClickAsync();
        await Expect(Viewer.Locator(".ff-viewer-about")).ToHaveTextAsync("artifact from plan.plan, execution 1, type std:artifact_types/requirements");
        await Expect(Viewer.Locator(".ff-doc")).ToContainTextAsync("List invoices");
    }
}
