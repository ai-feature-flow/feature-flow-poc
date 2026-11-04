using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.Slack.Tests;

static class Slack
{
    public const string Secret = "signing-secret";

    public static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public static readonly IReadOnlyDictionary<string, ChatPerson> Users = new Dictionary<string, ChatPerson>
    {
        ["U-ALICE"] = new("alice", ["approver"], ["acme-leads"]),
    };

    public static SlackNotifications Provider(FakeSlack fake, string? consoleUrl = null, string token = FakeSlack.Token) =>
        new(_ => Task.FromResult(ProviderResult<string>.Success(token)), _ => Task.FromResult(ProviderResult<string>.Success(Secret)),
            Users, consoleUrl, fake, "https://slack.example/api");

    /// <summary>A button press as Slack sends it: a form with one <c>payload</c> field holding the interaction's JSON.</summary>
    public static string Press(string user, string run, string wait, string choice, string type = "block_actions", string actionId = "ff_answer_0")
    {
        var payload = new JsonObject
        {
            ["type"] = type,
            ["user"] = new JsonObject { ["id"] = user, ["username"] = "someone" },
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["action_id"] = actionId,
                    ["value"] = new JsonObject { ["r"] = run, ["w"] = wait, ["c"] = choice }.ToJsonString(),
                },
            },
        };
        return "payload=" + Uri.EscapeDataString(payload.ToJsonString());
    }

    public static InboundRequest Signed(string body, DateTimeOffset? at = null, string secret = Secret, DateTimeOffset? received = null)
    {
        var stamp = (at ?? Now).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return new InboundRequest(new Dictionary<string, string>
        {
            ["x-slack-request-timestamp"] = stamp,
            ["X-Slack-Signature"] = SlackNotifications.Sign(secret, stamp, body),
        }, body, received ?? Now);
    }
}

/// <summary>The frozen notifications contract (docs/06 section 3.10) against the Slack provider and a fake Web API.</summary>
public sealed class SlackNotificationsContract : NotificationsContract
{
    readonly FakeSlack fake = new();

    public SlackNotificationsContract() => Notifications = Slack.Provider(fake);

    protected override INotificationsProvider Notifications { get; }

    protected override string Channel => "#ff";

    protected override string UnknownChannel => "#nowhere";

    protected override Task<IReadOnlyList<string>> MessagesAsync(string channel, MessageRef? thread) =>
        Task.FromResult<IReadOnlyList<string>>(fake.Messages.Where(m => m.Channel == channel && m.ThreadTs == thread?.Id).Select(m => m.Text).ToList());
}

public sealed class SlackNotificationsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_decision_carries_each_choice_as_a_button_naming_its_run_wait_and_choice_and_links_the_console()
    {
        var fake = new FakeSlack();
        var slack = Slack.Provider(fake, consoleUrl: "https://ff.example/console/");

        var posted = await slack.PostDecisionAsync("#ff", new DecisionRequest("run-1", "decision-3", "decision", "ticket.spec_approval", "Approve the spec?",
            ["approved", "rejected"], "approve_spec"), null, Ct);

        Assert.True(posted.Ok, posted.Error?.Message);
        var buttons = Assert.Single(fake.Messages).Blocks!.OfType<JsonObject>().Single(b => b["type"]!.GetValue<string>() == "actions")["elements"]!.AsArray();
        var answers = buttons.Where(b => b!["value"] is not null).Select(b => JsonNode.Parse(b!["value"]!.GetValue<string>())!).ToList();
        Assert.Equal(["approved", "rejected"], answers.Select(a => a["c"]!.GetValue<string>()));
        Assert.All(answers, a => Assert.Equal(("run-1", "decision-3"), (a["r"]!.GetValue<string>(), a["w"]!.GetValue<string>())));
        Assert.All(buttons.Where(b => b!["value"] is not null), b => Assert.StartsWith(SlackNotifications.AnswerAction, b!["action_id"]!.GetValue<string>()));
        Assert.Equal("https://ff.example/console/#/runs/run-1", buttons.Single(b => b!["url"] is not null)!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_reply_names_its_thread_and_a_thread_in_another_channel_is_not_found()
    {
        var fake = new FakeSlack();
        var slack = Slack.Provider(fake);
        var root = (await slack.SendAsync("#ff", new Notification("completed", "r", "t", "root"), null, Ct)).Value!;

        Assert.True((await slack.SendAsync("#ff", new Notification("completed", "r", "t", "child"), root, Ct)).Ok);
        Assert.Equal("not_found", (await slack.SendAsync("#other", new Notification("completed", "r", "t", "x"), root, Ct)).Error!.Reason);

        Assert.Equal(root.Id, fake.Messages[1].ThreadTs);
    }

    public static TheoryData<int, string, string> Errors => new()
    {
        { 200, """{ "ok": false, "error": "invalid_auth" }""", "unauthenticated" },
        { 200, """{ "ok": false, "error": "token_revoked" }""", "unauthenticated" },
        { 200, """{ "ok": false, "error": "missing_scope" }""", "rejected" },
        { 200, """{ "ok": false, "error": "not_in_channel" }""", "not_found" },
        { 200, """{ "ok": false, "error": "msg_too_long" }""", "validation_failed" },
        { 200, """{ "ok": false, "error": "ratelimited" }""", "rate_limited" },
        { 200, """{ "ok": true }""", "unexpected_response" },
        { 200, "not json", "unexpected_response" },
        { 404, "", "unexpected_response" },
        { 500, "", "server_error" },
        { 503, "", "server_error" },
    };

    [Theory]
    [MemberData(nameof(Errors))]
    public async Task Every_answer_of_Slack_maps_to_a_classified_result(int status, string body, string reason)
    {
        var fake = new FakeSlack { Respond = _ => new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") } };

        var result = await Slack.Provider(fake).SendAsync("#ff", new Notification("failed", "r", "t", "x"), null, Ct);

        Assert.Equal(reason, result.Error?.Reason);
    }

    [Fact]
    public async Task A_rate_limit_carries_slacks_retry_after_and_an_unreachable_slack_is_unreachable()
    {
        var limited = new FakeSlack
        {
            Respond = _ =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7));
                return r;
            },
        };
        var result = await Slack.Provider(limited).SendAsync("#ff", new Notification("failed", "r", "t", "x"), null, Ct);
        Assert.Equal(("rate_limited", TimeSpan.FromSeconds(7)), (result.Error!.Reason, result.Error.RetryAfter));

        var down = new FakeSlack { Respond = _ => throw new HttpRequestException("connection refused") };
        Assert.Equal("unreachable", (await Slack.Provider(down).SendAsync("#ff", new Notification("failed", "r", "t", "x"), null, Ct)).Error!.Reason);
    }

    [Fact]
    public async Task A_token_the_secrets_provider_cannot_give_is_its_error_and_nothing_is_sent()
    {
        var fake = new FakeSlack();
        var slack = new SlackNotifications(_ => Task.FromResult(ProviderResult<string>.Failure("not_found", "no secret slack/bot")),
            _ => Task.FromResult(ProviderResult<string>.Success(Slack.Secret)), Slack.Users, handler: fake, apiBase: "https://slack.example/api");

        Assert.Equal("not_found", (await slack.SendAsync("#ff", new Notification("failed", "r", "t", "x"), null, Ct)).Error!.Reason);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public async Task A_signed_button_press_is_the_mapped_persons_answer()
    {
        var slack = Slack.Provider(new FakeSlack());

        var answer = await slack.ReadAnswerAsync(Slack.Signed(Slack.Press("U-ALICE", "run-1", "decision-3", "approved")), Ct);

        Assert.True(answer.Ok, answer.Error?.Message);
        Assert.Equal(new ChatAnswer("U-ALICE", Slack.Users["U-ALICE"], "run-1", "decision-3", "approved"), answer.Value);
    }

    [Fact]
    public async Task A_press_by_a_user_with_no_mapping_has_no_person()
    {
        var answer = await Slack.Provider(new FakeSlack()).ReadAnswerAsync(Slack.Signed(Slack.Press("U-STRANGER", "run-1", "decision-3", "approved")), Ct);

        Assert.True(answer.Ok);
        Assert.Null(answer.Value!.Person);
    }

    [Fact]
    public async Task A_request_not_signed_by_the_apps_secret_or_changed_after_signing_or_too_old_is_unauthenticated()
    {
        var slack = Slack.Provider(new FakeSlack());
        var body = Slack.Press("U-ALICE", "run-1", "decision-3", "approved");

        // Another secret; the body changed after signing (the choice); six minutes old; six minutes ahead; no headers.
        var forged = Slack.Signed(body, secret: "someone-elses");
        var signed = Slack.Signed(body);
        var tampered = signed with { Body = Slack.Press("U-ALICE", "run-1", "decision-3", "rejected") };
        var old = Slack.Signed(body, at: Slack.Now.AddMinutes(-6));
        var ahead = Slack.Signed(body, at: Slack.Now.AddMinutes(6));
        var unsigned = new InboundRequest(new Dictionary<string, string>(), body, Slack.Now);
        foreach (var request in new[] { forged, tampered, old, ahead, unsigned })
            Assert.Equal("unauthenticated", (await slack.ReadAnswerAsync(request, Ct)).Error?.Reason);

        // The control: the same request, unchanged and on time, verifies; so does one four minutes old.
        Assert.True((await slack.ReadAnswerAsync(signed, Ct)).Ok);
        Assert.True((await slack.ReadAnswerAsync(Slack.Signed(body, at: Slack.Now.AddMinutes(-4)), Ct)).Ok);
    }

    [Fact]
    public async Task A_signed_request_that_is_not_an_answer_button_is_refused_as_invalid()
    {
        var slack = Slack.Provider(new FakeSlack());

        foreach (var body in new[]
                 {
                     Slack.Press("U-ALICE", "run-1", "decision-3", "approved", type: "view_submission"),
                     Slack.Press("U-ALICE", "run-1", "decision-3", "approved", actionId: "ff_open"),
                     Slack.Press("U-ALICE", "", "decision-3", "approved"),
                     "payload=not-json",
                     "nothing=here",
                 })
            Assert.Equal("validation_failed", (await slack.ReadAnswerAsync(Slack.Signed(body), Ct)).Error?.Reason);
    }

    [Fact]
    public void The_factory_reads_the_user_mapping_and_needs_an_actor_for_each()
    {
        var factory = Assert.Single(SlackProviders.Factories);
        var settings = (YMap)YamlWriter.Read("""
            token_ref: slack/bot
            signing_secret_ref: slack/signing
            users:
              U-ALICE: { actor: alice, roles: [approver], groups: [acme-leads] }
              U-BOB: { actor: bob }
            """, "settings");
        var provider = factory.Create(new ProviderContext(new ProviderInstanceConfig("notifications", "slack", "slack", settings), ".", _ => null));
        Assert.IsType<SlackNotifications>(provider);

        var broken = (YMap)YamlWriter.Read("token_ref: x\nusers: { U-BOB: { roles: [approver] } }", "settings");
        var e = Assert.Throws<InvalidOperationException>(() => factory.Create(new ProviderContext(new ProviderInstanceConfig("notifications", "slack", "slack", broken), ".", _ => null)));
        Assert.Contains("U-BOB needs an actor", e.Message);
    }
}
