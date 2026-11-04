using System.Security.Cryptography;
using System.Text;

namespace FeatureFlow.Providers.Abstractions;

// Findings (docs/01 section 6, docs/04 section 11): the library's findings_report and triage shapes, the fingerprint,
// and the ledger's states. The ledger itself is in the record store (FindingRecord, FindingEventRecord in Stores.cs).

/// <summary>A finding's state in the ledger.</summary>
public static class FindingStates
{
    public const string Open = "open";
    public const string Fixed = "fixed";
    public const string Declined = "declined";
    public const string Escalated = "escalated";

    public static readonly IReadOnlyList<string> All = [Open, Fixed, Declined, Escalated];
}

/// <summary>Severities a reviewer may give, highest first.</summary>
public static class FindingSeverities
{
    public static readonly IReadOnlyList<string> All = ["critical", "high", "medium", "low"];
}

/// <summary>A finding's basis: the reviewer proved it, or reasoned it.</summary>
public static class FindingBases
{
    public const string Demonstrated = "demonstrated";
    public const string Reasoned = "reasoned";
}

/// <summary>Where a finding is: the fields the fingerprint uses (never a line number), and the criterion concerned.</summary>
public sealed record FindingLocation(string Repository, string File, string? Symbol = null, string? Snippet = null, string? Criterion = null);

/// <summary>
/// One entry of a <c>findings_report</c>. <see cref="LedgerId"/> and <see cref="State"/> are set only in a ledger view
/// the engine stages for a step that consumes <c>findings_ledger</c>; a reviewer leaves them out.
/// </summary>
public sealed record ReportedFinding(
    string Rule,
    string Title,
    string Description,
    string Severity,
    bool Blocking,
    string Basis,
    FindingLocation Location,
    string? RequiredCorrection = null,
    string? Proof = null,
    string? LedgerId = null,
    string? State = null);

/// <summary>
/// <c>findings_report</c> (std's artifact type): one review pass's findings. A reviewer agent writes it; a findings source
/// provider returns one; the engine stages the ledger view in it. Read and written with <see cref="ArtifactShapes.ReadLibrary{T}"/>.
/// </summary>
public sealed record FindingsReport(int Schema, IReadOnlyList<ReportedFinding> Findings, string? Angle = null);

/// <summary>Whether a fix needs a regression test, and why not when it does not.</summary>
public sealed record RegressionTest(bool Applicable, string? Reason = null);

/// <summary>One decision of a <c>triage</c>: a ledger id, its new state, and the reason.</summary>
public sealed record TriageEntry(string Finding, string State, string Reason, RegressionTest? RegressionTest = null);

/// <summary><c>triage</c> (std's artifact type): per finding, the new state and the reason. Applied by <c>findings.triage</c>.</summary>
public sealed record Triage(int Schema, IReadOnlyList<TriageEntry> Entries);

/// <summary>
/// A finding's fingerprint (docs/01 section 6): rule, repository, file, enclosing symbol, and a hash of the normalised
/// snippet. Never a line number. Two reports of the same problem at unchanged code give the same fingerprint.
/// </summary>
public static class Fingerprints
{
    /// <summary>The fingerprint as one string: <c>rule|repository|file|symbol|snippet hash</c>.</summary>
    public static string Of(ReportedFinding finding) =>
        string.Join('|', finding.Rule, finding.Location.Repository, finding.Location.File, finding.Location.Symbol ?? "", SnippetHash(finding.Location.Snippet));

    /// <summary>
    /// A hash of the snippet with whitespace normalised: each line trimmed, runs of blanks made one space, empty lines
    /// dropped. Empty when there is no snippet.
    /// </summary>
    public static string SnippetHash(string? snippet)
    {
        if (string.IsNullOrWhiteSpace(snippet))
            return "";
        var lines = snippet.Split('\n')
            .Select(l => string.Join(' ', l.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries)))
            .Where(l => l.Length > 0);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))))[..16];
    }

    /// <summary>
    /// The ledger id of a fingerprint first raised in a run: <c>F-</c> and ten hex digits of a hash of both. Derived, not
    /// allocated, so two recorders racing on one finding, or a retried record, arrive at the same id; and unique across a
    /// run tree, so a parent's view of its children's ledgers has no clashes.
    /// </summary>
    public static string LedgerId(string runId, string fingerprint) =>
        "F-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(runId + "\n" + fingerprint)))[..10];
}
