using FeatureFlow.Spike3;
using Temporalio.Client;

var command = args.Length > 0 ? args[0] : "help";
var rest = args.Skip(1).ToArray();

async Task<ITemporalClient> Connect() =>
    await TemporalClient.ConnectAsync(new(Cli.Arg(rest, "--address") ?? Cli.DefaultAddress));

return command switch
{
    "worker" => await WorkerCommand.RunAsync(rest),
    "replay-spike1" => await Sanity.ReplaySpike1Async(),
    "sanity-lock" => await Sanity.LockAsync(await Connect()),
    "sanity-conflict" => await ConflictSanity.RunAsync(),
    "i3" => await Sanity.I3Async(await Connect()),
    "i4" => await Sanity.I4Async(await Connect()),
    "epic-run" => await EpicRun.RunAsync(await Connect(), rest),
    "oracle" => await Oracles.RunAsync(rest),
    "r4" => await R4Series.RunAsync(rest),
    "presubmit" => await Presubmit.RunAsync(rest),
    "c5" => await C5Run.RunAsync(await Connect(), rest),
    _ => Help(),
};

static int Help()
{
    Console.WriteLine("""
        spike3 <command>
          worker --kind epic|ticket --queue Q --repo DIR --branch B --base I --ticket T --run-id R --records DIR
          replay-spike1                 Spike 1's recorded histories against the changed interpreter
          sanity-lock                   three dummy holders, locked then bypassed
          sanity-conflict               A+C conflict and B+C clean through MergeBaseForwardAsync
          i3 | i4                       no-agent injections
          epic-run --suffix S           the main run, with I1, I2 and simultaneous approval
          oracle --suffix S             the section 3.4 oracles on the integration branch
          r4 --series NAME --diff F --spec epic|A|B|C --passes N
          presubmit --worktree DIR --plan P --step review [--base-override REF] [--omit-prior] [--readiness]
          c5 --suffix S                 the C5 parity runs
        """);
    return 1;
}
