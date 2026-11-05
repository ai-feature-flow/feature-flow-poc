using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Hosting;

/// <summary>
/// The startup checks of docs/07 section 7 that need both layers or the libraries, beyond what each file's reader checks
/// alone. A failure names the layer, the key and the reason.
/// </summary>
public static class StartupChecks
{
    public static void Check(InstallationConfig installation, OrganisationConfig organisation, DiagnosticBag d)
    {
        var runtimes = installation.Providers.GetValueOrDefault("agent_runtime")?.Instances.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
        foreach (var (role, mapping) in organisation.RoleMapping)
            if (!runtimes.Contains(mapping.AgentRuntime))
                Fail(d, $"organisation: role_mapping.{role}.agent_runtime '{mapping.AgentRuntime}' is not an agent_runtime instance of the installation ({string.Join(", ", runtimes)})");

        foreach (var name in organisation.Notifications.Routing.Keys.Concat(organisation.Notifications.Events).Distinct(StringComparer.Ordinal))
            if (!NotificationEvents.All.Contains(name))
                Fail(d, $"organisation: notifications: '{name}' is not a notification event; the events are {string.Join(", ", NotificationEvents.All)}");

        if (installation.ApiAuth.Mode == "oidc" && organisation.Roles.Count == 0)
            Fail(d, "organisation: roles: api.auth.mode is oidc in the installation, so people roles come from identity-provider groups, and the organisation maps none");

        var root = Path.GetDirectoryName(Path.GetFullPath(organisation.Location))!;
        foreach (var source in organisation.Libraries)
        {
            var path = Path.GetFullPath(Path.Combine(root, source.Path));
            var opened = new DiagnosticBag();
            if (!Directory.Exists(path))
                Fail(d, $"organisation: libraries.{source.Name}: the directory {path} does not exist");
            else if (Library.Library.Open(path, opened) is null || opened.HasErrors)
                Fail(d, $"organisation: libraries.{source.Name}: the library at {path} does not open: {string.Join("; ", opened.Where(x => x.Blocks).Take(3).Select(x => x.Message))}");
        }
    }

    static void Fail(DiagnosticBag d, string message) => d.Add(Checks.Schema, message);

    /// <summary>
    /// Every secret reference the installation names, with where: <c>api.auth.token_ref</c>, <c>agent_worker.token_key_ref</c>
    /// and every provider setting whose key ends in <c>_ref</c>, however deep (<c>model_access.key_ref</c>).
    /// </summary>
    public static IReadOnlyList<(string Key, string Reference)> SecretReferences(InstallationConfig installation)
    {
        var found = new List<(string, string)>();
        if (installation.ApiAuth.TokenRef is { } token)
            found.Add(("api.auth.token_ref", token));
        if (installation.AgentWorker?.TokenKeyRef is { } key)
            found.Add(("agent_worker.token_key_ref", key));
        foreach (var (type, config) in installation.Providers)
            foreach (var (name, instance) in config.Instances)
                Walk(instance.Settings, $"providers.{type}.instances.{name}");
        return found;

        void Walk(YNode? node, string path)
        {
            if (node is YMap map)
                foreach (var (k, v) in map.Entries)
                {
                    if (k.Text.EndsWith("_ref", StringComparison.Ordinal) && v.ScalarText() is { Length: > 0 } reference)
                        found.Add(($"{path}.{k.Text}", reference));
                    else
                        Walk(v, $"{path}.{k.Text}");
                }
            else if (node is YSeq seq)
                for (var i = 0; i < seq.Items.Count; i++)
                    Walk(seq.Items[i], $"{path}[{i}]");
        }
    }

    /// <summary>
    /// Startup resolves every secret reference (<see cref="SecretReferences"/>) through the secrets provider, so a missing
    /// secret stops the host at once rather than the first run that needs it. A failure names the key and the reference,
    /// never a value.
    /// </summary>
    public static async Task CheckSecretsAsync(InstallationConfig installation, ProviderSet providers, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var secrets = providers.ForRun("secrets", false) as ISecretsProvider;
        foreach (var (key, reference) in SecretReferences(installation))
        {
            if (secrets is null)
            {
                failures.Add($"{key} names the secret '{reference}', and no secrets provider is configured");
                continue;
            }

            var resolved = await secrets.ResolveAsync(reference, cancellationToken);
            if (!resolved.Ok)
                failures.Add($"{key}: the secret '{reference}' does not resolve ({resolved.Error!.Reason}: {resolved.Error.Message})");
            else if (string.IsNullOrEmpty(resolved.Value))
                failures.Add($"{key}: the secret '{reference}' is empty");
        }

        if (failures.Count > 0)
            throw new InvalidOperationException("the installation's secret references do not resolve:\n" + string.Join("\n", failures));
    }
}
