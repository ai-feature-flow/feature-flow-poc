using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Agent;

/// <summary>
/// Checks an agent's report against its step's contract (docs/04 section 3.3): one declared outcome; a summary
/// of at most 2 KB; for a success outcome every output the step reports, each of its declared type (a
/// <c>number</c> is whole, LOG 76); exactly the artifacts the outcome promises, each a file the agent wrote to its
/// output folder. The errors go back to the agent as a correction turn. The report's artifacts are still paths
/// relative to the output folder; the activity copies them out at the end of the step.
/// </summary>
public static class ReportCheck
{
    public const int SummaryLimit = 2048;

    /// <summary>
    /// <paramref name="report"/> is the JSON the agent gave: <c>outcome</c>, <c>summary</c>, <c>outputs</c> (an object),
    /// <c>artifacts</c> (an object of name to path, or a list of paths named by their file name). <paramref name="missing"/>
    /// lists the paths the sandbox client found absent or empty.
    /// </summary>
    public static (StepReport? Report, IReadOnlyList<string> Errors) Check(JsonElement report, StepContract contract, IReadOnlyCollection<string>? missing = null)
    {
        var errors = new List<string>();
        if (report.ValueKind != JsonValueKind.Object)
            return (null, ["the report must be an object with outcome, summary, outputs and artifacts"]);

        var outcome = Text(report, "outcome");
        var declared = contract.Outcomes.FirstOrDefault(o => o.Name == outcome);
        if (declared is null)
            errors.Add($"outcome '{outcome}' is not one of this step's outcomes: {string.Join(", ", contract.Outcomes.Select(o => o.Name))}");

        var summary = Text(report, "summary") ?? "";
        if (summary.Trim().Length == 0)
            errors.Add("summary must not be empty");
        if (Encoding.UTF8.GetByteCount(summary) > SummaryLimit)
            errors.Add($"summary is {Encoding.UTF8.GetByteCount(summary)} bytes; at most {SummaryLimit}");

        var outputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (report.TryGetProperty("outputs", out var givenOutputs) && givenOutputs.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in givenOutputs.EnumerateObject())
            {
                if (!contract.Reported.Contains(property.Name))
                {
                    errors.Add($"output '{property.Name}' is not one this step reports" +
                               (contract.Reported.Count > 0 ? $" ({string.Join(", ", contract.Reported)})" : " (it reports none)"));
                    continue;
                }

                var type = contract.OutputTypes.GetValueOrDefault(property.Name, "dyn");
                var (value, error) = Values.Convert(Plain(property.Value), type == "dyn" ? null : type);
                if (error is not null)
                    errors.Add($"output '{property.Name}': {error}");
                else
                    outputs[property.Name] = YamlWriter.WritePlain(value);
            }
        }
        else if (report.TryGetProperty("outputs", out var notObject) && notObject.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            errors.Add("outputs must be an object of name to value");

        if (declared is { Class: "success" })
            foreach (var name in contract.Reported.Where(n => !outputs.ContainsKey(n) && !errors.Any(e => e.StartsWith($"output '{n}'", StringComparison.Ordinal))))
                errors.Add($"output '{name}' is required for outcome '{outcome}' ({contract.OutputTypes.GetValueOrDefault(name, "any")})");

        var artifacts = Artifacts(report, errors);
        if (declared is not null)
        {
            var promised = contract.ArtifactsFor(declared.Name).ToHashSet(StringComparer.Ordinal);
            foreach (var name in artifacts.Keys.Where(n => !promised.Contains(n)).Order(StringComparer.Ordinal))
                errors.Add($"artifact '{name}' is not one outcome '{declared.Name}' produces" + (promised.Count > 0 ? $" ({string.Join(", ", promised)})" : ""));
            foreach (var name in promised.Where(n => !artifacts.ContainsKey(n)).Order(StringComparer.Ordinal))
                errors.Add($"outcome '{declared.Name}' must give artifact '{name}': its path in your output folder");
        }

        foreach (var (name, path) in artifacts)
        {
            if (path.StartsWith('/') || path.Split('/', '\\').Contains(".."))
                errors.Add($"artifact '{name}': '{path}' must be a path relative to your output folder");
            else if (missing?.Contains(path) == true)
                errors.Add($"artifact '{name}': '{path}' does not exist in your output folder or is empty");
        }

        if (errors.Count > 0)
            return (null, errors);
        return (new StepReport(outcome!, outputs, artifacts, summary), []);
    }

    /// <summary>The artifacts as name to path: an object, or a list of paths named by their file name without extension.</summary>
    static Dictionary<string, string> Artifacts(JsonElement report, List<string> errors)
    {
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!report.TryGetProperty("artifacts", out var given) || given.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return artifacts;
        switch (given.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in given.EnumerateObject())
                    if (property.Value.ValueKind == JsonValueKind.String)
                        artifacts[property.Name] = property.Value.GetString()!;
                    else
                        errors.Add($"artifact '{property.Name}' must be given as a path");
                break;
            case JsonValueKind.Array:
                foreach (var item in given.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String)
                        artifacts[Path.GetFileNameWithoutExtension(item.GetString()!)] = item.GetString()!;
                break;
            default:
                errors.Add("artifacts must be an object of artifact name to path");
                break;
        }

        return artifacts;
    }

    /// <summary>The paths a report names, for the sandbox client to check before it forwards the report.</summary>
    public static IEnumerable<string> Paths(JsonElement report) =>
        report.ValueKind == JsonValueKind.Object && report.TryGetProperty("artifacts", out var given)
            ? given.ValueKind switch
            {
                JsonValueKind.Object => given.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String).Select(p => p.Value.GetString()!),
                JsonValueKind.Array => given.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String).Select(i => i.GetString()!),
                _ => [],
            }
            : [];

    static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    /// <summary>A JSON value as the plain values the core converts: long or double, bool, string, lists and maps.</summary>
    public static object? Plain(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => e.EnumerateArray().Select(Plain).ToList(),
        JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => Plain(p.Value)),
        _ => null,
    };
}
