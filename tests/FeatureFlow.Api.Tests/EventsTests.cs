using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// <c>GET /events</c> (docs/02 section 7, G8): change notifications as server-sent events, filtered per run to its viewers,
/// replayed from the feed's ring on reconnection or reset beyond it, with a heartbeat. No test sleeps: the feed polls when
/// a test says (<see cref="ChangeFeed.PollAsync"/>), its heartbeat runs on a fake clock, and each read is bounded by a
/// timeout so a missing frame fails rather than hangs.
/// </summary>
public sealed class EventsTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Two teams, each viewing only its own repository by identity-provider group.</summary>
    internal static OrganisationConfig TwoTeams() =>
        new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "")
        {
            Teams = new Dictionary<string, TeamConfig>
            {
                ["pay"] = new("pay", ["acme/pay"], [], new Dictionary<string, PeopleRoleConfig> { ["viewer"] = new(["pay-eng"]) }),
                ["web"] = new("web", ["acme/web"], [], new Dictionary<string, PeopleRoleConfig> { ["viewer"] = new(["web-eng"]) }),
            },
        };

    /// <summary>
    /// A wait opening in the pay team's run reaches a pay viewer as <c>wait_opened</c>, and not a web viewer: the web viewer's
    /// next frame is the web run's own change, appended after the wait. Shared with the Postgres run of the same test.
    /// </summary>
    internal static async Task A_wait_opening_reaches_its_runs_viewers_only(ApiWorld world, string suffix)
    {
        string pay = "pay-" + suffix, web = "web-" + suffix;
        await world.RunAsync(pay, "acme/pay");
        await world.RunAsync(web, "acme/web");
        await world.Feed.PollAsync(Ct);
        await using var payStream = await EventStream.OpenAsync(world.As("pat", groups: "pay-eng"));
        await using var webStream = await EventStream.OpenAsync(world.As("wes", groups: "web-eng"));

        await world.WaitAsync(pay, "gate-1", "gate", "ticket.spec_approval");
        await world.RunAsync(web, "acme/web");
        await world.Feed.PollAsync(Ct);

        var opened = await payStream.NextAsync();
        Assert.Equal("wait_opened", opened.Event);
        Assert.Equal((pay, ChangeKind.WaitOpened, "gate-1"), (opened.Data!.RunId, opened.Data.Kind, opened.Data.WaitId));
        Assert.Equal(opened.Data.Sequence.ToString(System.Globalization.CultureInfo.InvariantCulture), opened.Id);
        var other = await webStream.NextAsync();
        Assert.Equal(("run_changed", web, ChangeKind.RunChanged, (string?)null), (other.Event, other.Data!.RunId, other.Data.Kind, other.Data.WaitId));
        Assert.True(other.Data.Sequence > opened.Data.Sequence);
    }

    [Fact]
    public async Task A_wait_opening_is_sent_to_a_viewer_of_its_run_and_not_to_others()
    {
        await using var world = new ApiWorld(TwoTeams());
        await A_wait_opening_reaches_its_runs_viewers_only(world, "sqlite");
    }

    /// <summary>
    /// Activity appended for a step reaches the run's viewer as <c>step_changed</c> with the run and step, and raises no
    /// <c>run_changed</c>: the viewer's next frame after it is the run's own later change.
    /// </summary>
    [Fact]
    public async Task Activity_appended_for_a_step_is_sent_as_step_changed_to_a_viewer_of_its_run_only()
    {
        await using var world = new ApiWorld(TwoTeams());
        await world.RunAsync("pay", "acme/pay");
        await world.RunAsync("web", "acme/web");
        await world.Feed.PollAsync(Ct);
        await using var payStream = await EventStream.OpenAsync(world.As("pat", groups: "pay-eng"));
        await using var webStream = await EventStream.OpenAsync(world.As("wes", groups: "web-eng"));

        await world.Records.AppendAsync(new RecordBatch(Activity:
        [
            new ActivityRecord("pay", "build", 1, 1, 1, ActivityKinds.Message, null, null, "Looking", null, false, DateTimeOffset.UtcNow),
            new ActivityRecord("pay", "build", 1, 1, 2, ActivityKinds.ToolStarted, "c1", "bash", null, null, false, DateTimeOffset.UtcNow),
        ]), Ct);
        await world.RunAsync("pay", "acme/pay");
        await world.RunAsync("web", "acme/web");
        await world.Feed.PollAsync(Ct);

        var changed = await payStream.NextAsync();
        Assert.Equal(("step_changed", "pay", ChangeKind.StepChanged, "build", (string?)null),
            (changed.Event, changed.Data!.RunId, changed.Data.Kind, changed.Data.StepId, changed.Data.WaitId));
        var next = await payStream.NextAsync();
        Assert.Equal(("run_changed", "pay", (string?)null), (next.Event, next.Data!.RunId, next.Data.StepId));
        var web = await webStream.NextAsync();
        Assert.Equal(("run_changed", "web"), (web.Event, web.Data!.RunId));
    }

    /// <summary>
    /// A9: a finding event reaches the run's viewer as <c>run_changed</c> and then <c>step_changed</c> for the step that wrote
    /// it; an agent event as <c>step_changed</c> for the step its key names, and an agent session's event (key
    /// <c>session/&lt;id&gt;</c>) as nothing: the viewer's next frame is the run's own later change.
    /// </summary>
    [Fact]
    public async Task Finding_events_and_agent_events_are_sent_as_step_changed_and_a_sessions_events_are_not()
    {
        await using var world = new ApiWorld();
        await world.RunAsync("r");
        await world.Feed.PollAsync(Ct);
        await using var stream = await EventStream.OpenAsync(world.As("vic", "viewer"));

        await world.Records.AppendAsync(new RecordBatch(FindingEvents:
            [new FindingEventRecord("r", "f1", "k1", FindingEventKinds.Raised, FindingStates.Open, "review", null, "verify.review.review_0.record", null, null, DateTimeOffset.UtcNow)]), Ct);
        await world.Records.AppendAsync(new RecordBatch(AgentEvents: [new AgentEventRecord("r/implement/1", "patch_staged", "{}", DateTimeOffset.UtcNow)]), Ct);
        await world.Records.AppendAsync(new RecordBatch(AgentEvents: [new AgentEventRecord("session/s-1", "hook_pre_tool_use", "Edit allow", DateTimeOffset.UtcNow)]), Ct);
        await world.RunAsync("r");
        await world.Feed.PollAsync(Ct);

        Assert.Equal(
            [
                ("run_changed", (string?)null), ("step_changed", "verify.review.review_0.record"), ("step_changed", "implement"), ("run_changed", null),
            ],
            new[] { await stream.NextAsync(), await stream.NextAsync(), await stream.NextAsync(), await stream.NextAsync() }.Select(f => (f.Event, f.Data!.StepId)));
    }

    [Fact]
    public async Task Last_event_id_within_the_ring_replays_what_followed_and_beyond_it_resets()
    {
        await using var world = new ApiWorld();
        await world.Feed.PollAsync(Ct);
        var before = (await world.Records.QueryAsync<ChangeRecord>(Queries.LatestChange, new Dictionary<string, string>(), Ct)).Unwrap().SingleOrDefault()?.Sequence ?? 0;
        // More changes than the ring holds: the first ten fall out of it.
        var count = ChangeFeed.RingSize + 10;
        await world.Records.AppendAsync(new RecordBatch(
            [.. Enumerable.Range(0, count).Select(i => new RunRecord($"r{i:D4}", null, "std:templates/t", "running", "x", [], false, DateTimeOffset.UtcNow, null))]), Ct);
        await world.Feed.PollAsync(Ct);
        HttpClient Viewer() => world.As("vic", "viewer");

        await using (var reconnect = await EventStream.OpenAsync(Viewer(), (before + count - 3).ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            var replayed = new[] { await reconnect.NextAsync(), await reconnect.NextAsync(), await reconnect.NextAsync() };
            Assert.Equal([$"r{count - 3:D4}", $"r{count - 2:D4}", $"r{count - 1:D4}"], replayed.Select(f => f.Data!.RunId));
            Assert.Equal([before + count - 2, before + count - 1, before + count], replayed.Select(f => f.Data!.Sequence));
        }

        // The oldest change the ring can replay after is the last one evicted; one before it is too old.
        await using (var oldest = await EventStream.OpenAsync(Viewer(), (before + 10).ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            var first = await oldest.NextAsync();
            Assert.Equal(("run_changed", "r0010"), (first.Event, first.Data!.RunId));
        }

        foreach (var lastEventId in new[] { (before + 9).ToString(System.Globalization.CultureInfo.InvariantCulture), "not-a-number" })
        {
            await using var tooOld = await EventStream.OpenAsync(Viewer(), lastEventId);
            var reset = await tooOld.NextAsync();
            Assert.Equal(("reset", (before + count).ToString(System.Globalization.CultureInfo.InvariantCulture), "{}"), (reset.Event, reset.Id, reset.Text));
        }
    }

    [Fact]
    public async Task Advancing_the_clock_by_the_heartbeat_interval_sends_a_ping()
    {
        await using var world = new ApiWorld();
        await using var stream = await EventStream.OpenAsync(world.As("vic", "viewer"));

        world.Time.Advance(TimeSpan.FromSeconds(15));

        Assert.Equal(": ping", (await stream.NextAsync()).Raw);
    }

    [Fact]
    public async Task A_subscriber_that_falls_a_full_channel_behind_is_closed()
    {
        await using var world = new ApiWorld();
        using var slow = await world.Feed.SubscribeAsync(null, Ct);

        // One more change than a subscriber's channel holds, none of it read.
        await world.Records.AppendAsync(new RecordBatch(
            [.. Enumerable.Range(0, ChangeFeed.SubscriberCapacity + 1).Select(i => new RunRecord($"r{i:D4}", null, "std:templates/t", "running", "x", [], false, DateTimeOffset.UtcNow, null))]), Ct);
        await world.Feed.PollAsync(Ct);
        using var later = await world.Feed.SubscribeAsync(null, Ct);
        await world.RunAsync("after");
        await world.Feed.PollAsync(Ct);

        using var bounded = Bounded();
        var waiting = new List<ChangeRecord?>();
        await foreach (var frame in slow.Frames.ReadAllAsync(bounded.Token))
            waiting.Add(frame);
        Assert.Equal(ChangeFeed.SubscriberCapacity, waiting.Count);
        Assert.Equal("r0000", waiting[0]!.RunId);
        // The feed goes on for the others.
        Assert.Equal("after", (await later.Frames.ReadAsync(bounded.Token))!.RunId);
    }

    [Fact]
    public async Task Events_without_a_credential_is_401()
    {
        await using var world = new ApiWorld();
        using var anonymous = new HttpClient { BaseAddress = world.Address };

        using var response = await anonymous.GetAsync("events", HttpCompletionOption.ResponseHeadersRead, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("unauthenticated", JsonSerializer.Deserialize<ErrorResponse>(await response.Content.ReadAsStringAsync(Ct), ApiJson.Options)!.Error);
    }

    /// <summary>The runs' repositories kept for filtering are bounded: past the bound, the run least recently used goes first.</summary>
    [Fact]
    public void The_feeds_run_repositories_are_bounded_and_the_least_recently_used_goes_first()
    {
        var cache = new ChangeFeed.RunRepositories(2);
        cache.Add("a", ["acme/a"]);
        cache.Add("b", ["acme/b"]);
        Assert.NotNull(cache.Get("a"));

        cache.Add("c", ["acme/c"]);

        Assert.Equal(2, cache.Count);
        Assert.Null(cache.Get("b"));
        Assert.Equal(["acme/a"], cache.Get("a"));
        Assert.Equal(["acme/c"], cache.Get("c"));
    }

    static CancellationTokenSource Bounded()
    {
        var bounded = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        return bounded;
    }

    /// <summary>One server-sent event: its lines as read (<see cref="Raw"/>), and its <c>id</c>, <c>event</c> and <c>data</c>.</summary>
    internal sealed record Frame(string Raw, string? Id, string? Event, string? Text)
    {
        public ChangeEvent? Data => Event is "run_changed" or "wait_opened" or "wait_closed" or "step_changed" ? JsonSerializer.Deserialize<ChangeEvent>(Text!, ApiJson.Options) : null;
    }

    /// <summary>An open <c>GET /events</c> response, read frame by frame; the first frame, <c>retry</c>, is checked and skipped. It owns its client.</summary>
    internal sealed class EventStream : IAsyncDisposable
    {
        readonly HttpClient http;
        readonly HttpResponseMessage response;
        readonly StreamReader reader;

        EventStream(HttpClient http, HttpResponseMessage response, StreamReader reader) => (this.http, this.response, this.reader) = (http, response, reader);

        public static async Task<EventStream> OpenAsync(HttpClient http, string? lastEventId = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "events");
            if (lastEventId is not null)
                request.Headers.Add("Last-Event-ID", lastEventId);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, Ct);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal(["no-store"], response.Headers.GetValues("Cache-Control"));
            Assert.Equal(["no"], response.Headers.GetValues("X-Accel-Buffering"));
            var stream = new EventStream(http, response, new StreamReader(await response.Content.ReadAsStreamAsync(Ct)));
            Assert.Equal("retry: 3000", (await stream.NextAsync()).Raw);
            return stream;
        }

        /// <summary>The next frame; fails if none comes within the bound or the stream ends.</summary>
        public async Task<Frame> NextAsync()
        {
            using var bounded = Bounded();
            var lines = new List<string>();
            while (await reader.ReadLineAsync(bounded.Token) is { } line)
            {
                if (line.Length == 0)
                {
                    string? Field(string name) => lines.FirstOrDefault(l => l.StartsWith(name + ": ", StringComparison.Ordinal))?[(name.Length + 2)..];
                    return new Frame(string.Join("\n", lines), Field("id"), Field("event"), Field("data"));
                }

                lines.Add(line);
            }

            throw new Xunit.Sdk.XunitException("the event stream ended");
        }

        public ValueTask DisposeAsync()
        {
            reader.Dispose();
            response.Dispose();
            http.Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
