using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// <c>delivery.report</c>'s record (docs/04 section 12): per repository, whether a merge actually happened, and the merge
/// that showed it. An engine shape, kept as an artifact when the step declares one.
/// </summary>
public sealed record DeliveryReport(bool Complete, IReadOnlyList<DeliveredRepository> Repositories);

/// <summary>One repository of a <see cref="DeliveryReport"/>: merged into <see cref="Target"/> at <see cref="Revision"/> by <see cref="MergedBy"/>, or why not.</summary>
public sealed record DeliveredRepository(string Repository, bool Merged, string Target, string? Revision, string? MergedBy, string? Reason);

/// <summary>Delivery (docs/04 section 12; RW15): which repositories merged. No deploy.</summary>
static class Delivery
{
    /// <summary>
    /// <c>delivery.report</c>: for each repository, the run's merge evidence (<c>merge_record</c> made by <c>scm.merge</c>,
    /// never an agent's artifact, F7) into the repository's parent branch, confirmed against the remote: the parent branch
    /// must contain the merged revision now. <c>complete</c> only if every repository merged, so a run redirected past a
    /// merge, or one whose merge was undone, cannot report success.
    /// </summary>
    public static async Task<ActionResult> ReportAsync(ActionRun run)
    {
        var names = run.Strings("repositories") is { Count: > 0 } given ? given : run.Scope.Repositories;
        if (names.Count == 0)
            throw ActionStop.CannotAnswer("delivery.report names no repositories and the run has none");
        var records = await run.EvidenceAsync<MergeEvidence>("merge_record", "scm.merge");
        var delivered = new List<DeliveredRepository>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var repo = new RepoRef(name);
            var target = await run.ParentBranchAsync(repo);
            delivered.Add(await ConfirmAsync(run, repo, target, records.Select(r => r.Value).Where(r => r.Repository == name && r.Target == target).ToList()));
        }

        var complete = delivered.All(d => d.Merged);
        foreach (var d in delivered)
            run.Events.Add(d.Merged
                ? $"{d.Repository}: merged into {d.Target} as {EngineActions.Short(d.Revision!)} by {d.MergedBy}"
                : $"{d.Repository}: not merged into {d.Target}: {d.Reason}");
        var outcome = complete ? "complete" : "partial";
        var report = new DeliveryReport(complete, delivered);
        var outputs = new Dictionary<string, object?>
        {
            ["merged"] = delivered.Where(d => d.Merged).Select(d => (object?)d.Repository).ToList(),
            ["not_merged"] = delivered.Where(d => !d.Merged).Select(d => (object?)d.Repository).ToList(),
        };
        return run.Report(outcome, outputs, run.Call.Contract.ArtifactsFor(outcome).Any() ? await run.PutJsonAsync(report) : null);
    }

    static async Task<DeliveredRepository> ConfirmAsync(ActionRun run, RepoRef repo, string target, IReadOnlyList<MergeEvidence> records)
    {
        if (records.Count == 0)
            return new DeliveredRepository(repo.FullName, false, target, null, null, "the run holds no merge record for it");
        var head = ActionRun.Must(await run.Vcs.ResolveAsync(repo, "refs/heads/" + target, run.Ct), $"read {target}");
        if (head is null)
            return new DeliveredRepository(repo.FullName, false, target, null, null, $"{target} does not exist");
        foreach (var record in records.Where(r => r.Revision.Length > 0))
        {
            var contained = await run.Vcs.DescendsFromAsync(repo, head, record.Revision, run.Ct);
            if (contained is { Ok: false, Error.Reason: "not_found" })
                continue;
            if (ActionRun.Must(contained, $"check that {target} contains the merge"))
                return new DeliveredRepository(repo.FullName, true, target, record.Revision, record.MergedBy, null);
        }

        return new DeliveredRepository(repo.FullName, false, target, null, null,
            $"{target} at {EngineActions.Short(head)} does not contain the recorded merge ({string.Join(", ", records.Select(r => EngineActions.Short(r.Revision)))})");
    }
}
