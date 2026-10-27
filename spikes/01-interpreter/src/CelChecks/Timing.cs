using System.Diagnostics;
using Cel;
using Cel.Checker;
using Cel.Tools;
using Google.Api.Expr.V1Alpha1;

namespace CelChecks;

/// <summary>
/// Cold start and warm evaluation timings, plus the resolved package facts.
/// </summary>
internal static class Timing
{
    private const int WarmIterations = 100_000;

    public static void Run()
    {
        Console.WriteLine("=== TIMING ===");
        Console.WriteLine();

        // ---- cold start: process-level first evaluation ----
        Console.WriteLine("-- cold start: first Env + parse + check + plan + eval of the process --");
        var sw = Stopwatch.StartNew();
        var env = RestrictedEnv.NewEngineEnv();
        var envMs = sw.Elapsed.TotalMilliseconds;

        const string expr = "findings.blocking_count == 0 && size(run.repositories) > 1";
        var args = new Dictionary<string, object>
        {
            ["params"] = new Dictionary<string, object> { ["ticket_id"] = "TICKET-1234" },
            ["run"] = new Dictionary<string, object>
            {
                ["repositories"] = new[] { "svc-alpha", "svc-beta", "svc-gamma" }
            },
            ["steps"] = new Dictionary<string, object>(),
            ["findings"] = new Dictionary<string, object> { ["blocking_count"] = 0L, ["open_count"] = 7L }
        };

        sw.Restart();
        var (prog, _) = Check3CostEstimate.Compile(env, expr);
        var compileMs = sw.Elapsed.TotalMilliseconds;

        sw.Restart();
        var firstResult = prog.Eval(args).Val.Value();
        var firstEvalMs = sw.Elapsed.TotalMilliseconds;

        Console.WriteLine($"  Env.NewCustomEnv(StdLib, ClearMacros, Declarations): {envMs,9:F3} ms");
        Console.WriteLine($"  parse + check + plan (\"{expr}\"): {compileMs,9:F3} ms");
        Console.WriteLine($"  first Eval():                          {firstEvalMs,9:F3} ms  -> {firstResult}");
        Console.WriteLine($"  TOTAL to first evaluation:             {envMs + compileMs + firstEvalMs,9:F3} ms");
        Console.WriteLine();

        // Second time round, in the same process, so JIT and static init are separated out.
        Console.WriteLine("-- second time round in the same process (JIT/static init already paid) --");
        sw.Restart();
        var env2 = RestrictedEnv.NewEngineEnv();
        var env2Ms = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var (prog2, _) = Check3CostEstimate.Compile(env2, expr);
        var compile2Ms = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        prog2.Eval(args);
        var eval2Ms = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  2nd Env.NewCustomEnv:   {env2Ms,9:F3} ms");
        Console.WriteLine($"  2nd parse+check+plan:   {compile2Ms,9:F3} ms");
        Console.WriteLine($"  2nd Eval:               {eval2Ms,9:F3} ms");
        Console.WriteLine($"  2nd TOTAL:              {env2Ms + compile2Ms + eval2Ms,9:F3} ms");
        Console.WriteLine();
        Console.WriteLine("  Reading: the first column is dominated by one-off JIT and static initialisation");
        Console.WriteLine("  (Antlr4 parser tables, Protobuf reflection, the standard-library overload tables).");
        Console.WriteLine("  The steady-state column is what a warm workflow worker pays.");
        Console.WriteLine();

        // ---- cold start per ScriptHost, for comparison with the README path ----
        sw.Restart();
        var host = ScriptHost.NewBuilder().Build();
        var script = host.BuildScript(expr).WithDeclarations(RestrictedEnv.Declarations()).Build();
        var hostFirst = script.Execute<object?>(args);
        var hostTotalMs = sw.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  ScriptHost path (build host + build script + first Execute): {hostTotalMs,9:F3} ms -> {hostFirst}");
        Console.WriteLine();

        // ---- warm evaluation ----
        Console.WriteLine($"-- warm: {WarmIterations:N0} evaluations of the same compiled program --");
        var samples = new double[WarmIterations];
        for (int i = 0; i < WarmIterations; i++)
        {
            sw.Restart();
            prog.Eval(args);
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Report("Eval (Env/Prog path)", samples);

        // warm via ScriptHost
        var hostSamples = new double[WarmIterations];
        for (int i = 0; i < WarmIterations; i++)
        {
            sw.Restart();
            script.Execute<object?>(args);
            hostSamples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Report("Execute (ScriptHost path)", hostSamples);

        // ---- warm compile, because load happens per workflow start ----
        Console.WriteLine("-- warm compile: 10000 parse+check+plan of the same expression --");
        var compileSamples = new double[10_000];
        for (int i = 0; i < compileSamples.Length; i++)
        {
            sw.Restart();
            Check3CostEstimate.Compile(env, expr);
            compileSamples[i] = sw.Elapsed.TotalMilliseconds;
        }

        Report("parse+check+plan", compileSamples);
        Console.WriteLine();
    }

    private static void Report(string label, double[] samples)
    {
        Array.Sort(samples);
        var mean = samples.Average();
        var p50 = samples[(int)(samples.Length * 0.50)];
        var p95 = samples[(int)(samples.Length * 0.95)];
        var p99 = samples[(int)(samples.Length * 0.99)];
        Console.WriteLine($"  {label}");
        Console.WriteLine($"      n={samples.Length,7}  mean={mean,8:F4} ms ({mean * 1000,8:F2} us)" +
                          $"  p50={p50 * 1000,7:F2} us  p95={p95 * 1000,7:F2} us  p99={p99 * 1000,7:F2} us  max={samples[^1] * 1000,8:F2} us");
    }
}
