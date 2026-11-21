using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Docker;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// F4: a repository's sandboxes are made from the image its manifest names (<c>environment.image</c>, LOG 440), when the
/// installation allows it (<c>allowed_images</c>, LOG 441). The test builds a small image <c>FROM</c> the base sandbox
/// image that adds a command the base lacks, and runs <c>test.run</c> in the Docker sandbox on a repository whose only tier
/// is that command: it passes only in the repository's image.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SandboxImageTests
{
    const string RepositoryImage = "feature-flow-f4-test:1";
    const string Marker = "ff-f4-marker";
    static readonly string[] Library = [Path.Combine(Harness.TestLibraries, "m5")];
    static readonly SemaphoreSlim Gate = new(1, 1);
    static bool built;

    /// <summary>The repository's image: the base sandbox image and a marker command (docs/06 section 3.1, the base image's contract).</summary>
    static async Task BuildRepositoryImageAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (built)
                return;
            await AgentWorld.ImageAsync();
            var folder = Directory.CreateTempSubdirectory("ff-f4-image-").FullName;
            await File.WriteAllTextAsync(Path.Combine(folder, "Dockerfile"), $"""
                FROM {AgentWorld.Image}
                RUN printf '#!/bin/sh\necho in the repository image\n' > /usr/local/bin/{Marker} && chmod 755 /usr/local/bin/{Marker}
                """);
            var (exit, output) = await AgentWorld.RunAsync("docker", ["build", "-q", "-t", RepositoryImage, folder]);
            Directory.Delete(folder, recursive: true);
            Assert.True(exit == 0, "the repository's image did not build:\n" + output);
            built = true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Runs <c>tests_only</c> on a repository whose one tier is the marker command, with the manifest's <paramref name="environment"/> line.</summary>
    static async Task<(Harness Harness, string RunId)> StartAsync(string name, string environment)
    {
        await BuildRepositoryImageAsync();
        var h = await Harness.StartAsync(name, libraries: Library, without: ["sandbox"], factories: DockerProviders.Factories,
            providers: $$"""  sandbox: { default: docker, instances: { docker: { image: "{{AgentWorld.Image}}", allowed_images: ["feature-flow-f4-test:*"] } } }""");
        var repo = h.Forge.CreateRepository("acme/imaged", new Dictionary<string, string>
        {
            ["feature-flow.yaml"] = $$"""
                api_version: feature-flow/v1
                kind: repository
                {{environment}}
                test_tiers:
                  - { name: unit, tags: [unit], command: {{Marker}} }
                """,
        });
        h.Forge.Protect(repo, new BranchProtection("main", true, true, 1, [], false));
        return (h, h.Start("m5:templates/tests_only", "--repo", "acme/imaged"));
    }

    [Fact]
    public async Task A_repository_that_names_an_image_runs_its_tests_in_that_image()
    {
        var (h, id) = await StartAsync("f4-image", $"environment: {{ image: \"{RepositoryImage}\" }}");
        await using var _ = h;

        var (exit, end, _) = h.Cli("wait", id, "--timeout", "180");

        var view = await h.ShowAsync(id);
        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), view.ToJsonString());
        Assert.Contains(view["timeline"]!.AsArray(), e => e!["detail"]?.ToString().Contains(": 1 passed, 0 failed", StringComparison.Ordinal) == true);
    }

    /// <summary>The control: the same tier in the installation's image fails, so the run above passed because of its image.</summary>
    [Fact]
    public async Task A_repository_that_names_no_image_runs_its_tests_in_the_installations_image()
    {
        var (h, id) = await StartAsync("f4-default", "");
        await using var _ = h;

        var (_, end, _) = h.Cli("wait", id, "--timeout", "180");

        var view = await h.ShowAsync(id);
        Assert.True(end.Trim() == $"run {id} failed", view.ToJsonString());
        // Failed by its tier, run in the installation's image, which has no marker command.
        Assert.Contains(view["timeline"]!.AsArray(), e => e!["detail"]?.ToString().Contains(": 0 passed, 1 failed", StringComparison.Ordinal) == true);
    }

    /// <summary>An image the installation does not allow stops the step for a person; no container is made from it.</summary>
    [Fact]
    public async Task An_image_the_installation_does_not_allow_stops_the_step_for_a_person()
    {
        var (h, id) = await StartAsync("f4-refused", $"environment: {{ image: \"{AgentWorld.Image}-elsewhere\" }}");
        await using var _ = h;

        Assert.NotNull(await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(3)));

        var wait = (await h.ShowAsync(id))["waits"]!.AsArray().Single()!;
        Assert.Contains("matches none of its allowed_images", wait["prompt"]!.ToString(), StringComparison.Ordinal);
    }
}
