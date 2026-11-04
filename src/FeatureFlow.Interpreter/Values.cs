using System.Globalization;
using FeatureFlow.Expressions;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

/// <summary>An expression could not be evaluated: a value it reads does not exist yet, or it failed.</summary>
public sealed class EvaluationException(string message) : Exception(message);

/// <summary>
/// Run-time values in the namespaces of docs/03 section 5.3, and value conversion to the declared types of
/// section 5.4. A <c>number</c> is a CEL int at load (docs/build/m1-results.md section 5), so at run time it is
/// held as a whole number (LOG 76).
/// </summary>
public static class Values
{
    /// <summary>
    /// Converts a reported value to its declared type, or returns an error. Every number, whatever its declared
    /// type, in a list or a map too, is a whole number (LOG 76): a fractional one is refused.
    /// </summary>
    public static (object? Value, string? Error) Convert(object? value, string? type)
    {
        switch (type)
        {
            case "number":
                return value switch
                {
                    long l => (l, null),
                    int i => ((long)i, null),
                    double d when Math.Abs(d) < 9.2e18 && d == Math.Floor(d) => ((long)d, null),
                    double d => (null, Fraction(d)),
                    decimal m when m == decimal.Floor(m) => ((long)m, null),
                    _ => (null, $"{Describe(value)} is not a number"),
                };
            case "boolean":
                return value is bool ? (value, null) : (null, $"{Describe(value)} is not a boolean");
            case "string":
                return value is string ? (value, null) : (null, $"{Describe(value)} is not a string");
            case "array":
                if (value is System.Collections.IEnumerable list and not string and not System.Collections.IDictionary)
                {
                    var items = new List<object?>();
                    foreach (var item in list)
                    {
                        var (converted, error) = Convert(item, null);
                        if (error is not null)
                            return (null, error);
                        items.Add(converted);
                    }

                    return (items, null);
                }

                return (null, $"{Describe(value)} is not a list");
            default:
                switch (value)
                {
                    case int i:
                        return ((long)i, null);
                    case double d when Math.Abs(d) < 9.2e18 && d == Math.Floor(d):
                        return ((long)d, null);
                    case double d:
                        return (null, Fraction(d));
                    case IReadOnlyDictionary<string, object?> map:
                        return ConvertMap(map);
                    case IDictionary<string, object?> map:
                        return ConvertMap(map.ToDictionary(kv => kv.Key, kv => kv.Value));
                    case System.Collections.IEnumerable items and not string:
                        return Convert(items, "array");
                    default:
                        return (value, null);
                }
        }
    }

    static (object? Value, string? Error) ConvertMap(IReadOnlyDictionary<string, object?> map)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, item) in map)
        {
            var (converted, error) = Convert(item, null);
            if (error is not null)
                return (null, error);
            result[key] = converted;
        }

        return (result, null);
    }

    static string Fraction(double d) =>
        $"{d.ToString(CultureInfo.InvariantCulture)} is not a whole number; a number holds a whole number (LOG 76)";

    static string Describe(object? value) => value switch
    {
        null => "null",
        string s => $"'{s}'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "?",
    };

    /// <summary>A literal YAML node as a plain value, typed by a declared parameter type when known.</summary>
    public static object? FromNode(YNode node, string? type)
    {
        var plain = node.ToPlain();
        if (type is "string" && node is YScalar s && !s.IsNull)
            return s.Text;
        var (value, error) = Convert(plain, type is "number" or "boolean" or "array" ? type : null);
        if (error is null && type is "number" && plain is not (long or double))
            error = $"{Describe(plain)} is not a number";
        if (error is not null)
            throw new EvaluationException(error);
        return value;
    }
}

/// <summary>Evaluates a plan's expressions against a run's state.</summary>
internal sealed class Evaluator(RunCore core)
{
    readonly Dictionary<string, ExpressionCheck> checks = new(StringComparer.Ordinal);

    public bool Guard(Expression e, Func<string, long> findings)
    {
        var value = Evaluate(e, findings);
        return value as bool? ?? throw new EvaluationException($"guard '{e.Text}' gave {value ?? "null"}, not a boolean");
    }

    public object? Evaluate(Expression e, Func<string, long> findings, IReadOnlyDictionary<string, object?>? extra = null)
    {
        var key = $"{e.Text}|{e.Scope.HasItem}|{string.Join(",", e.ExtraVariables ?? [])}";
        if (!checks.TryGetValue(key, out var check))
            checks[key] = check = CelExpressions.Check(e.Text, e.ExtraVariables, e.Scope.HasItem);
        if (!check.Ok)
            throw new EvaluationException($"'{e.Text}' does not check: {string.Join("; ", check.Errors)}");

        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        var typed = new Dictionary<string, string>(StringComparer.Ordinal);
        var paramValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        var findingValues = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var path in check.Paths)
        {
            switch (path[0])
            {
                case "params" when path.Count > 1:
                    paramValues[path[1]] = Parameter(e.Scope.DocumentScope, path[1], findings);
                    break;
                case "findings" when path.Count > 1:
                    findingValues[path[1]] = findings(path[1]);
                    break;
                case "run":
                    variables["run"] = RunValues();
                    break;
                case "item":
                    variables["item"] = Item(e.Scope);
                    break;
                case "steps" when path.Count > 1:
                    StepValue(path, e.Scope, variables, typed);
                    break;
            }
        }

        variables["params"] = paramValues;
        variables["findings"] = findingValues;
        foreach (var (name, value) in extra ?? new Dictionary<string, object?>())
            variables[name] = value;
        try
        {
            return CelExpressions.Evaluate(e.Text, variables, e.Scope.HasItem, typed.Count == 0 ? null : typed);
        }
        catch (InvalidOperationException x)
        {
            throw new EvaluationException($"'{e.Text}' failed: {x.Message}");
        }
    }

    Dictionary<string, object?> RunValues()
    {
        var run = core.Run;
        var level = core.Plan.Document.Level ?? "run";
        return new Dictionary<string, object?>
        {
            ["id"] = run.Id,
            ["level"] = level,
            ["repositories"] = run.Repositories.Cast<object?>().ToList(),
            ["branch"] = run.Repositories.ToDictionary(r => r, r => (object?)core.WorkBranch()),
            // A named parent branch (a child's, or a start's, LOG 137); otherwise the default branch run start read from the
            // code host. Only a run with no code host, or a history from before M5, shows main.
            ["parent_branch"] = run.Repositories.ToDictionary(r => r, r => (object?)(run.ParentBranch ?? run.DefaultBranches?.GetValueOrDefault(r) ?? "main")),
            ["tracker_issue"] = run.TrackerIssue ?? "",
            ["parent_id"] = run.ParentId ?? "",
        };
    }

    static object? Item(Scope scope)
    {
        for (var s = scope; s is { Kind: ScopeKind.Branch }; s = s.Parent)
            if (s.Item is { } item)
                return Values.FromNode(item, null);
        throw new EvaluationException("item is not known: the for_each list was not bound");
    }

    /// <summary>
    /// A parameter's value in a template or fragment scope (docs/03 section 6.2; LOG 62). It is evaluated at
    /// each read, because a fragment binding may read a step's outputs, which change when the step runs again.
    /// </summary>
    internal object? Parameter(Scope scope, string name, Func<string, long> findings)
    {
        var decl = scope.Document.Parameter(name) ?? throw new EvaluationException($"params.{name} is not declared");
        YNode? node = null;
        var evaluateIn = scope;
        if (scope.Kind == ScopeKind.Template && core.Run.Parameters.TryGetValue(name, out var given))
            node = given;
        else if (scope.Kind == ScopeKind.Fragment && scope.Bindings.TryGetValue(name, out var bound))
        {
            node = bound;
            evaluateIn = scope.Parent!;
        }

        if (node is null)
        {
            node = decl.Default ?? throw new EvaluationException($"params.{name} has no value");
            evaluateIn = scope;
        }

        object? value;
        if (node.IsSingleKeyMap("expr", out var text) && text is YScalar s)
        {
            var raw = Evaluate(new Expression(s.Text, ExpressionUse.Value, evaluateIn, s.Location, $"params.{name}"), findings);
            var (converted, error) = Values.Convert(raw, decl.Type is "number" or "boolean" or "string" or "array" ? decl.Type : null);
            value = error is null ? converted : throw new EvaluationException($"params.{name}: {error}");
        }
        else if (node is YUnbound)
            throw new EvaluationException($"params.{name} is unbound");
        else
            value = Values.FromNode(node, decl.Type);
        return value;
    }

    /// <summary>
    /// Binds a <c>steps.</c> path as a qualified variable with the type the load check gave it, so a guard is
    /// evaluated under the types it was checked with.
    /// </summary>
    void StepValue(IReadOnlyList<string> path, Scope scope, Dictionary<string, object?> variables, Dictionary<string, string> typed)
    {
        Step? step = null;
        var at = 0;
        for (var k = path.Count - 1; k >= 1 && step is null; k--)
            if (core.FindStep(string.Join(".", path.Skip(1).Take(k)), scope) is { } found)
            {
                step = found;
                at = k + 1;
            }

        if (step is null)
            throw new EvaluationException($"steps.{string.Join(".", path.Skip(1))} names no step");
        var record = core.State.Steps.GetValueOrDefault(step.Id);
        var stepPath = string.Join(".", path.Take(at));
        if (at >= path.Count)
        {
            variables[stepPath] = Snapshot(record?.Current, record);
            return;
        }

        var prefix = $"{stepPath}.{path[at]}";
        switch (path[at])
        {
            case "outcome":
                variables[prefix] = record?.Current?.Outcome ?? throw new EvaluationException($"{prefix}: {step.Id} has no outcome yet");
                typed[prefix] = "string";
                break;
            case "executions":
                variables[prefix] = (long)(record?.Executions ?? 0);
                typed[prefix] = "number";
                break;
            case "previous":
                variables[prefix] = Snapshot(record?.Previous ?? throw new EvaluationException($"{prefix}: {step.Id} has no previous execution"), null);
                break;
            case "outputs" when at + 1 < path.Count:
            {
                var name = path[at + 1];
                var outputs = record?.Current?.Outputs;
                if (outputs is null || !outputs.TryGetValue(name, out var value))
                    throw new EvaluationException($"{prefix}.{name}: {step.Id} has not reported output '{name}'");
                variables[$"{prefix}.{name}"] = value;
                if (RunCore.OutputType(step, name) is { } type and not "dyn")
                    typed[$"{prefix}.{name}"] = type;
                break;
            }
            case "artifacts" when at + 1 < path.Count:
            {
                var name = path[at + 1];
                var artifacts = record?.Current?.Artifacts;
                if (artifacts is null || !artifacts.TryGetValue(name, out var reference))
                    throw new EvaluationException($"{prefix}.{name}: {step.Id} has not produced artifact '{name}'");
                variables[$"{prefix}.{name}"] = reference;
                break;
            }
            default:
                variables[prefix] = path[at] switch
                {
                    "outputs" => record?.Current?.Outputs.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object?>(),
                    "artifacts" => record?.Current?.Artifacts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value) ?? new Dictionary<string, object?>(),
                    _ => throw new EvaluationException($"{prefix} is not a step value"),
                };
                break;
        }
    }

    static Dictionary<string, object?> Snapshot(Execution? execution, StepRecord? record)
    {
        var map = new Dictionary<string, object?>
        {
            ["outcome"] = execution?.Outcome,
            ["outputs"] = execution?.Outputs.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, object?>(),
            ["artifacts"] = execution?.Artifacts.ToDictionary(kv => kv.Key, kv => (object?)kv.Value) ?? new Dictionary<string, object?>(),
        };
        if (record is not null)
            map["executions"] = (long)record.Executions;
        return map;
    }
}
