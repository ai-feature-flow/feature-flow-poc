using System.Text;
using System.Text.RegularExpressions;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Validation;

/// <summary>
/// The readable form of a plan (docs/09 section 4.4): one line per step with its sequence, id, form, role
/// or action, effective write access, status and non-default routes; defaults stated once at the foot.
/// Generated from the plan, never maintained by hand.
/// </summary>
public static partial class ReadableForm
{
    [GeneratedRegex(@"^params\.([a-z][a-z0-9_]*)$")]
    private static partial Regex ParamPath();

    public static string Render(Plan plan, IReadOnlyDictionary<string, string>? statuses = null)
    {
        var steps = plan.AllSteps.ToList();
        var lanes = steps.GroupBy(s => s.Lane).ToDictionary(g => g.Key, g => g.OrderBy(s => s.LaneIndex).ToList());
        var templateDefaults = plan.Root.Document.Defaults;

        var rows = new List<string[]>();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            var routes = step.ResolvedRoutes
                .Where(r => !IsDefault(r, step, lanes, templateDefaults))
                .Select(r => $"{r.Outcome}->{Show(r.Target)}")
                .ToList();
            if (step.Protected)
                routes.Add("[protected]");
            rows.Add([
                (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                step.Id,
                Form(step),
                RoleOrAction(step),
                step.Form == StepForm.Agent ? step.EffectiveWriteAccess ?? step.Agent?.WriteAccess ?? "?" : "-",
                statuses?.GetValueOrDefault(step.Id) ?? "pending",
                string.Join("  ", routes),
            ]);
        }

        string[] header = ["#", "id", "form", "role / action", "access", "status", "routes (non-default)"];
        var widths = Enumerable.Range(0, header.Length - 1)
            .Select(c => rows.Select(r => r[c].Length).Append(header[c].Length).Max())
            .ToArray();

        var lines = new List<string>();
        string Line(string[] cells) =>
            (" " + cells[0].PadLeft(widths[0]) + "  " + string.Join("  ", cells.Skip(1).Take(widths.Length - 1).Select((c, k) => c.PadRight(widths[k + 1])))
             + "  " + cells[^1]).TrimEnd();

        var table = rows.Select(Line).ToList();
        var headerLine = Line(header);
        var width = table.Append(headerLine).Max(l => l.Length);

        var title = plan.Document.Kind == "template"
            ? $"Template {plan.Document.Name} (level {plan.Document.Level})"
            : $"Fragment {plan.Document.Name}";
        lines.Add(title);
        foreach (var policy in Policies(plan))
            lines.Add("  policy: " + policy);
        lines.Add(new string('─', headerLine.Length));
        lines.Add(headerLine);
        lines.AddRange(table);
        lines.Add("");

        var protectedIds = plan.Instances.Where(i => i.Protected).Select(i => i.Id + ".*")
            .Concat(steps.Where(s => s.Protected && !plan.Instances.Any(i => i.Protected && s.Id.StartsWith(i.Id + ".", StringComparison.Ordinal))).Select(s => s.Id))
            .ToList();
        if (protectedIds.Count > 0)
            lines.Add("Protected: " + string.Join(", ", protectedIds));
        lines.Add("Defaults: " + DescribeDefaults(templateDefaults));
        if (plan.Instances.Count > 0)
            lines.Add("Fragments: " + string.Join("; ", plan.Instances.Select(i => $"{i.Id} = {i.Fragment.Text}")));
        foreach (var container in steps.Where(s => s.Container is { FromForEach: true }))
        {
            var c = container.Container!;
            var items = c.Items is null ? "a literal list" : c.Items.Text;
            lines.Add(c.ItemsKnown
                ? $"for_each {container.Id}: {c.Branches.Count} item(s) from {items}, {c.Mode}"
                : $"for_each {container.Id}: items from {items} are known at binding; shown with one branch, {c.Mode}");
        }

        var sb = new StringBuilder();
        foreach (var line in lines)
            sb.Append(line).Append('\n');
        return sb.ToString();
    }

    static string Form(Step step) => step.Form switch
    {
        StepForm.Agent => "agent",
        StepForm.Decision => "decision",
        StepForm.Action => "action",
        StepForm.Chain => "chain",
        _ => step.Container?.FromForEach == true ? "for_each" : "parallel",
    };

    static string RoleOrAction(Step step) => step.Form switch
    {
        StepForm.Agent => step.Agent?.Role?.Reference?.Name ?? step.Agent?.Role?.ToString() ?? "?",
        StepForm.Decision => step.Decision?.Type ?? "?",
        StepForm.Action => step.Action ?? "?",
        _ => step.Container is { } c
            ? c.Mode == "chain"
                ? "in order"
                : $"join {c.Join}" + (c.MaxConcurrency is { } m ? $", max {m}" : "")
            : "?",
    };

    /// <summary>Whether the route is the one the foot's defaults describe: the template's class default, the next step for success, escalation when attempts run out.</summary>
    static bool IsDefault(ResolvedRoute route, Step step, Dictionary<string, List<Step>> lanes, Defaults defaults)
    {
        Resolved? expected = null;
        if (route.Class != OutcomeClass.None && defaults.ByClass.TryGetValue(route.Class, out var spec))
            expected = spec.Target switch
            {
                Escalate e => new ResolvedEscalate(e.DecisionType, e.Prompt),
                EndRun end => new ResolvedEnd(end.Status),
                _ => null,
            };
        else if (route.Class == OutcomeClass.Success)
        {
            var lane = lanes[step.Lane];
            expected = step.LaneIndex + 1 < lane.Count
                ? new ResolvedStep(lane[step.LaneIndex + 1].Id, false)
                : step.Lane.Length > 0
                    ? new ResolvedBranchEnd(step.Lane[..step.Lane.LastIndexOf('#')])
                    : new ResolvedEnd("completed");
        }
        else if (route.Outcome == Actions.AttemptsExhausted)
            expected = new ResolvedEscalate(null, null);

        return expected is not null && expected.Equals(route.Target);
    }

    static string Show(Resolved target) => target switch
    {
        ResolvedStep s => s.CleanUp ? $"{s.StepId} (clean_up)" : s.StepId,
        ResolvedEnd e => $"end({e.Status})",
        ResolvedEscalate { DecisionType: null, Prompt: null } => "escalate",
        ResolvedEscalate e => "escalate(" + string.Join(", ", new[] { e.DecisionType, e.Prompt?.Text }.OfType<string>()) + ")",
        ResolvedBranchEnd => "join",
        ResolvedExit x => $"exit({x.Exit})",
        ResolvedGuarded g => "[" + string.Join("; ", g.Entries.Select(x => x.When is null ? $"else {Show(x.Target)}" : $"if {x.When.Text}: {Show(x.Target)}")) + "]",
        _ => "?",
    };

    static string DescribeDefaults(Defaults defaults)
    {
        var parts = defaults.ByClass
            .GroupBy(kv => kv.Value.Target switch
            {
                Escalate { DecisionType: null, Prompt: null } => "escalate",
                Escalate e => $"escalate({e.DecisionType})",
                EndRun end => $"end({end.Status})",
                ToStep t => t.Id,
                _ => "?",
            })
            .Select(g => $"{string.Join(", ", g.Select(kv => kv.Key.Name()).Order(StringComparer.Ordinal))} -> {g.Key}")
            .ToList();
        if (!defaults.ByClass.ContainsKey(OutcomeClass.Success))
            parts.Add("success -> next");
        parts.Add("attempts_exhausted -> escalate");
        return string.Join("; ", parts);
    }

    /// <summary>The merge policy of each <c>scm.merge</c> step: who merges, traced through fragment bindings to the template.</summary>
    static IEnumerable<string> Policies(Plan plan)
    {
        foreach (var step in plan.AllSteps.Where(s => s.Action == "scm.merge"))
        {
            var value = step.With?["engine_can_merge"];
            var described = value is null ? "true (the action's default)" : Describe(value, step.Scope);
            yield return $"{step.Id} engine_can_merge = {described}";
        }
    }

    static string Describe(YNode value, Scope scope)
    {
        if (value.IsSingleKeyMap("expr", out var e) && e is YScalar s)
        {
            var match = ParamPath().Match(s.Text.Trim());
            if (!match.Success)
                return s.Text;
            var name = match.Groups[1].Value;
            var doc = scope.DocumentScope;
            if (doc.Kind == ScopeKind.Fragment && doc.Bindings.TryGetValue(name, out var bound))
                return Describe(bound, doc.Parent!);
            var decl = doc.Document.Parameter(name);
            if (doc.Kind == ScopeKind.Template)
                return $"params.{name}" + (decl?.Default is { } d ? $" (default {Describe(d, doc)})" : "");
            return decl?.Default is { } fd ? Describe(fd, doc) : $"params.{name} (unbound)";
        }

        return value.ToFlow();
    }
}
