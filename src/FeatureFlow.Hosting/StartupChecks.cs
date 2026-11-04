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
}
