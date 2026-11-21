using FeatureFlow.Model;

namespace FeatureFlow.Api;

/// <summary>A library source as last read: the library, or why it could not be read.</summary>
public sealed record CachedLibrary(LibrarySourceConfig Source, Library.Library? Library, string? Problem);

/// <summary>
/// The organisation's libraries for read routes (<c>GET /attention</c>, <c>can_answer</c> on a run, templates), read at most
/// once a minute per process so a read does not clone each <c>git</c> library per request (LOG 395). A library that could
/// not be read is kept as such for the same minute. The answer path does not use it: it reads the libraries at the moment of
/// use (docs/05 section 8).
/// </summary>
public sealed class LibraryCache(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    readonly SemaphoreSlim gate = new(1, 1);
    (OrganisationConfig Organisation, DateTimeOffset ReadAt, IReadOnlyList<CachedLibrary> Libraries)? cached;

    /// <summary>The organisation's libraries, read now if the last read is older than <see cref="Lifetime"/> or of another configuration.</summary>
    public async Task<IReadOnlyList<CachedLibrary>> ReadAsync(OrganisationConfig organisation, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            if (cached is { } c && ReferenceEquals(c.Organisation, organisation) && clock.GetUtcNow() - c.ReadAt < Lifetime)
                return c.Libraries;
            var libraries = new List<CachedLibrary>();
            foreach (var source in organisation.Libraries)
            {
                var (library, problem) = await LibrarySources.OpenAsync(source, organisation.Location, ct);
                libraries.Add(new CachedLibrary(source, library, problem));
            }

            cached = (organisation, clock.GetUtcNow(), libraries);
            return libraries;
        }
        finally
        {
            gate.Release();
        }
    }
}
