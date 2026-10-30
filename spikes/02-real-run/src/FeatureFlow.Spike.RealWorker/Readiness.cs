using System.Text.Json;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// <c>findings.evaluate_readiness</c> as a pure function of the ledger, so the
/// gated step and <c>ff-presubmit</c> evaluate it the same way (docs/17 5.2).
/// </summary>
public static class Readiness
{
    /// <summary>
    /// D8 and the organisation's <c>mergeBar: noUnresolvedBlocking</c>: only a
    /// blocking finding holds the merge. Spike 2 counted every open finding,
    /// contrary to its own comment; Spike 3 turns the documented bar on, so a
    /// non-blocking nit does not send a ticket round the fix loop.
    /// </summary>
    public static bool BlockingOnly;

    public static List<Finding> Unresolved(IEnumerable<Finding> open, IReadOnlyCollection<string> declined) =>
        open.Where(f => !declined.Contains(f.Id) && (!BlockingOnly || f.Blocking)).ToList();

    /// <summary>Canonical text of the evaluation: same ledger, same bytes.</summary>
    public static string Describe(IEnumerable<Finding> open, IReadOnlyCollection<string> declined)
    {
        var openList = open.ToList();
        var unresolved = Unresolved(openList, declined);
        return JsonSerializer.Serialize(new
        {
            bar = BlockingOnly ? "noUnresolvedBlocking" : "noUnresolved",
            open = openList.Select(f => f.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            declined = declined.OrderBy(x => x, StringComparer.Ordinal).ToList(),
            unresolved = unresolved.Select(f => f.Id).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            outcome = unresolved.Count == 0 ? "ready" : "findings_open",
        });
    }
}
