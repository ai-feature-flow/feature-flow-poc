namespace FeatureFlow.Api.Contracts;

/// <summary><c>GET /attention</c>: the open waits the caller may answer now, oldest first; and the caller's recent answers (the last 10, of 7 days).</summary>
public sealed record AttentionList(IReadOnlyList<AttentionItem> Waits, IReadOnlyList<RecentAnswerView>? Recent = null);

/// <summary>A wait that needs the caller, with its run and that run's ancestors (root first, to the run's parent).</summary>
public sealed record AttentionItem(RunSummary Run, WaitView Wait, IReadOnlyList<RunSummary>? Path = null);

/// <summary>An answer the caller gave: the run, the wait and its step and title, the choice, the reason, the result, and when.</summary>
public sealed record RecentAnswerView(
    RunSummary Run,
    string WaitId,
    string StepId,
    string? Title,
    string Choice,
    string? Reason,
    string Result,
    DateTimeOffset At);

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
