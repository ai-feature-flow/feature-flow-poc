using System.Text;
using FeatureFlow.Spike.RealWorker;
using Temporalio.Client;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Runs the ten checks from docs/08 2.2 and writes one report.
///
/// <para>
/// A check that cannot be evidenced says so and names what is missing. That is
/// more useful than a plausible-looking line, so the harness never invents
/// output.
/// </para>
/// </summary>
public static class Checks
{
    public static async Task<int> RunAsync(string[] args)
    {
        var only = HarnessProgram.Arg(args, "--only");
        var runId = HarnessProgram.Arg(args, "--run-id") ?? $"checks-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var branch = HarnessProgram.Arg(args, "--branch") ?? "ff/ticket/spike-2-1";
        var baseBranch = HarnessProgram.Arg(args, "--base") ?? "ff/epic/spike-2";

        var report = new StringBuilder();
        var failures = 0;
        var notEvidenced = new List<string>();

        void Say(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        HarnessProgram.WriteHeader("Spike 2: the ten checks from docs/08 2.2");
        Say($"Captured {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}.");
        Say($"Repository {RunContext.Owner}/{RunContext.Repository}, checkout {HarnessProgram.Fixture}, " +
            $"branch {branch} -> {baseBranch}.");
        Say($"Temporal {HarnessProgram.Address}, queue {HarnessProgram.Queue}, run {runId}.");
        Say("");

        var plan = PlanLoader.LoadTicketDelivery("TICKET-SPIKE2-1");
        var context = new RunContext(
            runId, plan, HarnessProgram.Fixture, branch, baseBranch,
            "TICKET-SPIKE2-1", HarnessProgram.Records);

        var client = await TemporalClient.ConnectAsync(new(HarnessProgram.Address));

        var checks = new (string Name, Func<Task<int>> Run)[]
        {
            ("R5", () => CheckR5.RunAsync(context, Say)),
            ("R6", () => CheckR6.RunAsync(context, Say)),
            ("R13", () => CheckR13.RunAsync(context, Say)),
            ("R4", () => CheckR4.RunAsync(context, Say)),
            ("R16", () => CheckR16.RunAsync(context, Say)),
            ("R8", () => CheckR8.RunAsync(client, context, Say)),
            ("K4-regression", () => CheckK4Regression.RunAsync(context, Say)),
            ("review11-C1", () => CheckOutputs.RunAsync(context, Say)),
            ("K4-decline", () => CheckK4Decline.RunAsync(context, Say)),
            ("K5", () => CheckK5.RunAsync(client, context, Say)),
        };

        foreach (var (name, run) in checks)
        {
            if (only is not null && only != name)
                continue;

            int outcome;
            try
            {
                outcome = await run();
            }
            catch (Exception ex)
            {
                Say($"## {name}: threw {ex.GetType().Name}: {ex.Message}");
                Say("");
                outcome = 1;
            }

            if (outcome != 0)
            {
                failures++;
                notEvidenced.Add(name);
            }
        }

        Say("## Summary");
        Say("");
        Say($"checks run:        {checks.Length}");
        Say($"failed or not evidenced: {failures}");
        if (notEvidenced.Count > 0)
            Say($"  {string.Join(", ", notEvidenced)}");
        Say("");

        var path = Path.Combine(HarnessProgram.Results, "spike2-checks.md");
        File.WriteAllText(path, report.ToString());
        Console.WriteLine($"wrote {path}");

        return failures == 0 ? 0 : 1;
    }
}
