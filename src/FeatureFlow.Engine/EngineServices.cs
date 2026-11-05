using System.Reflection;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine;

/// <summary>The engine's version, which library manifests' <c>requires.engine</c> is checked against (check 19).</summary>
public static class EngineInfo
{
    public static string Version { get; } =
        typeof(EngineInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0]
        ?? "0.0.0";
}

/// <summary>The task queues of docs/02 section 1.1, derived from the installation's orchestration queue.</summary>
public static class TaskQueues
{
    public static string Agent(string orchestrationQueue) =>
        orchestrationQueue.EndsWith("-orchestration", StringComparison.Ordinal)
            ? orchestrationQueue[..^"-orchestration".Length] + "-agent"
            : orchestrationQueue + "-agent";
}

/// <summary>What the engine's activities use: the composed providers and the installation and organisation configuration.</summary>
public sealed class EngineServices(ProviderSet providers, InstallationConfig installation, OrganisationConfig organisation)
{
    public ProviderSet Providers { get; } = providers;
    public InstallationConfig Installation { get; } = installation;
    public OrganisationConfig Organisation { get; } = organisation;

    public IArtifactStoreProvider ArtifactStore => Providers.Required<IArtifactStoreProvider>("artifact_store");

    public IRecordStoreProvider RecordStore => Providers.Required<IRecordStoreProvider>("record_store");

    /// <summary>
    /// The policy a run starting now enforces (docs/05 section 8): engine defaults from the installation, ceilings
    /// and allowed roles from the organisation, its <c>role_mapping</c>, and which decision types may be auto-resolved: the organisation's approval
    /// rules, then the <c>default_approval</c> of the libraries' <paramref name="declared"/> types, in the order
    /// <see cref="Approval.For"/> reads them.
    /// </summary>
    public RunPolicy Policy(IReadOnlyList<DecisionTypeItem> declared)
    {
        var policy = Organisation.Policy;
        return new RunPolicy(
            Installation.DefaultAttempts,
            Installation.DefaultRaces,
            policy.AttemptsPerRun ?? 200,
            policy.AttemptsPerStep,
            policy.AllowedRoles,
            Approval.AutoRules(policy, declared).Select(r => new AutoRule(r.Match, r.Auto)).ToList(),
            policy.HardPerStep,
            policy.HardPerRun,
            policy.EngineMayMerge,
            // Whether M5's actions run against providers for this run, fixed at its start like the rest of its policy.
            Providers.ForRun("vcs", false) is IVcsProvider && Providers.ForRun("code_host", false) is ICodeHostProvider,
            policy.MaxWorkItems,
            RoleMapping: Organisation.RoleMapping.Count > 0 ? Organisation.RoleMapping : null);
    }
}
