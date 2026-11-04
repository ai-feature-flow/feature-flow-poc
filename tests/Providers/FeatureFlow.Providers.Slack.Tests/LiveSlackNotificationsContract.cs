using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.Slack.Tests;

/// <summary>
/// The notifications contract against Slack itself. It posts real messages, so it is <c>Live</c> and is not run by
/// default (M7: no live Slack posts from the builders' tests). It needs <c>SLACK_BOT_TOKEN</c> and
/// <c>FF_SLACK_CHANNEL_ID</c>, a channel the bot is in, and reads posts back with <c>conversations.history</c> and
/// <c>conversations.replies</c> (scope <c>channels:history</c>).
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveSlackNotificationsContract : NotificationsContract
{
    static string Token => Environment.GetEnvironmentVariable("SLACK_BOT_TOKEN") is { Length: > 0 } t ? t : throw new InvalidOperationException("SLACK_BOT_TOKEN is not set");

    static string ChannelId => Environment.GetEnvironmentVariable("FF_SLACK_CHANNEL_ID") is { Length: > 0 } c ? c : throw new InvalidOperationException("FF_SLACK_CHANNEL_ID is not set");

    public LiveSlackNotificationsContract() =>
        Notifications = new SlackNotifications(_ => Task.FromResult(ProviderResult<string>.Success(Token)),
            _ => Task.FromResult(ProviderResult<string>.Failure("unauthenticated", "not used")), new Dictionary<string, ChatPerson>());

    protected override INotificationsProvider Notifications { get; }

    protected override string Channel => ChannelId;

    protected override string UnknownChannel => "C0000NOWHERE";

    protected override async Task<IReadOnlyList<string>> MessagesAsync(string channel, MessageRef? thread)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
        var url = thread is null
            ? $"https://slack.com/api/conversations.history?channel={channel}&limit=50"
            : $"https://slack.com/api/conversations.replies?channel={channel}&ts={thread.Id}&limit=50";
        var json = JsonNode.Parse(await http.GetStringAsync(url, Ct))!;
        Assert.True(json["ok"]?.GetValue<bool>() == true, json.ToJsonString());
        // conversations.replies lists the thread's first message too; a reply is any other.
        return json["messages"]!.AsArray().Where(m => thread is null || m!["ts"]!.GetValue<string>() != thread.Id)
            .Select(m => m!["text"]!.GetValue<string>()).ToList();
    }
}
