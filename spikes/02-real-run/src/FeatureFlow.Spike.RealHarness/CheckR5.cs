using FeatureFlow.Spike.Ci;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check R5: a Docker-dependent tier runs through CI delegation and matches what
/// CI would do.
///
/// <para>
/// The evidence is a real dispatched run: the tier's execution target from
/// <c>feature-flow.yaml</c>, the workflow the CI provider dispatched, and the
/// conclusion GitHub reported. "Matches what CI would do" is shown by the
/// delegated workflow being the one the manifest maps the tier to, and by the
/// run's own conclusion.
/// </para>
/// </summary>
public static class CheckR5
{
    public static async Task<int> RunAsync(RunContext context, Action<string> say)
    {
        say("## R5: a Docker-dependent tier runs through CI delegation");
        say("");

        var failures = 0;

        // The manifest is the source of truth for the tier's target. Read from
        // the branch, per step (docs/07 5.1.2).
        var manifest = await context.CodeHost.ReadFileAsync(
            context.CodeHostRepo, "feature-flow.yaml", context.Branch, CancellationToken.None);

        if (!manifest.IsOk)
        {
            say($"  FAIL: feature-flow.yaml unreadable: " +
                $"{manifest.Error!.Class}/{manifest.Error.Reason}");
            return 1;
        }

        var file = manifest.Value!;
        var tiers = Manifest.Tiers(file);

        say($"  manifest: sha256:{file.ContentHash} ({file.SizeBytes} bytes) on {file.Ref}");
        say($"  tiers declared: {string.Join(", ", tiers)}");
        say("");

        foreach (var tier in tiers)
        {
            var target = Manifest.TierTarget(file, tier);
            var mapping = context.Tiers.Get(tier);
            say($"  tier {tier}: target={target ?? "(none)"} " +
                $"workflow={mapping?.WorkflowFile ?? "(unmapped)"}");
        }

        say("");

        // The Docker-dependent tier is the one whose target is ci.
        var dockerTier = tiers.FirstOrDefault(t =>
            string.Equals(Manifest.TierTarget(file, t), "ci", StringComparison.OrdinalIgnoreCase));

        if (dockerTier is null)
        {
            say("  NOT EVIDENCED: no tier in the manifest declares target: ci, so there is ");
            say("  no Docker-dependent tier to delegate. The check needs a manifest tier with");
            say("  target: ci.");
            say("");
            say("  **NOT EVIDENCED**");
            say("");
            return 1;
        }

        say($"  Docker-dependent tier: {dockerTier}");

        var head = await HeadShaAsync(context);
        if (head is null)
        {
            say("  FAIL: could not resolve the branch head");
            return 1;
        }

        say($"  head: {head}");
        say("");

        // Dispatch through the CI provider. TriggerAsync returns without waiting.
        var key = context.IdempotencyKey("check-r5", dockerTier, head);
        var started = DateTimeOffset.UtcNow;
        var trigger = await context.Ci.TriggerAsync(
            context.CiRepo, dockerTier, head, key, CancellationToken.None);
        var elapsed = (DateTimeOffset.UtcNow - started).TotalSeconds;

        if (!trigger.IsOk)
        {
            say($"  FAIL: TriggerAsync: {trigger.Error!.Class}/{trigger.Error.Reason} " +
                $"{trigger.Error.Detail}");
            return 1;
        }

        var run = trigger.Value!;
        say($"  dispatched:  run {run.RunId} workflow={run.WorkflowFile} event={run.Event}");
        say($"  url:         {run.Url}");
        say($"  returned in: {elapsed:F1}s (without waiting for the run)");
        say("");

        // Poll to a terminal state. The engine does this inside one activity.
        CiRunStatus? status = null;
        var polls = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var poll = await context.Ci.GetRunStatusAsync(run, CancellationToken.None);
            polls++;

            if (!poll.IsOk)
            {
                say($"  FAIL: GetRunStatusAsync: {poll.Error!.Class}/{poll.Error.Reason}");
                failures++;
                break;
            }

            status = poll.Value!;
            say($"  poll {polls,2}: state={status.State} conclusion={status.Conclusion} " +
                $"checks={status.CompletedChecks}/{status.TotalChecks} " +
                $"registered={status.ChecksRegistered}");

            if (status.IsTerminal)
                break;

            await Task.Delay(TimeSpan.FromSeconds(10));
        }

        say("");

        if (status is null || !status.IsTerminal)
        {
            say("  FAIL: the run did not reach a terminal state");
            return 1;
        }

        say($"  conclusion:  {status.Conclusion}");
        say($"  url:         {status.Url ?? run.Url}");

        // "Matches what CI would do": the run the provider dispatched is the
        // workflow the manifest maps the tier to, and its conclusion is the one
        // GitHub reported for that workflow at that commit.
        var mapped = context.Tiers.Get(dockerTier);
        var matchesWorkflow = mapped is not null &&
                              string.Equals(run.WorkflowFile, mapped.WorkflowFile, StringComparison.Ordinal);
        say($"  workflow matches the manifest mapping ({run.WorkflowFile}): {matchesWorkflow}");
        if (!matchesWorkflow) failures++;

        // The tier's own commands are what CI ran. Show them, so the delegation
        // is visibly the same work, not a different job.
        var commands = Manifest.TierCommands(file, dockerTier);
        say($"  tier commands the workflow runs: {string.Join(" | ", commands)}");

        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }

    private static async Task<string?> HeadShaAsync(RunContext context)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");

        using var git = System.Diagnostics.Process.Start(startInfo);
        if (git is null)
            return null;

        var sha = await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();
        return git.ExitCode == 0 ? sha.Trim() : null;
    }
}
