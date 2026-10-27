using System.Diagnostics;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Review 12 A5: criterion 8 was measured indirectly, from a run of mostly short
/// steps. Measure it directly: count ActivityTaskScheduled events for the long
/// polled step. It should be one, plus one per re-attach.
/// </summary>
public static class EventCountTests
{
    public static async Task<int> RunAsync(ITemporalClient client, string queue, string[] args)
    {
        var stepSeconds = 0;
        foreach (var i in Enumerable.Range(0, args.Length - 1))
        {
            if (args[i] == "--step-seconds")
                int.TryParse(args[i + 1], out stepSeconds);
        }
        if (stepSeconds <= 0)
            stepSeconds = 60;

        var breakConnection = args.Contains("--break");

        Console.WriteLine($"EVENTS: one long polled step of {stepSeconds}s" +
                          (breakConnection ? ", with a connection break" : ""));

        var plan = LongStepPlan(stepSeconds);
        var id = $"events-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, queue));

        if (breakConnection)
        {
            // Wait for the command, then break the connection briefly.
            var command = await WaitForCommandAsync("poll_step", TimeSpan.FromSeconds(60));
            if (command is not null)
            {
                await PostAsync($"/commands/{command.Handle}/break-connection?seconds=20", new { });
                Console.WriteLine($"EVENTS: broke connection on {command.Handle} for 20s");
            }
        }

        await handle.GetResultAsync<string>(
            followRuns: true,
            new RpcOptions { Timeout = TimeSpan.FromSeconds(300) });

        var history = await FetchHistoryAsync(id);
        var counts = CountEvents(history);

        Console.WriteLine($"EVENTS: ActivityTaskScheduled={counts.Scheduled} " +
                          $"ActivityTaskStarted={counts.Started} " +
                          $"ActivityTaskCompleted={counts.Completed} " +
                          $"ActivityTaskTimedOut={counts.TimedOut} " +
                          $"total events={counts.Total}");

        // docs/05 1.2.1: one activity per step, not one per poll. A 60s step
        // polled every 500ms would be about 120 activities if polling were
        // per-poll.
        var pollsIfPerPoll = stepSeconds * 2;
        Console.WriteLine($"EVENTS: a per-poll design would have scheduled " +
                          $"about {pollsIfPerPoll} activities for this step");

        var ok = counts.Scheduled <= 3;
        Console.WriteLine($"EVENTS: scheduled={counts.Scheduled} (one, plus one per " +
                          $"re-attach) => {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    private static Plan LongStepPlan(int seconds)
    {
        var plan = new Plan { Entry = "poll_step", Revision = 1 };
        plan.Steps.Add(new Step
        {
            Id = "poll_step",
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

    private static EventCounts CountEvents(string historyJson)
    {
        var counts = new EventCounts();
        using var doc = JsonDocument.Parse(historyJson);
        if (!doc.RootElement.TryGetProperty("events", out var events))
            return counts;

        foreach (var e in events.EnumerateArray())
        {
            if (!e.TryGetProperty("eventType", out var typeProp))
                continue;
            counts.Total++;
            switch (typeProp.GetString())
            {
                case "EVENT_TYPE_ACTIVITY_TASK_SCHEDULED": counts.Scheduled++; break;
                case "EVENT_TYPE_ACTIVITY_TASK_STARTED": counts.Started++; break;
                case "EVENT_TYPE_ACTIVITY_TASK_COMPLETED": counts.Completed++; break;
                case "EVENT_TYPE_ACTIVITY_TASK_TIMED_OUT": counts.TimedOut++; break;
            }
        }
        return counts;
    }

    private sealed class EventCounts
    {
        public int Scheduled;
        public int Started;
        public int Completed;
        public int TimedOut;
        public int Total;
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
        return await process.StandardOutput.ReadToEndAsync();
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    private static async Task PostAsync(string path, object body)
    {
        var json = JsonSerializer.Serialize(body);
        var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        await Http.PostAsync("http://127.0.0.1:5199" + path, content);
    }

    private static async Task<CommandView?> WaitForCommandAsync(string stepId, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var response = await Http.GetAsync("http://127.0.0.1:5199/admin/state");
                if (response.IsSuccessStatusCode)
                {
                    var body = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(body);
                    if (doc.RootElement.TryGetProperty("commands", out var commands))
                    {
                        foreach (var c in commands.EnumerateArray())
                        {
                            if (c.TryGetProperty("sandboxKey", out var key) &&
                                key.GetString() == $"sbx-{stepId}")
                            {
                                return new CommandView
                                {
                                    Handle = c.GetProperty("handle").GetString() ?? "",
                                };
                            }
                        }
                    }
                }
            }
            catch
            {
                // Retry.
            }
            await Task.Delay(500);
        }
        return null;
    }

    private sealed class CommandView
    {
        public string Handle { get; set; } = "";
    }
}
