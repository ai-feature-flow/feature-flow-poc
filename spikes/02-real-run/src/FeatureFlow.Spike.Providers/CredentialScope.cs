using System.Text;

namespace FeatureFlow.Spike.Providers;

/// <summary>
/// A parsed credential scope. docs/04 section 4.6 and check R6: a credential is
/// "scoped to one repository". The scope is therefore modelled, not a free string,
/// so the broker can decide whether it can grant it.
///
/// Grammar, whitespace separated:
///
/// ```text
/// repo:<owner>/<name> [permission:access ...]
/// ```
///
/// for example `repo:ai-feature-flow/feature-flow-poc contents:write
/// pull_requests:write`. An empty permission list means the minimum the target
/// system grants, which for a GitHub App installation is `metadata:read`.
/// </summary>
public sealed record CredentialScope(
    string Repository,
    IReadOnlySet<string> Permissions)
{
    /// <summary>The canonical text form. Two equal scopes render identically.</summary>
    public string Text
    {
        get
        {
            var sb = new StringBuilder($"repo:{Repository}");
            foreach (var permission in Permissions.OrderBy(p => p, StringComparer.Ordinal))
                sb.Append(' ').Append(permission);
            return sb.ToString();
        }
    }

    public override string ToString() => Text;

    public static CredentialScope ForRepository(string repository, params string[] permissions) =>
        new(repository, new HashSet<string>(permissions, StringComparer.Ordinal));

    /// <summary>
    /// Parses a scope. Throws <see cref="FormatException"/> when the text is not a
    /// well-formed scope; the broker turns that into `validation_failed`.
    /// </summary>
    public static CredentialScope Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("scope is empty");

        var tokens = text.Split(
            (char[]?)null,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var repository = ParseRepository(tokens[0]);
        var permissions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var token in tokens.Skip(1))
        {
            if (!TryParsePermission(token, out var permission))
                throw new FormatException(
                    $"'{token}' is not a permission of the form <name>:<access>");

            permissions.Add(permission);
        }

        return new CredentialScope(repository, permissions);
    }

    public static bool TryParse(string? text, out CredentialScope scope)
    {
        scope = new CredentialScope("", new HashSet<string>(StringComparer.Ordinal));
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            scope = Parse(text);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string ParseRepository(string token)
    {
        if (!token.StartsWith("repo:", StringComparison.Ordinal))
            throw new FormatException($"scope must start with 'repo:<owner>/<name>', got '{token}'");

        var value = token["repo:".Length..];
        var slash = value.IndexOf('/');
        if (slash <= 0 || slash == value.Length - 1 || value.IndexOf('/', slash + 1) >= 0)
            throw new FormatException($"'{value}' is not <owner>/<name>");

        return value;
    }

    private static bool TryParsePermission(string token, out string permission)
    {
        permission = "";
        var colon = token.IndexOf(':');
        if (colon <= 0 || colon == token.Length - 1)
            return false;

        var name = token[..colon];
        var access = token[(colon + 1)..];

        if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_'))
            return false;

        if (access is not ("read" or "write" or "admin"))
            return false;

        permission = $"{name}:{access}";
        return true;
    }
}

/// <summary>
/// A scope the broker is allowed to grant. docs/05 section 12: `forbidden` means
/// "scope not grantable". The grantable set is explicit, so a request outside it
/// is refused rather than widened.
/// </summary>
public sealed record GrantableScope(
    string Repository,
    IReadOnlySet<string> Permissions)
{
    public static GrantableScope ForRepository(string repository, params string[] permissions) =>
        new(repository, new HashSet<string>(permissions, StringComparer.Ordinal));

    /// <summary>
    /// Whether this grant covers the requested scope: the same repository, and
    /// every requested permission is one the grant allows.
    /// </summary>
    public bool Covers(CredentialScope requested) =>
        string.Equals(Repository, requested.Repository, StringComparison.OrdinalIgnoreCase)
        && requested.Permissions.IsSubsetOf(Permissions);

    /// <summary>Which requested permissions this grant does not allow.</summary>
    public IReadOnlyList<string> Ungranted(CredentialScope requested) =>
        requested.Permissions
            .Where(p => !Permissions.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();
}
