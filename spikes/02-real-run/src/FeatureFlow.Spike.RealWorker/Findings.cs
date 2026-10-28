using System.Text.Json;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// Findings, kept outside workflow state. docs/09 7.2: findings are fingerprinted
/// on rule, repository, path, symbol and normalised snippet — never on line
/// numbers.
///
/// <para>
/// Spike scope: a JSON file beside the cost records. The point is the behaviour
/// the checks need — a declined finding stays declined, a verdict is a pure
/// function of its inputs — not the storage technology.
/// </para>
/// </summary>
public sealed record Finding(
    string Id,
    string Rule,
    string Path,
    string Symbol,
    string Snippet,
    bool Blocking,
    string? DeclineReason = null,
    bool Fixed = false,
    string? RegressionTest = null,
    string? NotApplicableReason = null);

public sealed record ReviewVerdict(
    string StepId,
    string Outcome,
    string? DiffHash,
    IReadOnlyList<string> PriorFindingIds,
    IReadOnlyList<Finding> Findings,
    DateTimeOffset At);

public sealed record TriageRecord(
    string StepId,
    IReadOnlyList<string> SelectedFindingIds,
    DateTimeOffset At);

/// <summary>
/// The findings store for one run.
/// </summary>
public static class Findings
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private static readonly object Gate = new();

    private static string Path(RunContext context) =>
        System.IO.Path.Combine(context.RecordDirectory, $"{context.RunId}.findings.json");

    private sealed class Store
    {
        public List<Finding> Findings { get; set; } = new();
        public List<ReviewVerdict> Verdicts { get; set; } = new();
        public List<TriageRecord> Triages { get; set; } = new();
        public int? PrNumber { get; set; }
    }

    private static Store Load(RunContext context)
    {
        var path = Path(context);
        if (!File.Exists(path))
            return new Store();

        try
        {
            return JsonSerializer.Deserialize<Store>(File.ReadAllText(path), Json) ?? new Store();
        }
        catch (JsonException)
        {
            return new Store();
        }
    }

    private static void Save(RunContext context, Store store)
    {
        File.WriteAllText(Path(context), JsonSerializer.Serialize(store, Json));
    }

    /// <summary>Seeds a finding. Used by the K4 checks to plant a known defect.</summary>
    public static void Seed(RunContext context, Finding finding)
    {
        lock (Gate)
        {
            var store = Load(context);
            store.Findings.RemoveAll(f => f.Id == finding.Id);
            store.Findings.Add(finding);
            Save(context, store);
        }
    }

    /// <summary>
    /// R4: the prior findings a review is given. Without them a verdict can
    /// change between passes on unchanged input.
    /// </summary>
    public static List<Finding> PriorFor(RunContext context, string stepId)
    {
        lock (Gate)
        {
            var store = Load(context);
            return store.Findings
                .Where(f => !f.Fixed && f.DeclineReason is null)
                .ToList();
        }
    }

    /// <summary>
    /// The verdict for a review. A pure function of the diff hash, the prior
    /// findings and the checks — which is what makes R4 evidenceable: the same
    /// inputs give the same verdict, twice.
    /// </summary>
    public static ReviewVerdict Verdict(string? diffHash, List<Finding> prior, string stepId)
    {
        // Deterministic: the verdict is derived from the inputs, not from a clock
        // or a random draw. A declined finding is never re-raised (K4).
        var open = prior.Where(f => f.DeclineReason is null && !f.Fixed).ToList();
        var blocking = open.Where(f => f.Blocking).ToList();

        var outcome = blocking.Count > 0 ? Outcomes.FindingsOpen : Outcomes.Completed;

        return new ReviewVerdict(
            StepId: stepId,
            Outcome: outcome,
            DiffHash: diffHash,
            PriorFindingIds: prior.Select(f => f.Id).ToList(),
            Findings: open,
            At: DateTimeOffset.UtcNow);
    }

    public static void Record(RunContext context, string stepId, ReviewVerdict verdict)
    {
        lock (Gate)
        {
            var store = Load(context);
            store.Verdicts.Add(verdict);
            Save(context, store);
        }
    }

    public static List<Finding> Open(RunContext context)
    {
        lock (Gate)
        {
            return Load(context).Findings.Where(f => !f.Fixed && f.DeclineReason is null).ToList();
        }
    }

    public static bool IsDeclined(RunContext context, string findingId)
    {
        lock (Gate)
        {
            var finding = Load(context).Findings.FirstOrDefault(f => f.Id == findingId);
            return finding?.DeclineReason is not null;
        }
    }

    /// <summary>
    /// K4: a declined finding stays declined until the code its fingerprint
    /// points at changes. It never restarts the review loop (docs/06 9.1).
    /// </summary>
    public static void Decline(RunContext context, string findingId, string reason)
    {
        lock (Gate)
        {
            var store = Load(context);
            var finding = store.Findings.FirstOrDefault(f => f.Id == findingId);
            if (finding is null)
                return;

            store.Findings.Remove(finding);
            store.Findings.Add(finding with { DeclineReason = reason });
            Save(context, store);
        }
    }

    public static void RecordTriage(RunContext context, string stepId, List<Finding> selected)
    {
        lock (Gate)
        {
            var store = Load(context);
            store.Triages.Add(new TriageRecord(
                stepId, selected.Select(f => f.Id).ToList(), DateTimeOffset.UtcNow));
            Save(context, store);
        }
    }

    public static Finding? Selected(RunContext context)
    {
        lock (Gate)
        {
            var store = Load(context);
            var last = store.Triages.LastOrDefault();
            if (last is null)
                return null;

            var id = last.SelectedFindingIds.FirstOrDefault();
            return id is null ? null : store.Findings.FirstOrDefault(f => f.Id == id);
        }
    }

    /// <summary>
    /// docs/06 10.1: a fix must add a regression test. The requirement is a step
    /// invariant, not a template switch (review 09 C4).
    /// </summary>
    public static bool HasRegressionTest(RunContext context, string? findingId)
    {
        if (findingId is null)
            return false;

        lock (Gate)
        {
            var finding = Load(context).Findings.FirstOrDefault(f => f.Id == findingId);
            return finding?.RegressionTest is not null;
        }
    }

    public static string? NotApplicableReason(RunContext context, string findingId)
    {
        lock (Gate)
        {
            return Load(context).Findings.FirstOrDefault(f => f.Id == findingId)?.NotApplicableReason;
        }
    }

    public static void AttachRegressionTest(RunContext context, string findingId, string test)
    {
        lock (Gate)
        {
            var store = Load(context);
            var finding = store.Findings.FirstOrDefault(f => f.Id == findingId);
            if (finding is null)
                return;

            store.Findings.Remove(finding);
            store.Findings.Add(finding with { RegressionTest = test });
            Save(context, store);
        }
    }

    public static void MarkFixed(RunContext context, string findingId)
    {
        lock (Gate)
        {
            var store = Load(context);
            var finding = store.Findings.FirstOrDefault(f => f.Id == findingId);
            if (finding is null)
                return;

            store.Findings.Remove(finding);
            store.Findings.Add(finding with { Fixed = true });
            Save(context, store);
        }
    }

    public static void RecordPr(RunContext context, int number)
    {
        lock (Gate)
        {
            var store = Load(context);
            store.PrNumber = number;
            Save(context, store);
        }
    }

    public static int? LastPrNumber(RunContext context)
    {
        lock (Gate)
        {
            return Load(context).PrNumber;
        }
    }

    public static List<ReviewVerdict> Verdicts(RunContext context)
    {
        lock (Gate)
        {
            return Load(context).Verdicts;
        }
    }

    public static List<Finding> All(RunContext context)
    {
        lock (Gate)
        {
            return Load(context).Findings;
        }
    }
}
