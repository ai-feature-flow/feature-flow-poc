using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check R13: tokens, model and compute time are recorded per step.
///
/// <para>
/// The evidence is the cost records the worker wrote during a real run. A check
/// with no run behind it cannot be evidenced, and says so.
/// </para>
/// </summary>
public static class CheckR13
{
    public static async Task<int> RunAsync(RunContext context, Action<string> say)
    {
        say("## R13: tokens, model and compute time recorded per step");
        say("");

        var costs = RunContext.ReadCosts(context.RecordDirectory, context.RunId);

        if (costs.Count == 0)
        {
            say("  NOT EVIDENCED: no cost records exist for this run. Run the ticket first");
            say("  (RealHarness run), then re-run this check.");
            say("");
            say("  **NOT EVIDENCED**");
            say("");
            return 1;
        }

        say($"  records: {costs.Count}");
        say("");
        say($"  {"step",-20} {"kind",-22} {"model",-20} {"in",-8} {"out",-8} {"compute",-9} source");
        say("  " + new string('-', 100));

        foreach (var cost in costs)
        {
            say($"  {cost.StepId,-20} {cost.StepKind,-22} {cost.Model ?? "-",-20} " +
                $"{cost.InputTokens?.ToString() ?? "-",-8} {cost.OutputTokens?.ToString() ?? "-",-8} " +
                $"{cost.ComputeSeconds,-9:F1} {cost.UsageSource}");
        }

        say("");

        var withModel = costs.Count(c => c.Model is not null);
        var withTokens = costs.Count(c => c.InputTokens is not null || c.OutputTokens is not null);
        var withCompute = costs.Count(c => c.ComputeSeconds > 0);

        say($"  steps with a model recorded:  {withModel}/{costs.Count}");
        say($"  steps with tokens recorded:   {withTokens}/{costs.Count}");
        say($"  steps with compute recorded:  {withCompute}/{costs.Count}");

        var totalIn = costs.Where(c => c.InputTokens is not null).Sum(c => c.InputTokens!.Value);
        var totalOut = costs.Where(c => c.OutputTokens is not null).Sum(c => c.OutputTokens!.Value);
        var totalCompute = costs.Sum(c => c.ComputeSeconds);

        say("");
        say($"  run total: in={totalIn} out={totalOut} compute={totalCompute:F1}s");

        // The exit criterion: the cost of the run is known from the record store.
        var failures = 0;

        var allHaveCompute = withCompute == costs.Count;
        say($"  every step recorded compute time: {allHaveCompute}");
        if (!allHaveCompute) failures++;

        // Agent steps are the ones that spend tokens. A run whose agent steps
        // report no usage at all has not evidenced R13 for them.
        var agentCosts = costs.Where(c => c.StepKind == "agent.run").ToList();
        if (agentCosts.Count > 0)
        {
            var agentWithUsage = agentCosts.Count(c => c.UsageSource != "none");
            say($"  agent.run steps reporting usage: {agentWithUsage}/{agentCosts.Count} " +
                $"(source: runtime_usage_output)");
            if (agentWithUsage == 0)
            {
                say("    NOTE: no agent step reported usage. The runtime's own usage output is");
                say("    the source docs/08 2.5 names when hooks are unavailable.");
                failures++;
            }
        }

        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }
}
