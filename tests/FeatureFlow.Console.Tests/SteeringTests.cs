using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The plan panel's steering (F18, G11): breakpoint, skip, grant attempts and amend, each through
/// <c>POST /runs/{id}/overrides</c> with a required reason, and shown only where the caller may use it.
/// </summary>
public sealed class SteeringTests(BrowserFixture browser) : BrowserTest(browser)
{
    ILocator Plan => Page.GetByRole(AriaRole.Region, new() { Name = "Plan" });

    ILocator Row(string stepId) => Plan.GetByRole(AriaRole.Row).Filter(new() { Has = Page.GetByText(stepId, new() { Exact = true }) });

    /// <summary>Fills the open steering form's reason and applies it.</summary>
    async Task ApplyAsync(string reason)
    {
        await Plan.GetByLabel("Reason").FillAsync(reason);
        await Plan.GetByRole(AriaRole.Button, new() { Name = "Apply" }).ClickAsync();
    }

    async Task<string> OpenRunAsync()
    {
        var runId = await World.StartRunAsync();
        await OpenAsync("#/runs/" + runId);
        await Expect(Row("go_ahead")).ToBeVisibleAsync();
        return runId;
    }

    [Fact]
    public async Task A_breakpoint_set_from_the_plan_pauses_the_run()
    {
        var runId = await OpenRunAsync();

        await Row("go_ahead").GetByRole(AriaRole.Button, new() { Name = "Set breakpoint" }).ClickAsync();
        await ApplyAsync("look before it goes");

        await Expect(Plan.GetByRole(AriaRole.Status)).ToContainTextAsync("applied");
        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("breakpoint", "go_ahead", "look before it goes", "true"), (o.Key, o.StepId, o.Reason, o.Change["set"]));
        var wait = Assert.Single(await Engine.OpenWaitsAsync(runId), w => w.Kind == "breakpoint");
        // Without a reload: the wait's form under Open waits (T4, T5); before those merge, T3's placeholder form.
        var form = Page.GetByRole(AriaRole.Region, new() { Name = "Open waits" }).GetByRole(AriaRole.Region, new() { Name = "Wait " + wait.WaitId })
            .Or(Page.Locator($"section[data-wait='{wait.WaitId}']"));
        await Expect(form).ToBeVisibleAsync();
        await Expect(Row("go_ahead").GetByRole(AriaRole.Button, new() { Name = "Clear breakpoint" })).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Skip_and_grant_attempts_send_their_arguments_and_the_plan_shows_them()
    {
        await OpenRunAsync();

        await Row("go_ahead").GetByRole(AriaRole.Button, new() { Name = "Skip" }).ClickAsync();
        await Plan.GetByLabel("Outcome").SelectOptionAsync("rejected");
        await ApplyAsync("not needed");
        await Expect(Row("go_ahead")).ToContainTextAsync("skipped with rejected");

        await Row("go_ahead").GetByRole(AriaRole.Button, new() { Name = "Grant attempts" }).ClickAsync();
        await Plan.GetByLabel("Attempts").FillAsync("2");
        await ApplyAsync("one more go");
        await Expect(Plan.GetByRole(AriaRole.Status)).ToHaveTextAsync("Grant attempts to go_ahead: applied");

        Assert.Collection(Engine.Overrides,
            o => Assert.Equal(("skip", "go_ahead", "not needed", "rejected"), (o.Key, o.StepId, o.Reason, o.Change["outcome"])),
            o => Assert.Equal(("grant_attempts", "go_ahead", "one more go", "2"), (o.Key, o.StepId, o.Reason, o.Change["attempts"])));
    }

    [Fact]
    public async Task An_amendment_is_sent_against_the_current_revision()
    {
        await OpenRunAsync();
        await Expect(Plan).ToContainTextAsync("Revision 1");

        await Plan.GetByRole(AriaRole.Button, new() { Name = "Amend plan" }).ClickAsync();
        var patch = Plan.GetByLabel("Patch");
        await Expect(patch).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("^api_version: feature-flow/v1\nkind: patch\nbase_revision: 1\nresume_at:\nchanges:\n"));
        await patch.FillAsync((await patch.InputValueAsync()) + "  - op: remove\n    target: go_ahead\n");
        await ApplyAsync("drop the gate");
        await Expect(Plan.GetByRole(AriaRole.Status)).ToHaveTextAsync("Amend the plan: applied");

        var o = Assert.Single(Engine.Overrides);
        Assert.Equal(("amend", null, "drop the gate"), (o.Key, o.StepId, o.Reason));
        Assert.Contains("base_revision: 1\n", o.Change["patch"]);
        Assert.Contains("op: remove", o.Change["patch"]);
        await Expect(Plan).ToContainTextAsync("Revision 2");

        await Plan.GetByRole(AriaRole.Button, new() { Name = "Amend plan" }).ClickAsync();
        await Expect(Plan.GetByLabel("Patch")).ToHaveValueAsync(new System.Text.RegularExpressions.Regex("base_revision: 2\n"));
    }

    [Fact]
    public async Task Controls_the_caller_may_not_use_are_not_shown()
    {
        UseWorld("none", overrides: new Dictionary<string, IReadOnlyList<string>> { ["skip"] = [], ["amend"] = [] });
        await OpenRunAsync();

        await Expect(Row("go_ahead").GetByRole(AriaRole.Button, new() { Name = "Set breakpoint" })).ToBeVisibleAsync();
        await Expect(Row("go_ahead").GetByRole(AriaRole.Button, new() { Name = "Grant attempts" })).ToBeVisibleAsync();
        await Expect(Plan.GetByRole(AriaRole.Button, new() { Name = "Skip" })).ToHaveCountAsync(0);
        await Expect(Plan.GetByRole(AriaRole.Button, new() { Name = "Amend plan" })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Every_override_needs_a_reason()
    {
        await OpenRunAsync();
        var apply = Plan.GetByRole(AriaRole.Button, new() { Name = "Apply" });

        foreach (var open in new[]
                 {
                     Row("go_ahead").GetByRole(AriaRole.Button, new() { Name = "Set breakpoint" }),
                     Row("go_ahead").GetByRole(AriaRole.Button, new() { Name = "Skip" }),
                     Row("go_ahead").GetByRole(AriaRole.Button, new() { Name = "Grant attempts" }),
                     Plan.GetByRole(AriaRole.Button, new() { Name = "Amend plan" }),
                 })
        {
            await open.ClickAsync();
            if (await Plan.GetByLabel("Attempts").CountAsync() > 0)
                await Plan.GetByLabel("Attempts").FillAsync("1");
            await Expect(apply).ToBeDisabledAsync();
            await Plan.GetByLabel("Reason").FillAsync("   ");
            await Expect(apply).ToBeDisabledAsync();
            await Plan.GetByLabel("Reason").FillAsync("because");
            await Expect(apply).ToBeEnabledAsync();
            await Plan.GetByRole(AriaRole.Button, new() { Name = "Cancel" }).ClickAsync();
        }

        Assert.Empty(Engine.Overrides);
    }
}
