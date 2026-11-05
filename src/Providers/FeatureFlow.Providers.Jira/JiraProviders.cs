using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Jira;

/// <summary>
/// The factories of this package, by type and kind, for a host to register (docs/06 section 4). The Jira tracker (docs/06 section 3.6). Empty until M8's
/// builder adds it; the three hosts already register this list.
/// </summary>
public static class JiraProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } = [];
}
