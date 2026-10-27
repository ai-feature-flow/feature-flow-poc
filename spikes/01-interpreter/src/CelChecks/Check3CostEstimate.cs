using Cel;
using Cel.Checker;
using Cel.Interpreter;
using Google.Api.Expr.V1Alpha1;

namespace CelChecks;

/// <summary>
/// Check 3: does Cel.NET expose a static cost estimate for an expression?
///
/// Method: (a) grep the reflected API dump for a cost surface; (b) actually call it
/// on real compiled programs and report Min/Max.
/// </summary>
internal static class Check3CostEstimate
{
    public static void Run()
    {
        Console.WriteLine("=== CHECK 3: static cost estimation ===");
        Console.WriteLine();

        // ---- 3a: what does reflection say exists? ----
        Console.WriteLine("-- 3a: cost surface present in the exported API --");
        var asm = typeof(Cel.Env).Assembly;
        foreach (var name in new[] { "Cel.Cel", "Cel.Interpreter.Cost", "Cel.Interpreter.ICoster", "Cel.Prog", "Cel.Interpreter.InterpretableUtils" })
        {
            var t = asm.GetType(name);
            Console.WriteLine($"  {name,-38} {(t is null ? "ABSENT" : "PRESENT")}");
        }

        var costType = typeof(Cost);
        Console.WriteLine($"  Cost.Min/Max properties: {costType.GetProperty("Min")}, {costType.GetProperty("Max")}");
        Console.WriteLine($"  Cost.None = {Cost.None}; Cost.Unknown = {Cost.Unknown}; Cost.OneOne = {Cost.OneOne}");
        Console.WriteLine($"  Cel.Cel.EstimateCost(object) static: {asm.GetType("Cel.Cel")!.GetMethod("EstimateCost")}");
        Console.WriteLine();

        // ---- 3b: actually compute a cost for real expressions ----
        Console.WriteLine("-- 3b: measured cost estimates (static, at compile time) --");
        var env = RestrictedEnv.NewEngineEnv();

        var exprs = new[]
        {
            "findings.blocking_count > 0",
            "size(run.repositories) > 1",
            "params.ticket_id.startsWith('TICKET-')",
            "1 + 1",
            "run.repositories[0] + '/' + run.branch",
            "findings.blocking_count == 0 && findings.open_count > 3 && size(run.repositories) > 1",
            // deliberately large: repeated concatenations of a long list
            string.Join(" + ", Enumerable.Range(0, 60).Select(i => $"params.k{i:D2}"))
        };

        foreach (var expr in exprs)
        {
            try
            {
                var (prog, ast) = Compile(env, expr);
                var viaProg = ((Cel.Prog)prog).Cost();
                var viaStatic = global::Cel.Cel.EstimateCost(prog);
                var shown = expr.Length > 70 ? expr[..67] + "..." : expr;
                Console.WriteLine($"  {shown}");
                Console.WriteLine($"      len={expr.Length,4}  Prog.Cost() min={viaProg.Min,6} max={viaProg.Max,6}   Cel.EstimateCost() min={viaStatic.Min,6} max={viaStatic.Max,6}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {expr[..Math.Min(70, expr.Length)]}");
                Console.WriteLine($"      FAILED: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("-- 3c: is the estimate purely static (no input needed)? --");
        Console.WriteLine("  Cost is read from the planned IInterpretable tree (Cel.Prog : ICoster),");
        Console.WriteLine("  before any Eval() call and with no activation. No input data is required.");
        Console.WriteLine();
    }

    /// <summary>
    /// The load-time path the engine would use: compile + check + plan.
    /// </summary>
    public static (IProgram prog, Ast ast) Compile(Env env, string expr)
    {
        var parsed = env.Parse(expr);
        if (parsed.HasIssues()) throw new InvalidOperationException("parse: " + parsed.Issues);
        var checked_ = env.Check(parsed.Ast);
        if (checked_.HasIssues()) throw new InvalidOperationException("check: " + checked_.Issues);
        var prog = env.Program(checked_.Ast);
        return (prog, checked_.Ast);
    }

    /// <summary>
    /// The static cost of an expression, or null if Cel.NET reports Unknown.
    /// </summary>
    public static Cost? Estimate(Env env, string expr)
    {
        var (prog, _) = Compile(env, expr);
        return global::Cel.Cel.EstimateCost(prog);
    }

    private static string FirstLine(string s) => s.Replace("\r\n", " ").Split('\n')[0];
}
