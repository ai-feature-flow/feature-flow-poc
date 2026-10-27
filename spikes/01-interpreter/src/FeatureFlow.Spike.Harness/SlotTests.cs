using System.Diagnostics;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Review 12 A3: the earlier slot conclusion did not follow from the evidence.
/// The slot test ran Linear(3) per run, one activity at a time, so 8 runs never
/// held more than 8 of 10 slots — slot starvation could not explain the
/// slowdown.
///
/// This measures what actually indicates slot waiting: schedule-to-start
/// latency, the gap between ActivityTaskScheduled and ActivityTaskStarted. That
/// is slot waiting by definition.
/// </summary>
public static class SlotTests
{
    public static async Task<int> RunAsync(ITemporalClient client, string queue, string[] args)
    {
        var concurrency = 0;
        foreach (var i in Enumerable.Range(0, args.Length - 1))
        {
            if (args[i] == "--concurrency")
                int.TryParse(args[i + 1], out concurrency);
        }
        if (concurrency <= 0)
            concurrency = 20;

        var stepSeconds = 0;
        foreach (var i in Enumerable.Range(0, args.Length - 1))
        {
            if (args[i] == "--step-seconds")
                int.TryParse(args[i + 1], out stepSeconds);
        }
        if (stepSeconds <= 0)
            stepSeconds = 20;

        Console.WriteLine($"SLOTS: {concurrency} concurrent long steps, " +
                          $"each {stepSeconds}s, against 10 worker slots");

        var ids = new List<string>();
        var started = DateTimeOffset.UtcNow;

        for (var i = 0; i < concurrency; i++)
        {
            var plan = LongStepPlan($"s{i}", stepSeconds);
            var id = $"slot-{i}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            ids.Add(id);
            await client.StartWorkflowAsync(
                (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
                new(id, queue));
        }

        var tasks = ids.Select(async id =>
        {
            var sw = Stopwatch.StartNew();
            // The dev server returns "Workflow is busy" under load, so poll
            // rather than long-polling for the result.
            while (true)
            {
                try
                {
                    await client.GetWorkflowHandle(id).GetResultAsync<string>();
                    break;
                }
                catch (Temporalio.Exceptions.RpcException)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                }
            }
            sw.Stop();
            return sw.Elapsed.TotalSeconds;
        }).ToList();

        var elapsed = await Task.WhenAll(tasks);
        var total = (DateTimeOffset.UtcNow - started).TotalSeconds;

        Console.WriteLine($"SLOTS: {concurrency} runs completed in {total:F1}s wall clock");
        Console.WriteLine($"SLOTS: per-run min={elapsed.Min():F1}s " +
                          $"max={elapsed.Max():F1}s mean={elapsed.Average():F1}s");

        // Schedule-to-start latency, from the histories.
        var latencies = new List<double>();
        foreach (var id in ids)
        {
            try
            {
                var history = await FetchHistoryAsync(id);
                latencies.AddRange(ScheduleToStartLatencies(history));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"SLOTS: could not read history for {id}: {ex.Message}");
            }
        }

        if (latencies.Count > 0)
        {
            latencies.Sort();
            Console.WriteLine($"SLOTS: schedule-to-start over {latencies.Count} activities: " +
                              $"min={latencies[0]:F2}s " +
                              $"p50={latencies[latencies.Count / 2]:F2}s " +
                              $"p95={latencies[(int)(latencies.Count * 0.95)]:F2}s " +
                              $"max={latencies[^1]:F2}s");
        }

        var path = Path.Combine(ResultsDir(), $"slots-latency-{concurrency}.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            concurrency,
            stepSeconds,
            totalSeconds = total,
            perRun = elapsed,
            scheduleToStart = latencies,
        }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"SLOTS: wrote {path}");
        return 0;
    }

    /// <summary>
    /// One long agent.run step. Unlike Linear(3), this holds a slot for the
    /// whole step, which is the H1 case.
    /// </summary>
    private static Plan LongStepPlan(string id, int seconds)
    {
        var plan = new Plan { Entry = id, Revision = 1 };
        plan.Steps.Add(new Step
        {
            Id = id,
            Kind = "agent.run",
            SimulatedDuration = seconds.ToString(),
            PollsInActivity = true,
            Limits = new StepLimits { Attempts = 3, StartToClose = "PT4H", Heartbeat = "PT30S" },
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.Completed] = RouteTarget.ToEnd(Outcomes.Completed),
                [Outcomes.Blocked] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.Failed] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.AttemptsExhausted] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.ReplanRequested] = RouteTarget.ToEnd(Outcomes.Failed),
            },
        });
        return plan;
    }

    /// <summary>
    /// The gap between ActivityTaskScheduled and ActivityTaskStarted for each
    /// activity. This is slot waiting by definition: the task was scheduled and
    /// then waited for a worker slot.
    /// </summary>
    private static List<double> ScheduleToStartLatencies(string historyJson)
    {
        var latencies = new List<double>();
        using var doc = JsonDocument.Parse(historyJson);

        if (!doc.RootElement.TryGetProperty("events", out var events))
            return latencies;

        // The CLI emits ids as strings, so parse them rather than reading them
        // as numbers.
        var scheduledAt = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        foreach (var e in events.EnumerateArray())
        {
            if (!e.TryGetProperty("eventId", out var idProp) ||
                !e.TryGetProperty("eventType", out var typeProp) ||
                !e.TryGetProperty("eventTime", out var timeProp))
                continue;

            var id = idProp.ValueKind == JsonValueKind.Number
                ? idProp.GetInt64().ToString()
                : idProp.GetString() ?? "";
            var type = typeProp.GetString() ?? "";
            var time = timeProp.GetDateTimeOffset();

            if (type == "EVENT_TYPE_ACTIVITY_TASK_SCHEDULED")
            {
                scheduledAt[id] = time;
            }
            else if (type == "EVENT_TYPE_ACTIVITY_TASK_STARTED")
            {
                // The started event references the scheduled event id.
                if (e.TryGetProperty("activityTaskStartedEventAttributes", out var attrs) &&
                    attrs.TryGetProperty("scheduledEventId", out var schedId))
                {
                    var key = schedId.ValueKind == JsonValueKind.Number
                        ? schedId.GetInt64().ToString()
                        : schedId.GetString() ?? "";
                    if (scheduledAt.TryGetValue(key, out var scheduled))
                        latencies.Add((time - scheduled).TotalSeconds);
                }
            }
        }
        return latencies;
    }

    private static async Task<string> FetchHistoryAsync(string id)
    {
        var cli = Environment.GetEnvironmentVariable("TEMPORAL_CLI")
                  ?? @"C:\tools\temporal\temporal.exe";
        var psi = new ProcessStartInfo(cli)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("workflow");
        psi.ArgumentList.Add("show");
        psi.ArgumentList.Add("--workflow-id");
        psi.ArgumentList.Add(id);
        psi.ArgumentList.Add("--address");
        psi.ArgumentList.Add("localhost:7233");
        psi.ArgumentList.Add("--output");
        psi.ArgumentList.Add("json");

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("could not start temporal CLI");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        return stdout;
    }

    private static string ResultsDir()
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results", "measurements"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}
