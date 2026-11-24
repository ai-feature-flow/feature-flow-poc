namespace FeatureFlow.Api.Contracts;

/// <summary>
/// <c>GET /runs/{id}/review</c> (A14): a run's review report. Its title and pull requests, as <see cref="RunSummary"/> gives
/// them; its review passes, oldest first, the last being the current review; the findings this run raised and those an
/// external review raised; its requirement trace; and a note where the run has no review to show.
/// </summary>
public sealed record ReviewReport(
    string RunId,
    string? Title,
    IReadOnlyList<PullRequestLink> PullRequests,
    IReadOnlyList<ReviewPassView> Passes,
    IReadOnlyList<ReviewFindingView> Findings,
    IReadOnlyList<ReviewFindingView> External,
    TraceView? Trace,
    string? Note);

/// <summary>One pass of the review: its number from 1, the review step entered, when it started and ended, and each angle's result in it.</summary>
public sealed record ReviewPassView(int Pass, string StepId, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, IReadOnlyList<ReviewAngleView> Angles);

/// <summary>
/// One angle of a pass: its name, the reviewer step (<c>verify.review.review_0.assess</c>), the execution whose report is
/// shown (null when the angle reported nothing in the pass), the reviewer's verdict and what it checked (from the report's
/// <c>summary</c>; null and empty when it recorded none), how many findings the report holds and how many block, and the report.
/// </summary>
public sealed record ReviewAngleView(
    string Angle,
    string StepId,
    int? Execution,
    string? Verdict,
    IReadOnlyList<string> Checked,
    int Findings,
    int Blocking,
    string? ReportRef);

/// <summary>A finding in the review report: the ledger entry, the pass it was raised in, and the latest triage's reason, who gave it and when.</summary>
public sealed record ReviewFindingView(FindingView Finding, int? RaisedInPass, string? Reason, string? ReasonBy, DateTimeOffset? DecidedAt);

/// <summary>The run's latest <c>trace_report</c>: its reference, whether every requirement traced, and each requirement.</summary>
public sealed record TraceView(string Ref, bool Traced, IReadOnlyList<TracedRequirementView> Requirements);

/// <summary>A requirement in a trace: its id, whether it traced, the tests that proved it and the gaps.</summary>
public sealed record TracedRequirementView(string Id, bool Traced, IReadOnlyList<string> Tests, IReadOnlyList<string> Gaps);
