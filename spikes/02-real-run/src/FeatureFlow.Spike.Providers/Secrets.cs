using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Providers;

/// <summary>
/// A reference to a secret. docs/07 section 8: "A reference names the secrets
/// provider and a key: secrets/github-app-key". Secrets are by reference only
/// (floor F2) — no configuration layer may inline a value.
/// </summary>
/// <param name="Provider">The named secrets provider instance, e.g. "secrets".</param>
/// <param name="Key">The key within that provider, e.g. "github-app-key".</param>
public sealed record SecretReference(string Provider, string Key)
{
    /// <summary>The canonical text form used in configuration.</summary>
    public string Text => $"{Provider}/{Key}";

    public override string ToString() => Text;

    /// <summary>
    /// Parses "secrets/github-app-key". The first segment is the provider, the
    /// rest is the key, so a key may itself contain a slash.
    /// </summary>
    public static SecretReference Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("secret reference is empty", nameof(text));

        var trimmed = text.Trim().TrimStart('$', '{').TrimEnd('}');
        var slash = trimmed.IndexOf('/');
        if (slash <= 0 || slash == trimmed.Length - 1)
            throw new ArgumentException(
                $"secret reference '{text}' must be of the form <provider>/<key>", nameof(text));

        return new SecretReference(trimmed[..slash], trimmed[(slash + 1)..]);
    }

    public static bool TryParse(string? text, out SecretReference reference)
    {
        reference = new SecretReference("", "");
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            reference = Parse(text);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

/// <summary>
/// A resolved secret value. The value is carried so the credential broker can
/// use it, and is never rendered by <see cref="ToString"/> so it cannot reach a
/// log by accident (docs/01 4.8).
/// </summary>
public sealed record SecretValue(
    string Value,
    SecretValueKind Kind = SecretValueKind.Inline,
    string? SourcePath = null)
{
    /// <summary>How many characters the value has. Safe to log.</summary>
    public int Length => Value.Length;

    /// <summary>
    /// A log-safe description: the kind, the length and where it came from, but
    /// never the value.
    /// </summary>
    public string Describe() => Kind switch
    {
        SecretValueKind.FileContent =>
            $"file content, {Length} chars, from {SourcePath ?? "(unknown)"}",
        _ => $"inline, {Length} chars",
    };

    public override string ToString() => $"<secret {Describe()}>";
}

public enum SecretValueKind
{
    /// <summary>The value was stored inline in the secrets file.</summary>
    Inline,

    /// <summary>The value named a file, and the file's content is the value.</summary>
    FileContent,
}

/// <summary>docs/04 section 10. Secrets are referenced, never stored in configuration.</summary>
public interface ISecretsProvider : IProvider
{
    Task<ProviderResult<SecretValue>> ResolveAsync(SecretReference reference, CancellationToken ct);
}

/// <summary>
/// The secrets capability record. docs/04 section 1: capabilities are typed
/// records, never strings.
/// </summary>
public sealed record SecretsCapabilities(
    bool ReadFileValues,
    bool OutsideRepositoryOnly);
