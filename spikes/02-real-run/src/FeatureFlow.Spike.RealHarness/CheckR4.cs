using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check R4: a review run twice on unchanged input gives the same verdict, with
/// prior findings passed.
///
/// <para>
/// <b>Review 14 A7.</b> This check used to pass by construction. The verdict was
/// a pure function of the diff hash and the prior findings, so two passes on
/// unchanged input were identical because there was no model in the loop to
/// disagree. R4 exists precisely because LLM review gates were unstable in the
/// prototype — a check that cannot fail does not test that.
/// </para>
///
/// <para>
/// It now runs a real reviewer model twice through the router and compares the
/// verdicts. If they differ, that is reported as the finding rather than
/// reconciled, because the honest answer is that the verdicts are unstable.
/// </para>
///
/// <para>
/// The control is unchanged and still matters: decline the finding and the
/// verdict must change, which proves the verdict depends on the prior findings
/// rather than being constant.
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
        var diffText = await CurrentDiffTextAsync(context);
        say($"  diff hash:      {diffHash ?? "(unresolved)"}");

        var prior = Findings.PriorFor(context, "review");
        var declined = Findings.DeclinedIds(context);
        say($"  prior findings: {prior.Count} ({string.Join(", ", prior.Select(f => f.Id))})");
        say($"  declined:       {declined.Count} ({string.Join(", ", declined)})");
        say("");

        // Two passes on the same inputs, through the real reviewer model.
        var first = await context.Reviewer.ReviewAsync(
            diffHash, diffText, prior, declined, "review", CancellationToken.None);
        var second = await context.Reviewer.ReviewAsync(
            diffHash, diffText, prior, declined, "review", CancellationToken.None);

        if (!first.IsOk || !second.IsOk)
        {
            var error = (!first.IsOk ? first.Error : second.Error)!;
            say($"  reviewer unavailable: {error.Class}/{error.Reason}: {error.Detail}");
            say("");
            say("  **NOT EVIDENCED**: R4 needs a real reviewer model; the router call failed.");
            say("");
            return 1;
        }

        var pass1 = first.Value!;
        var pass2 = second.Value!;

        say($"  pass 1: outcome={pass1.Outcome} findings={pass1.Findings.Count} " +
            $"prior={pass1.PriorFindingIds.Count} model={pass1.Model ?? "(unreported)"}");
        say($"  pass 2: outcome={pass2.Outcome} findings={pass2.Findings.Count} " +
            $"prior={pass2.PriorFindingIds.Count} model={pass2.Model ?? "(unreported)"}");
        say("");

        var sameOutcome = string.Equals(pass1.Outcome, pass2.Outcome, StringComparison.Ordinal);
        var sameFindings = pass1.Findings.Count == pass2.Findings.Count &&
                           pass1.Findings.Select(f => f.Id).SequenceEqual(pass2.Findings.Select(f => f.Id));
        var priorPassed = pass1.PriorFindingIds.Count > 0;

        say($"  same verdict:            {sameOutcome}");
        say($"  same finding set:        {sameFindings}");
        say($"  prior findings passed:   {priorPassed} ({pass1.PriorFindingIds.Count})");
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
        var declinedAfter = Findings.DeclinedIds(context);

        var afterDecline = await context.Reviewer.ReviewAsync(
            diffHash, diffText, priorAfterDecline, declinedAfter, "review", CancellationToken.None);

        if (!afterDecline.IsOk)
        {
            say($"  control reviewer unavailable: {afterDecline.Error!.Class}/{afterDecline.Error.Reason}");
            failures++;
        }
        else
        {
            var changed = !string.Equals(pass1.Outcome, afterDecline.Value!.Outcome, StringComparison.Ordinal);
            say($"  after decline: outcome={afterDecline.Value!.Outcome} " +
                $"findings={afterDecline.Value!.Findings.Count}");
            say($"  verdict changed when the prior findings changed: {changed}");
            if (!changed) failures++;
        }

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

    private static async Task<string> CurrentDiffTextAsync(RunContext context)
    {
        // The diff text is read from the checkout rather than through
        // GetDiffAsync, which returns a reference only (DP10). The reviewer
        // model needs the text; it is passed to the model and never written
        // into workflow state.
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add($"{context.IntegrationBranch}...{context.Branch}");

        using var git = System.Diagnostics.Process.Start(startInfo);
        if (git is null)
            return "";

        var text = await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();

        return git.ExitCode == 0 ? text : "";
    }
}
