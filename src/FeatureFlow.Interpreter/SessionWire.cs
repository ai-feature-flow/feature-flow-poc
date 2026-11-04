namespace FeatureFlow.Interpreter.Wire;

// Interactive sessions (docs/04 section 4.4): what the API and the run workflow exchange beyond the answer update. A
// submission is an answer (AnswerArgs with Revision); a pre-submit check is its own update, since it answers nothing.

/// <summary>The session's update and the names a session's records go by.</summary>
public static class SessionNames
{
    /// <summary>Update: run a pre-submit check, with <see cref="SessionCheckArgs"/>. Replies at once; the check runs on and records its result.</summary>
    public const string Check = "session_check";

    /// <summary>The agent-event key under which a session's workspace document is recorded (kind <see cref="WorkspaceEvent"/>, detail its reference).</summary>
    public static string EventKey(string sessionId) => "session/" + sessionId;

    public const string WorkspaceEvent = "workspace";

    /// <summary>A session's id: its run and the decision wait it serves, so the workflow finds the wait from the id alone.</summary>
    public static string Id(string runId, string waitId) => $"{runId}.{waitId}";

    /// <summary>The wait a session id names, or null when it is not one of <paramref name="runId"/>.</summary>
    public static string? WaitOf(string sessionId, string runId) =>
        sessionId.StartsWith(runId + ".", StringComparison.Ordinal) && sessionId.Length > runId.Length + 1 ? sessionId[(runId.Length + 1)..] : null;

    /// <summary>The run a session id names: everything before its last dot.</summary>
    public static string? RunOf(string sessionId) => sessionId.LastIndexOf('.') is > 0 and var dot ? sessionId[..dot] : null;

    /// <summary>The branch a session's person works on, in the session's repository.</summary>
    public static string Branch(string runId, string waitId) => $"ff/session/{runId}/{waitId}";

    /// <summary>
    /// The one action a pre-submit check may run (LOG 238): it reads the session's files and changes nothing. Other actions
    /// either act (merge, publish, open a pull request) or work on the run's work branch, not on the session's files.
    /// </summary>
    public const string CheckAction = "artifact.validate";

    /// <summary>The idempotency key of a check's call: never a step execution's, so what the check records is told apart.</summary>
    public static string CheckKey(string runId, string stepId, string checkId) => $"{runId}/{stepId}/{CheckSegment}/{checkId}";

    public static bool IsCheckKey(string key) => key.Contains($"/{CheckSegment}/", StringComparison.Ordinal);

    const string CheckSegment = "session-check";

    /// <summary>Whether a session id has the form <c>&lt;run&gt;.&lt;wait&gt;</c> with nothing a URL path would read as another segment.</summary>
    public static bool IsId(string? sessionId) =>
        sessionId is { Length: > 0 and <= 200 } id && RunOf(id) is { Length: > 0 } && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.') && !id.Contains("..", StringComparison.Ordinal);

    /// <summary>Whether a revision is a full commit id: what a check and a submission name, since a branch can move.</summary>
    public static bool IsCommit(string? revision) => revision is { Length: 40 or 64 } r && r.All(Uri.IsHexDigit);
}

/// <summary>
/// A pre-submit check (docs/04 section 4.4) the API authorised for <see cref="Actor"/>: run the check of <see cref="StepId"/>,
/// a step the session's <c>checks_from</c> names, on the session's files at <see cref="Revision"/>, a full commit id.
/// Idempotent by <see cref="CheckId"/>, which the API assigns.
/// </summary>
public sealed record SessionCheckArgs(string CheckId, string SessionId, string StepId, string Revision, string Actor);

/// <summary>One file of a session's workspace document: its path in the folder, the artifact it becomes and its type, and the draft it starts from.</summary>
public sealed record SessionDocumentFile(string Path, string Artifact, string? Type, string? SeedRef);

/// <summary>
/// What a person fetches (docs/04 section 4.4), stored in the artifact store when the session opens: where to work (a
/// branch of a repository, at a base revision, in a folder), the files to write, the rendered prompt, the artifacts the
/// decision consumes (the context), the later steps whose checks may run, and the outcomes that submit and abandon.
/// </summary>
public sealed record SessionDocument(
    string SessionId,
    string RunId,
    string WaitId,
    string StepId,
    string Repository,
    string Branch,
    string BaseRevision,
    string Folder,
    IReadOnlyList<SessionDocumentFile> Files,
    string Prompt,
    IReadOnlyDictionary<string, string> Context,
    IReadOnlyList<string> ChecksFrom,
    string SubmitOutcome,
    string? AbandonOutcome,
    string Instructions);
