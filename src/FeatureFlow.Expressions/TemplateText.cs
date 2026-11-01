namespace FeatureFlow.Expressions;

/// <summary>An expression found between <c>{{</c> and <c>}}</c> in task or prompt text (docs/03 section 6.3).</summary>
public sealed record TextExpression(string Text, int Line, int Column);

public static class TemplateText
{
    public static IReadOnlyList<TextExpression> Expressions(string text)
    {
        var list = new List<TextExpression>();
        var lines = text.Replace("\r\n", "\n").Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var start = 0;
            while ((start = line.IndexOf("{{", start, StringComparison.Ordinal)) >= 0)
            {
                var end = line.IndexOf("}}", start + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    list.Add(new TextExpression(line[(start + 2)..], i + 1, start + 1));
                    break;
                }

                list.Add(new TextExpression(line[(start + 2)..end].Trim(), i + 1, start + 1));
                start = end + 2;
            }
        }

        return list;
    }
}
