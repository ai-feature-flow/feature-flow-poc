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

    /// <summary>
    /// Whether an agent may not clear the finding alone (review F1, LOG 168, 248): it is escalated, or declined though
    /// blocking or demonstrated. A person decides such a finding unless a person already did (<see cref="NeedsPerson"/>).
    /// </summary>
    public static bool AgentMayNotClear(FindingRecord finding) =>
        finding.State == FindingStates.Escalated
        || (finding.State == FindingStates.Declined && (finding.Finding.Blocking || finding.Finding.Basis == FindingBases.Demonstrated));

    /// <summary>
    /// Whether the finding needs a person's decision (LOG 512): an agent may not clear it alone, and no person set its current
    /// state. <paramref name="history"/> is the finding's events in order; <paramref name="people"/> the actors of its run's answers.
    /// </summary>
    public static bool NeedsPerson(FindingRecord finding, IEnumerable<FindingEventRecord> history, IReadOnlySet<string> people) =>
        AgentMayNotClear(finding) && PersonDecision(history, people) is null;

    /// <summary>
    /// The person who set the finding's current state (LOG 512), or null: the actor of the triage event that last changed it,
    /// if a person's, or of a person's triage restating it since (the latest such). Re-raises and agents' events that leave
    /// the state alone do not undo a person's decision. <paramref name="people"/> are the actors of the run's answers (F8); a
    /// step's own triage is recorded under the step's id.
    /// </summary>
    public static string? PersonDecision(IEnumerable<FindingEventRecord> history, IReadOnlySet<string> people)
    {
        string? state = null, person = null;
        foreach (var e in history)
        {
            var persons = e.Kind == FindingEventKinds.Triaged && people.Contains(e.Actor) ? e.Actor : null;
            person = e.State != state ? persons : persons ?? person;
            state = e.State;
        }

        return person;
    }

    /// <summary>The ledger as a <c>findings_report</c>, each finding with its ledger id and state, in the ledger's order.</summary>
    public static FindingsReport View(IEnumerable<FindingRecord> ledger) =>
        new(1, ledger.Select(f => f.Finding with { LedgerId = f.FindingId, State = f.State }).ToList());
}
