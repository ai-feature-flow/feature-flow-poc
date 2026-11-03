namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// The provider instances an installation configures, by type and instance name, with each type's default and
/// its <c>dry_run</c> instance (docs/07 sections 2 and 5). Built by the host's composition (<c>Hosting</c>).
/// </summary>
public sealed class ProviderSet(IReadOnlyDictionary<string, IReadOnlyDictionary<string, IProvider>> instances,
    IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string> dryRunInstances)
{
    public IEnumerable<string> Types => instances.Keys;

    public IProvider? Instance(string type, string name) =>
        instances.TryGetValue(type, out var named) ? named.GetValueOrDefault(name) : null;

    /// <summary>Every instance of a type, by name: the default first, then the rest by name (several validator providers, docs/06 section 3.8).</summary>
    public IReadOnlyList<(string Name, IProvider Provider)> Instances(string type) =>
        instances.TryGetValue(type, out var named)
            ? named.OrderBy(kv => kv.Key == defaults.GetValueOrDefault(type) ? 0 : 1).ThenBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => (kv.Key, kv.Value)).ToList()
            : [];

    /// <summary>The instance a run uses for a type: the dry-run instance for a dry run where one is configured, else the default.</summary>
    public IProvider? ForRun(string type, bool dryRun)
    {
        if (dryRun && dryRunInstances.TryGetValue(type, out var dry))
            return Instance(type, dry);
        return defaults.TryGetValue(type, out var name) ? Instance(type, name) : null;
    }

    public T Required<T>(string type, bool dryRun = false) where T : class =>
        ForRun(type, dryRun) as T ?? throw new InvalidOperationException($"no {type} provider implementing {typeof(T).Name} is configured");

    /// <summary>The capability names the providers a run would use offer (check 21).</summary>
    public IReadOnlySet<string> Offered(bool dryRun)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var type in Types)
            if (ForRun(type, dryRun) is { } provider)
                names.UnionWith(provider.Describe().CapabilityNames());
        return names;
    }
}
