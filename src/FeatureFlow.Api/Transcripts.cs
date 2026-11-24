using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api;

/// <summary>
/// An agent session's transcript, the runtime's own output, read as turns (console A4) by the runtimes' shared reader
/// (<see cref="AgentTranscripts"/>) and given in the API's shape.
/// </summary>
public static class Transcripts
{
    /// <summary>The transcript's turns, or null when no line is an event of a runtime this reads.</summary>
    public static TranscriptTurns? Read(string text) => AgentTranscripts.Read(text) is { } read ? Map(read) : null;

    public static TranscriptTurns Map(AgentTurns read) => new(
        read.Format == TranscriptFormat.CopilotCli ? TranscriptRuntime.CopilotCli : TranscriptRuntime.ClaudeCode,
        read.Model, read.Started, read.DurationMs, read.ToolCalls, read.ModelCalls, read.InputTokens, read.OutputTokens,
        read.Turns.Select(Map).ToList(),
        read.Unread.Select(u => new UnreadEvents(u.Type, u.Count)).ToList());

    static TranscriptTurn Map(AgentTurn turn) => new(Map(turn.Kind), turn.At, turn.Text,
        turn.Tool is { } t ? new TranscriptTool(t.Name, t.Summary, t.Arguments, t.Output) : null, turn.Failed);

    static TranscriptTurnKind Map(AgentTurnKind kind) => kind switch
    {
        AgentTurnKind.Prompt => TranscriptTurnKind.Prompt,
        AgentTurnKind.Message => TranscriptTurnKind.Message,
        AgentTurnKind.Reasoning => TranscriptTurnKind.Reasoning,
        AgentTurnKind.Tool => TranscriptTurnKind.Tool,
        AgentTurnKind.Notice => TranscriptTurnKind.Notice,
        AgentTurnKind.Error => TranscriptTurnKind.Error,
        _ => TranscriptTurnKind.Result,
    };
}
