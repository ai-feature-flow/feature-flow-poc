using System.Diagnostics;
using System.Text;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;
using Temporalio.Client;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Drives one real ticket run and the ten checks from docs/08 2.2, plus the
/// review 11 C2 <c>insert_after</c> measurement.
///
/// <para>
/// Every check prints the evidence it came from. A check that cannot be
/// evidenced says so and names what is missing, rather than printing a
/// plausible-looking line.
/// </para>
/// </summary>
public static class HarnessProgram
{
    /// <summary>
    /// The Temporal address. Overridable, because other agents share this tree
    /// and each needs its own dev server.
    /// </summary>
    public static readonly string Address =
        Environment.GetEnvironmentVariable("FF_TEMPORAL_ADDRESS") ?? "localhost:7333";

    /// <summary>The task queue. Overridable for the same reason.</summary>
    public static readonly string Queue =
        Environment.GetEnvironmentVariable("FF_QUEUE") ?? "spike-2-real";

    /// <summary>
    /// The spike root: the directory holding <c>plans/</c>, <c>fixture/</c> and
    /// <c>src/</c>. Resolved by walking up rather than by counting levels, so the
    /// harness works from the project directory, the solution directory, or a
    /// published output folder.
    /// </summary>
    private static readonly string Root = ResolveRoot();

    private static string ResolveRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory.FullName, "plans")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        // Fall back to the known location rather than failing to start.
        return @"D:\Dev\feature-flow-poc\spikes\02-real-run";
    }

    private static readonly string FixtureDirectory =
        Environment.GetEnvironmentVariable("FF_FIXTURE_DIR")
        ?? ResolveFixtureDirectory();

    /// <summary>
    /// The fixture checkout. Resolved rather than assumed: the checkout is beside
    /// <c>plans/</c>, not under <c>src/</c>.
    /// </summary>
    private static string ResolveFixtureDirectory()
    {
        var candidate = Path.Combine(Root, "fixture");
        if (Directory.Exists(Path.Combine(candidate, ".git")))
            return candidate;

        return candidate;
    }

    private static readonly string ResultsDirectory =
        Path.Combine(Root, "results");

    private static readonly string RecordDirectory =
        Path.Combine(ResultsDirectory, "records");

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Directory.CreateDirectory(ResultsDirectory);
        Directory.CreateDirectory(RecordDirectory);

        var command = args.Length > 0 ? args[0] : "help";

        return command switch
        {
            "run" => await RunTicketAsync(args),
            "checks" => await Checks.RunAsync(args),
            "insert-after" => await InsertAfterMeasurement.RunAsync(args),
            "review14" => await Review14Checks.RunAsync(),
            "cleanup" => await Cleanup.RunAsync(args),
            _ => Help(),
        };
    }

    /// <summary>
    /// The exit criterion: one ticket runs to a merged PR against real providers.
    /// </summary>
    private static async Task<int> RunTicketAsync(string[] args)
    {
        var ticketId = Arg(args, "--ticket") ?? "TICKET-SPIKE2-1";
        var branch = Arg(args, "--branch") ?? "ff/ticket/spike-2-1";
        var baseBranch = Arg(args, "--base") ?? "ff/epic/spike-2";
        var runId = Arg(args, "--run-id") ?? $"real-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var humanApprovers = (Arg(args, "--human-approvers") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        WriteHeader("Spike 2: one real ticket run");
        Console.WriteLine($"utc:        {DateTimeOffset.UtcNow:O}");
        Console.WriteLine($"run id:     {runId}");
        Console.WriteLine($"ticket:     {ticketId}");
        Console.WriteLine($"repository: {RunContext.Owner}/{RunContext.Repository}");
        Console.WriteLine($"checkout:   {FixtureDirectory}");
        Console.WriteLine($"branch:     {branch} -> {baseBranch}");
        Console.WriteLine($"temporal:   {Address} queue {Queue}");
        Console.WriteLine();

        var plan = PlanLoader.LoadTicketDelivery(ticketId);
        Console.WriteLine($"plan:       {plan.Steps.Count} steps, entry {plan.Entry}");
        Console.WriteLine();

        var client = await TemporalClient.ConnectAsync(new(Address));

        await using var worker = new WorkerProcess(
            WorkerDirectory,
            Queue, Address, FixtureDirectory, branch, baseBranch, ticketId, runId, RecordDirectory,
            Path.Combine(ResultsDirectory, $"{runId}-worker.log"));

        var pid = await worker.StartAsync();
        Console.WriteLine($"worker:     pid={pid}");
        Console.WriteLine();

        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(runId, Queue));

        Console.WriteLine($"workflow:   {runId} started");
        Console.WriteLine();

        // Every decision records its actor. Without --human-approvers the actor
        // is the harness, which is deliberately unable to satisfy human policy
        // or floor F1 even though it may route the workflow through a gate.
        //
        // A gate is approved only while the workflow is waiting at it (it is a
        // cursor), and once per arrival. Approving every gate on every poll
        // recorded a merge approval before the first step had run, so the
        // merge authorisation passed by construction.
        var decisionLedger = new DecisionLedger(RecordDirectory, runId);
        var approvedAt = new Dictionary<string, int>(StringComparer.Ordinal);
        var approvals = Task.Run(async () =>
        {
            for (var i = 0; i < 600; i++)
            {
                await Task.Delay(2000);
                try
                {
                    var state = await handle.QueryAsync(w => w.State());
                    if (state.TerminalStatus is not null)
                        return;

                    foreach (var step in state.Plan.Steps.Where(s => s.Gate is not null && state.Cursors.Contains(s.Id)))
                    {
                        if (approvedAt.TryGetValue(step.Id, out var seen) && seen == state.StepsExecuted)
                            continue;
                        approvedAt[step.Id] = state.StepsExecuted;

                        if (humanApprovers.Length == 0)
                        {
                            decisionLedger.Record(new DecisionRecord(
                                step.Id,
                                step.Gate!.DecisionType,
                                Outcomes.Approved,
                                new DecisionActor(
                                    DecisionActorKind.Harness,
                                    "real-harness",
                                    Array.Empty<string>()),
                                DateTimeOffset.UtcNow));
                        }
                        else
                        {
                            foreach (var identity in humanApprovers)
                            {
                                var human = new DecisionActor(
                                    DecisionActorKind.Human,
                                    identity,
                                    new[] { "approver" });
                                decisionLedger.Record(new DecisionRecord(
                                    step.Id,
                                    step.Gate!.DecisionType,
                                    Outcomes.Approved,
                                    human,
                                    DateTimeOffset.UtcNow));

                                if (step.Id == "gate_merge" &&
                                    string.Equals(baseBranch, "main", StringComparison.OrdinalIgnoreCase))
                                {
                                    decisionLedger.Record(new DecisionRecord(
                                        step.Id,
                                        OrganisationConfiguration.DefaultBranchMergeDecision,
                                        Outcomes.Approved,
                                        human,
                                        DateTimeOffset.UtcNow));
                                }
                            }
                        }

                        await handle.ExecuteUpdateAsync(w => w.SubmitDecisionAsync(
                            new StepDecision(step.Id, Outcomes.Approved)));
                        var actor = humanApprovers.Length == 0
                            ? "harness:real-harness"
                            : $"human:{string.Join(",human:", humanApprovers)}";
                        Console.WriteLine($"  gate {step.Id} approved ({step.Gate!.DecisionType}) actor={actor}");
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"  gate poll: {ex.GetType().Name}");
                }
            }
        });

        string result;
        try
        {
            result = await handle.GetResultAsync<string>(
                followRuns: true,
                new RpcOptions { Timeout = TimeSpan.FromMinutes(30) });
        }
        catch (Exception ex)
        {
            result = $"threw {ex.GetType().Name}: {ex.Message}";
        }

        await approvals;

        var final = await handle.QueryAsync(w => w.State());
        Console.WriteLine();
        Console.WriteLine($"result:          {result}");
        Console.WriteLine($"steps executed:  {final.StepsExecuted}");
        Console.WriteLine($"continue-as-new: {final.ContinueAsNewCount}");
        Console.WriteLine();

        Console.WriteLine("step outcomes:");
        foreach (var (id, record) in final.StepOutcomes.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {id,-20} {record.Outcome}");
        }

        Console.WriteLine();

        // The exit criterion's second half: the cost of the run is known from
        // the record store.
        var costs = RunContext.ReadCosts(RecordDirectory, runId);
        Console.WriteLine($"cost records:    {costs.Count}");
        var totalCompute = costs.Sum(c => c.ComputeSeconds);
        var totalIn = costs.Where(c => c.InputTokens is not null).Sum(c => c.InputTokens!.Value);
        var totalOut = costs.Where(c => c.OutputTokens is not null).Sum(c => c.OutputTokens!.Value);
        Console.WriteLine($"compute:         {totalCompute:F1}s across {costs.Count} step(s)");
        Console.WriteLine($"tokens:          in={totalIn} out={totalOut}");
        Console.WriteLine();

        return result == "completed" ? 0 : 1;
    }

    private static int Help()
    {
        Console.WriteLine("usage: RealHarness <command> [options]");
        Console.WriteLine();
        Console.WriteLine("  run           one real ticket to a merged PR");
        Console.WriteLine("  checks        the ten checks from docs/08 2.2");
        Console.WriteLine("  insert-after  review 11 C2 patch form measurement");
        Console.WriteLine("  review14      A1, A5 and A6 executable regression checks");
        Console.WriteLine("  cleanup       delete branches and PRs created during verification");
        return 1;
    }

    public static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    public static bool HasFlag(string[] args, string name) => args.Contains(name);

    public static string Fixture => FixtureDirectory;
    public static string Records => RecordDirectory;
    public static string Results => ResultsDirectory;

    /// <summary>Where the real worker project lives, so the harness can start it.</summary>
    public static string WorkerDirectory => Path.Combine(Root, "src", "FeatureFlow.Spike.RealWorker");

    public static void WriteHeader(string title)
    {
        Console.WriteLine("=== " + title + " ===");
        Console.WriteLine();
    }
}
