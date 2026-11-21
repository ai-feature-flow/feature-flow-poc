namespace FeatureFlow.Model;

/// <summary>
/// A repository as a run names it (LOG 190, 198): a name an agent wrote (<c>api</c>, <c>Acme/API</c>) means the one of the
/// run's repositories it equals ignoring case, or that it is the last part of. A name that matches none, or several
/// (<c>api</c> against <c>acme/api</c> and <c>other/api</c>), is kept as written.
/// </summary>
public static class RepositoryNames
{
    public static string As(string written, IReadOnlyList<string> known)
    {
        var matches = known.Where(r => string.Equals(r, written, StringComparison.OrdinalIgnoreCase)
                                       || r.EndsWith("/" + written, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1 ? matches[0] : written;
    }
}
