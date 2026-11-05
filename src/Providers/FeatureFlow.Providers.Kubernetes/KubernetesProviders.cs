using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Kubernetes;

/// <summary>
/// The factories of this package, by type and kind, for a host to register (docs/06 section 4). The sandbox as a Kubernetes pod, with a network policy (docs/06 section 3.1). Empty until M8's
/// builder adds it; the three hosts already register this list.
/// </summary>
public static class KubernetesProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } = [];
}
