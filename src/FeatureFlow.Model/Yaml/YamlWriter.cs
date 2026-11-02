using System.Globalization;
using System.Text;
using System.Text.Json;

namespace FeatureFlow.Model.Yaml;

/// <summary>
/// Writes a node tree, or plain .NET values, as JSON text, which is also YAML. <see cref="YamlText.Parse"/>
/// reads the text back to a tree with the same meaning: a plain scalar that reads as null, a boolean or a
/// number is written bare; every other scalar is a quoted string. Source positions are not kept.
/// Used to carry patches, parameters and outputs through Temporal payloads.
/// </summary>
public static class YamlWriter
{
    public static string Write(YNode node)
    {
        var text = new StringBuilder();
        Write(node, text);
        return text.ToString();
    }

    static void Write(YNode node, StringBuilder text)
    {
        switch (node)
        {
            case YScalar s when s.Plain && s.IsNull:
                text.Append("null");
                break;
            case YScalar s when s.Plain && (s.AsBool() is not null || s.AsLong() is not null || IsFiniteNumber(s)):
                text.Append(s.Text);
                break;
            case YScalar s:
                text.Append(JsonSerializer.Serialize(s.Text));
                break;
            case YSeq q:
                text.Append('[');
                for (var i = 0; i < q.Items.Count; i++)
                {
                    if (i > 0)
                        text.Append(", ");
                    Write(q.Items[i], text);
                }

                text.Append(']');
                break;
            case YMap m:
                text.Append('{');
                for (var i = 0; i < m.Entries.Count; i++)
                {
                    if (i > 0)
                        text.Append(", ");
                    text.Append(JsonSerializer.Serialize(m.Entries[i].Key.Text)).Append(": ");
                    Write(m.Entries[i].Value, text);
                }

                text.Append('}');
                break;
            case YUnbound u:
                text.Append("{\"param\": ").Append(JsonSerializer.Serialize(u.Parameter)).Append('}');
                break;
        }
    }

    /// <summary>A plain number JSON can hold; anything else (an infinity, a hex form) is written as a quoted string.</summary>
    static bool IsFiniteNumber(YScalar s) =>
        s.AsNumber() is { } d && double.IsFinite(d)
        && double.TryParse(s.Text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out _);

    /// <summary>Writes plain values: strings, whole and fractional numbers, booleans, null, lists and string-keyed maps.</summary>
    public static string WritePlain(object? value) => JsonSerializer.Serialize(ToJsonable(value));

    static object? ToJsonable(object? value) => value switch
    {
        null or string or bool or long or int or double or decimal => value,
        IReadOnlyDictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => ToJsonable(kv.Value)),
        IDictionary<string, object?> map => map.ToDictionary(kv => kv.Key, kv => ToJsonable(kv.Value)),
        System.Collections.IEnumerable list => list.Cast<object?>().Select(ToJsonable).ToList(),
        _ => value.ToString(),
    };

    /// <summary>Reads text written by <see cref="WritePlain"/> back to plain values, as <see cref="YNodeExtensions.ToPlain"/> gives them.</summary>
    public static object? ReadPlain(string text)
    {
        var d = new DiagnosticBag();
        var node = YamlText.Parse(text, "value", d);
        if (node is null)
            throw new FormatException(string.Join("; ", d));
        return node.ToPlain();
    }

    /// <summary>Parses text this class wrote; throws on malformed text, which only a programming error can produce.</summary>
    public static YNode Read(string text, string file = "carried")
    {
        var d = new DiagnosticBag();
        return YamlText.Parse(text, file, d) ?? throw new FormatException(string.Join("; ", d));
    }
}
