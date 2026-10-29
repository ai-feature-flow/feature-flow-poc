using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;
using Temporalio.Client;
using Temporalio.Worker;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// The Spike 2 real-run worker. Same shape as Spike 1's worker — deliberately
/// killable, one queue — but the activities are backed by real providers.
///
/// <para>
/// Own queue and ports, because other agents share this tree: Spike 1 used
/// <c>spike-1</c> and port 7233, so this uses <c>spike-2-real</c> and 7333.
/// </para>
/// </summary>
public static class WorkerProgram
{
    public static async Task<int> Main(string[] args)
    {
        var taskQueue = Arg(args, "--queue") ?? "spike-2-real";
        var address = Arg(args, "--address") ?? "localhost:7333";
        var workingDirectory = Arg(args, "--repo") ?? @"D:\Dev\feature-flow-poc\spikes\02-real-run\fixture";
        var branch = Arg(args, "--branch") ?? "ff/ticket/spike-2-1";
        var integrationBranch = Arg(args, "--base") ?? "ff/epic/spike-2";
        var ticketId = Arg(args, "--ticket") ?? "TICKET-SPIKE2-1";
        var runId = Arg(args, "--run-id") ?? $"real-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var recordDirectory = Arg(args, "--records")
                              ?? @"D:\Dev\feature-flow-poc\spikes\02-real-run\results\records";
        var maxConcurrentActivities = int.Parse(Arg(args, "--max-concurrent-activities") ?? "10");
        var planPath = Arg(args, "--plan");
        var organisationPath = Arg(args, "--organisation");

        if (args.Contains("--fast"))
        {
            InterpreterWorkflow.HeartbeatTimeout = TimeSpan.FromSeconds(10);
            InterpreterWorkflow.MaxBackoff = TimeSpan.FromSeconds(5);
            InterpreterWorkflow.StalledAfter = TimeSpan.FromSeconds(60);
            InterpreterWorkflow.StartToClosePadding = 30;
            RealActivities.PollInterval = TimeSpan.FromSeconds(2);
            RealActivities.HeartbeatInterval = TimeSpan.FromSeconds(2);
            Console.WriteLine("WORKER fast mode: heartbeat=10s backoffCap=5s " +
                              "stalledAfter=60s poll=2s");
        }

        Console.WriteLine($"WORKER starting queue={taskQueue} address={address} " +
                          $"repo={workingDirectory} branch={branch} base={integrationBranch} " +
                          $"run={runId} pid={Environment.ProcessId}");

        var plan = planPath is null
            ? PlanLoader.LoadTicketDelivery(ticketId)
            : PlanLoader.Load(Path.GetFullPath(planPath), ticketId);
        var organisation = OrganisationConfiguration.Load(
            organisationPath is null ? null : Path.GetFullPath(organisationPath));
        Console.WriteLine($"WORKER plan {plan.Steps.Count} steps, entry {plan.Entry}");

        var validation = RunStartValidator.Validate(plan, organisation);
        validation.WriteTo(Console.Out);
        if (!validation.Accepted)
            return 2;
        if (args.Contains("--validate-only"))
            return 0;

        var context = new RunContext(
            runId, plan, workingDirectory, branch, integrationBranch, ticketId, recordDirectory,
            organisation);

        var client = await TemporalClient.ConnectAsync(new(address));

        using var tokenSource = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            tokenSource.Cancel();
        };

        var activities = new RealActivities(context);

        using var worker = new TemporalWorker(
            client,
            new TemporalWorkerOptions(taskQueue)
            {
                MaxConcurrentActivities = maxConcurrentActivities,
            }
            .AddWorkflow<InterpreterWorkflow>()
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
