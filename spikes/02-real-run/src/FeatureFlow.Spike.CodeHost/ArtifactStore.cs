using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.CodeHost;

/// <summary>
/// The artifact store seam. docs/04 section 8.
///
/// <para>
/// The code host needs exactly one thing from the artifact store: somewhere to
/// put a diff so the diff itself never enters workflow state (DP10). This file
/// declares that one method so the provider compiles and runs today. It is a
/// seam, not a second implementation — the sibling agent's
/// <c>FeatureFlow.Spike.Providers</c> owns <c>IArtifactStoreProvider</c>, whose
/// <c>PutAsync</c> has the same shape. When that type lands, delete this file
/// and bind to it.
/// </para>
/// </summary>
public interface IArtifactStore
{
    Task<ProviderResult<ArtifactRef>> PutAsync(Stream content, CancellationToken ct);
}

/// <summary>
/// An artifact store that keeps content in memory. For verification only: it
/// lets <see cref="ICodeHostProvider.GetDiffAsync"/> be exercised end to end
/// without a real store, while still proving that the diff leaves the provider
/// as a reference rather than a string.
/// </summary>
public sealed class InMemoryArtifactStore : IArtifactStore
{
    private readonly Dictionary<string, byte[]> content = new(StringComparer.Ordinal);

    public Task<ProviderResult<ArtifactRef>> PutAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();

        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))
            .ToLowerInvariant();
        var uri = $"memory://artifacts/{hash}";

        content[uri] = bytes;

        return Task.FromResult(ProviderResult<ArtifactRef>.Ok(
            new ArtifactRef(uri, ContentHash: hash, SizeBytes: bytes.LongLength)));
    }

    public bool TryGet(string uri, out byte[] bytes) => content.TryGetValue(uri, out bytes!);
}
