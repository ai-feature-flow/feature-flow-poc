using System.Collections.Concurrent;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>A message the in-memory notifications provider holds: where it went, its thread, its text, and the decision it asks for.</summary>
public sealed record PostedMessage(MessageRef Ref, MessageRef? Thread, string Text, DecisionRequest? Decision);

/// <summary>
/// Notifications in memory, for engine tests (docs/06 section 3.10): every post is kept by channel, in order. A channel
/// is known once <see cref="AddChannel"/> names it; posting to any other is <c>not_found</c>. Providers with the same
/// world share their messages.
/// </summary>
public sealed class InMemoryNotifications : INotificationsProvider
{
    static readonly ConcurrentDictionary<string, InMemoryNotifications> Worlds = new(StringComparer.Ordinal);

    readonly Lock gate = new();
    readonly HashSet<string> channels = new(StringComparer.Ordinal) { "#ff" };
    readonly List<PostedMessage> posted = [];

    public static InMemoryNotifications Named(string world) => Worlds.GetOrAdd(world, _ => new InMemoryNotifications());

    public InMemoryNotifications AddChannel(string channel)
    {
        lock (gate)
            channels.Add(channel);
        return this;
    }

    public ProviderDescription Describe() => new("notifications", "memory", "0.1.0", new NotificationCapabilities(["memory"], true, true));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<MessageRef>> SendAsync(string channel, Notification message, MessageRef? thread, CancellationToken cancellationToken) =>
        Task.FromResult(Post(channel, thread, $"[{message.Event}] {message.Title}\n{message.Text}", null));

    public Task<ProviderResult<MessageRef>> PostDecisionAsync(string channel, DecisionRequest request, MessageRef? thread, CancellationToken cancellationToken) =>
        Task.FromResult(Post(channel, thread,
            $"[{NotificationEvents.DecisionNeeded}: {request.Kind}] {request.DecisionType} in {request.RunId} ({request.WaitId})\n{request.Prompt}\nChoices: {string.Join(", ", request.Choices)}",
            request));

    ProviderResult<MessageRef> Post(string channel, MessageRef? thread, string text, DecisionRequest? decision)
    {
        lock (gate)
        {
            if (!channels.Contains(channel))
                return ProviderResult<MessageRef>.Failure("not_found", $"no channel {channel}");
            if (thread is not null && (thread.Channel != channel || !posted.Any(p => p.Ref == thread)))
                return ProviderResult<MessageRef>.Failure("not_found", $"no thread {thread.Id} in {channel}");
            var reference = new MessageRef(channel, $"m{posted.Count + 1}");
            posted.Add(new PostedMessage(reference, thread, text, decision));
            return ProviderResult<MessageRef>.Success(reference);
        }
    }

    /// <summary>The messages posted to <paramref name="channel"/>: at its top level, or in <paramref name="thread"/>, in order.</summary>
    public IReadOnlyList<PostedMessage> Messages(string channel, MessageRef? thread = null)
    {
        lock (gate)
            return posted.Where(p => p.Ref.Channel == channel && p.Thread == thread).ToList();
    }
}

/// <summary>
/// The session surface in memory, for engine tests (docs/06 section 3.11): it keeps each workspace prepared and answers
/// with the CLI command a person would run. Preparing a session again returns the same handle.
/// </summary>
public sealed class InMemorySessionSurface : ISessionSurfaceProvider
{
    static readonly ConcurrentDictionary<string, InMemorySessionSurface> Worlds = new(StringComparer.Ordinal);

    readonly ConcurrentDictionary<string, (SessionWorkspace Workspace, SessionHandle Handle)> prepared = new(StringComparer.Ordinal);

    public static InMemorySessionSurface Named(string world) => Worlds.GetOrAdd(world, _ => new InMemorySessionSurface());

    public ProviderDescription Describe() => new("session_surface", "memory", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<SessionHandle>> PrepareAsync(SessionWorkspace workspace, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(workspace.SessionId))
            return Task.FromResult(ProviderResult<SessionHandle>.Failure("validation_failed", "a session needs an id"));
        var entry = prepared.GetOrAdd(workspace.SessionId, id => (workspace, new SessionHandle(id, $"ff session fetch {id}")));
        return Task.FromResult(ProviderResult<SessionHandle>.Success(entry.Handle));
    }

    /// <summary>The workspace a session was first prepared with, or null.</summary>
    public SessionWorkspace? Workspace(string sessionId) => prepared.TryGetValue(sessionId, out var entry) ? entry.Workspace : null;
}
