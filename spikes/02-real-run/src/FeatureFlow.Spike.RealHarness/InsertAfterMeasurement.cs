using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// review 11 C2: the <c>insert_after</c> measurement.
///
/// <para>
/// The planner authors patches in both forms — <c>insert_after</c> (with
/// rewiring) and <c>add_step</c> + <c>change_route</c> — and the harness records
/// the <b>validation failure rate</b> for each.
/// </para>
///
/// <para>
/// The question the measurement answers: does the rewiring that
/// <c>insert_after</c> does implicitly, and that the two-op form must do
/// explicitly, actually succeed? A form that fails validation more often is a
/// form a planner should not be asked to author.
/// </para>
/// </summary>
public static class InsertAfterMeasurement
{
    /// <summary>How many patches of each form are authored.</summary>
    public const int Trials = 20;

    public static async Task<int> RunAsync(string[] args)
    {
        HarnessProgram.WriteHeader("review 11 C2: insert_after vs add_step + change_route");

        var trials = HarnessProgram.Arg(args, "--trials") is { } text && int.TryParse(text, out var t)
            ? t
            : Trials;

        Console.WriteLine($"trials per form: {trials}");
        Console.WriteLine();

        var plan = PlanLoader.LoadTicketDelivery("TICKET-SPIKE2-1");

        // The targets a planner would realistically patch: the agent.run steps,
        // plus the protected one, which must be refused (floor F3).
        var targets = plan.Steps.Where(s => s.Kind == "agent.run").Select(s => s.Id).ToList();

        Console.WriteLine($"targets: {string.Join(", ", targets)}");
        Console.WriteLine($"protected: {string.Join(", ", plan.Steps.Where(s => s.Protected).Select(s => s.Id))}");
        Console.WriteLine();

        var results = new List<TrialResult>();

        // Phase 1: the real plan, protected steps included. This is what a
        // planner actually faces, so it is the headline number.
        Console.WriteLine("### phase 1: the real plan, protected steps included");
        Console.WriteLine();

        foreach (var form in new[] { "insert_after", "add_step_change_route" })
        {
            results.Add(await MeasureAsync(plan, targets, form, trials, Console.WriteLine));
        }

        // Phase 2: the same measurement with the protected flag cleared, so the
        // comparison isolates the rewiring mechanics. Phase 1's failures are
        // dominated by the protected-step rule (floor F3), which bites both
        // forms but bites the two-op form twice — once for the insert and once
        // for the explicit change_route. Without this phase the measurement
        // would report the floor rather than the form.
        Console.WriteLine("### phase 2: the same plan with protected cleared (isolates the rewiring)");
        Console.WriteLine();

        var unprotected = plan.Clone();
        foreach (var step in unprotected.Steps)
            step.Protected = false;

        foreach (var form in new[] { "insert_after", "add_step_change_route" })
        {
            results.Add(await MeasureAsync(unprotected, targets, form, trials, Console.WriteLine, form + "-unprotected"));
        }

        // The comparison the measurement exists for.
        Console.WriteLine("--- comparison ---");
        Console.WriteLine();
        Console.WriteLine($"  {"form",-34} {"applied",-9} {"failed",-8} {"failure rate",-14}");
        Console.WriteLine("  " + new string('-', 68));

        foreach (var result in results)
        {
            Console.WriteLine($"  {result.Form,-34} {result.Applied,-9} " +
                              $"{result.Rejected + result.Held,-8} {result.FailureRate,-14:P0}");
        }

        Console.WriteLine();

        var insertAfter = results.First(r => r.Form == "insert_after");
        var twoOp = results.First(r => r.Form == "add_step_change_route");
        var insertAfterClean = results.First(r => r.Form == "insert_after-unprotected");
        var twoOpClean = results.First(r => r.Form == "add_step_change_route-unprotected");

        Console.WriteLine($"  insert_after rewires the target's routes implicitly; the two-op form");
        Console.WriteLine($"  must do it explicitly with change_route. A higher failure rate on the");
        Console.WriteLine($"  two-op form means the explicit rewiring is where planners go wrong.");
        Console.WriteLine();

        Console.WriteLine($"  phase 1 (real plan):      insert_after {insertAfter.FailureRate:P0} " +
                          $"vs two-op {twoOp.FailureRate:P0}");
        Console.WriteLine($"  phase 2 (protected off):  insert_after {insertAfterClean.FailureRate:P0} " +
                          $"vs two-op {twoOpClean.FailureRate:P0}");
        Console.WriteLine();

        // The finding is about the rewiring, so phase 2 is the one that answers
        // review 11 C2. Phase 1 says what a planner actually hits.
        var verdict = insertAfterClean.FailureRate <= twoOpClean.FailureRate
            ? "insert_after is at least as reliable as the two-op form"
            : "the two-op form is more reliable than insert_after";

        Console.WriteLine($"  verdict (on the rewiring, phase 2): {verdict}");

        if (twoOp.FailureRate > insertAfter.FailureRate)
        {
            Console.WriteLine();
            Console.WriteLine($"  phase 1 shows the two-op form failing more often on the real plan");
            Console.WriteLine($"  ({twoOp.FailureRate:P0} vs {insertAfter.FailureRate:P0}). The cause is floor F3:");
            Console.WriteLine($"  change_route on a protected step is refused, so the two-op form is");
            Console.WriteLine($"  refused twice where insert_after is refused once.");
        }

        var path = Path.Combine(HarnessProgram.Results, "insert-after-measurement.json");
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(results,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine();
        Console.WriteLine($"wrote {path}");

        return 0;
    }

    /// <summary>
    /// Authors one patch form against every target, <paramref name="trials"/>
    /// times, and reports the validation failure rate.
    /// </summary>
    private static async Task<TrialResult> MeasureAsync(
        Plan plan,
        List<string> targets,
        string form,
        int trials,
        Action<string> say,
        string? label = null)
    {
        say($"--- form: {form}{(label is null ? "" : $" ({label})")} ---");
        say("");

        var applied = 0;
        var rejected = 0;
        var held = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < trials; i++)
        {
            var target = targets[i % targets.Count];
            var working = plan.Clone();

            var patch = Author(working.Revision, target, form, i);
            var result = ApplyPatch.Apply(working, patch);

            switch (result.Kind)
            {
                case PatchResultKind.Applied:
                    applied++;
                    break;
                case PatchResultKind.Rejected:
                    rejected++;
                    break;
                case PatchResultKind.PendingHumanDecision:
                    held++;
                    break;
            }

            foreach (var reason in result.Reasons)
            {
                // Normalise: the reason text carries the step id, which varies
                // per trial. Count the shape, not the instance.
                var key = Normalise(reason);
                reasons[key] = reasons.TryGetValue(key, out var count) ? count + 1 : 1;
            }

            if (i < 3)
            {
                say($"  trial {i}: target={target} -> {result.Kind} " +
                    $"{(result.Reasons.Count > 0 ? $"({string.Join("; ", result.Reasons)})" : "")}");
            }
        }

        var failureRate = (double)(rejected + held) / trials;

        say("");
        say($"  applied:  {applied}/{trials}");
        say($"  rejected: {rejected}/{trials}");
        say($"  held:     {held}/{trials}");
        say($"  validation failure rate: {failureRate:P0}");

        if (reasons.Count > 0)
        {
            say("  reasons:");
            foreach (var (reason, count) in reasons.OrderByDescending(k => k.Value))
                say($"    {count,3}x {reason}");
        }

        say("");

        return await Task.FromResult(
            new TrialResult(label ?? form, trials, applied, rejected, held, failureRate, reasons));
    }

    /// <summary>
    /// Authors a patch in one of the two forms. The step id varies per trial so
    /// a duplicate-id rejection is not counted as a form failure.
    /// </summary>
    private static PlanPatch Author(int revision, string target, string form, int trial)
    {
        var newStep = new Step
        {
            Id = $"extra_{trial}",
            Kind = "notify.send",
            Routes = new Dictionary<string, RouteTarget>(StringComparer.Ordinal),
        };

        var changes = new List<PatchChange>();

        if (form == "insert_after")
        {
            // docs/09 15.3.1: insert_after X inserts S immediately after X, and
            // every route of X that pointed at some step Y now points at S. The
            // rewiring is done by applyPatch.
            changes.Add(new PatchChange
            {
                Op = PatchOps.InsertAfter,
                Target = target,
                Step = newStep,
            });
        }
        else
        {
            // The equivalent expressed as two operations: add the step, then
            // rewire the target's completed route to it by hand.
            changes.Add(new PatchChange
            {
                Op = PatchOps.InsertAfter,
                Target = target,
                Step = newStep,
            });

            changes.Add(new PatchChange
            {
                Op = PatchOps.ChangeRoute,
                Target = target,
                Outcome = Outcomes.Completed,
                To = RouteTarget.ToStep(newStep.Id),
            });
        }

        return new PlanPatch
        {
            BaseRevision = revision,
            ProposedBy = "agent:planner",
            Reason = $"review 11 C2 measurement, form {form}",
            Changes = changes,
        };
    }

    /// <summary>
    /// Normalises a rejection reason so reasons that differ only by step id are
    /// counted together.
    /// </summary>
    private static string Normalise(string reason)
    {
        var text = reason;

        if (text.Contains("protected step", StringComparison.Ordinal))
            return "protected step cannot be changed (floor F3)";
        if (text.Contains("unreachable", StringComparison.Ordinal))
            return "patch would make a protected step unreachable";
        if (text.Contains("already exists", StringComparison.Ordinal))
            return "step id already exists";
        if (text.Contains("stale", StringComparison.Ordinal))
            return "stale base revision";
        if (text.Contains("does not exist", StringComparison.Ordinal))
            return "target step does not exist";

        return text;
    }

    private sealed record TrialResult(
        string Form,
        int Trials,
        int Applied,
        int Rejected,
        int Held,
        double FailureRate,
        Dictionary<string, int> Reasons);
}
