using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Docker;

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class DockerProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("sandbox", "docker", ["image", "user", "docker"], c => new DockerSandbox(
            c.RequiredSetting("image"),
            c.Setting("user") ?? "node",
            () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("the Docker sandbox needs an artifact store"),
            new DockerCli(c.Setting("docker") ?? "docker"),
            c.SandboxReach)),
    ];
}
