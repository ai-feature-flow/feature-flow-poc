using System.Text.RegularExpressions;

namespace FeatureFlow.Model;

/// <summary>
/// A reference to a library content item: <c>&lt;library&gt;:&lt;type&gt;/&lt;name&gt;</c> (docs/08 section 1.1). It names an
/// item, never a file. A version constraint (<c>acme@&gt;=2.0.0:tasks/x</c>) is not supported in version 1: such text is not a
/// reference, and <see cref="ConstraintRefusal"/> says why (LOG 487).
/// </summary>
public sealed partial record LibraryReference(string Library, string Type, string Name)
{
    public static readonly IReadOnlyList<string> Types =
        ["templates", "fragments", "tasks", "skills", "roles", "artifact_types", "prompts"];

    [GeneratedRegex(@"^(?<lib>[a-z][a-z0-9_]*)(@(?<ver>[^:]+))?:(?<type>[a-z_]+)/(?<name>[a-z][a-z0-9_]*)$")]
    private static partial Regex Pattern();

    public static LibraryReference? TryParse(string text)
    {
        var match = Pattern().Match(text);
        if (!match.Success || match.Groups["ver"].Success || !Types.Contains(match.Groups["type"].Value))
            return null;
        return new LibraryReference(match.Groups["lib"].Value, match.Groups["type"].Value, match.Groups["name"].Value);
    }

    /// <summary>
    /// Why <paramref name="text"/>, which <see cref="TryParse"/> refuses, is not a reference when it would be one without its
    /// version constraint; otherwise null.
    /// </summary>
    public static string? ConstraintRefusal(string text) =>
        Pattern().Match(text) is { Success: true } match && match.Groups["ver"].Success
            ? $"'{text}': a version constraint in a reference (@{match.Groups["ver"].Value}) is not supported in version 1; remove it"
            : null;

    public override string ToString() => $"{Library}:{Type}/{Name}";
}
