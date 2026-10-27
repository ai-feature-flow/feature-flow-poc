using Cel;
using Cel.Checker;
using Cel.Interpreter;
using Google.Api.Expr.V1Alpha1;

namespace CelChecks;

/// <summary>
/// Check 4: is a cost limit enforceable?
///
/// 3 found a real static cost estimate, so the primary control is available.
/// There is no built-in "budget" parameter in the exported API, so this check
/// shows (a) the engine-side enforcement that the estimate makes possible, and
/// (b) the 8.5 caps (length 1000, AST nodes 200) working as defence in depth.
/// </summary>
internal static class Check4CostLimit
{
    // docs/09 8.5
    public const int MaxExpressionLength = 1000;
    public const int MaxAstNodes = 200;

    public static void Run()
    {
        Console.WriteLine("=== CHECK 4: cost limit enforceable ===");
        Console.WriteLine();

        var env = RestrictedEnv.NewEngineEnv();

        // ---- 4a: is there a built-in budget/limit parameter? ----
        Console.WriteLine("-- 4a: is there a built-in cost limit in the exported API? --");
        var asm = typeof(Env).Assembly;
        var found = asm.GetExportedTypes()
            .SelectMany(t => t.GetMembers(System.Reflection.BindingFlags.Public |
                                          System.Reflection.BindingFlags.Instance |
                                          System.Reflection.BindingFlags.Static |
                                          System.Reflection.BindingFlags.DeclaredOnly))
            .Where(m => m.Name.Contains("Cost", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Budget", StringComparison.OrdinalIgnoreCase)
                        || m.Name.Contains("Limit", StringComparison.OrdinalIgnoreCase))
            .Select(m => $"{m.DeclaringType!.FullName}.{m.Name}")
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Console.WriteLine($"  members matching Cost/Budget/Limit: {found.Count}");
        foreach (var f in found) Console.WriteLine($"    {f}");
        Console.WriteLine("  -> none of these is a 'reject when over budget' knob; the limit must be");
        Console.WriteLine("     applied by the engine against Cost.Max. Demonstrated in 4b.");
        Console.WriteLine();

        // ---- 4b: the engine's load-time gate, using the static estimate ----
        Console.WriteLine("-- 4b: engine-enforced cost limit, using Cel.Cel.EstimateCost --");
        Console.WriteLine($"  budget: Cost.Max <= {CostBudget}");
        Console.WriteLine();

        var budgetCases = new[]
        {
            ("findings.blocking_count > 0", "small guard"),
            ("size(run.repositories) > 1 && findings.open_count < 10", "typical route guard"),
            (string.Join(" + ", Enumerable.Range(0, 40).Select(i => $"params.k{i:D2}")), "40-term concatenation"),
            (string.Join(" + ", Enumerable.Range(0, 400).Select(i => $"params.k{i:D3}")), "400-term concatenation"),
            (string.Join(" && ", Enumerable.Range(0, 300).Select(i => $"params.k{i:D3} == {i}")), "300-conjunction guard")
        };

        foreach (var (expr, label) in budgetCases)
        {
            Console.WriteLine($"  [{label}]  len={expr.Length}");
            try
            {
                var cost = Check3CostEstimate.Estimate(env, expr);
                var nodes = CountAstNodes(Check3CostEstimate.Compile(env, expr).ast);
                var over = cost!.Max > CostBudget;
                Console.WriteLine($"      Cost = min={cost.Min} max={cost.Max}   AST nodes = {nodes}");
                Console.WriteLine($"      gate: {(over
                    ? $"REJECTED -- cost max {cost.Max} > budget {CostBudget}"
                    : $"ACCEPTED -- cost max {cost.Max} <= budget {CostBudget}")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"      FAILED: {ex.GetType().Name}: {FirstLine(ex.Message)}");
            }

            Console.WriteLine();
        }

        // ---- 4c: the 8.5 caps as the fallback control ----
        Console.WriteLine("-- 4c: 8.5 caps -- length <= 1000 chars, AST nodes <= 200 --");
        Console.WriteLine();

        var capCases = new[]
        {
            "findings.blocking_count > 0",
            "size(run.repositories) > 1 && findings.open_count < 10",
            string.Join(" + ", Enumerable.Range(0, 60).Select(i => $"params.k{i:D2}")),   // ~777 chars, 179 nodes
            string.Join(" + ", Enumerable.Range(0, 80).Select(i => $"params.k{i:D2}")),   // over 1000 chars
            string.Join(" && ", Enumerable.Range(0, 40).Select(i => $"params.k{i:D2} == {i}")), // many nodes
            string.Join(" + ", Enumerable.Range(0, 400).Select(i => $"params.k{i:D3}"))
        };

        Console.WriteLine("  " + "expression".PadRight(46) + "len".PadLeft(5) + "nodes".PadLeft(7) + "  gate");
        Console.WriteLine("  " + new string('-', 92));
        foreach (var expr in capCases)
        {
            var shown = expr.Length > 44 ? expr[..41] + "..." : expr;
            try
            {
                var (_, ast) = Check3CostEstimate.Compile(env, expr);
                var nodes = CountAstNodes(ast);
                var gate = Gate(expr, nodes);
                Console.WriteLine($"  {shown,-46} {expr.Length,5} {nodes,6}  {gate,-28}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  {shown,-46} {expr.Length,5} {"-",6}  COMPILE FAILED: {FirstLine(ex.Message)}");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  Note: the caps are checked on the source text (length) and on the parsed");
        Console.WriteLine("  and checked AST (node count), which is exactly the load-time position 8.5 asks for.");
        Console.WriteLine();

        // ---- 4d: runtime interrupt, if the static estimate is ever not enough ----
        RuntimeInterrupt(env);
    }

    /// <summary>
    /// The budget the engine would set. Chosen so the ordinary guards in docs/13
    /// sit far below it and the pathological ones sit above it.
    /// </summary>
    private const long CostBudget = 100;

    private static string Gate(string expr, long nodes)
    {
        if (expr.Length > MaxExpressionLength) return $"REJECTED: length {expr.Length} > {MaxExpressionLength}";
        if (nodes > MaxAstNodes) return $"REJECTED: nodes {nodes} > {MaxAstNodes}";
        return "ACCEPTED";
    }

    /// <summary>
    /// Counts AST nodes by walking the checked Expr tree.
    /// </summary>
    public static long CountAstNodes(Ast ast)
    {
        return Count(ast.Expr) + 1; // +1 for the root
    }

    private static long Count(Expr e)
    {
        long n = 0;
        switch (e.ExprKindCase)
        {
            case Expr.ExprKindOneofCase.ConstExpr:
                n += 1;
                break;
            case Expr.ExprKindOneofCase.IdentExpr:
                n += 1;
                break;
            case Expr.ExprKindOneofCase.SelectExpr:
                n += 1 + Count(e.SelectExpr.Operand);
                break;
            case Expr.ExprKindOneofCase.CallExpr:
                n += 1;
                if (e.CallExpr.Target is not null) n += Count(e.CallExpr.Target);
                foreach (var a in e.CallExpr.Args) n += Count(a);
                break;
            case Expr.ExprKindOneofCase.ListExpr:
                n += 1;
                foreach (var a in e.ListExpr.Elements) n += Count(a);
                break;
            case Expr.ExprKindOneofCase.StructExpr:
                n += 1;
                foreach (var entry in e.StructExpr.Entries)
                {
                    switch (entry.KeyKindCase)
                    {
                        case Google.Api.Expr.V1Alpha1.Expr.Types.CreateStruct.Types.Entry.KeyKindOneofCase.MapKey:
                            n += 1 + Count(entry.MapKey);
                            break;
                        case Google.Api.Expr.V1Alpha1.Expr.Types.CreateStruct.Types.Entry.KeyKindOneofCase.FieldKey:
                            n += 1;
                            break;
                    }

                    n += Count(entry.Value);
                }

                break;
            case Expr.ExprKindOneofCase.ComprehensionExpr:
                n += 1 + Count(e.ComprehensionExpr.IterRange)
                       + Count(e.ComprehensionExpr.AccuInit)
                       + Count(e.ComprehensionExpr.LoopCondition)
                       + Count(e.ComprehensionExpr.LoopStep)
                       + Count(e.ComprehensionExpr.Result);
                break;
        }

        return n;
    }

    /// <summary>
    /// 4d: a hard stop at evaluation time, using the EvalObserver decorator.
    /// This is what covers the case where the static estimate is Unknown.
    /// </summary>
    private static void RuntimeInterrupt(Env env)
    {
        Console.WriteLine("-- 4d: runtime step ceiling via ProgramOptions.CustomDecorator(DecObserveEval(...)) --");
        try
        {
            var expr = string.Join(" + ", Enumerable.Range(0, 400).Select(i => $"params.k{i:D3}"));
            var (_, ast) = Check3CostEstimate.Compile(env, expr);

            const int stepCeiling = 50;
            int steps = 0;
            bool tripped = false;
            var prog = (Prog)env.Program(ast,
                ProgramOptions.CustomDecorator(
                    Cel.Interpreter.InterpretableDecoratorUtils.DecObserveEval((id, _) =>
                    {
                        steps++;
                        if (steps > stepCeiling && !tripped)
                        {
                            tripped = true;
                            throw new CelStepCeilingException(stepCeiling, steps);
                        }
                    })));

            var args = new Dictionary<string, object>
            {
                ["params"] = Enumerable.Range(0, 400).ToDictionary(i => $"k{i:D3}", i => (object)"v"),
                ["run"] = new Dictionary<string, object>(),
                ["steps"] = new Dictionary<string, object>(),
                ["findings"] = new Dictionary<string, object>()
            };

            try
            {
                var r = prog.Eval(args);
                Console.WriteLine($"  expression: 400-term concatenation (len={expr.Length})");
                Console.WriteLine($"  step ceiling: {stepCeiling}");
                Console.WriteLine($"  observer fired {steps} times; ceiling tripped: {tripped}");
                Console.WriteLine($"  Eval() returned normally: type={r.Val.Value()?.GetType().FullName} len={(r.Val.Value() as string)?.Length}");
                Console.WriteLine("  -> the observer CANNOT abort evaluation: Cel.NET catches exceptions thrown");
                Console.WriteLine("     from the EvalObserver delegate and evaluation completes anyway.");
                Console.WriteLine("     A runtime step ceiling is therefore NOT available via this surface.");
                Console.WriteLine("     The static cost estimate in 4b is the only working enforcement point.");
            }
            catch (CelStepCeilingException ex)
            {
                Console.WriteLine($"  expression: 400-term concatenation (len={expr.Length})");
                Console.WriteLine($"  step ceiling: {stepCeiling}");
                Console.WriteLine("  result:     REJECTED AT RUNTIME");
                Console.WriteLine($"  exception:  {ex.GetType().FullName}");
                Console.WriteLine($"  message:    {ex.Message}");
                Console.WriteLine($"  observed eval steps before abort: {steps}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  other exception: {ex.GetType().FullName}: {FirstLine(ex.Message)} (steps={steps})");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  4d setup FAILED: {ex.GetType().Name}: {FirstLine(ex.Message)}");
        }

        Console.WriteLine();
    }

    private static string FirstLine(string s) => s.Replace("\r\n", " ").Split('\n')[0];
}

/// <summary>
/// Thrown by the 4d observer when the evaluation exceeds a step ceiling.
/// </summary>
internal sealed class CelStepCeilingException : Exception
{
    public CelStepCeilingException(int ceiling, int steps)
        : base($"evaluation step ceiling ({ceiling}) exceeded")
    {
        Ceiling = ceiling;
        Steps = steps;
    }

    public int Ceiling { get; }

    public int Steps { get; }
}
