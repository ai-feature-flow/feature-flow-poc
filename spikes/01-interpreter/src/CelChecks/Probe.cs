using Cel;
using Cel.Checker;
using Cel.Common.Types.Ref;
using Google.Api.Expr.V1Alpha1;

namespace CelChecks;

/// <summary>
/// Throwaway probe: what does a bare / custom Env accept before anything is declared?
/// Used to work out the API shape for the strict restricted env in check 1.
/// </summary>
internal static class Probe
{
    public static void Run()
    {
        Console.WriteLine("=== PROBE: baseline Env content ===");

        var bare = Env.NewEnv();
        Probe_("Env.NewEnv() [no options]", bare);

        var custom = Env.NewCustomEnv();
        Probe_("Env.NewCustomEnv() [no options]", custom);

        var customStd = Env.NewCustomEnv(LibraryOptions.StdLib());
        Probe_("Env.NewCustomEnv(StdLib())", customStd);

        var customNoMacros = Env.NewCustomEnv(LibraryOptions.StdLib(), EnvOptions.ClearMacros());
        Probe_("Env.NewCustomEnv(StdLib(), ClearMacros())", customNoMacros);

        Console.WriteLine();
        Console.WriteLine("--- ITypeProvider / registry inspection ---");
        Console.WriteLine($"bare.TypeProvider      = {bare.TypeProvider?.GetType().FullName}");
        Console.WriteLine($"custom.TypeProvider    = {custom.TypeProvider?.GetType().FullName}");

        Console.WriteLine();
        Console.WriteLine("--- functions the standard library registers (via Checker.StandardDeclarations) ---");
        var stdDecls = Cel.Checker.Checker.StandardDeclarations;
        var fnNames = stdDecls.Where(d => d.Name.StartsWith("_") || !d.Name.StartsWith("_"))
            .Select(d => d.Name)
            .Distinct()
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
        Console.WriteLine($"  {fnNames.Count} declarations; functions (non-operator):");
        foreach (var n in fnNames.Where(n => !n.StartsWith("_")))
            Console.WriteLine($"    {n}");
        Console.WriteLine();
        Console.WriteLine("  operators (the '_x_' names): " +
                          string.Join(" ", fnNames.Where(n => n.StartsWith("_"))));

        Console.WriteLine();
        Console.WriteLine("--- clock / randomness / I/O reachability under StdLib ---");
        var dangerous = new[]
        {
            "now()", "random()", "rand()", "uuid()", "currentTime()",
            "timestamp(0) > timestamp(1)", "duration('1s')", "getDate(0)",
            "getFullYear(0)", "math.greatest(1, 2)", "math.least(1, 2)",
            "'a'.format([1])", "dyn(1)"
        };
        var std = Env.NewCustomEnv(LibraryOptions.StdLib(), EnvOptions.ClearMacros(),
            EnvOptions.Declarations(RestrictedEnv.Declarations()));
        foreach (var expr in dangerous)
        {
            string outcome;
            try
            {
                var p = std.Parse(expr);
                if (p.HasIssues()) outcome = "PARSE FAIL: " + FirstLine(p.Issues.ToString());
                else
                {
                    var c = std.Check(p.Ast);
                    outcome = c.HasIssues() ? "CHECK REJECT: " + FirstLine(c.Issues!.ToString()) : "CHECK ACCEPT";
                }
            }
            catch (Exception ex)
            {
                outcome = "THROW " + ex.GetType().Name;
            }

            Console.WriteLine($"  {expr,-32} -> {outcome}");
        }

        Console.WriteLine();
        Console.WriteLine("--- the stdlib's own declarations for the 8.4 functions (as Decl protos) ---");
        foreach (var d in stdDecls.Where(d => d.Name is "startsWith" or "endsWith" or "matches" or "contains" or "size"))
            Console.WriteLine($"    {d}");
        Console.WriteLine();

        Console.WriteLine();
        Console.WriteLine("--- can the engine declare operators by name to get an exact 8.4 whitelist? ---");
        var strict = Env.NewCustomEnv(
            EnvOptions.ClearMacros(),
            EnvOptions.Declarations(ExactWhitelist()));
        foreach (var expr in new[]
                 {
                     "1 + 1",
                     "1 < 2",
                     "true && false",
                     "1 == 1",
                     "size('abc') == 3",
                     "params.ticket_id.startsWith('TICKET-')",
                     "params.ticket_id.matches('^T')",
                     "contains(params.ticket_id, 'TICKET')",
                     "lower('ABC')",
                     "upper('abc')",
                     "endsWith(params.ticket_id, '-1')",
                     "timestamp(0)",
                     "duration('1s')",
                     "dyn(1)",
                     "int('1')",
                     "now()",
                     "httpGet('x')",
                     "manifest.x"
                 })
        {
            string outcome;
            try
            {
                var p = strict.Parse(expr);
                if (p.HasIssues()) outcome = "PARSE FAIL: " + FirstLine(p.Issues.ToString());
                else
                {
                    var c = strict.Check(p.Ast);
                    outcome = c.HasIssues() ? "REJECT: " + FirstLine(c.Issues!.ToString()) : "ACCEPT";
                }
            }
            catch (Exception ex)
            {
                outcome = "THROW " + ex.GetType().Name + ": " + FirstLine(ex.Message);
            }

            Console.WriteLine($"  {expr,-42} -> {outcome}");
        }

        // Posture C: take the stdlib's OWN declaration protos and keep only the
        // names 8.4 allows. This is the whitelist done by filtering, not by
        // re-declaring, so the overload shapes are exactly Cel.NET's own.
        Console.WriteLine();
        Console.WriteLine("--- POSTURE C: stdlib decl protos filtered to the 8.4 allow-list ---");
        var keep = new HashSet<string>(StringComparer.Ordinal)
        {
            "size", "contains", "startsWith", "endsWith", "matches", "lower", "upper",
            // operators
            "_+_", "_-_", "_*_", "_/_", "_%_", "-_", "!_",
            "_<_", "_<=_", "_>_", "_>=_", "_==_", "_!=_", "_&&_", "_||_", "_?_:_", "_[_]",
            "in", "@in", "@not_strictly_false"
        };
        var filtered = stdDecls.Where(d => keep.Contains(d.Name)).ToList();
        Console.WriteLine($"  kept {filtered.Count} of {stdDecls.Count} stdlib declarations:");
        Console.WriteLine("    " + string.Join(", ", filtered.Select(d => d.Name).OrderBy(x => x, StringComparer.Ordinal)));
        Console.WriteLine();

        var filteredEnv = Env.NewCustomEnv(
            EnvOptions.ClearMacros(),
            EnvOptions.Declarations(RestrictedEnv.Declarations()),
            EnvOptions.Declarations(filtered));

        foreach (var expr in new[]
                 {
                     "1 + 1", "1 < 2", "true && false", "1 == 1", "!true", "-1", "1 % 2",
                     "size('abc') == 3", "size(run.repositories) > 1",
                     "params.ticket_id.startsWith('TICKET-')",
                     "params.ticket_id.matches('^T')",
                     "params.ticket_id.endsWith('-1')",
                     "params.ticket_id.contains('TICKET')",
                     "lower('ABC')", "upper('abc')",
                     "params.ticket_id in ['a', 'b']",
                     "findings.blocking_count > 0",
                     // these must all be rejected
                     "timestamp(0)", "duration('1s')", "dyn(1)", "int('1')", "string(1)",
                     "now()", "random()", "httpGet('x')", "manifest.x",
                     "run.repositories.all(r, r.startsWith('svc-'))"
                 })
        {
            string outcome;
            try
            {
                var p = filteredEnv.Parse(expr);
                if (p.HasIssues()) outcome = "PARSE FAIL: " + FirstLine(p.Issues.ToString());
                else
                {
                    var c = filteredEnv.Check(p.Ast);
                    outcome = c.HasIssues() ? "REJECT: " + FirstLine(c.Issues!.ToString()) : "ACCEPT";
                }
            }
            catch (Exception ex)
            {
                outcome = "THROW " + ex.GetType().Name + ": " + FirstLine(ex.Message);
            }

            Console.WriteLine($"  {expr,-44} -> {outcome}");
        }

        // Posture D: does the StringsLib extension library supply lower/upper, which
        // 8.4 lists but the core stdlib does not declare?
        Console.WriteLine();
        Console.WriteLine("--- POSTURE D: does Cel.Extension.StringsLib supply 'lower'/'upper'? ---");
        var stringsEnv = Env.NewCustomEnv(
            LibraryOptions.StdLib(),
            LibraryOptions.Lib(new Cel.Extension.StringsLib()),
            EnvOptions.ClearMacros(),
            EnvOptions.Declarations(RestrictedEnv.Declarations()));
        foreach (var expr in new[]
                 {
                     "lower('ABC')", "upper('abc')", "lower(params.ticket_id)",
                     "'ABC'.lower()", "'abc'.upper()",
                     "'a,b'.split(',')", "'a b'.replace(' ', '_')",
                     "'  a  '.trim()", "'abc'.join(['1'])", "'abc'.substring(1)"
                 })
        {
            string outcome;
            try
            {
                var p = stringsEnv.Parse(expr);
                if (p.HasIssues()) outcome = "PARSE FAIL: " + FirstLine(p.Issues.ToString());
                else
                {
                    var c = stringsEnv.Check(p.Ast);
                    outcome = c.HasIssues() ? "REJECT: " + FirstLine(c.Issues!.ToString()) : "ACCEPT";
                }
            }
            catch (Exception ex)
            {
                outcome = "THROW " + ex.GetType().Name + ": " + FirstLine(ex.Message);
            }

            Console.WriteLine($"  {expr,-28} -> {outcome}");
        }

        Console.WriteLine();
        var stringsOnly = Env.NewCustomEnv(LibraryOptions.Lib(new Cel.Extension.StringsLib()),
            EnvOptions.Declarations(RestrictedEnv.Declarations()));
        foreach (var expr in new[] { "lower('ABC')", "upper('abc')", "'a,b'.split(',')", "'abc'.replace('b','c')" })
        {
            string outcome;
            try
            {
                var p = stringsOnly.Parse(expr);
                if (p.HasIssues()) outcome = "PARSE FAIL: " + FirstLine(p.Issues.ToString());
                else
                {
                    var c = stringsOnly.Check(p.Ast);
                    outcome = c.HasIssues() ? "REJECT: " + FirstLine(c.Issues!.ToString()) : "ACCEPT";
                }
            }
            catch (Exception ex)
            {
                outcome = "THROW " + ex.GetType().Name + ": " + FirstLine(ex.Message);
            }

            Console.WriteLine($"  [StringsLib only] {expr,-24} -> {outcome}");
        }
    }

    /// <summary>
    /// The exact docs/09 8.4 whitelist: the ten functions plus the operators,
    /// each declared explicitly, with no standard library at all.
    /// </summary>
    private static IList<Decl> ExactWhitelist()
    {
        var dynMap = Decls.NewMapType(Decls.String, Decls.Dyn);
        var decls = new List<Decl>
        {
            Decls.NewVar("params", dynMap),
            Decls.NewVar("steps", dynMap),
            Decls.NewVar("run", dynMap),
            Decls.NewVar("findings", Decls.NewMapType(Decls.String, Decls.Int)),

            // 8.4 functions
            Decls.NewFunction("size",
                Decls.NewOverload("size_string", new[] { Decls.String }, Decls.Int),
                Decls.NewOverload("size_list", new[] { Decls.NewListType(Decls.Dyn) }, Decls.Int),
                Decls.NewOverload("size_map", new[] { Decls.NewMapType(Decls.String, Decls.Dyn) }, Decls.Int)),
            // Dyn-typed only, matching how the engine actually sees these variables
            // (a map(string, dyn) selection yields a dyn). No String-typed twin, so
            // Cel.NET's overlap check does not trip.
            Decls.NewFunction("contains",
                Decls.NewOverload("contains_dyn", new[] { Decls.Dyn, Decls.Dyn }, Decls.Bool)),
            Decls.NewFunction("startsWith",
                Decls.NewOverload("startsWith_dyn", new[] { Decls.Dyn, Decls.Dyn }, Decls.Bool),
                Decls.NewParameterizedInstanceOverload("startsWith_recv", new[] { Decls.Dyn }, Decls.Bool,
                    new List<string> { "A" })),
            Decls.NewFunction("endsWith",
                Decls.NewOverload("endsWith_dyn", new[] { Decls.Dyn, Decls.Dyn }, Decls.Bool),
                Decls.NewParameterizedInstanceOverload("endsWith_recv", new[] { Decls.Dyn }, Decls.Bool,
                    new List<string> { "A" })),
            Decls.NewFunction("matches",
                Decls.NewOverload("matches_dyn", new[] { Decls.Dyn, Decls.Dyn }, Decls.Bool),
                Decls.NewParameterizedInstanceOverload("matches_recv", new[] { Decls.Dyn }, Decls.Bool,
                    new List<string> { "A" })),
            Decls.NewFunction("lower",
                Decls.NewOverload("lower_dyn", new[] { Decls.Dyn }, Decls.String),
                Decls.NewParameterizedInstanceOverload("lower_recv", new List<Google.Api.Expr.V1Alpha1.Type>(),
                    Decls.String, new List<string> { "A" })),
            Decls.NewFunction("upper",
                Decls.NewOverload("upper_dyn", new[] { Decls.Dyn }, Decls.String),
                Decls.NewParameterizedInstanceOverload("upper_recv", new List<Google.Api.Expr.V1Alpha1.Type>(),
                    Decls.String, new List<string> { "A" })),

            // 8.4: "plus arithmetic, comparison and boolean operators"
            Decls.NewFunction("_+_",
                Decls.NewOverload("add_int64", new[] { Decls.Int, Decls.Int }, Decls.Int),
                Decls.NewOverload("add_string", new[] { Decls.String, Decls.String }, Decls.String)),
            Decls.NewFunction("_-_",
                Decls.NewOverload("subtract_int64", new[] { Decls.Int, Decls.Int }, Decls.Int)),
            Decls.NewFunction("_*_",
                Decls.NewOverload("multiply_int64", new[] { Decls.Int, Decls.Int }, Decls.Int)),
            Decls.NewFunction("_/_",
                Decls.NewOverload("divide_int64", new[] { Decls.Int, Decls.Int }, Decls.Int)),
            Decls.NewFunction("_%_",
                Decls.NewOverload("modulo_int64", new[] { Decls.Int, Decls.Int }, Decls.Int)),
            Decls.NewFunction("-<unary>",
                Decls.NewOverload("negate_int64", new[] { Decls.Int }, Decls.Int)),
            Decls.NewFunction("_<_",
                Decls.NewOverload("less_int64", new[] { Decls.Int, Decls.Int }, Decls.Bool)),
            Decls.NewFunction("_<=_",
                Decls.NewOverload("less_equals_int64", new[] { Decls.Int, Decls.Int }, Decls.Bool)),
            Decls.NewFunction("_>_",
                Decls.NewOverload("greater_int64", new[] { Decls.Int, Decls.Int }, Decls.Bool)),
            Decls.NewFunction("_>=_",
                Decls.NewOverload("greater_equals_int64", new[] { Decls.Int, Decls.Int }, Decls.Bool)),
            Decls.NewFunction("_==_",
                Decls.NewOverload("equals", new[] { Decls.Dyn, Decls.Dyn }, Decls.Bool)),
            Decls.NewFunction("_!=_",
                Decls.NewOverload("not_equals", new[] { Decls.Dyn, Decls.Dyn }, Decls.Bool)),
            Decls.NewFunction("_&&_",
                Decls.NewOverload("and", new[] { Decls.Bool, Decls.Bool }, Decls.Bool)),
            Decls.NewFunction("_||_",
                Decls.NewOverload("or", new[] { Decls.Bool, Decls.Bool }, Decls.Bool)),
            Decls.NewFunction("!_",
                Decls.NewOverload("not", new[] { Decls.Bool }, Decls.Bool)),
            Decls.NewFunction("_?_:_",
                Decls.NewParameterizedOverload("conditional",
                    new[] { Decls.Bool, Decls.NewTypeParamType("A"), Decls.NewTypeParamType("A") },
                    Decls.NewTypeParamType("A"), new List<string> { "A" })),
            Decls.NewFunction("_[_]",
                Decls.NewOverload("index_list", new[] { Decls.NewListType(Decls.Dyn), Decls.Int }, Decls.Dyn),
                Decls.NewOverload("index_map", new[] { Decls.NewMapType(Decls.String, Decls.Dyn), Decls.String }, Decls.Dyn)),
            Decls.NewFunction("in",
                Decls.NewOverload("in_list", new[] { Decls.Dyn, Decls.NewListType(Decls.Dyn) }, Decls.Bool))
        };
        return decls;
    }

    private static void Probe_(string label, Env env)
    {
        Console.WriteLine();
        Console.WriteLine($"[{label}]");
        foreach (var expr in new[]
                 {
                     "1 + 1",
                     "size('abc')",
                     "'abc'.contains('b')",
                     "'abc'.matches('a.*')",
                     "matches('abc', 'a.*')",
                     "[1,2].all(x, x > 0)",
                     "timestamp(0)",
                     "foo",
                     "x.y.z"
                 })
        {
            string outcome;
            try
            {
                var p = env.Parse(expr);
                if (p.HasIssues()) { outcome = "PARSE FAIL: " + FirstLine(p.Issues.ToString()); }
                else
                {
                    var c = env.Check(p.Ast);
                    if (c.HasIssues()) outcome = "CHECK REJECT: " + FirstLine(c.Issues!.ToString());
                    else outcome = "CHECK ACCEPT";
                }
            }
            catch (Exception ex)
            {
                outcome = "THROW " + ex.GetType().Name + ": " + FirstLine(ex.Message);
            }

            Console.WriteLine($"  {expr,-28} -> {outcome}");
        }
    }

    private static string FirstLine(string? s)
    {
        if (s is null) return "<null>";
        var lines = s.Replace("\r\n", "\n").Split('\n').Where(l => l.Trim().Length > 0).ToArray();
        return lines.Length > 0 ? lines[0].Trim() : s;
    }
}
