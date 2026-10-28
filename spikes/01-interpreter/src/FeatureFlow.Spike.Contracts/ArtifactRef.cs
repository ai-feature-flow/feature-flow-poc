namespace FeatureFlow.Spike.Contracts;

/// <summary>
/// A reference to stored content. The content itself never enters workflow
/// state (DP10).
///
/// <para>
/// Shared by every provider that produces or consumes stored content: the code
/// host (a diff), CI (logs, artifacts), the artifact store, and agent runs. It
/// lives in Contracts precisely because it must be one type — a diff reference
/// handed from the code host to a review step has to be the same shape as an
/// artifact reference handed from CI to a results step.
/// </para>
///
/// <para>
/// <c>Uri</c> is the only required member. Everything else is metadata that a
/// provider fills in when it knows it: <c>ContentHash</c> for content-addressed
/// stores, <c>ExpiresAt</c> for signed URLs, <c>Digest</c> for registries.
/// </para>
/// </summary>
public sealed record ArtifactRef(
    string Uri,
    string? Id = null,
    string? Name = null,
    string? ContentHash = null,
    long? SizeBytes = null,
    string? Digest = null,
    DateTimeOffset? ExpiresAt = null,
    string? ContentType = null)
{
    public override string ToString() =>
        Name is null ? Uri : $"{Name} ({Id ?? Uri})";
}
