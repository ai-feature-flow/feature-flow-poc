using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.Worker;
using Temporalio.Client;
using Temporalio.Worker;

namespace FeatureFlow.Spike.Worker;

/// <summary>
/// Spike worker. Deliberately killable: the harness starts this as a separate
/// process and kills it mid-step to test resume (docs/08 2.1).
/// </summary>
public static class WorkerProgram
{
    public static async Task<int> Main(string[] args)
    {
        var taskQueue = Arg(args, "--queue") ?? "spike-1";
        var sandboxUrl = Arg(args, "--sandbox") ?? "http://127.0.0.1:5199";
        var address = Arg(args, "--address") ?? "localhost:7233";
        var maxConcurrentActivities = int.Parse(Arg(args, "--max-concurrent-activities") ?? "10");

        // Review 13: the tunables that dominate test wall-clock time live in the
        // workflow, but the worker is a separate process, so they must be passed
        // in rather than set by the harness.
        if (args.Contains("--fast"))
        {
            InterpreterWorkflow.HeartbeatTimeout = TimeSpan.FromSeconds(3);
            InterpreterWorkflow.MaxBackoff = TimeSpan.FromSeconds(5);
            InterpreterWorkflow.StalledAfter = TimeSpan.FromSeconds(30);
            InterpreterWorkflow.StartToClosePadding = 5;
            Console.WriteLine("WORKER fast mode: heartbeat=3s backoffCap=5s " +
                              "stalledAfter=30s startToClosePadding=5s");
        }

        Console.WriteLine($"WORKER starting queue={taskQueue} sandbox={sandboxUrl} " +
                          $"maxConcurrentActivities={maxConcurrentActivities} pid={Environment.ProcessId}");

        var client = await TemporalClient.ConnectAsync(new(address));

        using var tokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tokenSource.Cancel();
        };

        var activities = new SandboxActivities(sandboxUrl);

        using var worker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(taskQueue)
            {
                // H1: a poll loop inside one activity holds a worker slot for the
                // whole step. This is the knob the worker-slot measurement turns.
                MaxConcurrentActivities = maxConcurrentActivities,
            }
            .AddWorkflow<InterpreterWorkflow>()
            // Registered explicitly: the [Activity] attributes are on the
            // interface, because the workflow calls through the interface.
            .AddActivity(activities.RunAgentAsync)
            .AddActivity(activities.RunGenericStepAsync)
            .AddActivity(activities.ProposePatchAsync)
            .AddActivity(activities.WaitForDecisionAsync)
            .AddActivity(activities.RecordPatchResultAsync)
            .AddActivity(activities.ReadHistorySizeAsync));

        Console.WriteLine("WORKER ready");
        try
        {
            await worker.ExecuteAsync(tokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("WORKER cancelled");
        }

        Console.WriteLine("WORKER exited");
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
