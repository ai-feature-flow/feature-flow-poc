using System.Text.RegularExpressions;

namespace FeatureFlow.Model;

/// <summary>
/// A reference to a library content item: <c>&lt;library&gt;[@&lt;version constraint&gt;]:&lt;type&gt;/&lt;name&gt;</c>
/// (docs/08 section 1.1). It names an item, never a file.
/// </summary>
public sealed partial record LibraryReference(string Library, string? Constraint, string Type, string Name)
{
    public static readonly IReadOnlyList<string> Types =
        ["templates", "fragments", "tasks", "skills", "roles", "artifact_types", "prompts"];

    [GeneratedRegex(@"^(?<lib>[a-z][a-z0-9_]*)(@(?<ver>[^:]+))?:(?<type>[a-z_]+)/(?<name>[a-z][a-z0-9_]*)$")]
    private static partial Regex Pattern();

    public static bool LooksLikeReference(string text) => Pattern().IsMatch(text);

    public static LibraryReference? TryParse(string text)
    {
        var match = Pattern().Match(text);
        if (!match.Success || !Types.Contains(match.Groups["type"].Value))
            return null;
        var constraint = match.Groups["ver"].Success ? match.Groups["ver"].Value : null;
        return new LibraryReference(match.Groups["lib"].Value, constraint, match.Groups["type"].Value, match.Groups["name"].Value);
    }

    public override string ToString() =>
        Constraint is null ? $"{Library}:{Type}/{Name}" : $"{Library}@{Constraint}:{Type}/{Name}";
}
