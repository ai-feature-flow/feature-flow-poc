using FeatureFlow.Api;
using FeatureFlow.Model;
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

        // docs/07 section 7: library sources are reachable. Startup is synchronous; a git source is fetched here once.
        foreach (var source in organisation.Libraries)
            if (LibrarySources.OpenAsync(source, organisation.Location, CancellationToken.None).GetAwaiter().GetResult() is { Library: null, Problem: { } problem })
                Fail(d, $"organisation: libraries.{source.Name}: {problem}");
    }

    static void Fail(DiagnosticBag d, string message) => d.Add(Checks.Schema, message);
}
