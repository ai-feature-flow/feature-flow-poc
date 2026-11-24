namespace FeatureFlow.Api.Contracts;

/// <summary>What an indexed artifact is: a declared artifact, evidence, an agent session's transcript or prompt, or a revision of the run's plan.</summary>
public enum ArtifactKind
{
    Artifact,
    Evidence,
    Transcript,
    Prompt,
    PlanRevision,
}

/// <summary><c>GET /runs/{id}/artifacts</c>: the run's own artifact index, in the order recorded.</summary>
public sealed record ArtifactList(IReadOnlyList<ArtifactView> Artifacts);

/// <summary>
/// An indexed artifact: the step and execution that produced it, its name and declared type, its reference (read with
/// <c>GET /runs/{id}/artifacts/{reference}</c>), the engine action that made it, and whether it is evidence.
/// </summary>
public sealed record ArtifactView(
    string StepId,
    int Execution,
    string Name,
    string? Type,
    string Ref,
    string? Action,
    bool Evidence,
    ArtifactKind Kind,
    DateTimeOffset At);

/// <summary>
/// <c>GET /runs/{id}/artifacts/{reference}/document</c>: an artifact read as a document: its format (<c>yaml</c>, <c>json</c>,
/// <c>markdown</c>, <c>text</c>), its data as JSON text (YAML or JSON parsed and written as JSON, or a Markdown document's
/// front matter; null for text), and its body as text (Markdown after the front matter, or plain text). The data is text, not
/// a JSON value, so every schema in the document stays typed.
/// </summary>
public sealed record ArtifactDocument(string Format, string? Data, string? Body);

/// <summary>The agent runtime whose output a transcript is.</summary>
public enum TranscriptRuntime
{
    CopilotCli,
    ClaudeCode,
}

/// <summary>
/// <c>GET /runs/{id}/artifacts/{reference}/turns</c>: an agent session's transcript as turns, one shape for every runtime: the
/// runtime, the model, when the session started (null where the runtime records no times), how long it took, its tool and
/// model calls, the tokens it used (input counting cached input; null where the transcript does not say), its turns in order,
/// and the event types it holds that are not shown, with how many of each.
/// </summary>
public sealed record TranscriptTurns(
    TranscriptRuntime Runtime,
    string? Model,
    DateTimeOffset? Started,
    long? DurationMs,
    int ToolCalls,
    int ModelCalls,
    long? InputTokens,
    long? OutputTokens,
    IReadOnlyList<TranscriptTurn> Turns,
    IReadOnlyList<UnreadEvents> Unread);

/// <summary>What a turn is: the prompt, a message from the agent, its reasoning, a tool call, a notice or error from the runtime, or the session's result.</summary>
public enum TranscriptTurnKind
{
    Prompt,
    Message,
    Reasoning,
    Tool,
    Notice,
    Error,
    Result,
}

/// <summary>
/// One turn: its kind, when it happened (null where the runtime records no times), its text (Markdown for a prompt or
/// message), the tool call for a tool turn, and whether it failed (a tool call that failed, an error, a failed result).
/// </summary>
public sealed record TranscriptTurn(TranscriptTurnKind Kind, DateTimeOffset? At, string? Text, TranscriptTool? Tool, bool Failed);

/// <summary>A tool call: the tool's name, the agent's summary of it, its arguments as indented JSON text, and its result or output as text.</summary>
public sealed record TranscriptTool(string Name, string? Summary, string? Arguments, string? Output);

/// <summary>An event type a transcript holds that is not shown, and how many; unreadable lines are counted as <c>(unreadable line)</c>.</summary>
public sealed record UnreadEvents(string Type, int Count);
