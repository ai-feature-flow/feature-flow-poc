using System.Globalization;

namespace FeatureFlow.Model.Yaml;

/// <summary>
/// A YAML node with its source position. Library documents are read into this tree first, because a
/// fragment's <c>{ param: name }</c> may stand in any field and is substituted before the document is
/// bound to the model (LOG 62).
/// </summary>
public abstract class YNode(SourceLocation location)
{
    public SourceLocation Location { get; } = location;

    public abstract string Describe();
}

public sealed class YScalar(string text, bool plain, SourceLocation location) : YNode(location)
{
    public string Text { get; } = text;

    /// <summary>True when unquoted, so that <c>~</c>, <c>true</c> and numbers keep their YAML meaning.</summary>
    public bool Plain { get; } = plain;

    public bool IsNull => Plain && Text is "" or "~" or "null" or "Null" or "NULL";

    public bool? AsBool() => Plain ? Text switch
    {
        "true" or "True" or "TRUE" => true,
        "false" or "False" or "FALSE" => false,
        _ => null,
    } : null;

    public long? AsLong() =>
        Plain && long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) ? value : null;

    public double? AsNumber() =>
        Plain && double.TryParse(Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    public override string Describe() => IsNull ? "null" : $"'{Text}'";
}

public sealed class YMap(IReadOnlyList<KeyValuePair<YScalar, YNode>> entries, SourceLocation location) : YNode(location)
{
    public IReadOnlyList<KeyValuePair<YScalar, YNode>> Entries { get; } = entries;

    public IEnumerable<string> Keys => Entries.Select(e => e.Key.Text);

    public YNode? this[string key] => Entries.FirstOrDefault(e => e.Key.Text == key).Value;

    public bool Has(string key) => Entries.Any(e => e.Key.Text == key);

    public override string Describe() => "a map";
}

public sealed class YSeq(IReadOnlyList<YNode> items, SourceLocation location) : YNode(location)
{
    public IReadOnlyList<YNode> Items { get; } = items;

    public override string Describe() => "a list";
}

/// <summary>
/// A fragment's <c>{ param: name }</c> left in place because the parameter has no literal value: when a
/// fragment is validated on its own, or when a binding is missing (check 14 reports that).
/// </summary>
public sealed class YUnbound(string parameter, SourceLocation location) : YNode(location)
{
    public string Parameter { get; } = parameter;

    public override string Describe() => $"unbound parameter '{Parameter}'";
}

public static class YNodeExtensions
{
    /// <summary>A map of exactly one key, such as <c>{ expr: "..." }</c> or <c>{ param: name }</c>.</summary>
    public static bool IsSingleKeyMap(this YNode node, string key, out YNode value)
    {
        if (node is YMap { Entries.Count: 1 } map && map.Entries[0].Key.Text == key)
        {
            value = map.Entries[0].Value;
            return true;
        }

        value = null!;
        return false;
    }

    public static string? ScalarText(this YNode? node) => node is YScalar { IsNull: false } s ? s.Text : null;

    /// <summary>Converts a literal node to plain .NET values: string, long, double, bool, null, lists and maps.</summary>
    public static object? ToPlain(this YNode node) => node switch
    {
        YScalar s when s.IsNull => null,
        YScalar s when s.AsBool() is { } b => b,
        YScalar s when s.AsLong() is { } l => l,
        YScalar s when s.AsNumber() is { } d => d,
        YScalar s => s.Text,
        YSeq q => q.Items.Select(ToPlain).ToList(),
        YMap m => m.Entries.ToDictionary(e => e.Key.Text, e => ToPlain(e.Value)),
        _ => null,
    };

    /// <summary>Renders a literal node in a compact, flow-style form for messages and the readable form.</summary>
    public static string ToFlow(this YNode node) => node switch
    {
        YScalar s when s.IsNull => "~",
        YScalar s => s.Plain ? s.Text : $"\"{s.Text}\"",
        YSeq q => "[" + string.Join(", ", q.Items.Select(ToFlow)) + "]",
        YMap m => "{ " + string.Join(", ", m.Entries.Select(e => $"{e.Key.Text}: {ToFlow(e.Value)}")) + " }",
        YUnbound u => $"{{ param: {u.Parameter} }}",
        _ => "?",
    };
}
