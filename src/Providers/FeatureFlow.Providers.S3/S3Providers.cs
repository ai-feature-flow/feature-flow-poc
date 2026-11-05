using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.S3;

/// <summary>
/// The factories of this package, by type and kind, for a host to register (docs/06 section 4). The artifact store on the S3 API, tested against MinIO (docs/06 section 3.12; LOG 266). Empty until M8's
/// builder adds it; the three hosts already register this list.
/// </summary>
public static class S3Providers
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } = [];
}
