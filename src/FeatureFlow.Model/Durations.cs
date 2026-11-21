using System.Globalization;

namespace FeatureFlow.Model;

/// <summary>Durations as plans and configuration write them (docs/03 section 2.1).</summary>
public static class Durations
{
    /// <summary>
    /// A duration such as <c>500ms</c>, <c>30s</c>, <c>5m</c>, <c>2h</c>, <c>7d</c> or <c>1.5h</c>: a non-negative number
    /// and a unit. Null for anything else.
    /// </summary>
    public static TimeSpan? Parse(string? text)
    {
        if (text is null)
            return null;
        var unit = text.EndsWith("ms", StringComparison.Ordinal) ? "ms" : text.Length > 0 ? text[^1..] : "";
        if (text.Length <= unit.Length
            || !double.TryParse(text[..^unit.Length], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            || n < 0 || !double.IsFinite(n))
            return null;
        try
        {
            return unit switch
            {
                "ms" => TimeSpan.FromMilliseconds(n),
                "s" => TimeSpan.FromSeconds(n),
                "m" => TimeSpan.FromMinutes(n),
                "h" => TimeSpan.FromHours(n),
                "d" => TimeSpan.FromDays(n),
                _ => null,
            };
        }
        catch (OverflowException)
        {
            return null;
        }
    }
}
