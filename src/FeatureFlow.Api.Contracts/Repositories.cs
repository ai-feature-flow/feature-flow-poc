namespace FeatureFlow.Api.Contracts;

/// <summary><c>GET /repositories</c>: the repositories registered, and those named by runs the caller views, by name.</summary>
public sealed record RepositoryList(IReadOnlyList<RepositoryView> Repositories);

/// <summary>
/// A repository: its name (<c>owner/name</c>), its host (<c>github</c>), its link and default branch where known, whether it
/// is registered, and who registered it and when.
/// </summary>
public sealed record RepositoryView(
    string Name,
    string Host,
    string? Url,
    string? DefaultBranch,
    bool Registered,
    string? RegisteredBy,
    DateTimeOffset? At);

/// <summary>
/// <c>POST /repositories</c>: register a repository, by <see cref="Github"/> (<c>owner/name</c> or
/// <c>https://github.com/owner/name[.git]</c>); a local <see cref="Path"/> is refused for now.
/// </summary>
public sealed record RegisterRepositoryRequest(string? Github = null, string? Path = null);
