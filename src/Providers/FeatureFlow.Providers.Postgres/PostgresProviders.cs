using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Postgres;

/// <summary>
/// The factories of this package, by type and kind, for a host to register (docs/06 section 4). The record store on Postgres (docs/06 section 3.12; the small-company profile, docs/07 section 6). Empty until M8's
/// builder adds it; the three hosts already register this list.
/// </summary>
public static class PostgresProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } = [];
}
