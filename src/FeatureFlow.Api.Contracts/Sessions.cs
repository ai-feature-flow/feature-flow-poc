namespace FeatureFlow.Api.Contracts;

/// <summary><c>GET /runs/{id}/sessions</c>: the run's interactive sessions, by session id.</summary>
public sealed record SessionList(IReadOnlyList<SessionView> Sessions);

/// <summary>
/// An interactive session (docs/04 section 4.4) on a decision's wait: for whom, where to work, its status, and once
/// submitted the commit imported and the artifacts it produced by name.
/// </summary>
public sealed record SessionView(
    string SessionId,
    string RunId,
    string WaitId,
    string StepId,
    string Person,
    string Repository,
    string Branch,
    string BaseRevision,
    string Status,
    string? SubmittedRevision,
    IReadOnlyDictionary<string, string>? Artifacts,
    DateTimeOffset At);

/// <summary><c>GET /sessions/{sid}</c>: the session, its workspace document, and the drafts' and the context's content inline, by path and by name.</summary>
public sealed record SessionDetail(
    SessionView Session,
    WorkspaceView Workspace,
    IReadOnlyDictionary<string, string> Drafts,
    IReadOnlyDictionary<string, string> Context);

/// <summary>
/// What a person fetches: where to work (a branch of a repository, at a base revision, in a folder), the files to write,
/// the rendered prompt, the artifacts the decision consumes by reference, the later steps whose checks may run, and the
/// outcomes that submit and abandon.
/// </summary>
public sealed record WorkspaceView(
    string SessionId,
    string RunId,
    string WaitId,
    string StepId,
    string Repository,
    string Branch,
    string BaseRevision,
    string Folder,
    IReadOnlyList<WorkspaceFileView> Files,
    string Prompt,
    IReadOnlyDictionary<string, string> Context,
    IReadOnlyList<string> ChecksFrom,
    string SubmitOutcome,
    string? AbandonOutcome,
    string Instructions);

/// <summary>One file of a workspace: its path in the folder, the artifact it becomes and its type, and the draft it starts from.</summary>
public sealed record WorkspaceFileView(string Path, string Artifact, string? Type, string? SeedRef);

/// <summary><c>GET /sessions/{sid}/checks</c>: the session's pre-submit checks, by check id.</summary>
public sealed record SessionChecks(IReadOnlyList<SessionCheckView> Checks);

/// <summary>
/// A pre-submit check: the later step it stands for, the commit, the digest of its inputs, its status (<c>running</c>,
/// <c>passed</c>, <c>failed</c>), its result's reference and content, and what the run's timeline says of it.
/// </summary>
public sealed record SessionCheckView(
    string CheckId,
    string StepId,
    string Revision,
    string Inputs,
    string Status,
    string? ResultRef,
    DateTimeOffset At,
    string? Detail,
    string? Result);

/// <summary><c>POST /sessions/{sid}/checks</c>: run the check of <see cref="Step"/> on <see cref="Revision"/>, a full commit id.</summary>
public sealed record SessionCheckRequest(string Step, string Revision);

public sealed record SessionCheckResponse(string CheckId, string Result);

/// <summary><c>POST /sessions/{sid}/submit</c>: submit the session's files at <see cref="Revision"/>, a full commit id pushed to its branch.</summary>
public sealed record SessionSubmitRequest(string Revision, string? Reason = null);

/// <summary><c>POST /sessions/{sid}/abandon</c>: answer the session's decision with its abandon outcome; a reason is needed only if that outcome routes the step as a failure.</summary>
public sealed record SessionAbandonRequest(string? Reason = null);
