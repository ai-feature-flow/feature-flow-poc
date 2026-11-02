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
    /// and allowed roles from the organisation, and which decision types may be auto-resolved.
    /// </summary>
    public RunPolicy Policy()
    {
        var policy = Organisation.Policy;
        return new RunPolicy(
            Installation.DefaultAttempts,
            Installation.DefaultRaces,
            policy.AttemptsPerRun ?? 200,
            policy.AttemptsPerStep,
            policy.AllowedRoles,
            policy.Approval.Select(r => new AutoRule(r.Match, r.Auto.Value)).ToList(),
            policy.HardPerStep,
            policy.HardPerRun);
    }
}
