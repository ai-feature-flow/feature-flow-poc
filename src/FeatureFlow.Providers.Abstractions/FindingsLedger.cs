namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// What the ledger answers (docs/01 section 6): the counts of the <c>findings</c> namespace route guards read (docs/03
/// section 5.3), and the ledger view the engine stages for a step that consumes <c>findings_ledger</c> (LOG 63). Both
/// read a run's ledger as <see cref="Queries.Findings"/> returns it: the run's own findings and its descendants'.
/// </summary>
public static class FindingsLedger
{
    /// <summary>The reserved artifact name a step consumes to be given the ledger view (LOG 63).</summary>
    public const string ViewName = "findings_ledger";

    /// <summary>The names of the <c>findings</c> namespace, in docs/03's order.</summary>
    public static readonly IReadOnlyList<string> Names =
        ["open", "open_blocking", "open_demonstrated", "fixed", "declined", "escalated", "reraised_unchanged", "repeat_fixes"];

    /// <summary>
    /// The counts: by state; open and blocking; open and demonstrated; whose latest re-raise found the code unchanged; and
    /// open again after being fixed at least twice (<c>repeat_fixes</c>).
    /// </summary>
    public static IReadOnlyDictionary<string, long> Counts(IEnumerable<FindingRecord> ledger)
    {
        var list = ledger.ToList();
        bool Open(FindingRecord f) => f.State == FindingStates.Open;
        return new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["open"] = list.Count(Open),
            ["open_blocking"] = list.Count(f => Open(f) && f.Finding.Blocking),
            ["open_demonstrated"] = list.Count(f => Open(f) && f.Finding.Basis == FindingBases.Demonstrated),
            ["fixed"] = list.Count(f => f.State == FindingStates.Fixed),
            ["declined"] = list.Count(f => f.State == FindingStates.Declined),
            ["escalated"] = list.Count(f => f.State == FindingStates.Escalated),
            ["reraised_unchanged"] = list.Count(f => f.ReraisedUnchanged),
            ["repeat_fixes"] = list.Count(IsRepeatFix),
        };
    }

    /// <summary>A finding open again after it was fixed at least twice: std escalates rather than fix it a third time.</summary>
    public static bool IsRepeatFix(FindingRecord finding) => finding.State == FindingStates.Open && finding.Fixes >= 2;

    /// <summary>The ledger as a <c>findings_report</c>, each finding with its ledger id and state, in the ledger's order.</summary>
    public static FindingsReport View(IEnumerable<FindingRecord> ledger) =>
        new(1, ledger.Select(f => f.Finding with { LedgerId = f.FindingId, State = f.State }).ToList());
}
