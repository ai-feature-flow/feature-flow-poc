namespace FeatureFlow.Providers.Abstractions;

// Evidence records the testing and analysis actions produce (docs/04 sections 9 and 10.1), stored as JSON
// artifacts. They live here so a validator provider can read them too.

/// <summary>A failed test: its name and the first line of its message.</summary>
public sealed record TestFailure(string Name, string Message);

/// <summary>
/// Test results evidence: one tier's results at one revision, parsed from TRX or JUnit (<see cref="ResultFormats"/>),
/// whether the tier ran in a sandbox or in CI. <see cref="Failures"/> lists every failed test, so a later run can be
/// compared with the baseline by name.
/// </summary>
public sealed record TestResults(string Revision, string Tier, int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures)
{
    public bool Clean => Failed == 0;
}

/// <summary>A SARIF issue's level, lowest to highest: <c>note</c>, <c>warning</c>, <c>error</c>.</summary>
public static class IssueLevels
{
    public const string Note = "note";
    public const string Warning = "warning";
    public const string Error = "error";
}

/// <summary>
/// One static analysis issue. <see cref="Fingerprint"/> identifies it across revisions (SARIF's
/// <c>partialFingerprints</c>, else the rule, path and message), so new issues can be told from the baseline's.
/// </summary>
public sealed record AnalysisIssue(string RuleId, string Level, string? Path, int? Line, string Message, string Fingerprint);

/// <summary>Analysis results evidence (docs/04 section 9.3): the analyser's issues at one revision, parsed from SARIF.</summary>
public sealed record AnalysisResults(string Revision, string Analyser, IReadOnlyList<AnalysisIssue> Issues);
