namespace FeatureFlow.Api.Contracts;

/// <summary><c>GET /attention</c>: the open waits the caller may answer now, oldest first.</summary>
public sealed record AttentionList(IReadOnlyList<AttentionItem> Waits);

/// <summary>A wait that needs the caller, with its run.</summary>
public sealed record AttentionItem(RunSummary Run, WaitView Wait);

/// <summary>
/// <c>GET /me</c>: the authenticated caller: the actor, how it reached the API (<c>api</c>, <c>chat</c>), its
/// identity-provider groups (null for the local token), its organisation-wide roles, and its roles in each team where it holds any.
/// </summary>
public sealed record MeResponse(
    string Actor,
    string Channel,
    IReadOnlyList<string>? Groups,
    IReadOnlyList<string> Roles,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Teams);
