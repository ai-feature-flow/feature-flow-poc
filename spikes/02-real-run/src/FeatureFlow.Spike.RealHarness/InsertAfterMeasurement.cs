using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// review 11 C2, re-run for review 14 A8: the <c>insert_after</c> measurement
/// with a <b>planner model</b> authoring the patches from the readable form.
///
/// <para>
/// <b>Why this was rewritten.</b> The first measurement generated its patches in
/// code by cycling targets, so the 25% and 50% failure rates it reported were
/// floor F3 refusals on the protected <c>design</c> step — the measurement
/// measured the floor, not the planner. A planner that reads the readable form
/// and chooses its own target can be measured; a loop that cycles targets
/// cannot.
/// </para>
///
/// <para>
/// What is counted, and why it is split:
///
/// <list type="bullet">
///   <item><b>planner failures</b> — the planner did not return a usable patch
///     (<c>unparseable</c>, <c>unknown_target</c>, <c>duplicate_step_id</c>).</item>
///   <item><b>validation failures</b> — the planner authored a patch and
///     <c>applyPatch</c> refused or held it.</item>
/// </list>
///
/// The first measurement conflated the two, which is what made it circular.
/// </para>
/// </summary>
public static class InsertAfterMeasurement
{
    /// <summary>How many patches of each form the planner authors.</summary>
    public const int Trials = 20;

    /// <summary>
    /// The intents a planner is given. Each is a realistic reason to insert a
    /// step, so the planner has to read the form and choose a target rather
    /// than being handed one.
    /// </summary>
    private static readonly string[] Intents =
    {
        "Notify the team on Slack after the plan is written.",
        "Send a notification after the design is approved.",
        "Post a message once the implementation is done.",
        "Notify the reviewer when tests have run.",
        "Send a summary after the fix is verified.",
    };

    public static async Task<int> RunAsync(string[] args)
    {
        HarnessProgram.WriteHeader("review 11 C2 / review 14 A8: insert_after vs add_step + change_route");

        var trials = HarnessProgram.Arg(args, "--trials") is { } text && int.TryParse(text, out var t)
            ? t
            : Trials;

        var plan = PlanLoader.LoadTicketDelivery("TICKET-SPIKE2-1");
        var context = new RunContext(
            $"insert-after-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            plan, HarnessProgram.Fixture, "ff/ticket/spike-2-1", "ff/epic/spike-2",
            "TICKET-SPIKE2-1", HarnessProgram.Records);

        Console.WriteLine($"trials per form: {trials}");
        Console.WriteLine($"planner model:   {context.Model.Model}");
        Console.WriteLine($"router endpoint: {context.Model.Endpoint}");
        Console.WriteLine($"protected steps: {string.Join(", ", plan.Steps.Where(s => s.Protected).Select(s => s.Id))}");
        Console.WriteLine();

        // The readable form the planner reads. Printed once, so the evidence
        // shows what the model was actually given.
        Console.WriteLine("### the readable form the planner reads (docs/12 section 4)");
        Console.WriteLine();
        Console.WriteLine(ReadableForm.Render(plan));

        var results = new List<TrialResult>();

        foreach (var form in new[] { "insert_after", "add_step_change_route" })
        {
            results.Add(await MeasureAsync(context, plan, form, trials, Console.WriteLine));
        }

        Console.WriteLine("--- comparison ---");
        Console.WriteLine();
        Console.WriteLine($"  {"form",-26} {"authored",-10} {"planner fail",-14} {"validation fail",-16} {"applied",-9}");
        Console.WriteLine("  " + new string('-', 78));

        foreach (var result in results)
        {
            Console.WriteLine($"  {result.Form,-26} {result.Authored,-10} {result.PlannerFailures,-14} " +
                              $"{result.ValidationFailures,-16} {result.Applied,-9}");
        }

        Console.WriteLine();

        foreach (var result in results)
        {
            Console.WriteLine($"  {result.Form}:");
            Console.WriteLine($"    authored:         {result.Authored}/{result.Trials}");
            Console.WriteLine($"    planner failures: {result.PlannerFailures} ({result.PlannerFailureRate:P0})");
            Console.WriteLine($"    validation fails: {result.ValidationFailures} ({result.ValidationFailureRate:P0})");
            Console.WriteLine($"    applied:          {result.Applied} ({result.AppliedRate:P0})");
            if (result.Reasons.Count > 0)
            {
                Console.WriteLine("    reasons:");
                foreach (var (reason, count) in result.Reasons.OrderByDescending(k => k.Value))
                    Console.WriteLine($"      {count,3}x {reason}");
            }
            Console.WriteLine();
        }

        var insertAfter = results.First(r => r.Form == "insert_after");
        var twoOp = results.First(r => r.Form == "add_step_change_route");

        Console.WriteLine("  insert_after rewires the target's routes implicitly; the two-op form");
        Console.WriteLine("  must do it explicitly with change_route. A higher validation failure");
        Console.WriteLine("  rate on the two-op form means the explicit rewiring is where planners");
        Console.WriteLine("  go wrong.");
        Console.WriteLine();
        Console.WriteLine($"  validation failure rate: insert_after {insertAfter.ValidationFailureRate:P0} " +
                          $"vs two-op {twoOp.ValidationFailureRate:P0}");
        Console.WriteLine($"  planner failure rate:    insert_after {insertAfter.PlannerFailureRate:P0} " +
                          $"vs two-op {twoOp.PlannerFailureRate:P0}");
        Console.WriteLine();

        var path = Path.Combine(HarnessProgram.Results, "insert-after-measurement.json");
        File.WriteAllText(path, JsonSerializer.Serialize(results,
            new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"wrote {path}");

        return 0;
    }

    /// <summary>
    /// Asks the planner to author <paramref name="trials"/> patches in one form,
    /// applies each, and counts what happened.
    /// </summary>
    private static async Task<TrialResult> MeasureAsync(
        RunContext context, Plan plan, string form, int trials, Action<string> say)
    {
        say($"--- form: {form} ---");
        say("");

        var authored = 0;
        var plannerFailures = 0;
        var validationFailures = 0;
        var applied = 0;
        var reasons = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 0; i < trials; i++)
        {
            var intent = Intents[i % Intents.Length];

            var proposal = await context.Planner.AuthorAsync(plan, form, intent, CancellationToken.None);

            if (!proposal.IsOk)
            {
                plannerFailures++;
                var key = proposal.Error!.Reason;
                reasons[key] = reasons.TryGetValue(key, out var c) ? c + 1 : 1;
                say($"  trial {i}: intent=\"{intent}\" -> planner failure: {key}");
                continue;
            }

            authored++;
            var patch = proposal.Value!;

            var working = plan.Clone();
            var result = ApplyPatch.Apply(working, patch);

            switch (result.Kind)
            {
                case PatchResultKind.Applied:
                    applied++;
                    break;
                case PatchResultKind.Rejected:
                case PatchResultKind.PendingHumanDecision:
                    validationFailures++;
                    break;
            }

            foreach (var reason in result.Reasons)
            {
                var key = Normalise(reason);
                reasons[key] = reasons.TryGetValue(key, out var c) ? c + 1 : 1;
            }

            if (i < 5)
            {
                var target = patch.Changes.Count > 0 ? patch.Changes[0].Target : "(none)";
                say($"  trial {i}: intent=\"{intent}\" -> target={target} " +
                    $"ops={patch.Changes.Count} {result.Kind}" +
                    $"{(result.Reasons.Count > 0 ? $" ({string.Join("; ", result.Reasons)})" : "")}");
            }
        }

        say("");
        say($"  authored:          {authored}/{trials}");
        say($"  planner failures:  {plannerFailures}");
        say($"  validation fails:  {validationFailures}");
        say($"  applied:           {applied}");
        say("");

        return new TrialResult(
            form, trials, authored, plannerFailures, validationFailures, applied, reasons);
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
        int Authored,
        int PlannerFailures,
        int ValidationFailures,
        int Applied,
        Dictionary<string, int> Reasons)
    {
        public double PlannerFailureRate => (double)PlannerFailures / Trials;
        public double ValidationFailureRate => (double)ValidationFailures / Trials;
        public double AppliedRate => (double)Applied / Trials;
    }
}
