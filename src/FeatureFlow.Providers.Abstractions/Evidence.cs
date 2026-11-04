namespace FeatureFlow.Providers.Abstractions;

// Evidence records the testing and analysis actions produce (docs/04 sections 9 and 10.1), stored as JSON
// artifacts. They live here so a validator provider can read them too.

/// <summary>A failed test: its name and the first line of its message.</summary>
public sealed record TestFailure(string Name, string Message);

/// <summary>
/// Test results evidence: one tier's results at one revision, parsed from TRX or JUnit (<see cref="ResultFormats"/>),
/// whether the tier ran in a sandbox or in CI. <see cref="Failures"/> lists every failed test, so a later run can be
/// compared with the baseline by name. <see cref="Passing"/> names every passed test, so a claim can be checked against
/// exactly the tests the engine saw pass (LOG 171); null for a tier judged by its exit code, whose tests cannot be claimed.
/// </summary>
public sealed record TestResults(string Revision, string Tier, int Passed, int Failed, int Skipped, IReadOnlyList<TestFailure> Failures,
    IReadOnlyList<string>? Passing = null)
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

// The JSON shapes of the engine's evidence artifacts (docs/04 section 10.1; std's evidence types have `schema: engine`),
// written with ArtifactShapes.Engine. They live here, not in the engine, so a validator provider reads the same shapes.

/// <summary>
/// <c>test_results</c>: one repository's tiers at one revision. <see cref="Passed"/> when every tier is clean. The tiers
/// come from the parent branch's manifest (LOG 138).
/// </summary>
public sealed record TestEvidence(string Repository, string Revision, bool Baseline, bool Passed, IReadOnlyList<TestResults> Tiers);

/// <summary><c>check_results</c>: every check on a head when <c>ci.checks</c> decided, with the pull request it waited on.</summary>
public sealed record CheckEvidence(string Repository, string Revision, string? PullRequest, bool Passed, int Skipped, IReadOnlyList<CheckRun> Checks);

/// <summary><c>analysis_results</c>: the analyser's issues at a revision, and the fingerprints counted as new against the baseline.</summary>
public sealed record AnalysisEvidence(string Repository, string Revision, string Analyser, bool Baseline, bool Passed, IReadOnlyList<AnalysisIssue> Issues,
    IReadOnlyList<string> New);

/// <summary>
/// <c>merge_record</c>: who merged, by which method, the head merged and the target revision it replaced, and the
/// resulting revision (docs/04 section 7.3). Read by <c>delivery.report</c>.
/// </summary>
public sealed record MergeEvidence(string Repository, string Target, string Method, string Head, string? TargetBefore, string Revision, string MergedBy,
    bool AlreadyMerged, string? PullRequest);

/// <summary>
/// <c>validation_report</c>, written by <c>artifact.validate</c> (docs/04 section 10.3): each validator's result over its
/// subjects, and the repository revisions frozen for the check (repository to revision). <see cref="Valid"/> when every
/// entry is.
/// </summary>
public sealed record ValidationReport(bool Valid, IReadOnlyDictionary<string, string> Repositories, IReadOnlyList<ValidationEntry> Validations);

/// <summary>One validator's run within a <see cref="ValidationReport"/>: the provider instance that ran it, its subjects as given, and its result.</summary>
public sealed record ValidationEntry(string Validator, string Provider, IReadOnlyList<ValidationSubject> Subjects, ValidationResult Result);
