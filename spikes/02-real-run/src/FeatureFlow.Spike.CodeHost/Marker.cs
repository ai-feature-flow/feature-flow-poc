using System.Security.Cryptography;
using System.Text;

namespace FeatureFlow.Spike.CodeHost;

/// <summary>
/// The hidden idempotency marker. docs/01 section 7.1, docs/05 section 1.1.
///
/// <para>
/// Every object the engine creates carries a stable hidden marker: a branch
/// name suffix, a PR body comment, a commit trailer. On retry a step searches
/// by marker and adopts the existing object instead of creating a duplicate
/// (S4).
/// </para>
///
/// <para>
/// The marker is derived from the idempotency key, which is
/// <c>hash(runId, stepId, stableInputs)</c>. Stable inputs exclude anything
/// that changes per attempt — no timestamps, no attempt numbers, no session
/// references — so a resumed attempt produces the same key as the attempt it
/// continues.
/// </para>
/// </summary>
public static class Marker
{
    public const string Prefix = "ff-marker";

    /// <summary>
    /// Builds the marker text for an idempotency key. Short enough to sit in a
    /// branch name, distinctive enough to search for.
    /// </summary>
    public static string For(string idempotencyKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        var hash = Convert.ToHexString(bytes)[..16].ToLowerInvariant();
        return $"{Prefix}-{hash}";
    }

    /// <summary>
    /// The marker as it appears in a branch name. Branch names cannot contain
    /// most punctuation, so the marker is appended as a path segment.
    /// </summary>
    public static string BranchSuffix(string idempotencyKey) => For(idempotencyKey);

    /// <summary>
    /// The marker as it appears in a PR body or comment. HTML comment so it is
    /// invisible when rendered but still searchable through the API.
    /// </summary>
    public static string Hidden(string idempotencyKey) => $"<!-- {For(idempotencyKey)} -->";

    /// <summary>
    /// The marker as it appears in a commit trailer.
    /// </summary>
    public static string Trailer(string idempotencyKey) => $"Feature-Flow-Idempotency: {For(idempotencyKey)}";

    /// <summary>
    /// Finds a marker in arbitrary text. Used by
    /// <see cref="ICodeHostProvider.FindByMarkerAsync"/> to match PR bodies and
    /// comments.
    /// </summary>
    public static bool Contains(string? text, string marker) =>
        !string.IsNullOrEmpty(text) &&
        text.Contains(marker, StringComparison.Ordinal);

    /// <summary>
    /// The deterministic idempotency key: <c>hash(runId, stepId, stableInputs)</c>.
    /// Callers pass only stable inputs; this method never adds a timestamp or an
    /// attempt number, which is what lets a retry re-adopt an existing object.
    /// </summary>
    public static string Key(string runId, string stepId, params string[] stableInputs)
    {
        var builder = new StringBuilder();
        builder.Append(runId).Append('\u001f').Append(stepId);
        foreach (var input in stableInputs)
            builder.Append('\u001f').Append(input);

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
