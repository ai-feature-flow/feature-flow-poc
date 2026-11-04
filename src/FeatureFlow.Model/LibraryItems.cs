using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model;

public sealed record LibraryDependency(string Name, string? Version);

public sealed record Entrypoint(string Template, string? When, SourceLocation Location);

/// <summary>A library's <c>library.yaml</c> (docs/08 section 2).</summary>
public sealed record Manifest(
    string Name,
    string? Version,
    string? Description,
    IReadOnlyList<string> RequiredCapabilities,
    IReadOnlyList<string> RequiredToolsets,
    string? RequiredEngine,
    long? PlanSchema,
    IReadOnlyList<LibraryDependency> Dependencies,
    YMap? Inputs,
    IReadOnlyList<string> Resources,
    IReadOnlyList<Entrypoint> Entrypoints,
    IReadOnlyList<string> NotificationEvents);

public sealed record RoleItem(string Name, string WriteAccess, IReadOnlyList<string> Tools, IReadOnlyList<string> Toolsets, ItemRef? DeprecatedBy);

public sealed record ArtifactTypeItem(string Name, string Format, bool Evidence, IReadOnlyList<ItemRef> CompatibleWith, IReadOnlyList<string> ProducedBy, ItemRef? DeprecatedBy);

public sealed record DecisionTypeItem(string Name, string? Description, SourceLocation Location)
{
    /// <summary>
    /// <c>default_approval.roles</c>: who may answer when no organisation approval rule matches the type
    /// (docs/05 section 4). The whole default is <see cref="DefaultApproval"/>.
    /// </summary>
    public IReadOnlyList<string> DefaultRoles { get; init; } = [];

    /// <summary>
    /// The whole <c>default_approval</c> (docs/05 section 4) as a rule matching this type: roles, <c>min_count</c>,
    /// <c>from_chat</c> and <c>auto</c>, applied when no organisation rule matches. Null where the library gives none.
    /// </summary>
    public ApprovalRule? DefaultApproval { get; init; }

    /// <summary>A declared name may be a pattern with <c>*</c> standing for one or more characters (docs/05 section 4).</summary>
    public bool Matches(string type)
    {
        if (!Name.Contains('*'))
            return Name == type;
        var pattern = "^" + string.Join(".+", Name.Split('*').Select(System.Text.RegularExpressions.Regex.Escape)) + "$";
        return System.Text.RegularExpressions.Regex.IsMatch(type, pattern);
    }
}

public sealed record StatusItem(string Name, string? Meaning);

public static class WriteAccess
{
    public static readonly IReadOnlyList<string> Levels = ["none", "branch", "repository"];

    public static int Rank(string level) => Levels.ToList().IndexOf(level);
}

/// <summary>Reads library items other than templates and fragments, refusing unknown keys (check 1).</summary>
public static class LibraryItemReader
{
    public const string ApiVersion = "feature-flow/v1";

    public static readonly IReadOnlyList<string> RoleTools = ["read", "search", "edit", "shell"];

    public static Manifest? ReadManifest(YNode root, DiagnosticBag d)
    {
        var r = MapReader.Of(root, d, "the manifest");
        if (r is null)
            return null;
        var name = r.String("name", required: true);
        var version = r.String("version");
        var description = r.String("description");

        IReadOnlyList<string> capabilities = [], toolsets = [];
        string? engine = null;
        long? planSchema = null;
        if (MapReader.Of(r.Node("requires"), d, "requires") is { } req)
        {
            engine = req.String("engine");
            planSchema = req.Int("plan_schema");
            capabilities = req.StringList("capabilities");
            toolsets = req.StringList("toolsets");
            req.Finish();
        }

        var dependencies = new List<LibraryDependency>();
        foreach (var item in Seq(r.Node("dependencies"), d, "dependencies"))
            if (MapReader.Of(item, d, "a dependency") is { } dep)
            {
                dependencies.Add(new LibraryDependency(dep.String("name", required: true) ?? "", dep.String("version")));
                dep.Finish();
            }

        var inputsNode = r.Node("inputs");
        var inputs = inputsNode as YMap;
        if (inputsNode is not null and not YMap and not YScalar { IsNull: true })
            d.Add(Checks.Schema, "inputs must be a map", inputsNode.Location);

        var resources = r.StringList("resources");

        var entrypoints = new List<Entrypoint>();
        foreach (var item in Seq(r.Node("entrypoints"), d, "entrypoints"))
            if (MapReader.Of(item, d, "an entrypoint") is { } ep)
            {
                entrypoints.Add(new Entrypoint(ep.String("template", required: true) ?? "", ep.String("when"), item.Location));
                ep.Finish();
            }

        IReadOnlyList<string> events = [];
        if (MapReader.Of(r.Node("notifications"), d, "notifications") is { } n)
        {
            events = n.StringList("events");
            n.Finish();
        }

        r.Finish();
        return name is null
            ? null
            : new Manifest(name, version, description, capabilities, toolsets, engine, planSchema, dependencies, inputs, resources, entrypoints, events);
    }

    /// <summary>Reads the common header: api_version, kind and metadata. Returns the metadata reader, unfinished.</summary>
    public static MapReader? Header(MapReader r, string kind, string expectedName, DiagnosticBag d, out ItemRef? deprecatedBy)
    {
        deprecatedBy = null;
        var api = r.String("api_version", required: true);
        if (api is not null && api != ApiVersion)
            r.Error($"api_version must be {ApiVersion}, not '{api}'", r.Map["api_version"]!.Location);
        var k = r.String("kind", required: true);
        if (k is not null && k != kind)
            r.Error($"kind must be '{kind}' here, not '{k}'", r.Map["kind"]!.Location);
        var meta = MapReader.Of(r.Required("metadata"), d, "metadata");
        if (meta is null)
            return null;
        var name = meta.String("name", required: true);
        if (name is not null && name != expectedName)
            meta.Error($"metadata.name '{name}' does not match the file name '{expectedName}'", meta.Map["name"]!.Location);
        meta.String("description");
        deprecatedBy = ReadDeprecated(meta, d);
        return meta;
    }

    /// <summary>
    /// <c>metadata.deprecated: { replacement: &lt;reference&gt; }</c> marks an item deprecated (check 35).
    /// </summary>
    static ItemRef? ReadDeprecated(MapReader meta, DiagnosticBag d)
    {
        if (MapReader.Of(meta.Node("deprecated"), d, "metadata.deprecated") is not { } dep)
            return null;
        var node = dep.Required("replacement");
        dep.String("reason");
        dep.Finish();
        return node is null ? null : Ref(node, dep, "metadata.deprecated.replacement");
    }

    public static RoleItem? ReadRole(YNode root, string name, DiagnosticBag d)
    {
        var r = MapReader.Of(root, d, "a role");
        if (r is null)
            return null;
        var meta = Header(r, "role", name, d, out var deprecated);
        meta?.Finish();
        var access = r.Enum("write_access", WriteAccess.Levels, required: true) ?? "none";
        var tools = r.StringList("tools");
        foreach (var tool in tools.Where(t => !RoleTools.Contains(t)))
            r.Error($"unknown tool class '{tool}'; tools are {string.Join(", ", RoleTools)} (LOG 65)", r.Map["tools"]!.Location);
        var toolsets = new List<string>();
        foreach (var item in Seq(r.Node("toolsets"), d, "toolsets"))
            if (MapReader.Of(item, d, "a toolset") is { } ts)
            {
                if (ts.String("name", required: true) is { } n)
                    toolsets.Add(n);
                ts.StringList("tools");
                ts.Finish();
            }

        r.StringList("requires");
        r.Finish();
        return new RoleItem(name, access, tools, toolsets, deprecated);
    }

    public static ArtifactTypeItem? ReadArtifactType(YNode root, string name, DiagnosticBag d)
    {
        var r = MapReader.Of(root, d, "an artifact type");
        if (r is null)
            return null;
        var meta = Header(r, "artifact_type", name, d, out var deprecated);
        meta?.Finish();
        var format = r.Enum("format", ["json", "yaml", "markdown"], required: true) ?? "json";
        var evidence = r.Bool("evidence") ?? false;
        var producedBy = r.StringList("produced_by");
        var compatible = new List<ItemRef>();
        foreach (var item in Seq(r.Node("compatible_with"), d, "compatible_with"))
            if (Ref(item, r, "compatible_with entry") is { } c)
                compatible.Add(c);
        if (r.Node("validators") is { } validators and not YSeq and not YScalar { IsNull: true })
            r.Error("validators must be a list", validators.Location);
        var schema = r.Node("schema");
        if (schema is YScalar s && s.Text != "engine")
            r.Error("schema must be a JSON Schema map, or 'engine' for an engine-defined shape", schema.Location);
        if (MapReader.Of(r.Node("markdown"), d, "markdown") is { } md)
        {
            md.StringList("required_headings");
            if (MapReader.Of(md.Node("front_matter"), d, "markdown.front_matter") is { } fm)
            {
                fm.StringList("required");
                fm.Finish();
            }

            md.StringList("identifier_patterns");
            md.Finish();
        }

        r.StringList("rules");
        r.Finish();
        return new ArtifactTypeItem(name, format, evidence, compatible, producedBy, deprecated);
    }

    public static IReadOnlyList<DecisionTypeItem> ReadDecisionTypes(YNode root, DiagnosticBag d)
    {
        var list = new List<DecisionTypeItem>();
        var r = MapReader.Of(root, d, "a decision types file");
        if (r is null)
            return list;
        var api = r.String("api_version", required: true);
        if (api is not null && api != ApiVersion)
            r.Error($"api_version must be {ApiVersion}", r.Map["api_version"]!.Location);
        if (r.String("kind", required: true) is { } k && k != "decision_types")
            r.Error("kind must be 'decision_types'", r.Map["kind"]!.Location);
        foreach (var item in Seq(r.Required("decision_types"), d, "decision_types"))
            if (MapReader.Of(item, d, "a decision type") is { } dt)
            {
                var name = dt.String("name", required: true);
                var description = dt.String("description");
                IReadOnlyList<string> roles = [];
                ApprovalRule? approval = null;
                if (MapReader.Of(dt.Node("default_approval"), d, "default_approval") is { } a)
                {
                    roles = a.StringList("roles");
                    var minCount = a.Int("min_count") ?? 1;
                    if (minCount < 1)
                        a.Error("default_approval.min_count must be at least 1", a.Map.Location);
                    approval = new ApprovalRule(name ?? "", roles, minCount, a.Bool("from_chat") ?? false, new Cap<bool>(a.Bool("auto") ?? false, false));
                    a.Finish();
                }

                dt.Finish();
                if (name is not null)
                    list.Add(new DecisionTypeItem(name, description, item.Location) { DefaultRoles = roles, DefaultApproval = approval });
            }

        r.Finish();
        return list;
    }

    public static IReadOnlyList<StatusItem> ReadStatusMap(YNode root, DiagnosticBag d)
    {
        var list = new List<StatusItem>();
        var r = MapReader.Of(root, d, "the status map");
        if (r is null)
            return list;
        foreach (var item in Seq(r.Required("statuses"), d, "statuses"))
            if (MapReader.Of(item, d, "a status") is { } s)
            {
                if (s.String("name", required: true) is { } n)
                    list.Add(new StatusItem(n, s.String("meaning")));
                s.Finish();
            }

        r.Finish();
        return list;
    }

    public static IReadOnlyList<YNode> Seq(YNode? node, DiagnosticBag d, string what)
    {
        switch (node)
        {
            case null or YScalar { IsNull: true }:
                return [];
            case YSeq seq:
                return seq.Items;
            default:
                d.Add(Checks.Schema, $"{what} must be a list, not {node.Describe()}", node.Location);
                return [];
        }
    }

    /// <summary>Reads a reference field. Malformed text is a check 1 error; whether it resolves is checked later.</summary>
    public static ItemRef? Ref(YNode? node, MapReader r, string what)
    {
        switch (node)
        {
            case null or YScalar { IsNull: true }:
                return null;
            case YUnbound u:
                return new ItemRef(u.Parameter, null, u.Location, Unbound: true);
            case YScalar s:
                var parsed = LibraryReference.TryParse(s.Text);
                if (parsed is null)
                {
                    r.Error($"{what} '{s.Text}' is not a library reference (<library>:<type>/<name>)", s.Location);
                    return null;
                }

                return new ItemRef(s.Text, parsed, s.Location);
            default:
                r.Error($"{what} must be a library reference, not {node.Describe()}", node.Location);
                return null;
        }
    }
}
