using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Standard;
using FeatureFlow.Providers.Testing;

namespace FeatureFlow.Hosting.Tests;

/// <summary>
/// The configuration folders the repository ships (deploy/k3d/smoke and the distribution template) pass the startup
/// checks and compose with the factories every host registers, so they cannot drift from the providers' settings.
/// Providers are not created: that needs the cluster.
/// </summary>
public class DeploymentConfigurationTests
{
    static readonly IEnumerable<ProviderFactory> HostFactories = StandardProviders.Factories.Concat(TestingProviders.Factories);

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
            TrackersMapStdsStatuses(configuration.Installation);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>
    /// The solo developer's example files (<c>deploy/local</c>) validate and compose, each installation with the one
    /// organisation file, and the example secrets file holds every secret the installations name.
    /// </summary>
    [Theory]
    [InlineData("installation.claude.yaml")]
    [InlineData("installation.copilot.yaml")]
    public void The_local_example_configuration_validates_and_composes_with_the_hosts_providers(string installationFile)
    {
        var root = Repository.Root;
        var folder = Path.Combine(root, "deploy", "local");
        var copy = Path.Combine(Path.GetTempPath(), $"ff-config-{Guid.NewGuid():N}");
        Directory.CreateDirectory(copy);
        try
        {
            File.Copy(Path.Combine(folder, installationFile), Path.Combine(copy, "installation.yaml"));
            File.WriteAllText(Path.Combine(copy, "organisation.yaml"),
                File.ReadAllText(Path.Combine(folder, "organisation.yaml")).Replace("<CHECKOUT>", root));
            var configuration = FeatureFlowConfiguration.Load(Path.Combine(copy, "installation.yaml"), Path.Combine(copy, "organisation.yaml"));
            var set = ProviderComposer.Compose(configuration.Installation,
                HostFactories.Select(f => f with { Create = _ => new DescribedOnlyProvider(f.Type) }));
            Assert.NotEmpty(set.Types);
            TrackersMapStdsStatuses(configuration.Installation);

            var secrets = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "secrets.example.json"))).RootElement;
            var referenced = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Path.Combine(folder, installationFile)), @"^[^#\n]*\bsecrets/(\w+)", System.Text.RegularExpressions.RegexOptions.Multiline)
                .Select(m => m.Groups[1].Value).Where(name => name != "github_app_key").Distinct();
            foreach (var name in referenced)
                Assert.True(secrets.TryGetProperty(name, out _), $"secrets.example.json has no '{name}', which {installationFile} names");
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    /// <summary>
    /// L8: a status a tracker's <c>status_map</c> does not name is the tracker status of the same name, so a shipped
    /// configuration's tracker maps each of std's statuses (<c>library/std/status_map.yaml</c>) to its own.
    /// </summary>
    static void TrackersMapStdsStatuses(FeatureFlow.Model.InstallationConfig installation)
    {
        string[] std = ["in_progress", "code_review", "done"];
        foreach (var instance in installation.Providers.GetValueOrDefault("tracker")?.Instances.Values ?? [])
        {
            var factory = HostFactories.Single(f => f.Type == "tracker" && f.Kind == instance.Kind);
            var tracker = (ITrackerProvider)factory.Create(new ProviderContext(instance, ".", _ => null));
            var mapped = tracker.MappedStatuses;
            Assert.True(mapped is null || std.All(mapped.Contains),
                $"tracker instance '{instance.Name}' maps {(mapped is { Count: > 0 } ? string.Join(", ", mapped.Order(StringComparer.Ordinal)) : "none")} of std's statuses; its status_map should map all three");
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
