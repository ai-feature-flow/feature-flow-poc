using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Testing.Workflows;

/// <summary>What a step does on one visit: an outcome, or an error class first (docs/09 section 4.3).</summary>
public sealed record Visit(string? Outcome, string? Error = null, int Times = 1);

/// <summary>A workflow test file (docs/09 section 4.3; LOG 75).</summary>
public sealed class WorkflowTest
{
    public required string Name { get; init; }
    public required string File { get; init; }
    public string? Description { get; init; }
    public required LibraryReference Template { get; init; }
    public Dictionary<string, YNode> Parameters { get; } = new(StringComparer.Ordinal);
    public List<string> Repositories { get; } = [];

    /// <summary>Step id to outcome per visit. The last entry applies to every later visit.</summary>
    public Dictionary<string, List<Visit>> Script { get; } = new(StringComparer.Ordinal);

    /// <summary>Step id to outputs per visit.</summary>
    public Dictionary<string, List<YMap>> Outputs { get; } = new(StringComparer.Ordinal);

    /// <summary><c>findings.&lt;name&gt;</c> values, one per route evaluation that reads the name.</summary>
    public Dictionary<string, List<long>> Findings { get; } = new(StringComparer.Ordinal);

    /// <summary>A <c>plan.expand</c> step id to the items of its list.</summary>
    public Dictionary<string, List<YNode>> Expand { get; } = new(StringComparer.Ordinal);

    public PatchDocument? Patch { get; init; }

    public List<string>? ExpectPath { get; init; }
    public List<string>? ExpectPathIncludes { get; init; }
    public string? ExpectStatus { get; init; }
    public List<int> ExpectValidationErrors { get; } = [];
    public Dictionary<string, long> ExpectExecutions { get; } = new(StringComparer.Ordinal);
    public List<string>? ExpectEscalated { get; init; }

    /// <summary>The steps a <c>clean_up</c> route reset the branch for, in order.</summary>
    public List<string>? ExpectCleanUp { get; init; }
}

public static class WorkflowTestReader
{
    public static readonly IReadOnlyList<string> ErrorClasses = ["infrastructure", "work"];

    /// <summary>Reads a test file. Errors are check 1 diagnostics.</summary>
    public static WorkflowTest? Read(string path, DiagnosticBag d)
    {
        var file = Path.GetFileName(path);
        if (YamlText.Parse(System.IO.File.ReadAllText(path), file, d) is not { } node || MapReader.Of(node, d, "a workflow test") is not { } r)
            return null;
        if (r.String("api_version", required: true) is { } version and not "feature-flow/v1")
            r.Error($"api_version must be feature-flow/v1, not '{version}'", r.Map["api_version"]!.Location);
        if (r.String("kind", required: true) is { } kind and not "workflow_test")
            r.Error($"kind must be workflow_test, not '{kind}'", r.Map["kind"]!.Location);
        var name = r.String("name", required: true) ?? Path.GetFileNameWithoutExtension(path);
        var description = r.String("description");
        var templateText = r.String("template", required: true);
        var template = templateText is null ? null : LibraryReference.TryParse(templateText);
        if (templateText is not null && template is not { Type: "templates" })
            r.Error($"template must reference a template, such as std:templates/ticket_delivery, not '{templateText}'", r.Map["template"]!.Location);

        PatchDocument? patch = null;
        if (r.Node("patch") is { } patchNode)
            patch = PatchReader.Read(patchNode, d);

        var expect = MapReader.Of(r.Required("expect"), d, "expect");
        var test = new WorkflowTest
        {
            Name = name,
            File = path,
            Description = description,
            Template = template ?? new LibraryReference("std", null, "templates", "unknown"),
            Patch = patch,
            ExpectPath = expect?.Has("path") == true ? expect.StringList("path").ToList() : null,
            ExpectPathIncludes = expect?.Has("path_includes") == true ? expect.StringList("path_includes").ToList() : null,
            ExpectStatus = expect?.Enum("status", ["running", "completed", "failed", "rejected", "partial", "aborted", "incomplete"]),
            ExpectEscalated = expect?.Has("escalated") == true ? expect.StringList("escalated").ToList() : null,
            ExpectCleanUp = expect?.Has("clean_up") == true ? expect.StringList("clean_up").ToList() : null,
        };

        if (MapReader.Of(r.Node("parameters"), d, "parameters") is { } ps)
            foreach (var (key, value) in ps.Map.Entries)
            {
                ps.Node(key.Text);
                test.Parameters[key.Text] = value;
            }

        test.Repositories.AddRange(r.Has("repositories") ? r.StringList("repositories") : ["acme/api"]);

        if (MapReader.Of(r.Node("script"), d, "script") is { } script)
            foreach (var (key, value) in script.Map.Entries)
            {
                script.Node(key.Text);
                // A work error fails `times` executions, each a visit of its own, then `then` (LOG 75).
                var visits = (value is YSeq seq ? seq.Items : [value]).Select(v => ReadVisit(v, d)).OfType<Visit>()
                    .SelectMany(v => v.Error == "work"
                        ? Enumerable.Repeat(new Visit(null, "work"), v.Times).Concat(v.Outcome is null ? [] : [new Visit(v.Outcome)])
                        : [v])
                    .ToList();
                if (visits.Count > 0)
                    test.Script[key.Text] = visits;
            }

        if (MapReader.Of(r.Node("outputs"), d, "outputs") is { } outputs)
            foreach (var (key, value) in outputs.Map.Entries)
            {
                outputs.Node(key.Text);
                var entries = value is YSeq seq ? seq.Items : [value];
                if (key.Text.StartsWith("findings.", StringComparison.Ordinal))
                {
                    var counts = entries.Select(e => (e as YScalar)?.AsLong()).ToList();
                    if (counts.Any(c => c is null))
                        outputs.Error($"{key.Text} takes whole numbers, one per route evaluation that reads it", value.Location);
                    else
                        test.Findings[key.Text["findings.".Length..]] = counts.Select(c => c!.Value).ToList();
                    continue;
                }

                var maps = entries.OfType<YMap>().ToList();
                if (maps.Count != entries.Count)
                    outputs.Error($"outputs for {key.Text} are a map, or a list of maps, one per visit", value.Location);
                else
                    test.Outputs[key.Text] = maps;
            }

        if (MapReader.Of(r.Node("expand"), d, "expand") is { } expand)
            foreach (var (key, value) in expand.Map.Entries)
            {
                expand.Node(key.Text);
                if (value is YSeq seq)
                    test.Expand[key.Text] = seq.Items.ToList();
                else
                    expand.Error($"expand.{key.Text} is the list plan.expand reads", value.Location);
            }

        if (expect is not null)
        {
            if (expect.Node("validation_errors") is YSeq errors)
                foreach (var item in errors.Items)
                    if ((item as YScalar)?.AsLong() is { } check)
                        test.ExpectValidationErrors.Add((int)check);
                    else
                        expect.Error("validation_errors lists check numbers", item.Location);
            if (MapReader.Of(expect.Node("executions"), d, "expect.executions") is { } ex)
                foreach (var (key, _) in ex.Map.Entries)
                    if (ex.Int(key.Text) is { } count)
                        test.ExpectExecutions[key.Text] = count;
            expect.Finish();
        }

        r.Finish();
        return test;
    }

    static Visit? ReadVisit(YNode node, DiagnosticBag d)
    {
        if (node is YScalar { IsNull: false } s)
            return new Visit(s.Text);
        if (MapReader.Of(node, d, "a script entry") is not { } m)
            return null;
        var error = m.Enum("error", ErrorClasses, required: true);
        var times = m.Int("times") ?? 1;
        var then = m.String("then");
        m.Finish();
        return new Visit(then, error, (int)times);
    }
}
