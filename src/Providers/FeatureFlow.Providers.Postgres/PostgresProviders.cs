using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Postgres;

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class PostgresProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("record_store", "postgres", ["connection_ref"], Create),
    ];

    /// <summary>
    /// The record store on Postgres. <c>connection_ref</c> names the secret holding the connection string
    /// (<c>Host=...;Database=...;Username=...;Password=...</c>), resolved by the secrets provider when the store is first
    /// used, never a value in configuration.
    /// </summary>
    static PostgresRecordStore Create(ProviderContext c)
    {
        var reference = c.RequiredSetting("connection_ref");
        return new PostgresRecordStore(ct => (c.Provider("secrets") as ISecretsProvider
                                              ?? throw new InvalidOperationException($"providers.record_store.instances.{c.Instance.Name} needs a secrets provider"))
            .ResolveAsync(reference, ct));
    }
}
