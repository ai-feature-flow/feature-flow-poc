using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Hosting;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.GitHub;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Slack;
using Temporalio.Client;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M7's surfaces on a Temporal dev server. A decision is posted to chat with its choices as buttons; a signed button press
/// reaches the inbound gateway and becomes the API's own answer as the mapped person; the run resumes and ends, and each
/// step is told in the root run's thread. A signed GitHub webhook wakes a wait that reads only every ten minutes: a forged
/// delivery changes nothing, a true-looking event about a check still running wakes the wait to read again and nothing
/// else, and only the event that matches what the read finds ends the wait (events wake, reads confirm).
/// </summary>
[Trait("Category", "Integration")]
public class SurfacesTests
{
    const string Repo = "acme/api";
    const string SlackSecret = "slack-signing-secret";
    const string HookSecret = "github-webhook-secret";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string Library => Path.Combine(Harness.TestLibraries, "m7s");

    /// <summary>Slack's chat.postMessage, recording each post's JSON and answering as Slack does.</summary>
    sealed class RecordingSlack : HttpMessageHandler
    {
        int next;

        public List<JsonObject> Posts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
            if (!request.RequestUri!.AbsolutePath.EndsWith("/chat.postMessage", StringComparison.Ordinal))
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{ "ok": true }""") };
            string ts;
            lock (Posts)
            {
                ts = $"1700000000.{++next:D6}";
                body["ts"] = ts;
                Posts.Add(body);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(new JsonObject { ["ok"] = true, ["ts"] = ts }.ToJsonString()) };
        }
    }

    static FeatureFlowConfiguration NotifyEverything(FeatureFlowConfiguration c) =>
        c with
        {
            Organisation = c.Organisation with
            {
                Notifications = new NotificationsConfig(NotificationEvents.All.ToDictionary(e => e, _ => new NotificationRoute("#ff")), NotificationEvents.All),
                // The harness's rule leaves from_chat at its default, false; this test's decision may be answered from chat.
                Policy = c.Organisation.Policy with { Approval = c.Organisation.Policy.Approval.Select(r => r with { FromChat = true }).ToList() },
            },
        };

    [Fact]
    public async Task A_decision_answered_with_a_chat_button_is_the_mapped_persons_answer_and_the_run_reports_in_one_thread()
    {
        var slack = new RecordingSlack();
        var factory = new ProviderFactory("notifications", "test_slack", [], _ => new SlackNotifications(
            _ => Task.FromResult(ProviderResult<string>.Success("xoxb-test")),
            _ => Task.FromResult(ProviderResult<string>.Success(SlackSecret)),
            new Dictionary<string, ChatPerson> { ["U-ALICE"] = new("alice", ["approver"], null) },
            "http://console.example", slack, "https://slack.example/api"));
        await using var h = await Harness.StartAsync("chat", omitTypes: ["notifications"], libraries: [Library],
            providers: "  notifications: { instances: { test_slack: {} } }", factories: [factory], configure: NotifyEverything);

        var id = h.Start("m7s:templates/decide");
        var waitId = await h.WaitForWaitAsync(id, TimeSpan.FromSeconds(60));
        var decision = await PostedAsync(slack, p => p["blocks"] is not null);

        // The person presses "approved": Slack sends back the button's own value, signed.
        var button = decision["blocks"]!.AsArray().Single(b => b!["type"]!.GetValue<string>() == "actions")!["elements"]!.AsArray()
            .Single(b => b!["text"]!["text"]!.GetValue<string>() == "approved")!;
        var press = Press("U-ALICE", button["action_id"]!.GetValue<string>(), button["value"]!.GetValue<string>());
        var (status, body) = await SignedAsync(h, press);
        Assert.True(status == HttpStatusCode.OK, body);
        Assert.Equal("recorded", JsonNode.Parse(body)!["result"]!.GetValue<string>());
        // The same press again (Slack's retry, or a second click) is never a second answer: the run answers "duplicate",
        // or, once the answer has ended it, refuses.
        var again = await SignedAsync(h, press);
        Assert.True(again.Status == HttpStatusCode.Conflict || JsonNode.Parse(again.Body)!["result"]?.GetValue<string>() == "duplicate", again.Body);

        var (exit, end, error) = h.Cli("wait", id, "--timeout", "60");
        Assert.True(exit == 0 && end.Trim() == $"run {id} completed", end + error);

        // Recorded as alice, the mapped person, never as the gateway; the run's timeline names her too.
        var records = h.Host.Providers.Required<IRecordStoreProvider>("record_store");
        var answers = (await records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = id }, Ct)).Unwrap();
        Assert.All(answers, a => Assert.Equal("alice", a.Actor));
        Assert.Single(answers, a => a.Result == "recorded");
        var view = await h.ShowAsync(id);
        Assert.Contains(view["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "answered" && e["detail"]!.ToString().EndsWith("by alice", StringComparison.Ordinal));

        // One thread: the decision starts it; resumed and completed reply in it.
        await PostedAsync(slack, p => p["text"]!.GetValue<string>().Contains($"Run {id} completed", StringComparison.Ordinal));
        Assert.Equal(waitId, JsonNode.Parse(button["value"]!.GetValue<string>())!["w"]!.GetValue<string>());
        Assert.Null(decision["thread_ts"]);
        var replies = slack.Posts.Where(p => p["thread_ts"]?.GetValue<string>() == decision["ts"]!.GetValue<string>()).Select(p => p["text"]!.GetValue<string>()).ToList();
        Assert.Equal(2, replies.Count);
        Assert.Contains(replies, r => r.Contains("was answered: approved by alice", StringComparison.Ordinal));
        Assert.Contains(replies, r => r.Contains($"Run {id} completed", StringComparison.Ordinal));
        Assert.Equal(3, slack.Posts.Count);
    }

    static async Task<JsonObject> PostedAsync(RecordingSlack slack, Func<JsonObject, bool> match)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            lock (slack.Posts)
                if (slack.Posts.FirstOrDefault(match) is { } post)
                    return post;
            await Task.Delay(200, Ct);
        }

        throw new TimeoutException("the post was not made: " + string.Join("\n", slack.Posts.Select(p => p.ToJsonString())));
    }

    static string Press(string user, string actionId, string value) =>
        "payload=" + Uri.EscapeDataString(new JsonObject
        {
            ["type"] = "block_actions",
            ["user"] = new JsonObject { ["id"] = user },
            ["actions"] = new JsonArray { new JsonObject { ["action_id"] = actionId, ["value"] = value } },
        }.ToJsonString());

    static async Task<(HttpStatusCode Status, string Body)> SignedAsync(Harness h, string body)
    {
        using var http = new HttpClient();
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{h.Api}/inbound/chat/slack") { Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded") };
        request.Headers.Add("X-Slack-Request-Timestamp", stamp);
        request.Headers.Add("X-Slack-Signature", SlackNotifications.Sign(SlackSecret, stamp, body));
        using var response = await http.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_webhook_wakes_a_wait_to_read_again_and_only_the_read_decides()
    {
        var world = $"it-wake-{Guid.NewGuid():N}";
        var forge = InMemoryForge.Named(world);
        var repo = forge.CreateRepository(Repo, new Dictionary<string, string> { ["src/App.cs"] = "class App {}\n" });
        forge.Protect(repo, new BranchProtection("main", true, true, 1, [], false));
        var head = forge.Commit(repo, "refs/heads/ff/slow", new Dictionary<string, string?> { ["src/App.cs"] = "class App { }\n" });
        forge.ReportCheck(repo, head, new CheckRun("build", CheckStates.InProgress, null, "ci"));
        var hooks = new ProviderFactory("code_host", "test_github", [], _ => new GitHubCodeHost(new GitHubApi(() => null))
        {
            WebhookSecret = _ => Task.FromResult(ProviderResult<string>.Success(HookSecret)),
        });
        await using var h = await Harness.StartAsync("wake", omitTypes: ["vcs", "code_host", "ci"], libraries: [Library], factories: [hooks],
            providers: $$"""
              vcs: { instances: { memory: { world: {{world}} } } }
              code_host: { default: memory, instances: { memory: { world: {{world}} }, test_github: {} } }
              ci: { instances: { memory: { world: {{world}} } } }
            """);
        var id = h.Start("m7s:templates/slow_wait", "--repo", Repo);
        var handle = h.Client.GetWorkflowHandle(id);
        var waiting = await InBackoffAsync(handle, null);

        // Forged: signed with a guess. Refused, and the wait is untouched.
        Assert.Equal(HttpStatusCode.Unauthorized, (await HookAsync(h, "d-forged", "guess")).Status);
        await Task.Delay(2000, Ct);
        Assert.Equal(waiting, await PendingAsync(handle));
        Assert.DoesNotContain(Timeline(await h.ShowAsync(id)), e => e.Kind == "interrupted");

        // True-looking but wrong: "completed", while the check is still running. It wakes the wait, which reads again,
        // finds the check running and goes back to waiting: no outcome, no attempt used.
        var (status, body) = await HookAsync(h, "d-wrong", HookSecret);
        Assert.True(status == HttpStatusCode.Accepted, body);
        Assert.Equal($"[\"{id}\"]", JsonNode.Parse(body)!["woken"]!.ToJsonString());
        var woken = await InBackoffAsync(handle, waiting);
        // The run's records may lag its activities: wait for the entry rather than read the timeline once.
        var timeline = await TimelineWithAsync(h, id, e => e is { Kind: "interrupted", Step: "checks" } && e.Detail.StartsWith("woken by github check_run.completed", StringComparison.Ordinal));
        Assert.DoesNotContain(timeline, e => e.Kind is "attempt_failed" or "completed");
        Assert.Equal("running", (await h.ShowAsync(id))["run"]!["status"]!.ToString());

        // The same delivery again is handled once: no further wake.
        Assert.Equal("duplicate", JsonNode.Parse((await HookAsync(h, "d-wrong", HookSecret)).Body)!["result"]!.GetValue<string>());
        await Task.Delay(2000, Ct);
        Assert.Equal(woken, await PendingAsync(handle));

        // The check completes; the event that says so ends the wait now, not in ten minutes.
        forge.ReportCheck(repo, head, new CheckRun("build", CheckStates.Completed, Conclusions.Success, "ci"));
        Assert.Equal(HttpStatusCode.Accepted, (await HookAsync(h, "d-true", HookSecret)).Status);
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "60");
        Assert.True(exit == 0 && end.Trim() == $"run {id} completed", end + error);
        timeline = Timeline(await h.ShowAsync(id));
        Assert.Equal(["passed"], timeline.Where(e => e is { Kind: "completed", Step: "checks" }).Select(e => e.Detail));
        Assert.Single(timeline, e => e is { Kind: "started", Step: "checks" });
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
        await h.RecordHistoryAsync(id, "wake");
    }

    static async Task<List<(string Kind, string Step, string Detail)>> TimelineWithAsync(Harness h, string id, Func<(string Kind, string Step, string Detail), bool> entry)
    {
        var until = DateTime.UtcNow.AddSeconds(30);
        var timeline = Timeline(await h.ShowAsync(id));
        while (!timeline.Any(entry) && DateTime.UtcNow < until)
        {
            await Task.Delay(250, Ct);
            timeline = Timeline(await h.ShowAsync(id));
        }

        Assert.Contains(timeline, e => entry(e));
        return timeline;
    }

    static List<(string Kind, string Step, string Detail)> Timeline(JsonNode view) =>
        view["timeline"]!.AsArray().Select(e => (e!["kind"]!.ToString(), e["stepId"]!.ToString(), e["detail"]?.ToString() ?? "")).ToList();

    /// <summary>The wait's pending activity as <c>id/attempt</c>: a new id is a restarted activity, a new attempt another read.</summary>
    static async Task<string> PendingAsync(WorkflowHandle handle) =>
        await ActivityAsync(handle) is { } a ? $"{a.ActivityId}/{a.Attempt}" : "";

    static async Task<Temporalio.Api.Workflow.V1.PendingActivityInfo?> ActivityAsync(WorkflowHandle handle) =>
        (await handle.Client.WorkflowService.DescribeWorkflowExecutionAsync(new Temporalio.Api.WorkflowService.V1.DescribeWorkflowExecutionRequest
        {
            Namespace = handle.Client.Options.Namespace,
            Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = handle.Id },
        })).PendingActivities.Where(a => a.ActivityType.Name == WaitActivity).SingleOrDefault();

    /// <summary>
    /// The wait's activity type. The run's own activities (writing its records, which also sends notifications) may be
    /// pending beside it, so the wait is picked by type; more than one of these pending would be two reads of the wait at
    /// once, which fails the test.
    /// </summary>
    const string WaitActivity = "RunAction";

    /// <summary>Waits until a wait activity other than <paramref name="previous"/> has read, found the check running, and gone back to its delay.</summary>
    static async Task<string> InBackoffAsync(WorkflowHandle handle, string? previous)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            var activity = await ActivityAsync(handle);
            if (activity is { Attempt: >= 2, State: Temporalio.Api.Enums.V1.PendingActivityState.Scheduled }
                && activity.LastFailure?.Message.StartsWith("waiting", StringComparison.Ordinal) == true && activity.ActivityId != previous?.Split('/')[0])
                return $"{activity.ActivityId}/{activity.Attempt}";
            await Task.Delay(250, Ct);
        }

        throw new TimeoutException("the wait did not go back to its delay: " + await PendingAsync(handle));
    }

    static async Task<(HttpStatusCode Status, string Body)> HookAsync(Harness h, string delivery, string secret)
    {
        const string body = """{ "action": "completed", "check_run": { "name": "build" }, "repository": { "full_name": "acme/api" } }""";
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{h.Api}/inbound/events/github") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-GitHub-Event", "check_run");
        request.Headers.Add("X-GitHub-Delivery", delivery);
        request.Headers.Add("X-Hub-Signature-256", GitHubCodeHost.Sign(secret, body));
        using var response = await http.SendAsync(request, Ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }
}
