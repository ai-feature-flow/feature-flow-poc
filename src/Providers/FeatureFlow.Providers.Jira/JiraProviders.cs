using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Jira;

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4): the Jira tracker (docs/06 section 3.6).</summary>
public static class JiraProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("tracker", "jira", ["base_url", "secret_ref", "project", "issue_type", "status_map"], Create),
    ];

    /// <summary>
    /// <c>base_url</c> is the Jira site; <c>secret_ref</c> a secret reference, resolved by the secrets provider when used,
    /// holding <c>&lt;account email&gt;:&lt;API token&gt;</c>, never a value in configuration. <c>project</c> is where issues
    /// go by default, <c>issue_type</c> what a created issue is (default <c>Task</c>).
    /// </summary>
    static JiraTracker Create(ProviderContext c)
    {
        var secretRef = c.RequiredSetting("secret_ref");
        var baseUrl = c.RequiredSetting("base_url");
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback)
            throw new InvalidOperationException($"providers.tracker.instances.{c.Instance.Name}.base_url must be an https URL, not '{baseUrl}'");
        var api = new JiraApi(ct => (c.Provider("secrets") as ISecretsProvider
                                     ?? throw new InvalidOperationException($"providers.tracker.instances.{c.Instance.Name} needs a secrets provider")).ResolveAsync(secretRef, ct),
            baseUrl);
        return new JiraTracker(api, c.Setting("project"), StatusMap(c), c.Setting("issue_type") ?? "Task");
    }

    /// <summary><c>status_map</c> (docs/07 section 2): a library status to a Jira status name. Unset, std's statuses map to <see cref="JiraTracker.DefaultStatusMap"/>.</summary>
    static IReadOnlyDictionary<string, string>? StatusMap(ProviderContext c)
    {
        if (c.Instance.Settings["status_map"] is not { } node)
            return null;
        if (node is not YMap map || map.Entries.Any(e => e.Value.ScalarText() is not { Length: > 0 }))
            throw new InvalidOperationException($"providers.tracker.instances.{c.Instance.Name}.status_map maps each library status to a Jira status name");
        return map.Entries.ToDictionary(e => e.Key.Text, e => e.Value.ScalarText()!, StringComparer.Ordinal);
    }
}
