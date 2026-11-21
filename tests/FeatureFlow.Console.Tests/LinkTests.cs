using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Slack;
using static Microsoft.Playwright.Assertions;

namespace FeatureFlow.Console.Tests;

/// <summary>Links into the console from outside it: the Slack form <c>&lt;console_url&gt;/#/runs/{id}</c> is kept (LOG 463).</summary>
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
}
