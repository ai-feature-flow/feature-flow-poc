using System.Text.Json;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;
using Temporalio.Worker;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Exit criterion 6: replay tests pass against the recorded histories (R2, R12).
/// A workflow that is not deterministic fails to replay, so this is the check
/// that the determinism boundary in docs/01 3.1 actually holds.
/// </summary>
public static class ReplayTests
{
    public static async Task<int> RunAsync(ITemporalClient client, string[] args)
    {
        var ids = ArgAll(args, "--id");
        if (ids.Count == 0)
        {
            Console.WriteLine("REPLAY usage: replay --id <workflow-id> [--id <another>]");
            return 1;
        }

        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions()
            .AddWorkflow<InterpreterWorkflow>());

        var failures = 0;
        foreach (var id in ids)
        {
            try
            {
                var history = await FetchHistoryAsync(client, id);
                await replayer.ReplayWorkflowAsync(history);
                Console.WriteLine($"REPLAY {id}: PASS ({history.Events.Count} events)");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"REPLAY {id}: FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }

        Console.WriteLine(failures == 0
            ? $"REPLAY all {ids.Count} history/histories replayed cleanly"
            : $"REPLAY {failures} of {ids.Count} failed");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Fetch a workflow's history from the server. The Temporalio client does
    /// not expose a history fetch on the handle, so use the CLI's gRPC surface
    /// through the same connection by shelling out to the temporal CLI.
    /// </summary>
    private static async Task<Temporalio.Common.WorkflowHistory> FetchHistoryAsync(
        ITemporalClient client, string id)
    {
        var cli = Environment.GetEnvironmentVariable("TEMPORAL_CLI") ?? @"C:\tools\temporal\temporal.exe";
        var temp = Path.Combine(Path.GetTempPath(), $"ff-history-{id}.json");

        var psi = new System.Diagnostics.ProcessStartInfo(cli)
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

        using var process = System.Diagnostics.Process.Start(psi)
            ?? throw new InvalidOperationException("could not start temporal CLI");
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"temporal CLI failed: {stderr}");

        return Temporalio.Common.WorkflowHistory.FromJson(id, stdout);
    }

    private static List<string> ArgAll(string[] args, string name)
    {
        var values = new List<string>();
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                values.Add(args[i + 1]);
        }
        return values;
    }
}
