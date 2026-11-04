using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.GitHub;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Slack;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// The inbound gateway's refusals, which need no Temporal: a chat answer that does not verify, or from a chat user mapped
/// to no one, or from a person without the approval rule's role, is refused before anything is recorded; a signed chat
/// request reaches no other endpoint; a webhook that does not verify is refused, and one delivered twice is handled once.
/// </summary>
public sealed class InboundTests : IAsyncDisposable
{
    const string SlackSecret = "slack-signing";
    const string HookSecret = "hook-secret";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-inbound-" + Guid.NewGuid().ToString("N"));
    readonly List<Microsoft.AspNetCore.Builder.WebApplication> apps = [];
    readonly SqliteRecordStore records;

    public InboundTests()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), """{ "api_token": "right" }""");
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var app in apps)
            await app.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    HttpClient Start(IReadOnlyList<IAuthenticator>? authenticators = null)
    {
        var slack = new SlackNotifications(_ => Task.FromResult(ProviderResult<string>.Success("xoxb")), _ => Task.FromResult(ProviderResult<string>.Success(SlackSecret)),
            new Dictionary<string, ChatPerson>
            {
                ["U-ALICE"] = new("alice", ["approver"], null),
                ["U-VIC"] = new("vic", ["viewer"], null),
                ["U-STEVE"] = new("steve", ["approver", "steerer"], null),
            });
        var github = new GitHubCodeHost(new GitHubApi(() => null)) { WebhookSecret = _ => Task.FromResult(ProviderResult<string>.Success(HookSecret)) };
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = records },
                ["secrets"] = new Dictionary<string, IProvider> { ["file"] = new FileSecrets(Path.Combine(folder, "secrets.json")) },
                ["notifications"] = new Dictionary<string, IProvider> { ["slack"] = slack },
                ["code_host"] = new Dictionary<string, IProvider> { ["github"] = github },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["secrets"] = "file", ["notifications"] = "slack", ["code_host"] = "github" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", "secrets/api_token", "tester", ["viewer", "approver", "steerer", "admin"]), 3, 10, Path.Combine(folder, "installation.yaml"));
        var organisation = new OrganisationConfig("t", [],
            new PolicyConfig([new ApprovalRule("*", ["approver"], 1, true, new Cap<bool>(false, false))], null, null, null, null),
            Path.Combine(folder, "organisation.yaml"));
        var app = ApiApp.Build(new ApiServices(providers, installation, organisation, null!) { Authenticators = authenticators ?? [] }, "http://127.0.0.1:0");
        app.StartAsync(Ct).GetAwaiter().GetResult();
        apps.Add(app);
        return new HttpClient { BaseAddress = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()) };
    }

    static string Press(string user, string choice = "approved", string wait = "decision-1") =>
        "payload=" + Uri.EscapeDataString(new JsonObject
        {
            ["type"] = "block_actions",
            ["user"] = new JsonObject { ["id"] = user },
            ["actions"] = new JsonArray { new JsonObject { ["action_id"] = "ff_answer_0", ["value"] = new JsonObject { ["r"] = "r1", ["w"] = wait, ["c"] = choice }.ToJsonString() } },
        }.ToJsonString());

    static HttpRequestMessage SlackRequest(string path, string body, string secret = SlackSecret)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded") };
        request.Headers.Add("X-Slack-Request-Timestamp", stamp);
        request.Headers.Add("X-Slack-Signature", SlackNotifications.Sign(secret, stamp, body));
        return request;
    }

    static HttpRequestMessage Hook(string body, string delivery, string secret = HookSecret)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "inbound/events/github") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-GitHub-Event", "check_run");
        request.Headers.Add("X-GitHub-Delivery", delivery);
        request.Headers.Add("X-Hub-Signature-256", GitHubCodeHost.Sign(secret, body));
        return request;
    }

    async Task<(HttpStatusCode Status, string Body)> SendAsync(HttpClient http, HttpRequestMessage request)
    {
        using (request)
        using (var response = await http.SendAsync(request, Ct))
            return (response.StatusCode, await response.Content.ReadAsStringAsync(Ct));
    }

    async Task OpenWaitAsync()
    {
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            [new RunRecord("r1", null, "m7s:templates/decide", "running", "x", ["o/r"], false, now, null)],
            [new WaitRecord("r1", "decision-1", "decision", "go_ahead", "m7s.go_ahead", "Go ahead?", ["approved", "rejected"], true, now)]), Ct);
    }

    async Task<IReadOnlyList<AnswerRecord>> AnswersAsync() =>
        (await records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = "r1" }, Ct)).Unwrap();

    [Fact]
    public async Task A_chat_answer_that_does_not_verify_or_from_a_user_mapped_to_no_one_is_refused_and_nothing_is_recorded()
    {
        var http = Start();
        await OpenWaitAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(http, SlackRequest("inbound/chat/slack", Press("U-ALICE"), secret: "guess"))).Status);
        var stranger = await SendAsync(http, SlackRequest("inbound/chat/slack", Press("U-STRANGER")));
        Assert.Equal(HttpStatusCode.Forbidden, stranger.Status);
        Assert.Contains("U-STRANGER is not mapped to a person", stranger.Body);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(http, SlackRequest("inbound/chat/slack", "payload=%7B%7D"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(http, SlackRequest("inbound/chat/teams", Press("U-ALICE")))).Status);

        Assert.Empty(await AnswersAsync());
    }

    [Fact]
    public async Task A_chat_answer_is_the_ordinary_answer_call_as_the_mapped_person_with_its_roles()
    {
        var http = Start();
        await OpenWaitAsync();

        // vic is mapped, but as a viewer: the approval rule refuses vic exactly as it would through the API.
        var viewer = await SendAsync(http, SlackRequest("inbound/chat/slack", Press("U-VIC")));
        Assert.Equal(HttpStatusCode.Forbidden, viewer.Status);
        Assert.Contains("vic may not answer m7s.go_ahead", viewer.Body);
        Assert.Empty(await AnswersAsync());

        // alice is an approver: the answer is recorded as alice (never the gateway) before delivery. This test's API has no
        // Temporal, so delivery itself fails; the integration test delivers it.
        await SendAsync(http, SlackRequest("inbound/chat/slack", Press("U-ALICE")));
        var sent = Assert.Single(await AnswersAsync(), a => a.Result == "sent");
        Assert.Equal(("alice", "decision-1"), (sent.Actor, sent.WaitId));
        Assert.Contains("\"outcome\":\"approved\"", sent.Answer);
    }

    [Fact]
    public async Task A_chat_retry_on_an_intervention_is_sent_as_its_recovery_action()
    {
        var http = Start();
        var now = DateTimeOffset.UtcNow;
        await records.AppendAsync(new RecordBatch(
            [new RunRecord("r1", null, "m7s:templates/decide", "running", "x", [], false, now, null)],
            [new WaitRecord("r1", "intervention-2", "intervention", "go_ahead", "ticket.escalation", "stuck", ["retry", "end"], true, now)]), Ct);

        // This API has no Temporal: the answer is recorded as sent, then its delivery fails.
        await SendAsync(http, SlackRequest("inbound/chat/slack", Press("U-STEVE", "retry", "intervention-2")));

        var sent = Assert.Single(await AnswersAsync(), a => a.Result == "sent");
        Assert.Equal("steve", sent.Actor);
        Assert.Contains("\"action\":\"retry\"", sent.Answer);
    }

    [Fact]
    public async Task A_timestamp_beyond_any_date_is_unauthenticated_not_a_server_error()
    {
        var http = Start();
        using var request = new HttpRequestMessage(HttpMethod.Post, "inbound/chat/slack") { Content = new StringContent(Press("U-ALICE"), Encoding.UTF8, "application/x-www-form-urlencoded") };
        request.Headers.Add("X-Slack-Request-Timestamp", "99999999999999");
        request.Headers.Add("X-Slack-Signature", "v0=00");

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(http, request)).Status);
    }

    [Fact]
    public async Task A_signed_chat_request_reaches_no_endpoint_but_the_gateway()
    {
        // Even listed among the API's authenticators, the gateway's authenticator accepts only the gateway's route.
        var http = Start([new ChatGatewayAuthenticator(), new LocalTokenAuthenticator()]);
        await OpenWaitAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(http, SlackRequest("runs/r1/abort", Press("U-ALICE")))).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(http, SlackRequest("runs/r1/answers", Press("U-ALICE")))).Status);
        Assert.Empty(await AnswersAsync());
    }

    [Fact]
    public async Task A_webhook_that_does_not_verify_is_refused_and_a_delivery_is_handled_once()
    {
        var http = Start();
        const string body = """{ "action": "completed", "repository": { "full_name": "o/elsewhere" } }""";

        Assert.Equal(HttpStatusCode.Unauthorized, (await SendAsync(http, Hook(body, "d-1", secret: "guess"))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await SendAsync(http, new HttpRequestMessage(HttpMethod.Post, "inbound/events/gitlab") { Content = new StringContent(body) })).Status);
        var first = await SendAsync(http, Hook(body, "d-1"));
        var again = await SendAsync(http, Hook(body, "d-1"));

        Assert.Equal(HttpStatusCode.Accepted, first.Status);
        Assert.Equal("[]", JsonNode.Parse(first.Body)!["woken"]!.ToJsonString());
        Assert.Equal("duplicate", JsonNode.Parse(again.Body)!["result"]!.GetValue<string>());
    }
}
