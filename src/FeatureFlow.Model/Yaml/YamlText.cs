using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace FeatureFlow.Model.Yaml;

/// <summary>Reads YAML text into a <see cref="YNode"/> tree. A malformed document is a check 1 error.</summary>
public static class YamlText
{
    public static YNode? Parse(string text, string file, DiagnosticBag diagnostics)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException e)
        {
            var inner = e.InnerException?.Message is { } m ? $": {m}" : "";
            diagnostics.Add(Checks.Schema, $"malformed YAML: {e.Message}{inner}", new SourceLocation(file, (int)e.Start.Line, (int)e.Start.Column));
            return null;
        }
        catch (ArgumentException e)
        {
            diagnostics.Add(Checks.Schema, $"malformed YAML: {e.Message}", new SourceLocation(file, 1, 1));
            return null;
        }

        switch (stream.Documents.Count)
        {
            case 0:
                diagnostics.Add(Checks.Schema, "the file holds no YAML document", new SourceLocation(file, 1, 1));
                return null;
            case > 1:
                diagnostics.Add(Checks.Schema, "the file holds more than one YAML document", new SourceLocation(file, 1, 1));
                return null;
        }

        return Convert(stream.Documents[0].RootNode, file, diagnostics);
    }

    static YNode Convert(YamlNode node, string file, DiagnosticBag diagnostics)
    {
        var location = new SourceLocation(file, (int)node.Start.Line, (int)node.Start.Column);
        switch (node)
        {
            case YamlScalarNode scalar:
                return new YScalar(scalar.Value ?? "", scalar.Style is ScalarStyle.Plain or ScalarStyle.Any, location);
            case YamlSequenceNode sequence:
                return new YSeq(sequence.Children.Select(c => Convert(c, file, diagnostics)).ToList(), location);
            case YamlMappingNode mapping:
                var entries = new List<KeyValuePair<YScalar, YNode>>();
                foreach (var (key, value) in mapping.Children)
                {
                    var keyLocation = new SourceLocation(file, (int)key.Start.Line, (int)key.Start.Column);
                    if (key is not YamlScalarNode scalarKey)
                    {
                        diagnostics.Add(Checks.Schema, "a map key must be a scalar", keyLocation);
                        continue;
                    }

                    entries.Add(new(new YScalar(scalarKey.Value ?? "", true, keyLocation), Convert(value, file, diagnostics)));
                }

                return new YMap(entries, location);
            default:
                diagnostics.Add(Checks.Schema, "unsupported YAML node", location);
                return new YScalar("", true, location);
        }
    }
}
