using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.CopilotCli;

/// <summary>
/// The factories of this package, by type and kind, for a host to register (docs/06 section 4). The Copilot CLI agent runtime (docs/06 section 3.2). Empty until M8's
/// builder adds it; the three hosts already register this list.
/// </summary>
public static class CopilotCliProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } = [];
}
