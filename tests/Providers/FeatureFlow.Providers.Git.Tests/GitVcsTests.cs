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

    /// <summary>
    /// The review's finding 2: the default cache is shared by every instance and worker on a host. Two instances on one
    /// cache run catch-ups of different branches at once, ten rounds: each must report its own branch, and no scratch
    /// reference may be left.
    /// </summary>
    [Fact]
    public async Task Two_instances_sharing_a_cache_run_concurrent_operations_without_reading_each_others_references()
    {
        var shared = Path.Combine(root, "shared-cache");
        GitVcs Instance() => new(BareRepository.Template(Path.Combine(root, "remotes")), shared,
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => null);
        var (a, b) = (Instance(), Instance());
        var heads = new Dictionary<string, string>();
        foreach (var branch in new[] { "ff/b0", "ff/b1" })
            heads[branch] = remote.Commit("refs/heads/" + branch, new Dictionary<string, string?> { [branch.Replace('/', '-') + ".txt"] = branch }, from: "refs/heads/main");

        for (var round = 0; round < 10; round++)
        {
            remote.Commit("refs/heads/main", new Dictionary<string, string?> { [$"main-{round}.txt"] = $"{round}" });
            var results = await Task.WhenAll(
                a.CatchUpAsync(repo, "refs/heads/main", "refs/heads/ff/b0", heads["ff/b0"], "catch up", Ct),
                b.CatchUpAsync(repo, "refs/heads/main", "refs/heads/ff/b1", heads["ff/b1"], "catch up", Ct));
            foreach (var (branch, result) in new[] { "ff/b0", "ff/b1" }.Zip(results))
            {
                Assert.True(result.Ok, $"round {round}, {branch}: {result.Error?.Reason} {result.Error?.Message}");
                Assert.Equal(remote.Resolve("refs/heads/" + branch), result.Value!.WorkRevision);
                Assert.Equal(branch, remote.Read(result.Value.WorkRevision, branch.Replace('/', '-') + ".txt"));
                heads[branch] = result.Value.WorkRevision;
            }
        }

        Assert.Equal("", remote.GitIn(shared + "/ai-feature-flow_race.git", "for-each-ref", "refs/ff-cache/op/").Trim());
    }

    /// <summary>Another process holding the cache's lock file (the test stands in for it) makes an operation wait until it lets go.</summary>
    [Fact]
    public async Task An_operation_waits_for_another_process_holding_the_caches_lock()
    {
        var shared = Path.Combine(root, "locked-cache");
        Directory.CreateDirectory(shared);
        var vcs = new GitVcs(BareRepository.Template(Path.Combine(root, "remotes")), shared,
            () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => null);
        var target = remote.Resolve("refs/heads/main")!;
        var tested = remote.Commit("refs/heads/ff/work", new Dictionary<string, string?> { ["a.txt"] = "1" }, from: "refs/heads/main");

        Task<FeatureFlow.Providers.Abstractions.ProviderResult<MergeResult>> merge;
        await using (new FileStream(Path.Combine(shared, "ai-feature-flow_race.git.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            merge = vcs.MergeAsync(repo, tested, "refs/heads/main", target, MergeMethods.Merge, "merge", "key-lock", Ct);
            await Task.Delay(TimeSpan.FromSeconds(1), Ct);
            Assert.False(merge.IsCompleted, "the merge ran while another process held the cache");
            Assert.Equal(target, remote.Resolve("refs/heads/main"));
        }

        Assert.False((await merge).Unwrap().AlreadyMerged);
    }

    /// <summary>git's messages, as GitHub's HTTPS answers produce them, classed as docs/06 section 1.1 says (review finding 3).</summary>
    [Theory]
    [InlineData("remote: Invalid username or token.\nfatal: Authentication failed for 'https://github.com/o/r.git/'", "unauthenticated")]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 401", "unauthenticated")]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 403", "forbidden")]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': The requested URL returned error: 429", "rate_limited")]
    [InlineData("fatal: unable to access 'https://github.com/o/r.git/': Could not resolve host: github.com", "unreachable")]
    [InlineData("error: RPC failed; curl 56 Recv failure: Connection reset by peer", "unreachable")]
    [InlineData("fatal: couldn't find remote ref refs/heads/x", "validation_failed")]
    public void Git_failures_are_classified(string stderr, string reason)
    {
        Assert.Equal(reason, GitVcs.Classify(stderr, "validation_failed"));
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

    /// <summary>
    /// The review's probe (finding 1): run 1 merges an agent's commit whose message carries run 2's merge key as a
    /// trailer. Run 2's merge must still land its change, not report it already merged.
    /// </summary>
    [Fact]
    public async Task A_trailer_forged_by_an_earlier_merge_does_not_count_as_landed()
    {
        var start = remote.Resolve("refs/heads/main")!;
        var forged = remote.Commit("refs/heads/ff/run-1", new Dictionary<string, string?> { ["agent.txt"] = "x" }, from: "refs/heads/main",
            message: "An agent's commit\n\nFeature-Flow-Merge: run-2/scm.merge/1");
        var first = (await Worker("a").MergeAsync(repo, forged, "refs/heads/main", start, MergeMethods.Merge, "merge run 1", "run-1/scm.merge/1", Ct)).Unwrap();
        var tested = remote.Commit("refs/heads/ff/run-2", new Dictionary<string, string?> { ["run2.txt"] = "run 2" }, from: first.Revision);

        var second = (await Worker("a").MergeAsync(repo, tested, "refs/heads/main", first.Revision, MergeMethods.Squash, "merge run 2", "run-2/scm.merge/1", Ct)).Unwrap();

        Assert.False(second.AlreadyMerged, "run 2 was reported merged but nothing landed");
        Assert.Equal("run 2", remote.Read("refs/heads/main", "run2.txt"));
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
