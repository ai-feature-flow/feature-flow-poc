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

    /// <summary>The types a dry run takes from the default instance when it has no <c>dry_run</c> one: the engine's own (docs/07 section 5).</summary>
    public static readonly IReadOnlySet<string> SharedInDryRun = new HashSet<string>(StringComparer.Ordinal) { "artifact_store", "record_store", "secrets", "credential_broker" };

    /// <summary>
    /// The instance a run uses for a type: its default; in a dry run its <c>dry_run</c> instance, and none where it has none,
    /// except the types in <see cref="SharedInDryRun"/>, so a dry run never acts through a provider that acts for real.
    /// </summary>
    public IProvider? ForRun(string type, bool dryRun) => NameForRun(type, null, dryRun) is { } name ? Instance(type, name) : null;

    /// <summary>
    /// The name of the instance a run uses for a type: the one the run chose (<paramref name="chosen"/>, an agent role's
    /// runtime), else the default; in a dry run the type's <c>dry_run</c> instance in its place, as <see cref="ForRun(string, bool)"/>.
    /// </summary>
    public string? NameForRun(string type, string? chosen, bool dryRun)
    {
        if (dryRun && dryRunInstances.TryGetValue(type, out var dry))
            return dry;
        if (dryRun && !SharedInDryRun.Contains(type))
            return null;
        return (dryRun ? null : chosen) ?? defaults.GetValueOrDefault(type);
    }

    /// <summary>
    /// The named instance of a type, chosen by the run (an agent role's runtime, <c>role_mapping</c>), as the run may use it:
    /// the named instance; in a dry run the type's <c>dry_run</c> instance in its place, as <see cref="ForRun(string, bool)"/>
    /// selects for any choice (LOG 362, 384).
    /// </summary>
    public IProvider? ForRun(string type, string name, bool dryRun) => NameForRun(type, name, dryRun) is { } chosen ? Instance(type, chosen) : null;

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
