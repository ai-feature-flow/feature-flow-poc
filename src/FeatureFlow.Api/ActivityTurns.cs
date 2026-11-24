using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api;

/// <summary>
/// A running agent session's recorded activity read as turns, in the transcript's shape (design s3): a message, reasoning,
/// notice or error is a turn of its kind; a tool call's start and finish, matched by call id, are one tool turn, whose
/// output stays null until it finishes (a finished call's output is never null); a finish with no start is a tool turn of
/// its own; the <c>truncated</c> marker is a notice that the rest is in the transcript.
/// </summary>
public static class ActivityTurns
{
    public const string TruncatedNotice = "More in the transcript once the session ends";

    /// <summary>The turns of one session's records, in sequence order. Kinds this does not know are passed over.</summary>
    public static IReadOnlyList<TranscriptTurn> Of(IEnumerable<ActivityRecord> records)
    {
        var turns = new List<TranscriptTurn>();
        var started = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var a in records.OrderBy(a => a.Sequence))
            switch (a.Kind)
            {
                case ActivityKinds.Message:
                    turns.Add(new TranscriptTurn(TranscriptTurnKind.Message, a.At, a.Text, null, false));
                    break;
                case ActivityKinds.Reasoning:
                    turns.Add(new TranscriptTurn(TranscriptTurnKind.Reasoning, a.At, a.Text, null, false));
                    break;
                case ActivityKinds.Notice:
                    turns.Add(new TranscriptTurn(TranscriptTurnKind.Notice, a.At, a.Text, null, false));
                    break;
                case ActivityKinds.Error:
                    turns.Add(new TranscriptTurn(TranscriptTurnKind.Error, a.At, a.Text, null, true));
                    break;
                case ActivityKinds.ToolStarted:
                    if (a.CallId is not null)
                        started[a.CallId] = turns.Count;
                    turns.Add(new TranscriptTurn(TranscriptTurnKind.Tool, a.At, null, new TranscriptTool(a.Name ?? "", a.Text, a.Detail, null), false));
                    break;
                case ActivityKinds.ToolFinished when a.CallId is not null && started.Remove(a.CallId, out var at):
                    turns[at] = turns[at] with { Tool = turns[at].Tool! with { Output = a.Detail ?? "" }, Failed = a.Failed };
                    break;
                case ActivityKinds.ToolFinished:
                    turns.Add(new TranscriptTurn(TranscriptTurnKind.Tool, a.At, null, new TranscriptTool(a.Name ?? "", null, null, a.Detail ?? ""), a.Failed));
                    break;
                case ActivityKinds.Truncated:
                    turns.Add(new TranscriptTurn(TranscriptTurnKind.Notice, a.At, TruncatedNotice, null, false));
                    break;
            }

        return turns;
    }
}
