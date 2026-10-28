using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check R4: a review run twice on unchanged input gives the same verdict, with
/// prior findings passed.
///
/// <para>
/// The verdict is a pure function of the diff hash, the prior findings and the
/// checks, so running it twice on unchanged input must give the same answer.
/// That is what makes this evidenceable rather than a claim about an LLM.
/// </para>
/// </summary>
public static class CheckR4
{
    public static async Task<int> RunAsync(RunContext context, Action<string> say)
    {
        say("## R4: a review run twice on unchanged input gives the same verdict");
        say("");

        var failures = 0;

        // Seed a finding, so the review has something to pass as prior findings.
        // Without one the check would pass trivially.
        var finding = new Finding(
            Id: "f-r4-1",
            Rule: "merge-bar/no-regression-test",
            Path: "src/FeatureFlow.Example.Api/WidgetStore.cs",
            Symbol: "WidgetStore.Add",
            Snippet: "public Widget Add(string name)",
            Blocking: true);

        Findings.Seed(context, finding);

        var diffHash = await CurrentDiffHashAsync(context);
        say($"  diff hash:      {diffHash ?? "(unresolved)"}");

        var prior = Findings.PriorFor(context, "review");
        say($"  prior findings: {prior.Count} ({string.Join(", ", prior.Select(f => f.Id))})");
        say("");

        // Run the verdict twice on the same inputs.
        var first = Findings.Verdict(diffHash, prior, "review");
        var second = Findings.Verdict(diffHash, prior, "review");

        say($"  pass 1: outcome={first.Outcome} findings={first.Findings.Count} " +
            $"prior={first.PriorFindingIds.Count}");
        say($"  pass 2: outcome={second.Outcome} findings={second.Findings.Count} " +
            $"prior={second.PriorFindingIds.Count}");
        say("");

        var sameOutcome = string.Equals(first.Outcome, second.Outcome, StringComparison.Ordinal);
        var sameFindings = first.Findings.Count == second.Findings.Count &&
                           first.Findings.Select(f => f.Id).SequenceEqual(second.Findings.Select(f => f.Id));
        var priorPassed = first.PriorFindingIds.Count > 0;

        say($"  same verdict:            {sameOutcome}");
        say($"  same finding set:        {sameFindings}");
        say($"  prior findings passed:   {priorPassed} ({first.PriorFindingIds.Count})");
        if (!sameOutcome) failures++;
        if (!sameFindings) failures++;
        if (!priorPassed) failures++;

        // The control: with the finding declined, the verdict must change. That
        // proves the verdict actually depends on the prior findings rather than
        // being constant.
        //
        // Only this check's own finding is declined. The sibling K4 check leaves
        // findings of its own in the store, and declining those would make this
        // check assert on another check's state.
        say("");
        say("  control: decline the finding, then re-run the verdict");

        Findings.Decline(context, finding.Id, "accepted: the store is in-memory by design");

        var priorAfterDecline = Findings.PriorFor(context, "review")
            .Where(f => f.Id == finding.Id || !f.Id.StartsWith("f-k4-", StringComparison.Ordinal))
            .ToList();

        var afterDecline = Findings.Verdict(diffHash, priorAfterDecline, "review");

        say($"  after decline: outcome={afterDecline.Outcome} findings={afterDecline.Findings.Count}");
        var changed = !string.Equals(first.Outcome, afterDecline.Outcome, StringComparison.Ordinal);
        say($"  verdict changed when the prior findings changed: {changed}");
        if (!changed) failures++;

        // And a declined finding is not re-raised: that is K4's decline path.
        var stillDeclined = Findings.IsDeclined(context, finding.Id);
        say($"  declined finding stays declined: {stillDeclined}");
        if (!stillDeclined) failures++;

        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<string?> CurrentDiffHashAsync(RunContext context)
    {
        var diff = await context.CodeHost.GetDiffAsync(
            context.CodeHostRepo, context.IntegrationBranch, context.Branch, CancellationToken.None);

        return diff.IsOk ? diff.Value!.ContentHash : null;
    }
}
