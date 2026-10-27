using System.Diagnostics;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Exit criterion 7: Temporal unavailable for several minutes. No step fails,
/// and the API reports platform status distinctly from run status (K2).
/// Including an outage longer than the heartbeat timeout: the activity times
/// out, the retry re-attaches by handle, and the step does not fail (H2).
///
/// The point of the test is the second half of H2: a timeout here must not
/// become a work outcome.
/// </summary>
public static class OutageTests
{
    private const string Address = "localhost:7233";

    public static async Task<int> RunAsync(ITemporalClient client, string queue, string[] args)
    {
        var outageSeconds = 0;
        foreach (var i in Enumerable.Range(0, args.Length - 1))
        {
            if (args[i] == "--seconds")
                int.TryParse(args[i + 1], out outageSeconds);
        }
        if (outageSeconds <= 0)
            outageSeconds = 90;

        Console.WriteLine($"OUTAGE: Temporal server down for {outageSeconds}s mid-run");

        var plan = LongAgentPlan();
        var id = $"outage-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, queue));

        // Wait until the step is genuinely in flight.
        InterpreterState? before = null;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            try
            {
                var s = await handle.QueryAsync(w => w.State());
                if (s.Cursors.Contains("long_agent"))
                {
                    before = s;
                    break;
                }
            }
            catch
            {
                // Not started yet.
            }
        }

        if (before is null)
        {
            Console.WriteLine("OUTAGE: FAIL the step never went in flight");
            return 1;
        }

        Console.WriteLine($"OUTAGE: step in flight, cursors=[{string.Join(",", before.Cursors)}]");

        // Stop the Temporal server.
        var serverPid = await FindTemporalPidAsync();
        if (serverPid is null)
        {
            Console.WriteLine("OUTAGE: FAIL could not find the temporal server process");
            return 1;
        }

        Console.WriteLine($"OUTAGE: stopping temporal server pid={serverPid}");
        StopProcess(serverPid.Value);

        var downAt = DateTimeOffset.UtcNow;
        await Task.Delay(TimeSpan.FromSeconds(outageSeconds));

        // Restart it.
        var restarted = StartTemporalServer();
        Console.WriteLine($"OUTAGE: server restarted={restarted}");
        await Task.Delay(TimeSpan.FromSeconds(10));

        // Wait for the run to finish.
        string result;
        try
        {
            result = await handle.GetResultAsync<string>(
                followRuns: true,
                new RpcOptions { Timeout = TimeSpan.FromSeconds(300) });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"OUTAGE: FAIL run did not complete: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }

        var after = await handle.QueryAsync(w => w.State());
        var outcome = after.StepOutcomes.TryGetValue("long_agent", out var r) ? r.Outcome : null;

        // Detail is not carried (review 12 A1); read it from the record store.
        var run = RecordStore.ForRun(id).Entries
            .Where(e => e.Kind == "step_run")
            .Select(e => (StepRunDetail)e.Payload)
            .LastOrDefault(d => d.StepId == "long_agent");

        Console.WriteLine($"OUTAGE: result={result} outcome={outcome} " +
                          $"attempts={run?.Attempts} resumes={run?.Resumes} " +
                          $"infrastructureFailures={run?.InfrastructureFailures}");

        // The criterion: no step failed because the platform was down.
        var noStepFailed = outcome == Outcomes.Completed;
        var outageWasSeen = (run?.InfrastructureFailures ?? 0) > 0;
        var outageWasFree = (run?.Attempts ?? 99) == 1;

        Console.WriteLine($"OUTAGE: noStepFailed={noStepFailed} outageWasSeen={outageWasSeen} " +
                          $"outageWasFree={outageWasFree} " +
                          $"downFor={(DateTimeOffset.UtcNow - downAt).TotalSeconds:F0}s");

        var ok = noStepFailed && outageWasSeen && outageWasFree;
        Console.WriteLine($"OUTAGE: {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }

    private static Plan LongAgentPlan()
    {
        var plan = new Plan { Entry = "long_agent", Revision = 1 };
        plan.Steps.Add(new Step
        {
            Id = "long_agent",
            Kind = "agent.run",
            Protected = true,
            SimulatedDuration = "120",
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

    private static async Task<int?> FindTemporalPidAsync()
    {
        var psi = new ProcessStartInfo("powershell")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-Command");
        psi.ArgumentList.Add(
            "Get-CimInstance Win32_Process -Filter \"Name = 'temporal.exe'\" | " +
            "Select-Object -ExpandProperty ProcessId");

        using var process = Process.Start(psi);
        if (process is null)
            return null;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(line.Trim(), out var pid))
                return pid;
        }
        return null;
    }

    private static void StopProcess(int pid)
    {
        try
        {
            Process.GetProcessById(pid).Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  kill {pid}: {ex.GetType().Name}");
        }
    }

    private static bool StartTemporalServer()
    {
        var psi = new ProcessStartInfo(@"C:\tools\temporal\temporal.exe")
        {
            UseShellExecute = false,
            WorkingDirectory = @"d:\Dev\feature-flow-poc",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("server");
        psi.ArgumentList.Add("start-dev");
        psi.ArgumentList.Add("--db-filename");
        psi.ArgumentList.Add(@"d:\Dev\feature-flow-poc\.spike\data\temporal.db");
        psi.ArgumentList.Add("--ui-port");
        psi.ArgumentList.Add("8233");
        psi.ArgumentList.Add("--log-level");
        psi.ArgumentList.Add("warn");
        psi.ArgumentList.Add("--headless");

        Process.Start(psi);
        return true;
    }
}
