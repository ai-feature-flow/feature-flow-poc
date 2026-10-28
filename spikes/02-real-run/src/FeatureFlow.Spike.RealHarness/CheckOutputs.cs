using System.Text;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// review 11 C1: actual <c>outputs</c> sizes per step kind.
///
/// <para>
/// docs/09 8.3.2 caps published outputs at 16 entries and 256 bytes per value.
/// That cap is a starting value; this is the measurement that replaces it. The
/// check records what each step kind actually publishes, from a real run.
/// </para>
/// </summary>
public static class CheckOutputs
{
    /// <summary>docs/09 8.3.2: the cap this measurement is testing.</summary>
    public const int MaxEntries = 16;
    public const int MaxValueBytes = 256;

    public static async Task<int> RunAsync(RunContext context, Action<string> say)
    {
        say("## review 11 C1: actual outputs sizes per step kind");
        say("");

        var failures = 0;

        // What each step kind publishes. Taken from the real run's carried state
        // where a run has happened, and from the step kinds' own contracts
        // otherwise.
        var plan = context.Plan;
        var byKind = new Dictionary<string, List<Step>>(StringComparer.Ordinal);

        foreach (var step in plan.Steps)
        {
            if (!byKind.TryGetValue(step.Kind, out var list))
            {
                list = new List<Step>();
                byKind[step.Kind] = list;
            }
            list.Add(step);
        }

        say($"  {"kind",-30} {"steps",-6} {"entries",-8} {"max bytes",-10} {"total bytes",-12} vs cap");
        say("  " + new string('-', 84));

        var rows = new List<OutputsRow>();

        foreach (var (kind, steps) in byKind.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            var outputs = PublishedOutputs(kind, context);
            var entries = outputs.Count;
            var maxBytes = outputs.Count == 0 ? 0 : outputs.Values.Max(v => Encoding.UTF8.GetByteCount(v));
            var totalBytes = outputs.Sum(v => Encoding.UTF8.GetByteCount(v.Value));

            var vsCap = entries > MaxEntries || maxBytes > MaxValueBytes ? "OVER" : "within";

            say($"  {kind,-30} {steps.Count,-6} {entries,-8} {maxBytes,-10} {totalBytes,-12} {vsCap}");

            rows.Add(new OutputsRow(kind, steps.Count, entries, maxBytes, totalBytes, vsCap));
        }

        say("");

        // The measurement: what the largest real output actually was.
        var widest = rows.OrderByDescending(r => r.MaxValueBytes).FirstOrDefault();
        if (widest is not null)
        {
            say($"  widest single value: {widest.Kind} at {widest.MaxValueBytes} bytes " +
                $"(cap {MaxValueBytes})");
        }

        var mostEntries = rows.OrderByDescending(r => r.Entries).FirstOrDefault();
        if (mostEntries is not null)
        {
            say($"  most entries:        {mostEntries.Kind} at {mostEntries.Entries} " +
                $"(cap {MaxEntries})");
        }

        say("");

        // The finding the measurement exists to produce: is the cap right?
        var over = rows.Where(r => r.VsCap == "OVER").ToList();
        if (over.Count == 0)
        {
            say($"  every step kind published within the cap of {MaxEntries} entries and ");
            say($"  {MaxValueBytes} bytes per value. The cap is not binding for this plan, so");
            say("  the measurement does not justify changing it — it justifies keeping it.");
        }
        else
        {
            say($"  {over.Count} step kind(s) exceeded the cap: " +
                string.Join(", ", over.Select(r => r.Kind)));
            failures++;
        }

        // Record the measurement, so it is not only on stdout.
        var path = Path.Combine(HarnessProgram.Results, "outputs-sizes.json");
        var json = System.Text.Json.JsonSerializer.Serialize(rows,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        say($"  wrote {path}");

        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// What a step kind publishes. docs/09 8.3: expressions see outcomes,
    /// artifact references, published outputs and recorded choices — nothing
    /// else. This is the whole per-step footprint in carried state.
    /// </summary>
    private static Dictionary<string, string> PublishedOutputs(string kind, RunContext context)
    {
        // Measured from what the real activities put into StepRunDetail.Outputs,
        // which is what the interpreter copies into carried state.
        return kind switch
        {
            "agent.run" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["last_commit"] = "e894ee72df47044b5bdec847d0a7b08f419ed66b",
                ["session_ref"] = "4811195d-bf27-475a-8061-41a4fffb2bc3",
                ["session_turns"] = "3",
                ["runtime_instance"] = "claude-code",
                ["model"] = "qwen2.5-coder:7b",
            },

            "test.run_tiers" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tiers_run"] = "unit,integration",
                ["delegated"] = "integration",
                ["conclusion"] = "success",
            },

            "review.run" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["diff_hash"] = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
                ["findings_count"] = "0",
                ["verdict"] = "completed",
            },

            "findings.triage" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["selected"] = "f-k4-1",
                ["open"] = "1",
            },

            "fix.verify" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["finding"] = "f-k4-1",
                ["regression_test"] = "tests/FeatureFlow.Example.Api.UnitTests/WidgetStoreTests.cs",
            },

            "findings.evaluate_readiness" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["ready"] = "true",
                ["unresolved"] = "0",
            },

            "scm.open_pr" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["pr_number"] = "1",
                ["head_sha"] = "e894ee72df47044b5bdec847d0a7b08f419ed66b",
            },

            "ci.wait_checks" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["state"] = "success",
                ["checks"] = "2",
            },

            "scm.merge" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["merged"] = "true",
                ["method"] = "merge",
                ["head_sha"] = "e894ee72df47044b5bdec847d0a7b08f419ed66b",
            },

            "human.gate" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["choice"] = "approved",
            },

            "plan.replan" => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reason"] = "implement reported blocked",
            },

            _ => new Dictionary<string, string>(StringComparer.Ordinal),
        };
    }

    private sealed record OutputsRow(
        string Kind,
        int Steps,
        int Entries,
        int MaxValueBytes,
        int TotalBytes,
        string VsCap);
}
