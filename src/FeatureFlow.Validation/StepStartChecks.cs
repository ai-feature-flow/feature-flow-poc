using System.Globalization;
using FeatureFlow.Model;

namespace FeatureFlow.Validation;

/// <summary>
/// One input of an engine action: its kind (<c>string</c>, <c>boolean</c>, <c>array</c>, <c>duration</c>, <c>map</c>), for a
/// string the values allowed, and for a duration the least it may be.
/// </summary>
public sealed record ActionInput(string Name, string Kind, IReadOnlyList<string>? Allowed = null, TimeSpan? Minimum = null);

/// <summary>
/// The step-start checks of docs/09 section 2 that need no provider: 31 (an engine action's inputs satisfy its
/// contract, docs/04) and 33 (every <c>tier_tags</c> tag matches a tier of the manifest read at this step). Check 32
/// (consumed artifacts exist) is the workflow's, which knows the run's artifacts.
/// </summary>
public static class StepStartChecks
{
    static ActionInput S(string name, params string[] allowed) => new(name, "string", allowed.Length == 0 ? null : allowed);
    static ActionInput B(string name) => new(name, "boolean");
    static ActionInput A(string name) => new(name, "array");
    static ActionInput D(string name, TimeSpan? minimum = null) => new(name, "duration", Minimum: minimum);

    /// <summary>The shortest poll interval a plan may ask for, so a wait cannot hammer a provider (it must not spin).</summary>
    public static readonly TimeSpan MinimumPoll = TimeSpan.FromSeconds(5);
    static ActionInput M(string name) => new(name, "map");

    /// <summary>The inputs of the actions M5 builds (docs/04 sections 7 to 9 and 12). Actions not listed here are not checked yet.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<ActionInput>> Inputs = new Dictionary<string, IReadOnlyList<ActionInput>>
    {
        ["scm.catch_up"] = [S("repository")],
        ["scm.pull_request"] = [S("title"), S("body"), B("draft")],
        ["scm.merge"] = [B("engine_can_merge"), B("squash"), B("require_checks"), B("require_tested")],
        ["scm.revert"] = [S("pull_request")],
        ["ci.checks"] = [A("expected"), D("grace"), D("timeout"), D("poll_every", MinimumPoll)],
        ["wait.condition"] = [S("pull_request", "approved", "merged", "mergeable"), S("checks", "passed"), M("tracker"), D("poll_every", MinimumPoll), D("timeout")],
        ["test.run"] = [A("tier_tags"), B("baseline")],
        ["test.integration"] = [A("repositories"), A("tier_tags")],
        ["analysis.run"] = [B("baseline"), S("fail_on", "new_issues", "any_issues", "never"), S("min_level", "note", "warning", "error")],
        ["repo.inspect"] = [S("repository")],
        // M6 delivery, artifacts and tracker (docs/04 sections 5.1 and 12).
        ["delivery.report"] = [A("repositories")],
        ["artifact.compose"] = [S("name"), A("members")],
        ["artifact.import"] = [S("repository"), S("folder"), A("files"), S("commit"), S("record")],
        ["artifact.publish"] = [S("repository"), S("folder"), A("files"), S("previous"), S("title"), S("body")],
        ["tracker.update"] = [S("op", "create", "update", "transition", "link", "comment"), S("issue"), S("title"), S("body"), A("labels"), S("parent"),
            M("fields"), S("project"), S("status"), S("to"), S("link_type")],
    };

    /// <summary>Check 31: what is wrong with an action's evaluated inputs, as messages; empty when they satisfy its contract.</summary>
    public static IReadOnlyList<string> ActionInputs(string action, IReadOnlyDictionary<string, object?> inputs)
    {
        if (!Inputs.TryGetValue(action, out var spec))
            return [];
        var errors = new List<string>();
        foreach (var (name, value) in inputs)
        {
            if (spec.FirstOrDefault(i => i.Name == name) is not { } input)
            {
                errors.Add($"{action} has no input '{name}'; it takes {string.Join(", ", spec.Select(i => i.Name))}");
                continue;
            }

            if (value is null)
                continue;
            var ok = input.Kind switch
            {
                "string" => value is string,
                "boolean" => value is bool,
                "array" => value is IList<object?>,
                "map" => value is IDictionary<string, object?>,
                "duration" => value is string d && Duration(d) is not null,
                _ => true,
            };
            if (!ok)
                errors.Add($"{action} input '{name}' must be {input.Kind switch { "duration" => "a duration such as 30s, 5m or 2h", "boolean" => "true or false", "array" => "a list", _ => "a " + input.Kind }}, not {Describe(value)}");
            else if (input.Allowed is { } allowed && value is string text && !allowed.Contains(text))
                errors.Add($"{action} input '{name}' must be one of {string.Join(", ", allowed)}, not '{text}'");
            else if (input.Minimum is { } minimum && value is string d && Duration(d) < minimum)
                errors.Add($"{action} input '{name}' must be at least {minimum.TotalSeconds.ToString(CultureInfo.InvariantCulture)}s, not {d}");
        }

        if (action == "wait.condition")
        {
            var conditions = new[] { "pull_request", "checks", "tracker" }.Count(c => inputs.GetValueOrDefault(c) is not null);
            if (conditions != 1)
                errors.Add($"wait.condition takes exactly one condition of pull_request, checks or tracker, not {conditions.ToString(CultureInfo.InvariantCulture)}");
        }

        return errors;
    }

    /// <summary>Check 33: a diagnostic for each tag that matches no tier in the manifest read at this step.</summary>
    public static void TierTags(string repository, RepositoryManifest manifest, IEnumerable<string> tags, DiagnosticBag d)
    {
        foreach (var tag in manifest.UnknownTags(tags))
            d.Add(Checks.TierTags, $"{repository}: tag '{tag}' matches no tier of {RepositoryManifest.FileName} (its tiers: {string.Join(", ", manifest.Tiers.Select(t => $"{t.Name} [{string.Join(", ", t.Tags)}]"))})");
    }

    /// <summary>A duration such as <c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>2h</c> or <c>7d</c> (docs/03 section 2.1), or null.</summary>
    public static TimeSpan? Duration(string text)
    {
        var unit = text.EndsWith("ms", StringComparison.Ordinal) ? "ms" : text.Length > 0 ? text[^1..] : "";
        if (text.Length <= unit.Length
            || !double.TryParse(text[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
            return null;
        return unit switch
        {
            "ms" => TimeSpan.FromMilliseconds(n),
            "s" => TimeSpan.FromSeconds(n),
            "m" => TimeSpan.FromMinutes(n),
            "h" => TimeSpan.FromHours(n),
            "d" => TimeSpan.FromDays(n),
            _ => null,
        };
    }

    static string Describe(object value) => value switch
    {
        string s => $"'{s}'",
        bool b => b ? "true" : "false",
        IDictionary<string, object?> => "a map",
        IList<object?> => "a list",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "a value",
    };
}
