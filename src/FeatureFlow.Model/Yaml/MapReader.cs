namespace FeatureFlow.Model.Yaml;

/// <summary>
/// Reads one YAML map into the model. Every key read is remembered; <see cref="Finish"/> reports any key
/// left unread as a check 1 error, so unknown keys are refused rather than ignored (docs/07 section 4).
/// </summary>
public sealed class MapReader
{
    readonly YMap map;
    readonly DiagnosticBag diagnostics;
    readonly string context;
    readonly string? stepId;
    readonly HashSet<string> read = [];

    public MapReader(YMap map, DiagnosticBag diagnostics, string context, string? stepId = null)
    {
        this.map = map;
        this.diagnostics = diagnostics;
        this.context = context;
        this.stepId = stepId;
    }

    public YMap Map => map;

    /// <summary>Reads a map node, or reports that the node is not a map and returns null.</summary>
    public static MapReader? Of(YNode? node, DiagnosticBag diagnostics, string context, string? stepId = null)
    {
        if (node is null)
            return null;
        if (node is YMap m)
            return new MapReader(m, diagnostics, context, stepId);
        if (node is not YUnbound)
            diagnostics.Add(Checks.Schema, $"{context} must be a map, not {node.Describe()}", node.Location, stepId);
        return null;
    }

    public bool Has(string key) => map.Has(key);

    public YNode? Node(string key)
    {
        read.Add(key);
        return map[key];
    }

    public YNode? Required(string key)
    {
        var node = Node(key);
        if (node is null || node is YScalar { IsNull: true })
        {
            Error($"{context} needs '{key}'", node?.Location ?? map.Location);
            return null;
        }

        return node;
    }

    public string? String(string key, bool required = false)
    {
        var node = required ? Required(key) : Node(key);
        return AsString(node, $"{context}.{key}");
    }

    public string? AsString(YNode? node, string what)
    {
        switch (node)
        {
            case null or YScalar { IsNull: true } or YUnbound:
                return null;
            case YScalar s:
                return s.Text;
            default:
                Error($"{what} must be a string, not {node.Describe()}", node.Location);
                return null;
        }
    }

    public bool? Bool(string key)
    {
        var node = Node(key);
        switch (node)
        {
            case null or YScalar { IsNull: true } or YUnbound:
                return null;
            case YScalar s when s.AsBool() is { } b:
                return b;
            default:
                Error($"{context}.{key} must be true or false, not {node.Describe()}", node.Location);
                return null;
        }
    }

    public long? Int(string key)
    {
        var node = Node(key);
        switch (node)
        {
            case null or YScalar { IsNull: true } or YUnbound:
                return null;
            case YScalar s when s.AsLong() is { } l:
                return l;
            default:
                Error($"{context}.{key} must be a whole number, not {node.Describe()}", node.Location);
                return null;
        }
    }

    public IReadOnlyList<string> StringList(string key)
    {
        var node = Node(key);
        switch (node)
        {
            case null or YScalar { IsNull: true } or YUnbound:
                return [];
            case YSeq seq:
                var list = new List<string>();
                foreach (var item in seq.Items)
                    if (AsString(item, $"{context}.{key} entry") is { } s)
                        list.Add(s);
                return list;
            default:
                Error($"{context}.{key} must be a list, not {node.Describe()}", node.Location);
                return [];
        }
    }

    public string? Enum(string key, IReadOnlyCollection<string> allowed, bool required = false)
    {
        var value = String(key, required);
        if (value is not null && !allowed.Contains(value))
        {
            Error($"{context}.{key} must be one of {string.Join(", ", allowed)}, not '{value}'", map[key]!.Location);
            return null;
        }

        return value;
    }

    public void Error(string message, SourceLocation location) => diagnostics.Add(Checks.Schema, message, location, stepId);

    /// <summary>Reports every key not read. Call once all known keys are read.</summary>
    public void Finish()
    {
        foreach (var (key, _) in map.Entries)
            if (!read.Contains(key.Text))
                Error($"unknown key '{key.Text}' in {context}", key.Location);
    }
}
