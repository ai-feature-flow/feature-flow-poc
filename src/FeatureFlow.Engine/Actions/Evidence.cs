using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

// The JSON shapes of the engine's evidence artifacts (docs/04 section 10.1; std's evidence types have `schema: engine`).
// Test and analysis results per tier are the Providers.Abstractions records, so a validator provider reads the same.

/// <summary>
/// <c>test_results</c>: one repository's tiers at one revision. <see cref="Passed"/> when every tier is clean.
/// <see cref="Manifest"/> is the hash of the manifest the tiers came from (the parent branch's, LOG 138).
/// </summary>
public sealed record TestEvidence(string Repository, string Revision, bool Baseline, bool Passed, IReadOnlyList<TestResults> Tiers, string? Manifest = null);

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
