using System.Collections.Concurrent;
using System.Threading.Channels;
using FeatureFlow.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace FeatureFlow.Api;

/// <summary>
/// The change notifications <c>GET /events</c> streams (docs/02 section 7, LOG A3-0j, A3-0k): one feed per API process
/// polls the record store's changes (<see cref="Queries.Changes"/>) and fans each out to every subscriber. It reads the
/// store, never memory shared with the workers, since the API may run apart from them. Each subscriber has a bounded
/// channel; one that is full is closed rather than waited for, and its client reconnects. The last
/// <see cref="RingSize"/> changes serve a reconnection's <c>Last-Event-ID</c>; one older than those is told to re-read
/// (<see cref="Subscription.ResetTo"/>). A heartbeat goes to every subscriber at its interval. Polling and the heartbeat
/// run on <see cref="ApiServices.Time"/>, so tests drive both (<see cref="PollAsync"/>, a fake clock).
/// </summary>
public sealed class ChangeFeed(ApiServices services, ILogger<ChangeFeed> logger) : IAsyncDisposable
{
    /// <summary>How many of the latest changes a reconnection can be given.</summary>
    public const int RingSize = 1024;

    /// <summary>How many frames wait for one subscriber before it is closed.</summary>
    public const int SubscriberCapacity = 256;

    /// <summary>The page size of a poll: the store's limit.</summary>
    const int PageSize = 500;

    static readonly IReadOnlyDictionary<string, string> NoParameters = new Dictionary<string, string>();

    readonly SemaphoreSlim polling = new(1, 1);
    readonly Lock gate = new();
    readonly Queue<ChangeRecord> ring = new();
    readonly List<Channel<ChangeRecord?>> subscribers = [];
    readonly ConcurrentDictionary<string, IReadOnlyList<string>> repositories = new(StringComparer.Ordinal);
    readonly CancellationTokenSource stopping = new();
    ITimer? heartbeat;
    Task? loop;

    /// <summary>Whether the feed has read where the store's changes stand; until then it has no position.</summary>
    volatile bool started;

    /// <summary>The last change read.</summary>
    long last;

    /// <summary>The change after which the ring holds every change read: a reconnection from here on can be replayed.</summary>
    long floor;

    IRecordStoreProvider Records => services.Providers.Required<IRecordStoreProvider>("record_store");

    /// <summary>Starts polling every <see cref="ApiServices.EventPoll"/> and the heartbeat every <see cref="ApiServices.EventHeartbeat"/>.</summary>
    public void Start()
    {
        heartbeat = services.Time.CreateTimer(_ => Beat(), null, services.EventHeartbeat, services.EventHeartbeat);
        loop = Task.Run(async () =>
        {
            while (!stopping.IsCancellationRequested)
            {
                try
                {
                    await PollAsync(stopping.Token);
                }
                catch (OperationCanceledException) when (stopping.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    // Retried at the next tick; subscribers stay open.
                    logger.LogWarning(e, "polling the record store's changes failed");
                }

                try
                {
                    await Task.Delay(services.EventPoll, services.Time, stopping.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    /// <summary>Stops polling and closes every subscriber, so their streams end and the host can stop.</summary>
    public void Stop()
    {
        stopping.Cancel();
        heartbeat?.Dispose();
        lock (gate)
        {
            foreach (var subscriber in subscribers)
                subscriber.Writer.TryComplete();
            subscribers.Clear();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        if (loop is not null)
            await loop;
        stopping.Dispose();
    }

    /// <summary>
    /// Reads the changes after the last one read, in pages until a short page, and gives each to every subscriber. The
    /// first poll only finds where the changes stand (<see cref="Queries.LatestChange"/>): a feed does not replay history.
    /// </summary>
    public async Task PollAsync(CancellationToken ct = default)
    {
        await polling.WaitAsync(ct);
        try
        {
            if (!started)
            {
                var latest = (await Records.QueryAsync<ChangeRecord>(Queries.LatestChange, NoParameters, ct)).Unwrap().SingleOrDefault();
                lock (gate)
                {
                    last = floor = latest?.Sequence ?? 0;
                    started = true;
                }

                return;
            }

            while (true)
            {
                var page = (await Records.QueryAsync<ChangeRecord>(Queries.Changes,
                    new Dictionary<string, string> { ["after"] = last.ToString(System.Globalization.CultureInfo.InvariantCulture), ["limit"] = PageSize.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                    ct)).Unwrap();
                lock (gate)
                    foreach (var change in page)
                        Publish(change);
                if (page.Count < PageSize)
                    return;
            }
        }
        finally
        {
            polling.Release();
        }
    }

    void Publish(ChangeRecord change)
    {
        ring.Enqueue(change);
        if (ring.Count > RingSize)
            floor = ring.Dequeue().Sequence;
        last = change.Sequence;
        Send(change);
    }

    /// <summary>A heartbeat: a null frame to every subscriber.</summary>
    void Beat()
    {
        lock (gate)
            Send(null);
    }

    /// <summary>Gives a frame to every subscriber; one whose channel is full is closed and dropped (LOG A3-0k).</summary>
    void Send(ChangeRecord? frame)
    {
        for (var i = subscribers.Count - 1; i >= 0; i--)
            if (!subscribers[i].Writer.TryWrite(frame))
            {
                subscribers[i].Writer.TryComplete();
                subscribers.RemoveAt(i);
            }
    }

    /// <summary>
    /// A new subscriber. With <paramref name="lastEventId"/> (a reconnection's <c>Last-Event-ID</c>), the changes after it
    /// if the ring still holds them all, else a reset to the latest change; -1 means an id that could not be read. The
    /// subscriber is registered in the same step, so no change falls between what it is given and what it is sent.
    /// </summary>
    public async Task<Subscription> SubscribeAsync(long? lastEventId, CancellationToken ct)
    {
        if (!started)
            await PollAsync(ct);
        var channel = Channel.CreateBounded<ChangeRecord?>(new BoundedChannelOptions(SubscriberCapacity) { SingleReader = true });
        lock (gate)
        {
            if (stopping.IsCancellationRequested)
                channel.Writer.TryComplete();
            else
                subscribers.Add(channel);
            if (lastEventId is not { } id)
                return new Subscription(this, channel, [], null);
            return id >= floor && id <= last
                ? new Subscription(this, channel, ring.Where(c => c.Sequence > id).ToList(), null)
                : new Subscription(this, channel, [], last);
        }
    }

    void Unsubscribe(Channel<ChangeRecord?> channel)
    {
        lock (gate)
            subscribers.Remove(channel);
    }

    /// <summary>A run's repositories, which never change, cached for filtering frames by scope; null if the run is not recorded.</summary>
    public async Task<IReadOnlyList<string>?> RepositoriesAsync(string runId, CancellationToken ct)
    {
        if (repositories.TryGetValue(runId, out var known))
            return known;
        var run = (await Records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = runId }, ct)).Unwrap().FirstOrDefault();
        return run is null ? null : repositories.GetOrAdd(runId, run.Repositories);
    }

    /// <summary>
    /// One subscriber's frames: <see cref="Replay"/> first, then <see cref="Frames"/>, where null is a heartbeat. The
    /// frames end when the subscriber falls behind or the feed stops. <see cref="ResetTo"/>, when set, is the latest
    /// change: the client was told to re-read everything as of it.
    /// </summary>
    public sealed class Subscription(ChangeFeed feed, Channel<ChangeRecord?> channel, IReadOnlyList<ChangeRecord> replay, long? resetTo) : IDisposable
    {
        public IReadOnlyList<ChangeRecord> Replay { get; } = replay;

        public long? ResetTo { get; } = resetTo;

        public ChannelReader<ChangeRecord?> Frames => channel.Reader;

        public void Dispose() => feed.Unsubscribe(channel);
    }
}
