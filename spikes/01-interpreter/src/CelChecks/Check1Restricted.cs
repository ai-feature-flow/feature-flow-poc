using Cel;
using Cel.Checker;
using Cel.Tools;
using Google.Api.Expr.V1Alpha1;

namespace CelChecks;

/// <summary>
/// Check 1: the checker rejects an unregistered function and an unknown variable.
/// </summary>
internal static class Check1Restricted
{
    public static void Run()
    {
        Console.WriteLine("=== CHECK 1: restricted checker ===");
        Console.WriteLine();

        var host = ScriptHost.NewBuilder().Build();
        var decls = RestrictedEnv.Declarations();

        Console.WriteLine("-- registered declarations --");
        foreach (var d in decls) Console.WriteLine($"   {d.Name}");
        Console.WriteLine();

        // --- 1a: an expression using only what is registered must be accepted ---
        Try(host, decls, "findings.blocking_count > 0", "1a control: allowed variable + operator");

        // --- 1b: an unregistered function ---
        Try(host, decls, "httpGet('https://example.com')", "1b unregistered function 'httpGet'");

        // --- 1c: a function that exists in CEL's standard library but that we did not declare ---
        Try(host, decls, "size(run.repositories) > 1", "1c stdlib function 'size' (declared vars)");

        // --- 1d: an unknown variable name ---
        Try(host, decls, "manifest.repositories.size() > 0", "1d unknown variable 'manifest'");

        // --- 1e: a known variable, unknown field (dyn map, so this is a runtime concern) ---
        Try(host, decls, "run.nonexistent_field == 1", "1e known var, unknown field");

        // --- 1f: an unregistered stdlib function invoked on a declared var ---
        Try(host, decls, "params.ticket_id.matches('^TICKET-[0-9]+$')", "1f 'matches' as a method call");

        // --- 1g: comprehension macro (not registered) ---
        Try(host, decls, "run.repositories.all(r, r.startsWith('svc-'))", "1g comprehension macro 'all'");

        Console.WriteLine();
        StrictEnv();
    }

    /// <summary>
    /// Two postures the engine could adopt, both built with Env.NewCustomEnv so that
    /// the stdlib is opt-in rather than automatic.
    /// </summary>
    private static void StrictEnv()
    {
        // ---- Posture A: stdlib functions, macros (comprehensions) disabled, only declared vars ----
        Console.WriteLine("-- 1h POSTURE A: Env.NewCustomEnv(StdLib(), ClearMacros(), Declarations(vars)) --");
        Console.WriteLine("   variables:  params, steps, run, findings");
        Console.WriteLine("   functions:  the Cel.NET standard library");
        Console.WriteLine("   macros:     none (ClearMacros()) -- 8.4 says no comprehensions");
        Console.WriteLine();

        var envA = Env.NewCustomEnv(
            LibraryOptions.StdLib(),
            EnvOptions.ClearMacros(),
            EnvOptions.Declarations(RestrictedEnv.Declarations()));

        TryEnv(envA, "size(run.repositories) > 1", "1h-1 control: allowed function 'size'");
        TryEnv(envA, "params.ticket_id.startsWith('TICKET-')", "1h-2 control: allowed function 'startsWith'");
        TryEnv(envA, "params.ticket_id.matches('^TICKET-[0-9]+$')", "1h-3 allowed function 'matches'");
        TryEnv(envA, "httpGet('https://example.com')", "1h-4 UNREGISTERED function 'httpGet' -> must be rejected");
        TryEnv(envA, "run.repositories.all(r, r.startsWith('svc-'))", "1h-5 comprehension 'all' (macros off) -> must be rejected");
        TryEnv(envA, "manifest.repositories.size() > 0", "1h-6 UNDECLARED variable 'manifest' -> must be rejected");
        TryEnv(envA, "timestamp('2026-01-01T00:00:00Z') > timestamp(0)", "1h-7 stdlib 'timestamp' -- NOT in 8.4, so a gap");
        TryEnv(envA, "run.nonexistent_field == 1", "1h-8 declared var, undeclared field (dyn map)");

        // ---- Posture B: nothing but what we declare. Proves the whitelist is real. ----
        Console.WriteLine("-- 1h POSTURE B: Env.NewCustomEnv(Declarations(...)) with no stdlib at all --");
        Console.WriteLine("   Only the four variables plus four hand-declared functions. No stdlib, no macros.");
        Console.WriteLine();

        var envB = Env.NewCustomEnv(
            EnvOptions.ClearMacros(),
            EnvOptions.Declarations(WhitelistDeclarations()));

        TryEnv(envB, "size('abc') > 1", "1h-9  declared function 'size' -> must be accepted");
        TryEnv(envB, "startsWith(params.ticket_id, 'TICKET')", "1h-10 declared function 'startsWith' -> must be accepted");
        TryEnv(envB, "size('abc') == 3 && startsWith('abc', 'a')", "1h-11 declared funcs + '&&'/'==' -> operators come from stdlib");
        TryEnv(envB, "'abc'.matches('a.*')", "1h-12 UNDECLARED function 'matches' -> must be rejected");
        TryEnv(envB, "timestamp(0)", "1h-13 UNDECLARED function 'timestamp' -> must be rejected");
        TryEnv(envB, "httpGet('x')", "1h-14 UNDECLARED function 'httpGet' -> must be rejected");
        TryEnv(envB, "manifest.x", "1h-15 UNDECLARED variable 'manifest' -> must be rejected");
        Console.WriteLine();

        WhitelistEnv();
    }

    /// <summary>
    /// Posture C, and the one the engine should adopt: Cel.NET's own stdlib decls
    /// filtered to the 8.4 allow-list, plus the four variable namespaces, macros off.
    /// </summary>
    private static void WhitelistEnv()
    {
        Console.WriteLine("-- 1i POSTURE C: stdlib decl protos filtered to the docs/09 8.4 allow-list --");
        var std = Cel.Checker.Checker.StandardDeclarations;
        var kept = std.Where(d => RestrictedEnv.AllowedNames.Contains(d.Name)).ToList();
        Console.WriteLine($"   kept {kept.Count} of {std.Count} stdlib declarations, macros off,");
        Console.WriteLine($"   variables: params, steps, run, findings");
        Console.WriteLine();

        var env = RestrictedEnv.NewWhitelistEnv();

        Console.WriteLine("   -- must be ACCEPTED (8.4 allows these) --");
        TryEnv(env, "1 + 1", "1i-1 arithmetic operator");
        TryEnv(env, "1 < 2", "1i-2 comparison operator");
        TryEnv(env, "true && false", "1i-3 boolean operator");
        TryEnv(env, "findings.blocking_count > 0", "1i-4 findings namespace");
        TryEnv(env, "size(run.repositories) > 1", "1i-5 size()");
        TryEnv(env, "params.ticket_id.startsWith('TICKET-')", "1i-6 startsWith()");
        TryEnv(env, "params.ticket_id.endsWith('-1')", "1i-7 endsWith()");
        TryEnv(env, "params.ticket_id.matches('^TICKET-[0-9]+$')", "1i-8 matches()");
        TryEnv(env, "params.ticket_id.contains('TICKET')", "1i-9 contains()");

        Console.WriteLine("   -- must be REJECTED (not in 8.4) --");
        TryEnv(env, "lower('ABC')", "1i-10 'lower' -- NOT declared by Cel.NET at all");
        TryEnv(env, "upper('abc')", "1i-11 'upper' -- NOT declared by Cel.NET at all");
        TryEnv(env, "timestamp(0)", "1i-12 'timestamp' (clock-adjacent)");
        TryEnv(env, "duration('1s')", "1i-13 'duration'");
        TryEnv(env, "dyn(1)", "1i-14 'dyn'");
        TryEnv(env, "int('1')", "1i-15 'int'");
        TryEnv(env, "now()", "1i-16 'now' (a clock, if it existed)");
        TryEnv(env, "random()", "1i-17 'random'");
        TryEnv(env, "httpGet('https://example.com')", "1i-18 'httpGet' (I/O)");
        TryEnv(env, "manifest.repositories.size() > 0", "1i-19 'manifest' namespace (8.3 says it does not exist)");
        TryEnv(env, "run.repositories.all(r, r.startsWith('svc-'))", "1i-20 comprehension 'all' (8.4: no comprehensions)");
        TryEnv(env, "run.repositories.exists(r, r == 'x')", "1i-21 comprehension 'exists'");
        TryEnv(env, "run.repositories.map(r, r + '!')", "1i-22 comprehension 'map'");
        Console.WriteLine();
    }

    /// <summary>
    /// Hand-declared whitelist: the four variables plus a few of the 8.4 functions.
    /// Deliberately incomplete, so that the rejections in 1h-12..15 prove the point.
    /// </summary>
    private static IList<Decl> WhitelistDeclarations()
    {
        var dynMap = Decls.NewMapType(Decls.String, Decls.Dyn);

        return new List<Decl>
        {
            Decls.NewVar("params", dynMap),
            Decls.NewVar("steps", dynMap),
            Decls.NewVar("run", dynMap),
            Decls.NewVar("findings", Decls.NewMapType(Decls.String, Decls.Int)),

            Decls.NewFunction("size",
                Decls.NewOverload("size_string", new[] { Decls.String }, Decls.Int)),
            Decls.NewFunction("contains",
                Decls.NewOverload("contains_string", new[] { Decls.String, Decls.String }, Decls.Bool)),
            Decls.NewFunction("startsWith",
                Decls.NewOverload("startsWith_string", new[] { Decls.String, Decls.String }, Decls.Bool)),
            Decls.NewFunction("endsWith",
                Decls.NewOverload("endsWith_string", new[] { Decls.String, Decls.String }, Decls.Bool))
        };
    }

    /// <summary>
    /// Only the functions in docs/09 8.4 that take declared arguments. The
    /// comparison/arithmetic/boolean operators are built in to CEL and cannot be
    /// unregistered; they are covered by 8.5's "impossible" row.
    /// </summary>
    private static IList<Decl> StrictDeclarations()
    {
        var dynMap = Decls.NewMapType(Decls.String, Decls.Dyn);
        var strList = Decls.NewListType(Decls.String);

        var decls = new List<Decl>
        {
            Decls.NewVar("params", dynMap),
            Decls.NewVar("steps", dynMap),
            Decls.NewVar("run", dynMap),
            Decls.NewVar("findings", Decls.NewMapType(Decls.String, Decls.Int)),

            // 8.4 allowed functions, explicitly registered
            Decls.NewFunction("size",
                Decls.NewOverload("size_string", new[] { Decls.String }, Decls.Int),
                Decls.NewOverload("size_list", new[] { strList }, Decls.Int),
                Decls.NewOverload("size_map", new[] { dynMap }, Decls.Int)),
            Decls.NewFunction("contains",
                Decls.NewOverload("contains_string", new[] { Decls.String, Decls.String }, Decls.Bool)),
            Decls.NewFunction("startsWith",
                Decls.NewOverload("startsWith_string", new[] { Decls.String, Decls.String }, Decls.Bool)),
            Decls.NewFunction("endsWith",
                Decls.NewOverload("endsWith_string", new[] { Decls.String, Decls.String }, Decls.Bool))
        };
        return decls;
    }

    private static void TryEnv(Env env, string expr, string label)
    {
        Console.WriteLine($"[{label}]");
        Console.WriteLine($"  expression: {expr}");
        try
        {
            var (_, ast) = Check3CostEstimate.Compile(env, expr);
            Console.WriteLine("  result:     ACCEPTED (parse+check+plan all passed)");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  result:     REJECTED");
            Console.WriteLine($"  exception:  {ex.GetType().FullName}");
            Console.WriteLine($"  message:    {FirstLines(ex.Message, 4)}");
        }

        Console.WriteLine();
    }

    private static void Try(ScriptHost host, IList<Decl> decls, string expr, string label)
    {
        Console.WriteLine($"[{label}]");
        Console.WriteLine($"  expression: {expr}");
        try
        {
            var script = host.BuildScript(expr)
                .WithDeclarations(decls)
                .Build();
            Console.WriteLine("  result:     ACCEPTED (script built)");
            // Try evaluating to show it got past the checker.
            try
            {
                var args = new Dictionary<string, object>
                {
                    ["params"] = new Dictionary<string, object> { ["ticket_id"] = "TICKET-1" },
                    ["run"] = new Dictionary<string, object> { ["repositories"] = new[] { "svc-a", "svc-b" } },
                    ["steps"] = new Dictionary<string, object>(),
                    ["findings"] = new Dictionary<string, object> { ["blocking_count"] = 2L, ["open_count"] = 5L }
                };
                var v = script.Execute<object?>(args);
                Console.WriteLine($"  evaluated:  {v}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  accepted at check time, FAILED at eval: {ex.GetType().FullName}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  result:     REJECTED");
            Console.WriteLine($"  exception:  {ex.GetType().FullName}");
            Console.WriteLine($"  message:    {FirstLines(ex.Message, 6)}");
            if (ex is ScriptCreateException sce && sce.Issues is not null)
            {
                foreach (var e in sce.Issues.Errors)
                    Console.WriteLine($"  issue:      {e}");
            }
        }

        Console.WriteLine();
    }

    private static string FirstLines(string s, int n)
    {
        var lines = s.Replace("\r\n", "\n").Split('\n');
        return string.Join(" / ", lines.Take(n).Select(l => l.Trim()).Where(l => l.Length > 0));
    }
}
