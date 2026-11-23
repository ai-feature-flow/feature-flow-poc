using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Slack;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// Links into the console from outside it: the Slack form <c>&lt;console_url&gt;/#/runs/{id}</c> is kept (LOG 463), and a
/// decision's link (<c>#/runs/{run}/waits/{run}/{wait}</c>) opens its run tree at the wait's step with its popup.
/// </summary>
public sealed partial class LinkTests(BrowserFixture browser) : BrowserTest(browser)
{
    /// <summary>Captures what Slack is sent, and answers as Slack does.</summary>
    sealed class Slack : HttpMessageHandler
    {
        public List<JsonNode> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sent.Add(JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "ok": true, "channel": "C1", "ts": "1.1" }""") };
        }
    }

    [GeneratedRegex(@"<(?<link>[^|>]+)\|Open the run in the console>")]
    private static partial Regex ConsoleLink();

    [Fact]
    public async Task A_slack_link_opens_the_run()
    {
        // An id that needs encoding in a URL: a space, an ampersand and a letter outside ASCII.
        const string runId = "run-é 1&2";
        await Engine.RunAsync(runId);
        var slack = new Slack();
        var notifications = new SlackNotifications(_ => Task.FromResult(ProviderResult<string>.Success("xoxb-test")), _ => Task.FromResult(ProviderResult<string>.Success("s")),
            new Dictionary<string, ChatPerson>(), consoleUrl: World.ConsoleUrl, handler: slack);
        (await notifications.SendAsync("C1", new Notification("run_completed", runId, "Run completed", "done"), null, Ct)).Unwrap();
        var link = ConsoleLink().Match(Assert.Single(slack.Sent)["text"]!.GetValue<string>()).Groups["link"].Value;
        Assert.StartsWith(World.ConsoleUrl + "/#/runs/run-", link);

        await OpenAtAsync(link);

        await Expect(Page.Locator(".ff-run-id")).ToHaveTextAsync(runId);
        await Expect(Page.Locator(".ff-run-template")).ToHaveTextAsync("m7s:templates/decide");
    }

    [Fact]
    public async Task A_url_from_the_api_that_is_not_https_never_becomes_a_link_out()
    {
        var tree = await Engine.ProjectTreeAsync();
        await Engine.RunWithAsync("bad-links", trackerIssue: "acme/api#9", trackerTitle: "Bad links", trackerUrl: "javascript:alert(1)");
        await Engine.PullRequestAsync(tree.Merge.RunId, FakeEngine.ScenarioRepository, 201, "deliver.merge", title: "List invoices (#201)",
            checks: [new CheckRun("build", CheckStates.Completed, Conclusions.Success, "ci.yml", "http://ci.example.com/runs/1")]);

        await OpenAtAsync($"{World.ConsoleUrl}/#/runs/bad-links");
        await Expect(Page.GetByRole(Microsoft.Playwright.AriaRole.Link, new() { Name = "acme/api#9 ↗" })).ToHaveAttributeAsync("href", "#/");

        await OpenAtAsync($"{World.ConsoleUrl}/#/runs/{Uri.EscapeDataString(tree.Root)}/waits/{Uri.EscapeDataString(tree.Merge.RunId)}/{Uri.EscapeDataString(tree.Merge.WaitId)}");
        var dialog = Page.GetByRole(Microsoft.Playwright.AriaRole.Dialog);
        await Expect(dialog.GetByRole(Microsoft.Playwright.AriaRole.Table, new() { Name = "Checks" }).GetByRole(Microsoft.Playwright.AriaRole.Link, new() { Name = "log ↗" }))
            .ToHaveAttributeAsync("href", "#/");
        var hrefs = await Page.Locator("a[href]").EvaluateAllAsync<string[]>("links => links.map(a => a.getAttribute('href'))");
        Assert.DoesNotContain(hrefs, h => h.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) || h.StartsWith("http:", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_decision_link_to_a_child_runs_wait_opens_the_roots_tree_at_its_step_with_its_popup()
    {
        // The form SlackNotifications.LinkOf builds for a decision: the wait's own run as the tree, which the console resolves to its root.
        var tree = await Engine.ProjectTreeAsync();
        var ticket = Uri.EscapeDataString(tree.Merge.RunId);

        await OpenAtAsync($"{World.ConsoleUrl}/#/runs/{ticket}/waits/{ticket}/{Uri.EscapeDataString(tree.Merge.WaitId)}");

        await Expect(Page.GetByRole(Microsoft.Playwright.AriaRole.Dialog)).ToContainTextAsync("acme/api#201");
        await Expect(Page.Locator(".ff-tree .ff-row-run").First).ToHaveAttributeAsync("data-run", tree.Root);
        var selected = Page.Locator(".ff-tree .ff-row-selected");
        await Expect(selected).ToHaveAttributeAsync("data-run", tree.Merge.RunId);
        await Expect(selected).ToHaveAttributeAsync("data-step", "deliver.merge");
        await Expect(Page.Locator(".ff-run-id")).ToHaveTextAsync(tree.Merge.RunId);
    }
}
