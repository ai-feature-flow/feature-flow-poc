using System.Diagnostics;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Drives the failure injections and collects the six measurements.
/// docs/08 2.1. Spike code: correctness of the measurements matters.
/// </summary>
public static class HarnessProgram
{
    private const string Address = "localhost:7233";

    public static async Task<int> Main(string[] args)
    {
        var command = args.Length > 0 ? args[0] : "help";
        var queue = Arg(args, "--queue") ?? "spike-1";

        var client = await TemporalClient.ConnectAsync(new(Address));

        return command switch
        {
            "smoke" => await SmokeAsync(client, queue),
            "history" => await HistoryAsync(client, queue, int.Parse(Arg(args, "--steps") ?? "200")),
            "slots" => await SlotsAsync(client, queue, int.Parse(Arg(args, "--concurrency") ?? "8")),
            "inspect" => await InspectAsync(client, Arg(args, "--id") ?? ""),
            "patch" => await PatchTests.RunAsync(client, queue),
            "replay" => await ReplayTests.RunAsync(client, args),
            "crash" => await CrashTests.RunAsync(client, queue, args),
            _ => Help(),
        };
    }

    /// <summary>A short run against the spec plan, to prove the loop works.</summary>
    private static async Task<int> SmokeAsync(ITemporalClient client, string queue)
    {
        var plan = PlanGenerator.SpecPlan();
        var id = $"smoke-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        Console.WriteLine($"HARNESS starting {id} with {plan.Steps.Count} steps");
        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, queue)
            {
                IdConflictPolicy = Temporalio.Api.Enums.V1.WorkflowIdConflictPolicy.UseExisting,
            });

        // Approve the gates as they open.
        var approvals = Task.Run(async () =>
        {
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(2000);
                try
                {
                    var state = await handle.QueryAsync(w => w.State());
                    foreach (var step in state.Plan.Steps.Where(s => s.Gate is not null))
                    {
                        if (state.StepRuns.ContainsKey(step.Id))
                            continue;
                        await handle.ExecuteUpdateAsync(w => w.SubmitDecisionAsync(
                            new StepDecision(step.Id, Outcomes.Approved)));
                        Console.WriteLine($"HARNESS approved gate {step.Id}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"HARNESS gate poll: {ex.GetType().Name}");
                }
            }
        });

        var result = await handle.GetResultAsync<string>();
        await approvals;

        var state = await handle.QueryAsync(w => w.State());
        Console.WriteLine($"HARNESS result={result} stepsExecuted={state.StepsExecuted} " +
                          $"continueAsNew={state.ContinueAsNewCount}");
        return 0;
    }

    /// <summary>
    /// Measurement 1: history size and event count at each continue-as-new.
    /// This is what closes brief Q6 (docs/03 Q6).
    /// </summary>
    private static async Task<int> HistoryAsync(ITemporalClient client, string queue, int steps)
    {
        var plan = PlanGenerator.Linear(steps);
        var id = $"history-{steps}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        Console.WriteLine($"HARNESS history run {id}: {steps} steps");
        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput
            {
                Plan = plan,
                HistoryEventThreshold = 500,
            }),
            new(id, queue));

        var result = await handle.GetResultAsync<string>();

        // The final sample is recorded by the workflow before it returns, so it
        // survives completion. Measurement 1.
        var samples = await handle.QueryAsync(w => w.HistorySamples());

        Console.WriteLine($"HARNESS result={result}");
        foreach (var s in samples)
        {
            Console.WriteLine($"HARNESS continue-as-new #{s.ContinueAsNewIndex}: " +
                              $"events={s.HistoryEventCount} bytes={s.HistoryLengthBytes} " +
                              $"steps={s.StepsExecuted}");
        }

        var path = Path.Combine(ResultsDir(), $"history-{steps}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            steps,
            result,
            samples,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"HARNESS wrote {path}");
        return 0;
    }

    /// <summary>
    /// Measurement 3: worker slot capacity. How many concurrent long polled
    /// steps before a task queue starves (H1).
    /// </summary>
    private static async Task<int> SlotsAsync(ITemporalClient client, string queue, int concurrency)
    {
        Console.WriteLine($"HARNESS slot test: {concurrency} concurrent long polled steps");
        var started = DateTimeOffset.UtcNow;

        var handles = new List<WorkflowHandle>();
        for (var i = 0; i < concurrency; i++)
        {
            var plan = PlanGenerator.Linear(3, $"c{i}_");
            var handle = await client.StartWorkflowAsync(
                (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
                new($"slot-{i}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", queue));
            handles.Add(handle);
        }

        var tasks = handles.Select(async h =>
        {
            var sw = Stopwatch.StartNew();
            await h.GetResultAsync<string>();
            sw.Stop();
            return sw.Elapsed.TotalSeconds;
        }).ToList();

        var elapsed = await Task.WhenAll(tasks);
        var total = (DateTimeOffset.UtcNow - started).TotalSeconds;

        Console.WriteLine($"HARNESS {concurrency} runs completed in {total:F1}s wall clock");
        Console.WriteLine($"HARNESS per-run seconds: min={elapsed.Min():F1} " +
                          $"max={elapsed.Max():F1} mean={elapsed.Average():F1}");

        var path = Path.Combine(ResultsDir(), $"slots-{concurrency}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            concurrency,
            totalSeconds = total,
            perRun = elapsed,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"HARNESS wrote {path}");
        return 0;
    }

    /// <summary>Query a running or completed workflow, for diagnosis.</summary>
    private static async Task<int> InspectAsync(ITemporalClient client, string id)
    {
        var handle = client.GetWorkflowHandle<InterpreterWorkflow>(id);
        var state = await handle.QueryAsync(w => w.State());
        Console.WriteLine($"HARNESS {id}: stepsExecuted={state.StepsExecuted} " +
                          $"continueAsNew={state.ContinueAsNewCount} terminal={state.TerminalStatus}");
        Console.WriteLine($"HARNESS cursors=[{string.Join(",", state.Cursors)}]");
        Console.WriteLine($"HARNESS lastError={state.LastInfrastructureError}");
        Console.WriteLine($"HARNESS stepRuns:");
        foreach (var (stepId, run) in state.StepRuns)
            Console.WriteLine($"  {stepId} -> {run.Outcome} attempts={run.Attempts} resumes={run.Resumes}");
        return 0;
    }

    private static string ResultsDir()
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "results"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static int Help()
    {
        Console.WriteLine("""
            usage: FeatureFlow.Spike.Harness <command> [options]

              smoke                       run the spec plan once
              history  --steps 200        measure history size at continue-as-new
              slots    --concurrency 8    measure worker slot capacity
              patch                       patch tests: exit criteria 2, 3, 4
              replay                      replay tests against recorded histories
              crash [all|1-resume-after-crash|2-repeated-kill-no-progress|
                    3-hook-unreachable-infrastructure|4-break-connection]
                                          crash tests: exit criterion 1 and its measurements
              inspect  --id <workflow>    query a run's state
            """);
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }
}
