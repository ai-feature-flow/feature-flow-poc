namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// The names the engine indexes beside declared artifacts (LOG A3-0f, A3-0q): an agent session's transcript and prompt,
/// <c>transcript:&lt;n&gt;</c> and <c>prompt:&lt;n&gt;</c> (the <c>kind:</c> form of evidence's <c>evidence:&lt;type&gt;</c>, which no
/// declared artifact can take), and each accepted plan revision, under no step with its revision as the execution.
/// </summary>
public static class ArtifactNames
{
    public const string TranscriptType = "transcript";
    public const string PromptType = "prompt";
    public const string PlanRevision = "plan_revision";

    public static string Transcript(int session) => $"{TranscriptType}:{session}";

    public static string Prompt(int session) => $"{PromptType}:{session}";

    /// <summary>Whether an indexed record is a transcript, a prompt or a plan revision: read by people, never consumed by a parent.</summary>
    public static bool ForPeopleOnly(ArtifactRecord record) =>
        record.StepId.Length == 0
        || record.Name.StartsWith(TranscriptType + ":", StringComparison.Ordinal)
        || record.Name.StartsWith(PromptType + ":", StringComparison.Ordinal);
}
