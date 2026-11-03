using System.Collections.Concurrent;
using System.Text;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// A findings source in memory (docs/06 section 3.7), for engine tests: a test scripts what a review of a repository at a
/// revision returns, after how many polls. A target with no script, or scripted with no report, is <c>unavailable</c>, as
/// a repository without the external reviewer would be. Sources configured with the same <c>world</c> share scripts.
/// </summary>
public sealed class InMemoryFindingsSource(Func<IArtifactStoreProvider> store) : IFindingsSourceProvider
{
    static readonly ConcurrentDictionary<string, InMemoryFindingsSource> Worlds = new(StringComparer.Ordinal);

    public static InMemoryFindingsSource Named(string name, Func<IArtifactStoreProvider> store) => Worlds.GetOrAdd(name, _ => new InMemoryFindingsSource(store));

    sealed record Script(FindingsReport? Report, int PollsUntilReady);

    sealed class Request
    {
        public required FindingsTarget Target;
        public required Script Script;
        public int Polls;
    }

    readonly Lock gate = new();
    readonly Dictionary<(string Repository, string Revision), Script> scripts = [];
    readonly Dictionary<string, Request> requests = new(StringComparer.Ordinal);

    public ProviderDescription Describe() => new("findings_source", "memory", "0.1.0", new FindingsSourceCapabilities(true, true, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    /// <summary>A review of <paramref name="repository"/> at <paramref name="revision"/> returns <paramref name="report"/> (null: unavailable) once polled <paramref name="pollsUntilReady"/> times.</summary>
    public void Review(RepoRef repository, string revision, FindingsReport? report, int pollsUntilReady = 0)
    {
        lock (gate)
            scripts[(repository.FullName, revision)] = new Script(report, pollsUntilReady);
    }

    /// <summary>How many review requests were made: a retried request with the same key counts once.</summary>
    public int Requests
    {
        get
        {
            lock (gate)
                return requests.Count;
        }
    }

    public Task<ProviderResult<CollectHandle>> RequestAsync(FindingsTarget target, string key, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!requests.ContainsKey(key))
                requests[key] = new Request
                {
                    Target = target,
                    Script = scripts.GetValueOrDefault((target.Repository.FullName, target.Revision)) ?? new Script(null, 0),
                };
            return Task.FromResult(ProviderResult<CollectHandle>.Success(new CollectHandle(key)));
        }
    }

    public Task<ProviderResult<CollectStatus>> GetStatusAsync(CollectHandle handle, CancellationToken cancellationToken)
    {
        lock (gate)
        {
            if (!requests.TryGetValue(handle.Id, out var request))
                return Task.FromResult(ProviderResult<CollectStatus>.Failure("not_found", $"no review request {handle.Id}"));
            return Task.FromResult(ProviderResult<CollectStatus>.Success(Status(request, poll: true)));
        }
    }

    public async Task<ProviderResult<ArtifactRef>> ReadFindingsAsync(CollectHandle handle, CancellationToken cancellationToken)
    {
        FindingsReport report;
        lock (gate)
        {
            if (!requests.TryGetValue(handle.Id, out var request))
                return ProviderResult<ArtifactRef>.Failure("not_found", $"no review request {handle.Id}");
            if (Status(request, poll: false).State != CollectStates.Ready)
                return ProviderResult<ArtifactRef>.Failure("validation_failed", $"review request {handle.Id} is not ready");
            report = request.Script.Report!;
        }

        return await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(ArtifactShapes.WriteLibrary(report))), cancellationToken);
    }

    static CollectStatus Status(Request request, bool poll)
    {
        if (request.Script.Report is null)
            return new CollectStatus(CollectStates.Unavailable, $"no review is configured for {request.Target.Repository.FullName}");
        if (request.Polls < request.Script.PollsUntilReady)
        {
            if (poll)
                request.Polls++;
            return new CollectStatus(CollectStates.Pending);
        }

        return new CollectStatus(CollectStates.Ready);
    }
}
