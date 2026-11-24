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
using FeatureFlow.Providers.Testing;

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
        .Concat(CopilotCliProviders.Factories).Concat(JiraProviders.Factories).Concat(TestingProviders.Factories);

    [Theory]
    [InlineData("deploy/k3d/smoke")]
    [InlineData("deploy/distribution-template/config")]
    public void A_shipped_configuration_validates_and_composes_with_the_hosts_providers(string folder)
    {
        var root = Repository.Root;
        var copy = Path.Combine(Path.GetTempPath(), $"ff-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copy);
        try
        {
            // std is in the image at /opt/feature-flow/library/std; here it is the repository's. The trial Git server's
            // repositories (deploy/k3d/git.yaml) are made here as it makes them, from the folder's repositories/.
            var served = Path.Combine(copy, "served");
            var repositories = Path.Combine(root, folder, "repositories");
            if (Directory.Exists(repositories))
                foreach (var repository in Directory.GetDirectories(repositories).SelectMany(Directory.GetDirectories))
                    Serve(repository, Path.Combine(served, Path.GetRelativePath(repositories, repository) + ".git"));
            foreach (var file in Directory.GetFiles(Path.Combine(root, folder)))
                File.WriteAllText(Path.Combine(copy, Path.GetFileName(file)),
                    File.ReadAllText(file).Replace("/opt/feature-flow/library/std", Path.Combine(root, "library", "std"))
                        .Replace("git://git.feature-flow.svc/", new Uri(served + "/").AbsoluteUri));
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

    /// <summary>A bare repository at <paramref name="bare"/> holding <paramref name="folder"/>'s files in one commit on main.</summary>
    static void Serve(string folder, string bare)
    {
        var work = bare + ".work";
        CopyTree(folder, work);
        Git(work, "init", "-q", "-b", "main");
        Git(work, "add", "-A");
        Git(work, "-c", "user.name=test", "-c", "user.email=test@feature-flow.invalid", "commit", "-q", "-m", "seed");
        Git(work, "clone", "-q", "--bare", work, bare);
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    static void Git(string directory, params string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
    }
}
