using System.Diagnostics;
using System.Text.Json.Nodes;

namespace FeatureFlow.Hosting.Tests;

/// <summary>
/// R1's done-when for F6 (docs/06 section 4): the sample distribution (<c>samples/distribution</c>) adds a provider of its
/// own and runs a workflow test with it, built against the engine's packages only. The packages go to a temporary folder
/// feed, never to any external feed; the sample is copied outside the repository, so no project, source or
/// <c>Directory.*.props</c> of the repository can reach it; its package cache is its own, so no earlier pack of the same
/// version is served. Third-party packages come from nuget.org. Needs Temporal (<c>FF_TEMPORAL_ADDRESS</c>) and about two minutes.
/// </summary>
public class DistributionTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string TemporalAddress =>
        Environment.GetEnvironmentVariable("FF_TEMPORAL_ADDRESS") is { Length: > 0 } address ? address : "localhost:7233";

    [Fact]
    [Trait("Category", "Integration")]
    public async Task The_sample_host_runs_its_workflow_test_built_from_the_packages_only()
    {
        var root = Directory.CreateTempSubdirectory("ff-distribution-").FullName;
        try
        {
            var feed = Path.Combine(root, "feed");
            var sample = Path.Combine(root, "sample");
            // Built under the temporary folder (--artifacts-path), so the pack never touches the outputs other tests run from.
            var pack = await DotnetAsync(Repository.Root, new Dictionary<string, string>(),
                "pack", Path.Combine(Repository.Root, "FeatureFlow.slnx"), "-c", "Release", "-p:ConsoleBuild=false", "-o", feed,
                "--artifacts-path", Path.Combine(root, "build"));
            Assert.True(pack.Exit == 0, pack.Output);

            Copy(Path.Combine(Repository.Root, "samples", "distribution"), sample);
            await File.WriteAllTextAsync(Path.Combine(sample, "NuGet.config"), NuGetConfig(feed), Ct);
            var test = await DotnetAsync(sample,
                new Dictionary<string, string> { ["NUGET_PACKAGES"] = Path.Combine(root, "packages"), ["FF_TEMPORAL_ADDRESS"] = TemporalAddress },
                "test", Path.Combine("Acme.Host.Tests", "Acme.Host.Tests.csproj"));
            Assert.True(test.Exit == 0, test.Output);
            Assert.Matches(@"Passed:\s+1\b", test.Output);

            var libraries = Directory.EnumerateFiles(sample, "project.assets.json", SearchOption.AllDirectories)
                .SelectMany(path => JsonNode.Parse(File.ReadAllText(path))!["libraries"]!.AsObject())
                .Select(l => (Name: l.Key.Split('/')[0], Type: l.Value!["type"]!.ToString()))
                .Distinct()
                .ToList();
            var engine = libraries.Where(l => l.Name.StartsWith("FeatureFlow.", StringComparison.Ordinal)).ToList();
            Assert.Contains(("FeatureFlow.Hosting", "package"), engine);
            Assert.Contains(("FeatureFlow.Providers.Standard", "package"), engine);
            Assert.Contains(("FeatureFlow.Providers.Testing", "package"), engine);
            Assert.All(engine, l => Assert.Equal("package", l.Type));
            Assert.All(libraries.Where(l => l.Type == "project"), l => Assert.StartsWith("Acme.", l.Name, StringComparison.Ordinal));
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The engine's packages from the folder feed only; every other package from nuget.org.</summary>
    static string NuGetConfig(string feed) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="local" value="{feed}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
          </packageSources>
          <packageSourceMapping>
            <packageSource key="local"><package pattern="FeatureFlow.*" /></packageSource>
            <packageSource key="nuget.org"><package pattern="*" /></packageSource>
          </packageSourceMapping>
        </configuration>
        """;

    /// <summary>Copies the sample's sources, leaving out any local build output.</summary>
    static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(to, relative))!);
            File.Copy(file, Path.Combine(to, relative));
        }
    }

    /// <summary>Runs <c>dotnet</c> in <paramref name="directory"/>, without the MSBuild settings the test runner's own build may leave in the environment.</summary>
    static async Task<(int Exit, string Output)> DotnetAsync(string directory, IReadOnlyDictionary<string, string> environment, params string[] args)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("MSBuild", StringComparison.OrdinalIgnoreCase)).ToList())
            start.Environment.Remove(name);
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var (stdout, stderr) = (process.StandardOutput.ReadToEndAsync(Ct), process.StandardError.ReadToEndAsync(Ct));
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await stdout + await stderr);
    }
}
