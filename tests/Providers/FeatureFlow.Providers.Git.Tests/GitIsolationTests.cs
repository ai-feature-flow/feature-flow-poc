using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Git.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "changes the process environment";
}

/// <summary>
/// Review finding 10: the engine's git ignores the worker's own git configuration. The worker's global configuration
/// (set for this process) rewrites the remote's URL to a path that does not exist; the provider must not see it.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class GitIsolationTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ff-git-isolation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task The_workers_global_git_configuration_does_not_reach_the_engines_git()
    {
        var repo = new RepoRef("ai-feature-flow/isolated");
        var remotes = Path.Combine(root, "remotes");
        var remote = new BareRepository(remotes, repo.FullName);
        var global = Path.Combine(root, "global.gitconfig");
        File.WriteAllText(global, $"[url \"{Path.Combine(root, "nowhere")}/\"]\n\tinsteadOf = {remotes}/\n");
        var before = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", global);
        try
        {
            var vcs = new GitVcs(BareRepository.Template(remotes), Path.Combine(root, "cache"),
                () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => null);

            var main = await vcs.ResolveAsync(repo, "refs/heads/main", TestContext.Current.CancellationToken);

            Assert.True(main.Ok, $"{main.Error?.Reason}: {main.Error?.Message}");
            Assert.Equal(remote.Resolve("refs/heads/main"), main.Value);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", before);
        }
    }
}
