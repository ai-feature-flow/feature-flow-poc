using System.Text.Json.Serialization;

namespace FeatureFlow.Spike.Contracts;

/// <summary>
/// A route target: a step id, a terminal directive, or an ordered list of
/// guarded routes. docs/09 section 6.1 and 6.2.
/// </summary>
public sealed class RouteTarget
{
    /// <summary>A step id. Mutually exclusive with End and Resume.</summary>
    public string? Step { get; set; }

    /// <summary>Terminal directive. docs/09 6.2: end is a run status, never "resume".</summary>
    public string? End { get; set; }

    /// <summary>{ resume: true }. Valid only on a plan.replan step. docs/09 6.2.</summary>
    public bool Resume { get; set; }

    /// <summary>An ordered list of guarded routes, ending in an unguarded fallback.</summary>
    public List<GuardedRoute>? Guarded { get; set; }

    [JsonIgnore]
    public bool IsDirective => End is not null;

    [JsonIgnore]
    public bool IsResume => Resume;

    public static RouteTarget ToStep(string id) => new() { Step = id };
    public static RouteTarget ToEnd(string status) => new() { End = status };
    public static RouteTarget ToResume() => new() { Resume = true };
}

public sealed class GuardedRoute
{
    /// <summary>A CEL expression. Null means the unguarded fallback.</summary>
    public string? When { get; set; }

    public RouteTarget? To { get; set; }
}

public sealed class StepLimits
{
    public int Attempts { get; set; } = 3;

    /// <summary>ISO 8601 duration, e.g. PT4H.</summary>
    public string? StartToClose { get; set; }

    /// <summary>ISO 8601 duration, e.g. PT30S.</summary>
    public string? Heartbeat { get; set; }
}

public sealed class StepRequires
{
    public List<string> Capabilities { get; set; } = new();
}

public sealed class StepGate
{
    public string DecisionType { get; set; } = "";
}

public sealed class ParallelInputs
{
    public List<string> Branches { get; set; } = new();

    /// <summary>all | any | n</summary>
    public string Join { get; set; } = "all";

    public int? JoinCount { get; set; }

    public int MaxConcurrency { get; set; } = 1;

    public static ParallelInputs FromInputs(Dictionary<string, object?> inputs)
    {
        var result = new ParallelInputs();
        if (inputs.TryGetValue("branches", out var branches))
            result.Branches = ToStringList(branches);
        if (inputs.TryGetValue("join", out var join))
        {
            var text = join?.ToString() ?? "all";
            if (int.TryParse(text, out var n))
            {
                result.Join = "n";
                result.JoinCount = n;
            }
            else
            {
                result.Join = text;
            }
        }
        if (inputs.TryGetValue("max_concurrency", out var mc) && mc is not null)
            result.MaxConcurrency = ToInt(mc);
        return result;
    }

    /// <summary>
    /// Inputs arrive as object? and may be a JsonElement after deserialization,
    /// so a plain cast to IEnumerable is not enough.
    /// </summary>
    private static List<string> ToStringList(object? value)
    {
        if (value is System.Text.Json.JsonElement e &&
            e.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            return e.EnumerateArray().Select(x => x.ToString()).ToList();
        }
        if (value is IEnumerable<object?> list)
            return list.Select(x => x?.ToString() ?? "").ToList();
        return new List<string>();
    }

    /// <summary>
    /// Inputs arrive as object? and may be a JsonElement after deserialization,
    /// so Convert.ToInt32 is not enough.
    /// </summary>
    private static int ToInt(object? value) => value switch
    {
        int i => i,
        long l => (int)l,
        string s when int.TryParse(s, out var parsed) => parsed,
        System.Text.Json.JsonElement e when e.ValueKind == System.Text.Json.JsonValueKind.Number =>
            e.GetInt32(),
        System.Text.Json.JsonElement e when e.ValueKind == System.Text.Json.JsonValueKind.String &&
            int.TryParse(e.GetString(), out var parsed) => parsed,
        IConvertible c => Convert.ToInt32(c),
        _ => 1,
    };
}

public sealed class Step
{
    public string Id { get; set; } = "";

    public string Kind { get; set; } = "";

    public string? Scope { get; set; }

    public bool Protected { get; set; }

    public Dictionary<string, object?> Inputs { get; set; } = new(StringComparer.Ordinal);

    public StepRequires? Requires { get; set; }

    public StepLimits? Limits { get; set; }

    /// <summary>Outcome to route target. docs/09 6.1.</summary>
    public Dictionary<string, RouteTarget> Routes { get; set; } = new(StringComparer.Ordinal);

    public StepGate? Gate { get; set; }

    public List<string>? Produces { get; set; }

    public List<string>? Consumes { get; set; }

    /// <summary>Spike-only: how long a fake activity should pretend to work.</summary>
    public string? SimulatedDuration { get; set; }

    /// <summary>Spike-only: whether this step polls inside one activity (docs/05 1.2.1).</summary>
    public bool PollsInActivity { get; set; }

    public ParallelInputs? Parallel =>
        Inputs.TryGetValue("branches", out var _) ? ParallelInputs.FromInputs(Inputs) : null;

    public Step Clone() => (Step)MemberwiseClone();
}

public sealed class Plan
{
    public string ApiVersion { get; set; } = "feature-flow/v1";

    public int Revision { get; set; } = 1;

    public string Entry { get; set; } = "";

    public List<Step> Steps { get; set; } = new();

    public Dictionary<string, object?> Parameters { get; set; } = new(StringComparer.Ordinal);

    public Step? ById(string id) => Steps.FirstOrDefault(s => s.Id == id);

    public Plan Clone()
    {
        var copy = (Plan)MemberwiseClone();
        copy.Steps = Steps.Select(s =>
        {
            var step = s.Clone();
            step.Inputs = new Dictionary<string, object?>(s.Inputs, StringComparer.Ordinal);
            step.Routes = new Dictionary<string, RouteTarget>(s.Routes, StringComparer.Ordinal);
            return step;
        }).ToList();
        copy.Parameters = new Dictionary<string, object?>(Parameters, StringComparer.Ordinal);
        return copy;
    }
}
