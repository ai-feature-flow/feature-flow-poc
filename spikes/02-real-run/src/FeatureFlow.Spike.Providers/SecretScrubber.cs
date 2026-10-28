using System.Text;
using System.Text.RegularExpressions;

namespace FeatureFlow.Spike.Providers;

/// <summary>
/// Redacts known secret values from arbitrary text. docs/01 section 4.8:
/// "Secrets scrubbed at the logging layer."
///
/// Two mechanisms, because either alone leaks:
///
/// 1. **Known values.** Anything resolved through a secrets provider is
///    registered here, so an exact occurrence is redacted wherever it appears.
/// 2. **Shapes.** GitHub tokens have recognisable prefixes, and a PEM has a
///    recognisable header. A value that was never registered — one pasted into a
///    log by a future code path — is still caught.
///
/// The scrubber is the single place that knows what a secret looks like, so no
/// provider needs to remember to redact.
/// </summary>
public sealed class SecretScrubber
{
    public const string Redaction = "[REDACTED]";

    /// <summary>Values shorter than this are not registered: too likely to be a
    /// common word, so redacting them would mangle ordinary output.</summary>
    private const int MinimumSecretLength = 8;

    private readonly object gate = new();
    private readonly HashSet<string> known = new(StringComparer.Ordinal);

    private static readonly Regex[] Shapes =
    {
        // GitHub App / installation / PAT tokens.
        new(@"\bgh[pousr]_[A-Za-z0-9]{16,}", RegexOptions.Compiled),
        new(@"\bghs_[A-Za-z0-9]{16,}", RegexOptions.Compiled),
        // A JWT: three base64url segments.
        new(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}", RegexOptions.Compiled),
        // A PEM block, header through footer.
        new(@"-----BEGIN [A-Z ]*PRIVATE KEY-----[\s\S]*?-----END [A-Z ]*PRIVATE KEY-----",
            RegexOptions.Compiled),
    };

    /// <summary>How many values are registered. Safe to log.</summary>
    public int Count
    {
        get { lock (gate) { return known.Count; } }
    }

    /// <summary>
    /// Registers a value for redaction. Returns false and registers nothing when
    /// the value is too short to be safely redacted.
    /// </summary>
    public bool Register(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinimumSecretLength)
            return false;

        lock (gate)
        {
            known.Add(value);
        }

        return true;
    }

    /// <summary>True when the text contains a registered value or a secret shape.</summary>
    public bool ContainsSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        lock (gate)
        {
            foreach (var value in known)
            {
                if (text.Contains(value, StringComparison.Ordinal))
                    return true;
            }
        }

        return Shapes.Any(shape => shape.IsMatch(text));
    }

    /// <summary>
    /// Returns the text with every registered value and every secret shape
    /// replaced by <see cref="Redaction"/>.
    /// </summary>
    public string Scrub(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text ?? "";

        var result = text;

        // Longest first, so a value that contains another is not partially
        // replaced and left half-visible.
        string[] values;
        lock (gate)
        {
            values = known.OrderByDescending(v => v.Length).ToArray();
        }

        foreach (var value in values)
        {
            if (result.Contains(value, StringComparison.Ordinal))
                result = result.Replace(value, Redaction, StringComparison.Ordinal);
        }

        foreach (var shape in Shapes)
        {
            result = shape.Replace(result, Redaction);
        }

        return result;
    }

    /// <summary>
    /// A log-safe rendering of a credential: enough to correlate with a remote
    /// record, never enough to use.
    /// </summary>
    public static string Fingerprint(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "(none)";

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return "sha256:" + Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }
}
