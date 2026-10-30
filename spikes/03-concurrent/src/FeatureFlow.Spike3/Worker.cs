using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;
using Temporalio.Client;
using Temporalio.Worker;

namespace FeatureFlow.Spike3;

/// <summary>
/// One worker process per ticket on its own queue, plus one for the epic
/// (docs/17 7.2). Spike 2's RealActivities, with the Spike 3 switches on.
/// </summary>
public static class WorkerCommand
{
    public static void ConfigureSpike3()
    {
        RealActivities.SyncWorktree = true;
        Readiness.BlockingOnly = true;
        Prompt.Override = Texts.AgentPrompt;
        Prompt.ReviewSpecification = Texts.ReviewSpecification;
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var kind = Cli.Arg(args, "--kind") ?? "ticket";
        var queue = Cli.Arg(args, "--queue")!;
        var address = Cli.Arg(args, "--address") ?? Cli.DefaultAddress;
        var repo = Path.GetFullPath(Cli.Arg(args, "--repo")!);
        var branch = Cli.Arg(args, "--branch")!;
        var baseBranch = Cli.Arg(args, "--base")!;
        var ticket = Cli.Arg(args, "--ticket") ?? "EPIC3";
        var runId = Cli.Arg(args, "--run-id")!;
        var records = Path.GetFullPath(Cli.Arg(args, "--records")!);
        var planPath = Cli.Arg(args, "--plan") ?? Path.Combine(Cli.PlansDir, kind == "epic" ? "epic.yaml" : "ticket-delivery.yaml");

        ConfigureSpike3();

        var plan = PlanYaml.Load(planPath, new Dictionary<string, string> { ["ticket_id"] = ticket });
        var organisation = OrganisationConfiguration.Load(Path.Combine(Cli.PlansDir, "organisation.yaml"));
        var validation = RunStartValidator.Validate(plan, organisation);
        validation.WriteTo(Console.Out);
        if (!validation.Accepted)
            return 2;

        Console.WriteLine($"WORKER {kind} queue={queue} repo={repo} branch={branch} base={baseBranch} " +
                          $"ticket={ticket} run={runId} pid={Environment.ProcessId}");

        // ff-presubmit reads the run binding from here; .git is never cleaned by the sync.
        File.WriteAllText(Path.Combine(repo, ".git", "ff-run.json"), System.Text.Json.JsonSerializer.Serialize(new
        {
            ticket, branch, @base = baseBranch, runId, records, plan = Path.GetFullPath(planPath),
        }));

        var context = new RunContext(runId, plan, repo, branch, baseBranch, ticket, records, organisation);
        var client = await TemporalClient.ConnectAsync(new(address));
        var activities = new RealActivities(context);
        var lockActivities = new LockActivities(client);

        var options = new TemporalWorkerOptions(queue) { MaxConcurrentActivities = 10 }
            .AddWorkflow<InterpreterWorkflow>()
            .AddActivity(activities.RunAgentAsync)
            .AddActivity(activities.RunGenericStepAsync)
            .AddActivity(activities.ProposePatchAsync)
            .AddActivity(activities.WaitForDecisionAsync)
            .AddActivity(activities.RecordPatchResultAsync)
            .AddActivity(activities.ReadHistorySizeAsync)
            .AddActivity(lockActivities.RequestLockAsync)
            .AddActivity(lockActivities.IsWorkflowOpenAsync);
        if (kind == "epic")
            options.AddWorkflow<LockWorkflow>();

        using var worker = new TemporalWorker(client, options);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();
        Console.WriteLine("WORKER ready");
        try
        {
            await worker.ExecuteAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
        }
        Console.WriteLine("WORKER exited");
        return 0;
    }
}
