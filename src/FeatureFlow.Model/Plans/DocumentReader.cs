using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model.Plans;

/// <summary>Reads a template or fragment document's header: everything except the steps (docs/03 section 1; LOG 62).</summary>
public static class DocumentReader
{
    public static readonly IReadOnlyList<string> ParameterTypes = ["string", "number", "boolean", "array", "object", "artifact", "reference"];

    public sealed record Result(DocumentInfo Info, YSeq? Steps, MapReader? Defaults);

    /// <param name="kind">"template" or "fragment".</param>
    public static Result? Read(YNode root, string kind, string name, string file, LibraryReference? reference, DiagnosticBag d)
    {
        var r = MapReader.Of(root, d, $"a {kind}");
        if (r is null)
            return null;
        var meta = LibraryItemReader.Header(r, kind, name, d, out var deprecated);
        string? level = null, description = null, branchPattern = null;
        SourceLocation? branchPatternLocation = null;
        if (meta is not null)
        {
            description = meta.Map["description"].ScalarText();
            level = kind == "template" ? meta.String("level", required: true) : null;
            branchPattern = meta.String("branch_pattern");
            branchPatternLocation = meta.Map["branch_pattern"]?.Location;
            meta.Finish();
        }

        var parameters = new List<ParameterDecl>();
        if (MapReader.Of(r.Node("parameters"), d, "parameters") is { } ps)
            foreach (var (key, value) in ps.Map.Entries)
            {
                ps.Node(key.Text);
                if (!StepReader.SnakeCase().IsMatch(key.Text))
                    ps.Error($"parameter name '{key.Text}' must be snake_case", key.Location);
                if (MapReader.Of(value, d, $"parameter {key.Text}") is not { } p)
                    continue;
                var type = p.Enum("type", ParameterTypes, required: true) ?? "string";
                var required = p.Bool("required") ?? false;
                var dflt = p.Node("default");
                var desc = p.String("description");
                p.Finish();
                parameters.Add(new ParameterDecl(key.Text, type, required, dflt, desc, key.Location));
            }

        var types = new List<ItemRef>();
        foreach (var item in LibraryItemReader.Seq(r.Node("artifact_types"), d, "artifact_types"))
            if (LibraryItemReader.Ref(item, r, "artifact_types entry") is { } t)
                types.Add(t);

        long? maxWorkItems = null;
        if (MapReader.Of(r.Node("budget"), d, "budget") is { } b)
        {
            maxWorkItems = b.Int("max_work_items");
            b.Finish();
        }

        var exits = new List<Outcome>();
        SourceLocation? exitsLocation = null;
        var exitsNode = r.Node("exits");
        if (kind == "fragment")
        {
            exitsLocation = exitsNode?.Location;
            if (MapReader.Of(exitsNode, d, "exits") is { } ex)
                foreach (var (key, value) in ex.Map.Entries)
                {
                    ex.Node(key.Text);
                    var cls = value is YScalar { IsNull: true } ? OutcomeClass.None : OutcomeClasses.Parse(value.ScalarText());
                    if (cls is null)
                        ex.Error($"exit '{key.Text}' has class {value.Describe()}; classes are success, failure, blocked or ~", value.Location);
                    else
                        exits.Add(new Outcome(key.Text, cls.Value));
                }
            else if (exitsNode is null)
                r.Error("a fragment needs exits (LOG 62)", r.Map.Location);
        }
        else if (exitsNode is not null)
            r.Error("only a fragment declares exits", exitsNode.Location);

        var defaults = MapReader.Of(r.Node("defaults"), d, "defaults");

        YSeq? steps = null;
        var stepsNode = r.Required("steps");
        if (stepsNode is YSeq s)
            steps = s;
        else if (stepsNode is not null)
            r.Error("steps must be a list", stepsNode.Location);

        r.Finish();
        var info = new DocumentInfo
        {
            Kind = kind,
            Name = name,
            File = file,
            Reference = reference,
            Level = level,
            Description = description,
            BranchPattern = branchPattern,
            BranchPatternLocation = branchPatternLocation,
            Parameters = parameters,
            ArtifactTypes = types,
            Exits = exits,
            ExitsLocation = exitsLocation,
            MaxWorkItems = maxWorkItems,
            DeprecatedBy = deprecated,
        };
        return new Result(info, steps, defaults);
    }

    /// <summary>Reads <c>defaults</c>: route defaults by class and by outcome name, and limits.</summary>
    public static Defaults ReadDefaults(MapReader? r, Scope scope, DiagnosticBag d)
    {
        if (r is null)
            return Defaults.None;
        var byClass = new Dictionary<OutcomeClass, RouteSpec>();
        var byOutcome = new Dictionary<string, RouteSpec>();
        if (MapReader.Of(r.Node("routes"), d, "defaults.routes") is { } routes)
        {
            if (MapReader.Of(routes.Node("by_class"), d, "defaults.routes.by_class") is { } bc)
                foreach (var (key, value) in bc.Map.Entries)
                {
                    bc.Node(key.Text);
                    var cls = OutcomeClasses.Parse(key.Text);
                    if (cls is null or OutcomeClass.None)
                    {
                        bc.Error($"by_class key must be success, failure or blocked, not '{key.Text}'", key.Location);
                        continue;
                    }

                    if (StepReader.ReadTarget(value, scope, d, null, $"default route for {key.Text}") is { } t)
                        byClass[cls.Value] = new RouteSpec(key.Text, t, scope, key.Location);
                }

            if (MapReader.Of(routes.Node("by_outcome"), d, "defaults.routes.by_outcome") is { } bo)
                foreach (var (key, value) in bo.Map.Entries)
                {
                    bo.Node(key.Text);
                    if (StepReader.ReadTarget(value, scope, d, null, $"default route for {key.Text}") is { } t)
                        byOutcome[key.Text] = new RouteSpec(key.Text, t, scope, key.Location);
                }

            routes.Finish();
        }

        var limits = StepReader.ReadLimits(r.Node("limits"), d, null);
        r.Finish();
        return new Defaults(byClass, byOutcome, limits);
    }
}
