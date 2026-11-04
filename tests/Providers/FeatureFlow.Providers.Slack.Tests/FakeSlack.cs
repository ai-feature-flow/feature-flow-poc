using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace FeatureFlow.Providers.Slack.Tests;

/// <summary>
/// Slack's Web API as the provider uses it, in memory: <c>chat.postMessage</c> to known channels (by the name given),
/// replies by <c>thread_ts</c>, and <c>auth.test</c>. It answers as Slack documents: 200 with <c>ok</c> false and an error
/// name for an unknown channel, thread or token. <see cref="Respond"/> replaces the answer for error tests.
/// </summary>
public sealed class FakeSlack : HttpMessageHandler
{
    public const string Token = "xoxb-test";

    readonly Lock gate = new();
    readonly HashSet<string> channels = new(StringComparer.Ordinal) { "#ff" };
    int next;

    public sealed record Posted(string Channel, string Ts, string? ThreadTs, string Text, JsonArray? Blocks);

    public List<Posted> Messages { get; } = [];

    public List<string> Calls { get; } = [];

    /// <summary>When set, answers every request instead of the fake Web API.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var method = request.RequestUri!.AbsolutePath.TrimStart('/').Replace("api/", "", StringComparison.Ordinal);
        lock (gate)
            Calls.Add(method);
        if (Respond is { } respond)
            return respond(request);
        if (request.Headers.Authorization?.Parameter != Token)
            return Json(new JsonObject { ["ok"] = false, ["error"] = "invalid_auth" });
        var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!.AsObject();
        switch (method)
        {
            case "auth.test":
                return Json(new JsonObject { ["ok"] = true, ["user_id"] = "UBOT" });
            case "chat.postMessage":
                lock (gate)
                {
                    var channel = body["channel"]!.GetValue<string>();
                    if (!channels.Contains(channel))
                        return Json(new JsonObject { ["ok"] = false, ["error"] = "channel_not_found" });
                    var thread = body["thread_ts"]?.GetValue<string>();
                    if (thread is not null && !Messages.Any(m => m.Channel == channel && m.Ts == thread && m.ThreadTs is null))
                        return Json(new JsonObject { ["ok"] = false, ["error"] = "thread_not_found" });
                    var ts = $"1700000000.{++next:D6}";
                    Messages.Add(new Posted(channel, ts, thread, body["text"]!.GetValue<string>(), body["blocks"]?.DeepClone() as JsonArray));
                    return Json(new JsonObject { ["ok"] = true, ["channel"] = "C0FAKE", ["ts"] = ts });
                }

            default:
                return Json(new JsonObject { ["ok"] = false, ["error"] = "unknown_method" });
        }
    }

    public static HttpResponseMessage Json(JsonObject body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
}
