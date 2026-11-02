using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Git.Tests;

/// <summary>Behaviour of the Git provider beyond the VCS contract, over local bare repositories.</summary>
public sealed class GitVcsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ff-git-tests-" + Guid.NewGuid().ToString("N"));
    readonly RepoRef repo = new("ai-feature-flow/race");
    readonly BareRepository remote;

    public GitVcsTests() => remote = new BareRepository(Path.Combine(root, "remotes"), repo.FullName);

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A worker of its own: the provider with its own clone cache.</summary>
    GitVcs Worker(string name) => new(BareRepository.Template(Path.Combine(root, "remotes")), Path.Combine(root, "cache-" + name),
        () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => null);

    /// <summary>
    /// DR18. Two workers run the same merge at once (a retried activity): the remote holds each push in a hook, so the
    /// second worker reads the target before the first push lands and its own push is refused. Re-reading the target
    /// after the refusal finds the change landed, so both report success and the change lands once.
    /// </summary>
    [Fact]
    public async Task Two_workers_running_the_same_merge_land_it_once_and_both_succeed()
    {
        var target = remote.Resolve("refs/heads/main")!;
        var tested = remote.Commit("refs/heads/ff/work", new Dictionary<string, string?> { ["a.txt"] = "tested" }, from: "refs/heads/main");
        var hook = Path.Combine(remote.Path, "hooks", "pre-receive");
        File.WriteAllText(hook, "#!/bin/sh\ncat > /dev/null\nsleep 3\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var first = Worker("a").MergeAsync(repo, tested, "refs/heads/main", target, MergeMethods.Merge, "merge", "key-race", Ct);
        // Over a second apart, so the two merge commits differ in their timestamps; identical commits would land as one push.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), Ct);
        var second = Worker("b").MergeAsync(repo, tested, "refs/heads/main", target, MergeMethods.Merge, "merge", "key-race", Ct);
        var results = await Task.WhenAll(first, second);

        Assert.All(results, r => Assert.True(r.Ok, $"{r.Error?.Reason}: {r.Error?.Message}"));
        Assert.Equal([false, true], results.Select(r => r.Value!.AlreadyMerged).Order());
        Assert.Equal([results[0].Value!.Revision], remote.FirstParents(target, "refs/heads/main"));
    }

    /// <summary>
    /// The catch-up's expected-revision push: someone pushes to the work branch after the provider fetched it and
    /// before it pushes the merge. The push is refused, and their commit stays.
    /// </summary>
    [Fact]
    public async Task A_catch_up_whose_work_branch_moves_before_its_push_is_refused_and_loses_nothing()
    {
        var work = remote.Commit("refs/heads/ff/work", new Dictionary<string, string?> { ["a.txt"] = "ours" }, from: "refs/heads/main");
        remote.Commit("refs/heads/main", new Dictionary<string, string?> { ["b.txt"] = "theirs" });
        string? theirs = null;
        var vcs = new GitVcs(BareRepository.Template(Path.Combine(root, "remotes")), Path.Combine(root, "cache"),
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => null)
        {
            BeforePushForTests = reference =>
            {
                theirs ??= remote.Commit(reference, new Dictionary<string, string?> { ["c.txt"] = "pushed meanwhile" });
                return Task.CompletedTask;
            },
        };

        var result = await vcs.CatchUpAsync(repo, "refs/heads/main", "refs/heads/ff/work", work, "catch up", Ct);

        Assert.Equal("base_moved", result.Error?.Reason);
        Assert.Equal(theirs, remote.Resolve("refs/heads/ff/work"));
    }

    [Fact]
    public async Task A_catch_up_of_a_missing_branch_is_not_found()
    {
        var result = await Worker("a").CatchUpAsync(repo, "refs/heads/main", "refs/heads/ff/none", "0000000000000000000000000000000000000000", "catch up", Ct);
        Assert.Equal("not_found", result.Error?.Reason);
    }

    [Fact]
    public async Task An_unknown_merge_method_is_refused()
    {
        var target = remote.Resolve("refs/heads/main")!;
        var result = await Worker("a").MergeAsync(repo, target, "refs/heads/main", target, "rebase", "merge", "key", Ct);
        Assert.Equal("validation_failed", result.Error?.Reason);
    }

    [Fact]
    public async Task Merge_commits_carry_the_key_as_a_trailer_and_the_engines_identity()
    {
        var target = remote.Resolve("refs/heads/main")!;
        var tested = remote.Commit("refs/heads/ff/work", new Dictionary<string, string?> { ["a.txt"] = "tested" }, from: "refs/heads/main");

        var merged = (await Worker("a").MergeAsync(repo, tested, "refs/heads/main", target, MergeMethods.Merge, "Merge ticket T-1", "run-1/merge/1", Ct)).Unwrap();

        Assert.Equal([target, tested], remote.Parents(merged.Revision));
        Assert.Equal("run-1/merge/1", remote.Git("log", "-1", "--format=%(trailers:key=Feature-Flow-Merge,valueonly)", merged.Revision).Trim());
        Assert.Equal("Feature Flow", remote.Git("log", "-1", "--format=%an", merged.Revision).Trim());
    }

    [Fact]
    public async Task A_key_that_only_prefixes_a_landed_key_is_not_already_merged()
    {
        var target = remote.Resolve("refs/heads/main")!;
        var tested = remote.Commit("refs/heads/ff/work", new Dictionary<string, string?> { ["a.txt"] = "1" }, from: "refs/heads/main");
        var landed = (await Worker("a").MergeAsync(repo, tested, "refs/heads/main", target, MergeMethods.Squash, "squash", "key-10", Ct)).Unwrap();
        var next = remote.Commit("refs/heads/ff/other", new Dictionary<string, string?> { ["b.txt"] = "2" }, from: landed.Revision);

        var result = (await Worker("a").MergeAsync(repo, next, "refs/heads/main", landed.Revision, MergeMethods.Squash, "squash", "key-1", Ct)).Unwrap();

        Assert.False(result.AlreadyMerged);
    }
}
