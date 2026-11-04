using System.Text.Json;
using System.Text.RegularExpressions;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model;

/// <summary>
/// The checks of a produced artifact's content that its type alone defines (docs/04 section 10.2, "schema and structure";
/// LOG 246): it reads in its format; a YAML or JSON document fits the type's schema; a Markdown document has its type's
/// front matter keys, headings and identifiers. The type's named validators are the caller's, through the validator
/// providers. Each error names the field or heading and the rule, so an agent can correct it (docs/01 section 5.4).
/// </summary>
public static partial class ArtifactContent
{
    public static IReadOnlyList<string> Check(ArtifactTypeItem type, string text, string file)
    {
        if (text.Trim().Length == 0)
            return ["the document is empty"];
        return type.Format switch
        {
            "markdown" => Markdown(type, text, file),
            "json" when !IsJson(text, out var error) => [$"does not parse as JSON: {error}"],
            _ => Data(type, text, file),
        };
    }

    static bool IsJson(string text, out string? error)
    {
        try
        {
            using var _ = JsonDocument.Parse(text);
            error = null;
            return true;
        }
        catch (JsonException e)
        {
            error = e.Message;
            return false;
        }
    }

    static IReadOnlyList<string> Data(ArtifactTypeItem type, string text, string file)
    {
        var d = new DiagnosticBag();
        var node = YamlText.Parse(text, file, d);
        if (node is null || d.HasErrors)
            return d.Select(x => $"does not parse as {type.Format.ToUpperInvariant()}: line {x.Location?.Line}: {x.Message}").ToList();
        return type.Schema is null ? [] : ArtifactSchema.Check(type.Schema, node).Select(e => e.ToString()).ToList();
    }

    [GeneratedRegex(@"^\s{0,3}#{1,6}\s+(?<text>.*?)\s*#*\s*$")]
    private static partial Regex Heading();

    static IReadOnlyList<string> Markdown(ArtifactTypeItem type, string text, string file)
    {
        var errors = new List<string>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var body = 0;
        if (type.FrontMatterRequired.Count > 0)
        {
            var close = lines.Length > 0 && lines[0].Trim() == "---" ? Array.FindIndex(lines, 1, l => l.Trim() == "---") : -1;
            if (close < 0)
                errors.Add($"must begin with front matter between '---' lines, giving {string.Join(", ", type.FrontMatterRequired)}");
            else
            {
                body = close + 1;
                var d = new DiagnosticBag();
                var front = YamlText.Parse(string.Join('\n', lines[1..close]), file, d) as YMap;
                if (front is null)
                    errors.Add("the front matter must be a YAML map" + (d.HasErrors ? $": {string.Join("; ", d.Select(x => x.Message))}" : ""));
                else
                    foreach (var key in type.FrontMatterRequired.Where(k => front[k] is null or YScalar { IsNull: true }))
                        errors.Add($"front matter: '{key}' is required");
            }
        }

        var headings = lines[body..].Select(l => Heading().Match(l)).Where(m => m.Success).Select(m => m.Groups["text"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var heading in type.RequiredHeadings.Where(h => !headings.Contains(h)))
            errors.Add($"heading '{heading}' is required");
        foreach (var pattern in type.IdentifierPatterns.Where(p => !Regex.IsMatch(text, p, RegexOptions.Multiline, TimeSpan.FromSeconds(1))))
            errors.Add($"must hold an identifier matching {pattern}");
        return errors;
    }
}
