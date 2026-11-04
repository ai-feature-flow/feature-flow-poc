using System.Globalization;
using System.Text.RegularExpressions;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model;

/// <summary>One way a document breaks its type's schema: where (a path such as <c>requirements[0].owner</c>), the rule, and why.</summary>
public sealed record SchemaError(string Path, string Rule, string Message, SourceLocation? Location)
{
    public override string ToString() => $"{Path}{(Location is null ? "" : $" (line {Location.Line})")}: {Message} [{Rule}]";
}

/// <summary>
/// The JSON Schema keywords an artifact type may use (docs/04 section 10.2), checked against a parsed YAML or JSON
/// document. The subset std's types use, plus their obvious neighbours: <c>type</c>, <c>properties</c>, <c>required</c>,
/// <c>additionalProperties</c>, <c>items</c>, <c>minItems</c>, <c>maxItems</c>, <c>minLength</c>, <c>maxLength</c>,
/// <c>pattern</c>, <c>minimum</c>, <c>maximum</c>, <c>enum</c>, <c>const</c>, <c>not</c>, and <c>$ref</c> to <c>#/$defs/</c>.
/// A library that uses any other keyword is refused when it loads (<see cref="Problems"/>), so no rule is silently skipped
/// (LOG 246). Errors name the field and the rule, for an agent's correction turn (docs/01 section 5.4).
/// </summary>
public static class ArtifactSchema
{
    public static readonly IReadOnlySet<string> Keywords = new HashSet<string>(StringComparer.Ordinal)
    {
        "type", "properties", "required", "additionalProperties", "items", "minItems", "maxItems", "minLength", "maxLength",
        "pattern", "minimum", "maximum", "enum", "const", "not", "$ref", "$defs",
    };

    static readonly IReadOnlySet<string> Annotations = new HashSet<string>(StringComparer.Ordinal)
    {
        "description", "title", "default", "examples", "$comment", "$schema", "format",
    };

    static readonly IReadOnlySet<string> TypeNames = new HashSet<string>(StringComparer.Ordinal)
    {
        "object", "array", "string", "integer", "number", "boolean", "null",
    };

    /// <summary>What makes a schema unusable here: a keyword outside the subset, an unknown type, a bad pattern or reference.</summary>
    public static IReadOnlyList<(string Message, SourceLocation Location)> Problems(YNode schema)
    {
        var problems = new List<(string, SourceLocation)>();
        Walk(schema, schema, problems);
        return problems;
    }

    static void Walk(YNode node, YNode root, List<(string, SourceLocation)> problems)
    {
        if (node is YScalar { Plain: true } b && b.AsBool() is not null)
            return;
        if (node is not YMap map)
        {
            problems.Add(("a schema must be a map (or true or false)", node.Location));
            return;
        }

        foreach (var (key, value) in map.Entries)
        {
            switch (key.Text)
            {
                case "properties" or "$defs":
                    if (value is YMap children)
                        foreach (var child in children.Entries)
                            Walk(child.Value, root, problems);
                    else
                        problems.Add(($"{key.Text} must be a map of name to schema", value.Location));
                    break;
                case "items" or "additionalProperties" or "not":
                    Walk(value, root, problems);
                    break;
                case "type":
                    foreach (var name in value is YSeq seq ? seq.Items : [value])
                        if (name is not YScalar s || !TypeNames.Contains(s.Text))
                            problems.Add(($"type must be one of {string.Join(", ", TypeNames)}", name.Location));
                    break;
                case "pattern":
                    if (value is not YScalar p || !ValidPattern(p.Text))
                        problems.Add(("pattern must be a regular expression", value.Location));
                    break;
                case "$ref":
                    if (value is not YScalar r || Resolve(root, r.Text) is null)
                        problems.Add(("$ref must name a schema under #/$defs/ in this type", value.Location));
                    break;
                default:
                    if (!Keywords.Contains(key.Text) && !Annotations.Contains(key.Text))
                        problems.Add(($"schema keyword '{key.Text}' is not supported (supported: {string.Join(", ", Keywords)})", key.Location));
                    break;
            }
        }
    }

    static bool ValidPattern(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    static YNode? Resolve(YNode root, string reference) =>
        reference.StartsWith("#/$defs/", StringComparison.Ordinal) && root is YMap map && map["$defs"] is YMap defs ? defs[reference["#/$defs/".Length..]] : null;

    /// <summary>The ways <paramref name="document"/> breaks <paramref name="schema"/>, at most <paramref name="limit"/>.</summary>
    public static IReadOnlyList<SchemaError> Check(YNode schema, YNode document, int limit = 20)
    {
        var errors = new List<SchemaError>();
        Validate(schema, schema, document, "", errors, limit, 0);
        return errors;
    }

    static void Validate(YNode schema, YNode root, YNode value, string path, List<SchemaError> errors, int limit, int depth)
    {
        if (errors.Count >= limit || depth > 64)
            return;
        if (schema is YScalar { Plain: true } boolean && boolean.AsBool() is { } allowed)
        {
            if (!allowed)
                Add(errors, path, "false", "is not allowed here", value);
            return;
        }

        if (schema is not YMap s)
            return;
        if (s["$ref"] is YScalar reference && Resolve(root, reference.Text) is { } target)
            Validate(target, root, value, path, errors, limit, depth + 1);

        if (s["type"] is { } type)
        {
            var names = type is YSeq seq ? seq.Items.OfType<YScalar>().Select(t => t.Text).ToList() : [((YScalar)type).Text];
            if (!names.Any(n => Is(value, n)))
            {
                Add(errors, path, "type", $"must be {string.Join(" or ", names.Select(Describe))}, not {DescribeValue(value)}", value);
                return;
            }
        }

        if (s["enum"] is YSeq options && !options.Items.Any(o => Same(o, value)))
            Add(errors, path, "enum", $"must be one of {string.Join(", ", options.Items.Select(YamlWriter.Write))}, not {YamlWriter.Write(value)}", value);
        if (s["const"] is { } constant && !Same(constant, value))
            Add(errors, path, "const", $"must be {YamlWriter.Write(constant)}, not {YamlWriter.Write(value)}", value);

        if (value is YScalar text && Kind(value) == "string")
        {
            var length = new StringInfo(text.Text).LengthInTextElements;
            if (Long(s, "minLength") is { } min && length < min)
                Add(errors, path, "minLength", min == 1 ? "must not be empty" : $"must be at least {min} characters", value);
            if (Long(s, "maxLength") is { } max && length > max)
                Add(errors, path, "maxLength", $"must be at most {max} characters", value);
            if (s["pattern"] is YScalar pattern && !Regex.IsMatch(text.Text, pattern.Text, RegexOptions.None, TimeSpan.FromSeconds(1)))
                Add(errors, path, "pattern", $"'{text.Text}' must match {pattern.Text}", value);
        }

        if (value is YScalar number && number.AsNumber() is { } n && Kind(value) is "integer" or "number")
        {
            if (Number(s, "minimum") is { } minimum && n < minimum)
                Add(errors, path, "minimum", $"must be at least {minimum.ToString(CultureInfo.InvariantCulture)}", value);
            if (Number(s, "maximum") is { } maximum && n > maximum)
                Add(errors, path, "maximum", $"must be at most {maximum.ToString(CultureInfo.InvariantCulture)}", value);
        }

        if (value is YSeq list)
        {
            if (Long(s, "minItems") is { } min && list.Items.Count < min)
                Add(errors, path, "minItems", min == 1 ? "must not be empty" : $"must have at least {min} items", value);
            if (Long(s, "maxItems") is { } max && list.Items.Count > max)
                Add(errors, path, "maxItems", $"must have at most {max} items", value);
            if (s["items"] is { } items)
                for (var i = 0; i < list.Items.Count; i++)
                    Validate(items, root, list.Items[i], $"{path}[{i}]", errors, limit, depth + 1);
        }

        if (value is YMap map)
        {
            var properties = s["properties"] as YMap;
            foreach (var name in (s["required"] as YSeq)?.Items.OfType<YScalar>().Select(r => r.Text) ?? [])
                if (!map.Has(name))
                    Add(errors, path, "required", $"field '{name}' is required", value);
            foreach (var (key, child) in map.Entries)
            {
                var at = path.Length == 0 ? key.Text : $"{path}.{key.Text}";
                if (properties?[key.Text] is { } property)
                    Validate(property, root, child, at, errors, limit, depth + 1);
                else if (s["additionalProperties"] is { } additional)
                {
                    if (additional is YScalar { Plain: true } a && a.AsBool() == false)
                        Add(errors, at, "additionalProperties",
                            $"field '{key.Text}' is not allowed" + (properties is null ? "" : $" (allowed: {string.Join(", ", properties.Keys)})"), key);
                    else
                        Validate(additional, root, child, at, errors, limit, depth + 1);
                }
            }
        }

        if (s["not"] is { } not && Check(not, root, value) == 0)
            Add(errors, path, "not", (not as YMap)?["description"] is YScalar why ? why.Text : $"must not match {YamlWriter.Write(not)}", value);
    }

    /// <summary>How many errors <paramref name="value"/> has against <paramref name="schema"/>: zero means it matches.</summary>
    static int Check(YNode schema, YNode root, YNode value)
    {
        var errors = new List<SchemaError>();
        Validate(schema, root, value, "", errors, 1, 0);
        return errors.Count;
    }

    static void Add(List<SchemaError> errors, string path, string rule, string message, YNode at) =>
        errors.Add(new SchemaError(path.Length == 0 ? "(document)" : path, rule, message, at.Location));

    static long? Long(YMap schema, string key) => (schema[key] as YScalar)?.AsLong();

    static double? Number(YMap schema, string key) => (schema[key] as YScalar)?.AsNumber();

    /// <summary>A YAML value's JSON type: a quoted scalar is a string, whatever it reads as.</summary>
    static string Kind(YNode value) => value switch
    {
        YMap => "object",
        YSeq => "array",
        YScalar { IsNull: true } => "null",
        YScalar s when s.AsBool() is not null => "boolean",
        YScalar s when s.AsLong() is not null => "integer",
        YScalar s when s.Plain && s.AsNumber() is { } d && double.IsFinite(d) => "number",
        _ => "string",
    };

    static bool Is(YNode value, string type) => Kind(value) == type || (type == "number" && Kind(value) == "integer");

    static bool Same(YNode a, YNode b) => YamlWriter.Write(a) == YamlWriter.Write(b);

    static string Describe(string type) => type switch
    {
        "object" => "a map",
        "array" => "a list",
        "string" => "text",
        "integer" => "a whole number",
        "number" => "a number",
        "boolean" => "true or false",
        _ => "null",
    };

    static string DescribeValue(YNode value) => Kind(value) switch
    {
        "string" => $"text '{Trim(((YScalar)value).Text)}'",
        var kind => Describe(kind),
    };

    static string Trim(string text) => text.Length > 40 ? text[..40] + "..." : text;
}
