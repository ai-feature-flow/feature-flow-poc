namespace Sp2;

// Shared by the replica and the harness. Signal and update names are explicit so
// the replica can signal by name without referencing the workflow type.

public static class Names
{
    public const string OpenQuestion = "open_question";
    public const string Report = "report";
    public const string Answer = "answer";
}

public sealed record QuestionOpen(string Id, string Text, List<string> Options);

public sealed record ReportIn(string Outcome, string Summary);

public sealed record AnswerIn(string QuestionId, string Answer);

/// <summary>The record-store entry for one question, written only by the workflow's activity.</summary>
public sealed record QuestionRecord(string Id, string Status, string? Answer);

public static class RecordStore
{
    public static string PathFor(string storeDir, string stepId, string questionId) =>
        Path.Combine(storeDir, stepId, questionId + ".json");
}
