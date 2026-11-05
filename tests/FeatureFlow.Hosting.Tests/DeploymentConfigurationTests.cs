using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.ClaudeCode;
using FeatureFlow.Providers.CopilotCli;
using FeatureFlow.Providers.Docker;
using FeatureFlow.Providers.Git;
using FeatureFlow.Providers.GitHub;
using FeatureFlow.Providers.Jira;
using FeatureFlow.Providers.Kubernetes;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Postgres;
using FeatureFlow.Providers.S3;
using FeatureFlow.Providers.Slack;
using FeatureFlow.Providers.StdValidators;

namespace FeatureFlow.Hosting.Tests;

/// <summary>
/// The configuration folders the repository ships (deploy/k3d/smoke and the distribution template) pass the startup
/// checks and compose with the factories every host registers, so they cannot drift from the providers' settings.
/// Providers are not created: that needs the cluster.
/// </summary>
public class DeploymentConfigurationTests
{
    static readonly IEnumerable<ProviderFactory> HostFactories = LocalProviders.Factories.Concat(DockerProviders.Factories)
        .Concat(ClaudeCodeProviders.Factories).Concat(GitProviders.Factories).Concat(GitHubProviders.Factories).Concat(StdValidatorProviders.Factories)
        .Concat(SlackProviders.Factories).Concat(PostgresProviders.Factories).Concat(S3Providers.Factories).Concat(KubernetesProviders.Factories)
        .Concat(CopilotCliProviders.Factories).Concat(JiraProviders.Factories);

    [Theory]
    [InlineData("deploy/k3d/smoke")]
    [InlineData("deploy/distribution-template/config")]
    public void A_shipped_configuration_validates_and_composes_with_the_hosts_providers(string folder)
    {
        var root = FindRoot();
        var copy = Path.Combine(Path.GetTempPath(), $"ff-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copy);
        try
        {
            // std is in the image at /opt/feature-flow/library/std; here it is the repository's.
            foreach (var file in Directory.GetFiles(Path.Combine(root, folder)))
                File.WriteAllText(Path.Combine(copy, Path.GetFileName(file)),
                    File.ReadAllText(file).Replace("/opt/feature-flow/library/std", Path.Combine(root, "library", "std")));
            var configuration = FeatureFlowConfiguration.Load(Path.Combine(copy, "installation.yaml"), Path.Combine(copy, "organisation.yaml"));
            var set = ProviderComposer.Compose(configuration.Installation,
                HostFactories.Select(f => f with { Create = _ => new DescribedOnlyProvider(f.Type) }));
            Assert.NotEmpty(set.Types);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "FeatureFlow.slnx")))
            dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
