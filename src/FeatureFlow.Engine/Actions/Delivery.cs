using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// <c>delivery.report</c>'s record (docs/04 section 12): per repository, whether a merge actually happened, and the merge
/// that showed it. An engine shape, kept as an artifact when the step declares one.
/// </summary>
public sealed record DeliveryReport(bool Complete, IReadOnlyList<DeliveredRepository> Repositories);

/// <summary>One repository of a <see cref="DeliveryReport"/>: merged into <see cref="Target"/> at <see cref="Revision"/> by <see cref="MergedBy"/>, or why not.</summary>
public sealed record DeliveredRepository(string Repository, bool Merged, string Target, string? Revision, string? MergedBy, string? Reason);

/// <summary>
/// <c>revert_record</c>, written by <c>scm.revert</c>: the pull request reverted and the revert pull request. An engine shape;
/// <c>delivery.report</c> reads it, so a merge the run reverted is not delivered.
/// </summary>
public sealed record RevertEvidence(string Repository, string PullRequest, string RevertPullRequest);

/// <summary>Delivery (docs/04 section 12; RW15): which repositories merged. No deploy.</summary>
static class Delivery
{
    /// <summary>
    /// <c>delivery.report</c>: for each repository, the run's merge evidence (<c>merge_record</c> made by <c>scm.merge</c>,
    /// never an agent's artifact, F7) into the repository's parent branch, for the work branch's current head, confirmed
    /// against the remote: the parent branch must contain the merged revision now, and no revert the run opened of that
    /// pull request (<c>revert_record</c>, made by <c>scm.revert</c>) may be merged or open. <c>complete</c> only if every
    /// repository merged, so a run redirected past a merge, with work after its merge, or whose merge was undone, cannot
    /// report success.
    /// </summary>
    public static async Task<ActionResult> ReportAsync(ActionRun run)
    {
        var names = run.Strings("repositories") is { Count: > 0 } given ? given : run.Scope.Repositories;
        if (names.Count == 0)
            throw ActionStop.CannotAnswer("delivery.report names no repositories and the run has none");
        var records = await run.EvidenceAsync<MergeEvidence>("merge_record", "scm.merge");
        // From the artifact index, where scm.revert's evidence is whether or not its step declared it.
        var reverts = await run.IndexedEvidenceAsync<RevertEvidence>("revert_record", "scm.revert");
        var delivered = new List<DeliveredRepository>();
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var repo = new RepoRef(name);
            var target = await run.ParentBranchAsync(repo);
            delivered.Add(await ConfirmAsync(run, repo, target, records.Select(r => r.Value).Where(r => r.Repository == name && r.Target == target).ToList(),
                reverts.Where(r => r.Repository == name).ToList()));
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

    static async Task<DeliveredRepository> ConfirmAsync(ActionRun run, RepoRef repo, string target, IReadOnlyList<MergeEvidence> records,
        IReadOnlyList<RevertEvidence> reverts)
    {
        DeliveredRepository Not(string reason) => new(repo.FullName, false, target, null, null, reason);
        if (records.Count == 0)
            return Not("the run holds no merge record for it");
        // A work branch that no longer exists (deleted after a person's merge) is no later work: its merges count. One whose
        // head has moved past every merge holds work that was never merged (LOG 180).
        var work = ActionRun.Must(await run.Vcs.ResolveAsync(repo, run.WorkReference, run.Ct), "read the work branch");
        var current = records.Where(r => (work is null || r.Head == work) && r.Revision.Length > 0).ToList();
        if (current.Count == 0 && work is null)
            return Not("the run holds no merge record with a merged revision for it");
        if (current.Count == 0)
            return Not($"no merge record is for the work branch's head {EngineActions.Short(work!)} (merged: {string.Join(", ", records.Select(r => EngineActions.Short(r.Head)))}): work came after the merge");
        var head = ActionRun.Must(await run.Vcs.ResolveAsync(repo, "refs/heads/" + target, run.Ct), $"read {target}");
        if (head is null)
            return Not($"{target} does not exist");
        foreach (var record in current)
        {
            var contained = await run.Vcs.DescendsFromAsync(repo, head, record.Revision, run.Ct);
            if (contained is { Ok: false, Error.Reason: "not_found" })
                continue;
            if (!ActionRun.Must(contained, $"check that {target} contains the merge"))
                continue;
            if (await RevertedAsync(run, record, reverts) is { } reverted)
                return Not(reverted);
            return new DeliveredRepository(repo.FullName, true, target, record.Revision, record.MergedBy, null);
        }

        return Not($"{target} at {EngineActions.Short(head)} does not contain the recorded merge ({string.Join(", ", current.Select(r => EngineActions.Short(r.Revision)))})");
    }

    /// <summary>Why a merge counts as undone: a revert the run opened of its pull request has merged, or is still open. Null otherwise.</summary>
    static async Task<string?> RevertedAsync(ActionRun run, MergeEvidence record, IReadOnlyList<RevertEvidence> reverts)
    {
        foreach (var revert in reverts.Where(r => record.PullRequest is not null && r.PullRequest == record.PullRequest))
        {
            if (ActionRun.ParsePullRequest(revert.RevertPullRequest) is not { } pr)
                continue;
            var state = ActionRun.Must(await run.CodeHost.GetPullRequestAsync(pr, run.Ct), "read the revert pull request");
            if (state.State == PullRequestStates.Merged)
                return $"its merge was reverted by {revert.RevertPullRequest}";
            if (state.State == PullRequestStates.Open)
                return $"a revert of its merge, {revert.RevertPullRequest}, is open";
        }

        return null;
    }
}
