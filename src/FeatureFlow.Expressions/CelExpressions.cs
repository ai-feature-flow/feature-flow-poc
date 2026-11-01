using System.Collections.Concurrent;
using Cel;
using Cel.Checker;
using Cel.Common.Types;
using Cel.Common.Types.Ref;
using Cel.Interpreter.Functions;
using Google.Api.Expr.V1Alpha1;
using CelType = Google.Api.Expr.V1Alpha1.Type;

namespace FeatureFlow.Expressions;

/// <summary>The result of parsing and type-checking one expression.</summary>
public sealed record ExpressionCheck(
    string Text,
    IReadOnlyList<string> Errors,
    string ResultType,
    bool ResultIsBoolOrDyn,
    IReadOnlyList<IReadOnlyList<string>> Paths,
    int Nodes,
    long? CostMax)
{
    /// <summary>Paths used as the operand of an index, such as <c>params</c> in <c>params['x']</c>.</summary>
    public IReadOnlyList<IReadOnlyList<string>> IndexedPaths { get; init; } = [];

    public bool Ok => Errors.Count == 0;
}

/// <summary>
/// CEL restricted as docs/03 section 5.2 says: an explicit allow-list, never Cel.NET's default
/// environment. Ported from Spike 1's checks (<c>spikes/01-interpreter/src/CelChecks</c>,
/// archive/docs/15 section 9.2).
/// </summary>
public static class CelExpressions
{
    public const int MaxLength = 1000;
    public const int MaxNodes = 200;

    /// <summary>The cap on CEL's static cost estimate (Spike 1, check 4: <c>Cost.Max &lt;= 100</c>).</summary>
    public const long MaxCost = 100;

    /// <summary>The namespaces of docs/03 section 5.3, apart from <c>item</c>, which exists only in a <c>for_each</c>.</summary>
    public static readonly IReadOnlyList<string> Namespaces = ["params", "steps", "run", "findings"];

    /// <summary>
    /// The functions of docs/03 section 5.2 that Cel.NET declares, and the operators. <c>lower</c> and
    /// <c>upper</c> are declared by the engine below; <c>matches</c> (regular expressions) is left out.
    /// </summary>
    static readonly HashSet<string> AllowedStandardNames = new(StringComparer.Ordinal)
    {
        "size", "contains", "startsWith", "endsWith",
        "_+_", "_-_", "_*_", "_/_", "_%_", "-_", "!_",
        "_<_", "_<=_", "_>_", "_>=_", "_==_", "_!=_", "_&&_", "_||_", "_?_:_", "_[_]",
        "@in", "@not_strictly_false",
    };

    /// <summary>
    /// No macros. CEL's <c>all</c> and <c>exists</c> are comprehension macros, and Cel.NET estimates a
    /// comprehension over a list of unknown size at an unbounded cost, so the cost cap would refuse them
    /// anyway; docs/03 section 5.2 also says "no comprehensions" (LOG 71, awaiting a decision).
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedMacros = [];

    static readonly ConcurrentDictionary<string, Env> Environments = new();

    /// <summary>The CEL type of a declared value type: output types (docs/03 section 5.4), and "dyn".</summary>
    static CelType TypeOf(string type) => type switch
    {
        "string" => Decls.String,
        "number" => Decls.Int,
        "boolean" => Decls.Bool,
        "array" => Decls.NewListType(Decls.Dyn),
        _ => Decls.Dyn,
    };

    static Env EnvFor(IReadOnlyList<string> extra, bool withItem, IReadOnlyDictionary<string, string>? typed = null)
    {
        var typedKey = typed is null ? "" : string.Join(",", typed.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"{kv.Key}:{kv.Value}"));
        var key = string.Join(",", extra) + (withItem ? "|item" : "") + "|" + typedKey;
        return Environments.GetOrAdd(key, _ =>
        {
            var dyn = Decls.NewMapType(Decls.String, Decls.Dyn);
            var decls = new List<Decl>
            {
                Decls.NewVar("params", dyn),
                Decls.NewVar("steps", dyn),
                Decls.NewVar("run", dyn),
                Decls.NewVar("findings", Decls.NewMapType(Decls.String, Decls.Int)),
            };
            if (withItem)
                decls.Add(Decls.NewVar("item", Decls.Dyn));
            decls.AddRange(extra.Where(v => !Namespaces.Contains(v) && v != "item").Distinct().Select(v => Decls.NewVar(v, Decls.Dyn)));

            // A qualified name such as steps.triage.outputs.to_fix is resolved before the select chain, so
            // declaring it gives the path its declared type instead of the namespace's dyn.
            decls.AddRange((typed ?? new Dictionary<string, string>()).Select(kv => Decls.NewVar(kv.Key, TypeOf(kv.Value))));
            decls.AddRange(Cel.Checker.Checker.StandardDeclarations.Where(d => AllowedStandardNames.Contains(d.Name)));

            // Comparisons between int and double (for example a number output with 0.5); CEL equality across
            // the two types cannot be declared, so an output of type number is checked as an int.
            decls.AddRange(Cel.Checker.Checker.CrossTypeNumericComparisonDeclarations);
            decls.Add(Decls.NewFunction("lower",
                Decls.NewInstanceOverload("string_lower", [Decls.String], Decls.String)));
            decls.Add(Decls.NewFunction("upper",
                Decls.NewInstanceOverload("string_upper", [Decls.String], Decls.String)));
            return Env.NewCustomEnv(EnvOptions.ClearMacros(), EnvOptions.Declarations(decls));
        });
    }

    /// <summary>Culture-invariant case conversion: a culture-sensitive form would be a replay hazard (Spike 1).</summary>
    static readonly Overload[] EngineFunctions =
    [
        .. Overload.StandardOverloads(),
        Overload.Unary("lower", v => v is IVal s && s.Value() is string text ? StringT.StringOf(text.ToLowerInvariant()) : Err.NoSuchOverload(v, "lower", null)),
        Overload.Unary("string_lower", v => v is IVal s && s.Value() is string text ? StringT.StringOf(text.ToLowerInvariant()) : Err.NoSuchOverload(v, "lower", null)),
        Overload.Unary("upper", v => v is IVal s && s.Value() is string text ? StringT.StringOf(text.ToUpperInvariant()) : Err.NoSuchOverload(v, "upper", null)),
        Overload.Unary("string_upper", v => v is IVal s && s.Value() is string text ? StringT.StringOf(text.ToUpperInvariant()) : Err.NoSuchOverload(v, "upper", null)),
    ];

    /// <summary>Parses and type-checks an expression, and applies the bounds of docs/03 section 5.2.</summary>
    /// <param name="extraVariables">Further names in scope, such as a task's <c>with</c> keys.</param>
    /// <param name="withItem">Whether <c>item</c> is in scope.</param>
    /// <param name="typedPaths">Qualified paths with a known type, such as <c>steps.triage.outputs.to_fix</c> to "number".</param>
    public static ExpressionCheck Check(string text, IEnumerable<string>? extraVariables = null, bool withItem = false,
        IReadOnlyDictionary<string, string>? typedPaths = null)
    {
        var errors = new List<string>();
        if (text.Length > MaxLength)
            errors.Add($"is {text.Length} characters; the limit is {MaxLength}");

        var env = EnvFor((extraVariables ?? []).Order(StringComparer.Ordinal).ToList(), withItem, typedPaths);
        var parsed = env.Parse(text);
        if (parsed.HasIssues())
        {
            errors.Add("does not parse: " + Flatten(parsed.Issues!.ToString()));
            return new ExpressionCheck(text, errors, "?", false, [], 0, null);
        }

        var paths = new List<IReadOnlyList<string>>();
        var indexed = new List<IReadOnlyList<string>>();
        var nodes = Walk(parsed.Ast!.Expr, paths, false, indexed);
        if (nodes > MaxNodes)
            errors.Add($"has {nodes} syntax tree nodes; the limit is {MaxNodes}");

        var checkedAst = env.Check(parsed.Ast!);
        if (checkedAst.HasIssues())
        {
            errors.Add("does not type-check: " + Flatten(checkedAst.Issues!.ToString()));
            return new ExpressionCheck(text, errors, "?", false, paths, nodes, null) { IndexedPaths = indexed };
        }

        long? cost = null;
        try
        {
            var program = env.Program(checkedAst.Ast!, ProgramOptions.Functions(EngineFunctions));
            cost = Cel.Cel.EstimateCost(program).Max;
            if (cost > MaxCost)
                errors.Add($"has an estimated cost of {cost}; the limit is {MaxCost}");
        }
        catch (Exception e)
        {
            errors.Add("cannot be planned: " + Flatten(e.Message));
        }

        var type = checkedAst.Ast!.ResultType;
        return new ExpressionCheck(text, errors, Describe(type), IsBoolOrDyn(type), paths, nodes, cost) { IndexedPaths = indexed };
    }

    /// <summary>
    /// Evaluates a checked expression. Variables are plain values: strings, longs, doubles, booleans, lists and
    /// maps. A variable may be a qualified path such as <c>steps.triage.outputs.to_fix</c>; with
    /// <paramref name="typedPaths"/> it is declared with the same type the load check gave it, so a guard
    /// evaluates under the types it was checked with.
    /// </summary>
    public static object? Evaluate(string text, IReadOnlyDictionary<string, object?> variables, bool withItem = false,
        IReadOnlyDictionary<string, string>? typedPaths = null)
    {
        var env = EnvFor(variables.Keys
            .Where(k => !Namespaces.Contains(k) && k != "item" && typedPaths?.ContainsKey(k) != true)
            .Order(StringComparer.Ordinal).ToList(), withItem, typedPaths);
        var parsed = env.Parse(text);
        if (parsed.HasIssues())
            throw new InvalidOperationException(parsed.Issues.ToString());
        var checkedAst = env.Check(parsed.Ast!);
        if (checkedAst.HasIssues())
            throw new InvalidOperationException(checkedAst.Issues.ToString());
        var program = env.Program(checkedAst.Ast!, ProgramOptions.Functions(EngineFunctions));
        var bindings = Namespaces.ToDictionary(n => n, n => (object)new Dictionary<string, object>());
        foreach (var (key, value) in variables)
            bindings[key] = ToCel(value)!;
        var result = program.Eval(bindings).Val;
        if (result is Err err)
            throw new InvalidOperationException(err.ToString());
        return FromCel(result);
    }

    static object? ToCel(object? value) => value switch
    {
        null => NullT.NullValue,
        int i => (long)i,
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(kv => (object)kv.Key, kv => ToCel(kv.Value)!),
        IDictionary<string, object?> map => map.ToDictionary(kv => (object)kv.Key, kv => ToCel(kv.Value)!),
        string s => s,
        System.Collections.IEnumerable list => list.Cast<object?>().Select(ToCel).ToList(),
        _ => value,
    };

    static object? FromCel(IVal value) => value.Value() switch
    {
        IList<IVal> list => list.Select(FromCel).ToList(),
        var v => v,
    };

    /// <summary>Counts AST nodes and collects select paths rooted at an identifier, such as <c>[steps, plan, outputs, x]</c>.</summary>
    static int Walk(Expr e, List<IReadOnlyList<string>> paths, bool insideSelect, List<IReadOnlyList<string>> indexed)
    {
        switch (e.ExprKindCase)
        {
            case Expr.ExprKindOneofCase.IdentExpr:
                if (!insideSelect)
                    paths.Add([e.IdentExpr.Name]);
                return 1;
            case Expr.ExprKindOneofCase.SelectExpr:
                if (!insideSelect && SelectPath(e) is { } path)
                    paths.Add(path);
                return 1 + Walk(e.SelectExpr.Operand, paths, true, indexed);
            case Expr.ExprKindOneofCase.CallExpr:
                if (e.CallExpr.Function == "_[_]" && e.CallExpr.Args.Count == 2 && Root(e.CallExpr.Args[0]) is { } operand)
                    indexed.Add(operand);
                var n = 1;
                if (e.CallExpr.Target is not null)
                    n += Walk(e.CallExpr.Target, paths, false, indexed);
                foreach (var arg in e.CallExpr.Args)
                    n += Walk(arg, paths, false, indexed);
                return n;
            case Expr.ExprKindOneofCase.ListExpr:
                return 1 + e.ListExpr.Elements.Sum(x => Walk(x, paths, false, indexed));
            case Expr.ExprKindOneofCase.StructExpr:
                return 1 + e.StructExpr.Entries.Sum(x =>
                    (x.MapKey is null ? 0 : Walk(x.MapKey, paths, false, indexed)) + Walk(x.Value, paths, false, indexed));
            case Expr.ExprKindOneofCase.ComprehensionExpr:
                var c = e.ComprehensionExpr;
                return 1 + Walk(c.IterRange, paths, false, indexed) + Walk(c.AccuInit, paths, false, indexed) + Walk(c.LoopCondition, paths, false, indexed)
                       + Walk(c.LoopStep, paths, false, indexed) + Walk(c.Result, paths, false, indexed);
            default:
                return 1;
        }
    }

    /// <summary>The path of an identifier or a select chain rooted at one, or null.</summary>
    static List<string>? Root(Expr e) => e.ExprKindCase == Expr.ExprKindOneofCase.IdentExpr ? [e.IdentExpr.Name] : SelectPath(e);

    static List<string>? SelectPath(Expr e)
    {
        var parts = new List<string>();
        while (e.ExprKindCase == Expr.ExprKindOneofCase.SelectExpr)
        {
            parts.Add(e.SelectExpr.Field);
            e = e.SelectExpr.Operand;
        }

        if (e.ExprKindCase != Expr.ExprKindOneofCase.IdentExpr)
            return null;
        parts.Add(e.IdentExpr.Name);
        parts.Reverse();
        return parts;
    }

    static bool IsBoolOrDyn(CelType? type) =>
        type is null
        || type.TypeKindCase == CelType.TypeKindOneofCase.Dyn
        || (type.TypeKindCase == CelType.TypeKindOneofCase.Primitive && type.Primitive == CelType.Types.PrimitiveType.Bool);

    static string Describe(CelType? type) => type?.TypeKindCase switch
    {
        null => "?",
        CelType.TypeKindOneofCase.Dyn => "dyn",
        CelType.TypeKindOneofCase.Primitive => type.Primitive.ToString().ToLowerInvariant(),
        CelType.TypeKindOneofCase.ListType => "list",
        CelType.TypeKindOneofCase.MapType => "map",
        CelType.TypeKindOneofCase.Null => "null",
        _ => type.TypeKindCase.ToString().ToLowerInvariant(),
    };

    static string Flatten(string message) =>
        string.Join(" ", message.Replace("\r\n", "\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
}
