using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Slack;

/// <summary>
/// Notifications through Slack (docs/06 section 3.10), and the receiving side of its buttons. A message is posted with
/// <c>chat.postMessage</c>, a reply with the thread's <c>ts</c>; a decision carries its choices as buttons whose value
/// names the run, the wait and the choice. A button press reaches <see cref="ReadAnswerAsync"/> as a request Slack signed
/// with the app's signing secret: it is verified (signature and a five-minute age) and the Slack user is mapped to a
/// person by the installation's <c>users</c> setting, never by anything in the request. The bot token and signing secret
/// come from the secrets provider. No Slack type leaves this class.
/// </summary>
public sealed class SlackNotifications(
    Func<CancellationToken, Task<ProviderResult<string>>> botToken,
    Func<CancellationToken, Task<ProviderResult<string>>> signingSecret,
    IReadOnlyDictionary<string, ChatPerson> users,
    string? consoleUrl = null,
    HttpMessageHandler? handler = null,
    string apiBase = "https://slack.com/api") : INotificationsProvider, IChatInbound
{
    static readonly HttpMessageHandler Shared = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10) };

    /// <summary>Slack refuses a request older than five minutes; so does this, so a captured request cannot be replayed later.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(5);

    /// <summary>The action id prefix of an answer button; the console link button has another.</summary>
    public const string AnswerAction = "ff_answer";

    // Slack's limits: 25 elements in an actions block, 75 characters of button text, 2000 of a button's value, 3000 of a section.
    const int MaxButtons = 25;
    const int MaxButtonText = 75;
    const int MaxValue = 2000;
    const int MaxSection = 3000;

    readonly HttpClient http = new(handler ?? Shared, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    readonly string api = apiBase.TrimEnd('/');

    public string Source => "slack";

    public ProviderDescription Describe() => new("notifications", "slack", "0.1.0", new NotificationCapabilities(["slack"], true, true));

    public async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        var result = await CallAsync("auth.test", new JsonObject(), cancellationToken);
        return result.Ok ? ProviderResult<Unit>.Success(default) : new ProviderResult<Unit>(default, result.Error);
    }

    public Task<ProviderResult<MessageRef>> SendAsync(string channel, Notification message, MessageRef? thread, CancellationToken cancellationToken)
    {
        var text = $"*{message.Title}*\n{message.Text}";
        if (LinkOf(message.RunId, message.Link) is { } link)
            text += $"\n<{link}|Open the run in the console>";
        return PostAsync(channel, text, null, thread, cancellationToken);
    }

    public Task<ProviderResult<MessageRef>> PostDecisionAsync(string channel, DecisionRequest request, MessageRef? thread, CancellationToken cancellationToken)
    {
        // The plain text is what a notification and a client without blocks show; it names everything an answer needs.
        var text = $"*A person is needed ({request.Kind}): {request.DecisionType}* in run {request.RunId}, wait {request.WaitId}"
                   + (request.StepId is null ? "" : $", step {request.StepId}")
                   + $"\n{request.Prompt}"
                   + (request.Choices.Count == 0 ? "" : $"\nChoices: {string.Join(", ", request.Choices)}");
        var link = LinkOf(request.RunId, request.Link);
        var elements = new JsonArray();
        foreach (var (choice, i) in request.Choices.Take(link is null ? MaxButtons : MaxButtons - 1).Select((c, i) => (c, i)))
        {
            var value = new JsonObject { ["r"] = request.RunId, ["w"] = request.WaitId, ["c"] = choice }.ToJsonString();
            if (value.Length > MaxValue)
                continue;
            elements.Add(new JsonObject
            {
                ["type"] = "button",
                ["action_id"] = $"{AnswerAction}_{i}",
                ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = Clip(choice, MaxButtonText) },
                ["value"] = value,
            });
        }

        if (link is not null)
            elements.Add(new JsonObject
            {
                ["type"] = "button",
                ["action_id"] = "ff_open",
                ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = "Open in the console" },
                ["url"] = link,
            });
        var blocks = new JsonArray { new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = Clip(text, MaxSection) } } };
        if (elements.Count > 0)
            blocks.Add(new JsonObject { ["type"] = "actions", ["block_id"] = "ff_decision", ["elements"] = elements });
        return PostAsync(channel, text, blocks, thread, cancellationToken);
    }

    string? LinkOf(string runId, string? link) =>
        link ?? (consoleUrl is { Length: > 0 } c ? $"{c.TrimEnd('/')}/#/runs/{Uri.EscapeDataString(runId)}" : null);

    static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    async Task<ProviderResult<MessageRef>> PostAsync(string channel, string text, JsonArray? blocks, MessageRef? thread, CancellationToken cancellationToken)
    {
        if (thread is not null && thread.Channel != channel)
            return ProviderResult<MessageRef>.Failure("not_found", $"thread {thread.Id} is in {thread.Channel}, not {channel}");
        var body = new JsonObject { ["channel"] = channel, ["text"] = text, ["unfurl_links"] = false };
        if (blocks is not null)
            body["blocks"] = blocks;
        if (thread is not null)
            body["thread_ts"] = thread.Id;
        var result = await CallAsync("chat.postMessage", body, cancellationToken);
        if (!result.Ok)
            return new ProviderResult<MessageRef>(null, result.Error);
        return result.Value!["ts"]?.GetValue<string>() is { Length: > 0 } ts
            ? ProviderResult<MessageRef>.Success(new MessageRef(channel, ts))
            : ProviderResult<MessageRef>.Failure("unexpected_response", "chat.postMessage answered ok without a ts");
    }

    /// <summary>
    /// Calls a Web API method with the bot token. Slack answers 200 with <c>ok</c> false for most errors; those map to
    /// docs/06 section 1.1 by Slack's error name. A 429 is <c>rate_limited</c> with its <c>Retry-After</c>.
    /// </summary>
    async Task<ProviderResult<JsonObject>> CallAsync(string method, JsonObject body, CancellationToken cancellationToken)
    {
        var token = await botToken(cancellationToken);
        if (!token.Ok)
            return new ProviderResult<JsonObject>(null, token.Error);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{api}/{method}")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            return ProviderResult<JsonObject>.Failure("unreachable", $"slack {method}: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderResult<JsonObject>.Failure("timeout", $"slack {method} did not answer within {http.Timeout.TotalSeconds:0} s");
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                return new ProviderResult<JsonObject>(null, new ProviderError("rate_limited", $"slack {method}: rate limited",
                    response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(30)));
            if ((int)response.StatusCode >= 500)
                return ProviderResult<JsonObject>.Failure("server_error", $"slack {method}: {(int)response.StatusCode}");
            JsonObject? json = null;
            try
            {
                json = JsonNode.Parse(text) as JsonObject;
            }
            catch (JsonException)
            {
            }

            if (response.StatusCode != HttpStatusCode.OK || json is null)
                return ProviderResult<JsonObject>.Failure("unexpected_response", $"slack {method}: {(int)response.StatusCode} {Clip(text, 200)}");
            if (json["ok"]?.GetValue<bool>() == true)
                return ProviderResult<JsonObject>.Success(json);
            var error = json["error"]?.GetValue<string>() ?? "unknown";
            return ProviderResult<JsonObject>.Failure(Classify(error), $"slack {method}: {error}");
        }
    }

    /// <summary>Slack's error names (https://api.slack.com/methods/chat.postMessage) by docs/06 section 1.1's reasons.</summary>
    public static string Classify(string error) => error switch
    {
        "channel_not_found" or "not_in_channel" or "is_archived" or "thread_not_found" => "not_found",
        "invalid_auth" or "not_authed" or "account_inactive" or "token_revoked" or "token_expired" => "unauthenticated",
        "ratelimited" or "rate_limited" => "rate_limited",
        "msg_too_long" or "no_text" or "invalid_blocks" or "invalid_arguments" or "too_many_attachments" or "invalid_form_data" => "validation_failed",
        "internal_error" or "fatal_error" or "service_unavailable" or "request_timeout" => "server_error",
        // A permission the app lacks does not heal: a configuration fault for a person (LOG 119).
        _ => "rejected",
    };

    /// <summary>
    /// A button press, verified as Slack documents (https://api.slack.com/authentication/verifying-requests-from-slack):
    /// <c>v0=</c> and the hex HMAC-SHA256, under the signing secret, of <c>v0:{timestamp}:{body}</c>, compared in constant
    /// time, with the timestamp no more than five minutes from when the request arrived.
    /// </summary>
    public async Task<ProviderResult<ChatAnswer>> ReadAnswerAsync(InboundRequest request, CancellationToken cancellationToken)
    {
        if (request.Header("X-Slack-Request-Timestamp") is not { } stamp || request.Header("X-Slack-Signature") is not { } signature
            || !long.TryParse(stamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
            return ProviderResult<ChatAnswer>.Failure("unauthenticated", "the request is not signed by Slack");
        var age = request.ReceivedAt - DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (age.Duration() > MaxAge)
            return ProviderResult<ChatAnswer>.Failure("unauthenticated", $"the request's timestamp is {age.Duration().TotalMinutes:0} minutes from now");
        var secret = await signingSecret(cancellationToken);
        if (!secret.Ok)
            return new ProviderResult<ChatAnswer>(null, secret.Error);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret.Value!, stamp, request.Body)), Encoding.ASCII.GetBytes(signature)))
            return ProviderResult<ChatAnswer>.Failure("unauthenticated", "the request's signature does not verify");

        JsonObject? payload;
        try
        {
            payload = Form(request.Body).GetValueOrDefault("payload") is { } p ? JsonNode.Parse(p) as JsonObject : null;
        }
        catch (JsonException)
        {
            payload = null;
        }

        if (payload?["type"]?.GetValue<string>() != "block_actions" || payload["user"]?["id"]?.GetValue<string>() is not { Length: > 0 } user)
            return ProviderResult<ChatAnswer>.Failure("validation_failed", "the request is not a button press");
        var action = (payload["actions"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(a => a["action_id"]?.GetValue<string>()?.StartsWith(AnswerAction, StringComparison.Ordinal) == true);
        JsonObject? value = null;
        try
        {
            value = action?["value"]?.GetValue<string>() is { } v ? JsonNode.Parse(v) as JsonObject : null;
        }
        catch (JsonException)
        {
        }

        if (value?["r"]?.GetValue<string>() is not { Length: > 0 } run || value["w"]?.GetValue<string>() is not { Length: > 0 } wait
            || value["c"]?.GetValue<string>() is not { Length: > 0 } choice)
            return ProviderResult<ChatAnswer>.Failure("validation_failed", "the button press does not name a run, a wait and a choice");
        return ProviderResult<ChatAnswer>.Success(new ChatAnswer(user, users.GetValueOrDefault(user), run, wait, choice));
    }

    /// <summary>Slack's request signature: <c>v0=</c> and the lower-case hex HMAC-SHA256 of <c>v0:{timestamp}:{body}</c>.</summary>
    public static string Sign(string secret, string timestamp, string body) =>
        "v0=" + Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"v0:{timestamp}:{body}")));

    static Dictionary<string, string> Form(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=');
            var key = Unescape(at < 0 ? pair : pair[..at]);
            form.TryAdd(key, at < 0 ? "" : Unescape(pair[(at + 1)..]));
        }

        return form;

        static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));
    }
}
