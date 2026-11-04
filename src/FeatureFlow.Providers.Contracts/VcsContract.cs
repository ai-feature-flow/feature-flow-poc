using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>Helpers the contract suites share.</summary>
public abstract class ContractBase
{
    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>The value of a result that must have succeeded; the error's reason and message otherwise.</summary>
    protected static T Ok<T>(ProviderResult<T> result)
    {
        Assert.True(result.Ok, $"expected success, got {result.Error?.Reason}: {result.Error?.Message}");
        return result.Value!;
    }

    /// <summary>Asserts a result failed with <paramref name="reason"/>.</summary>
    protected static void Refused<T>(ProviderResult<T> result, string reason)
    {
        Assert.False(result.Ok, $"expected {reason}, got success");
        Assert.Equal(reason, result.Error!.Reason);
    }
}

/// <summary>
/// The VCS provider's contract (docs/06 section 3.3): expected-revision pushes, catch-up and merge on the remote.
/// A provider's test project subclasses it with a fresh repository per test (xUnit makes an instance per test).
/// Working-copy methods need a sandbox and are covered by the agent step's tests, not here.
/// </summary>
public abstract class VcsContract : ContractBase
{
    protected const string Main = "refs/heads/main";
    protected const string Work = "refs/heads/ff/work";

    protected abstract IVcsProvider Vcs { get; }

    /// <summary>A repository whose default branch, <c>main</c>, has one commit holding <c>README.md</c>.</summary>
    protected abstract RepoRef Repo { get; }

    /// <summary>
    /// Commits <paramref name="changes"/> (a null value deletes the path) on top of <paramref name="reference"/> on
    /// the remote, as someone other than the engine, and moves the reference. A reference that does not exist starts
    /// from <paramref name="from"/>, a reference or a revision. Returns the new revision.
    /// </summary>
    protected abstract Task<string> CommitOnRemoteAsync(string reference, IReadOnlyDictionary<string, string?> changes, string? from = null);

    /// <summary>The content of <paramref name="path"/> at <paramref name="revision"/> on the remote, or null.</summary>
    protected abstract Task<string?> ReadOnRemoteAsync(string revision, string path);

    async Task<string> At(string reference) => Ok(await Vcs.ResolveAsync(Repo, reference, Ct)) ?? throw new InvalidOperationException($"{reference} does not exist");

    Task<string> Change(string reference, string path, string content, string? from = null) =>
        CommitOnRemoteAsync(reference, new Dictionary<string, string?> { [path] = content }, from);

    [Fact]
    public async Task A_missing_reference_resolves_to_null()
    {
        Assert.Null(Ok(await Vcs.ResolveAsync(Repo, "refs/heads/no-such-branch", Ct)));
        Assert.NotNull(Ok(await Vcs.ResolveAsync(Repo, Main, Ct)));
    }

    [Fact]
    public async Task A_push_expecting_no_reference_creates_it_and_is_refused_once_it_exists()
    {
        var main = await At(Main);

        var created = Ok(await Vcs.PushAsync(Repo, Work, main, null, null, Ct));
        Assert.Equal(new PushResult(Work, null, main), created);
        Refused(await Vcs.PushAsync(Repo, Work, main, null, null, Ct), "base_moved");
    }

    [Fact]
    public async Task A_push_expecting_a_revision_the_reference_has_left_is_refused_with_base_moved()
    {
        var before = await At(Main);
        var theirs = await Change(Main, "theirs.txt", "someone else");
        await Change(Work, "ours.txt", "ours", from: before);
        var ours = await At(Work);

        Refused(await Vcs.PushAsync(Repo, Main, ours, null, before, Ct), "base_moved");
        Assert.Equal(theirs, await At(Main));
    }

    [Fact]
    public async Task A_push_may_move_a_reference_back_when_it_names_the_current_revision()
    {
        // A clean_up route resets the work branch to where its target step started (docs/03 section 4.1).
        var start = await Change(Work, "a.txt", "1", from: Main);
        var later = await Change(Work, "a.txt", "2");

        Ok(await Vcs.PushAsync(Repo, Work, start, null, later, Ct));
        Assert.Equal(start, await At(Work));
    }

    [Fact]
    public async Task A_push_moving_a_reference_back_is_refused_once_it_has_moved_on()
    {
        // A reset must not throw away a commit it did not see.
        var start = await Change(Work, "a.txt", "1", from: Main);
        var later = await Change(Work, "a.txt", "2");
        var theirs = await Change(Work, "a.txt", "3");

        Refused(await Vcs.PushAsync(Repo, Work, start, null, later, Ct), "base_moved");
        Assert.Equal(theirs, await At(Work));
    }

    [Fact]
    public async Task Descent_follows_history()
    {
        var main = await At(Main);
        var work = await Change(Work, "a.txt", "1", from: Main);
        var other = await Change(Main, "b.txt", "2");

        Assert.True(Ok(await Vcs.DescendsFromAsync(Repo, work, main, Ct)));
        Assert.True(Ok(await Vcs.DescendsFromAsync(Repo, work, work, Ct)));
        Assert.False(Ok(await Vcs.DescendsFromAsync(Repo, main, work, Ct)));
        Assert.False(Ok(await Vcs.DescendsFromAsync(Repo, work, other, Ct)));
    }

    [Fact]
    public async Task The_merge_base_is_where_two_histories_part()
    {
        var main = await At(Main);
        var work = await Change(Work, "a.txt", "1", from: Main);
        var other = await Change(Main, "b.txt", "2");

        Assert.Equal(main, Ok(await Vcs.MergeBaseAsync(Repo, work, other, Ct)));
        Assert.Equal(main, Ok(await Vcs.MergeBaseAsync(Repo, other, work, Ct)));
        Assert.Equal(work, Ok(await Vcs.MergeBaseAsync(Repo, work, work, Ct)));
        Assert.Equal(main, Ok(await Vcs.MergeBaseAsync(Repo, work, main, Ct)));
    }

    [Fact]
    public async Task A_catch_up_with_nothing_to_merge_succeeds_unchanged()
    {
        // GitHub's merge API answered this with 204 and no body, which Spike 3 misread as an error.
        var work = await Change(Work, "a.txt", "1", from: Main);

        var result = Ok(await Vcs.CatchUpAsync(Repo, Main, Work, work, "catch up", Ct));

        Assert.False(result.Changed);
        Assert.False(result.Conflicted);
        Assert.Equal(work, result.WorkRevision);
        Assert.Equal(await At(Main), result.ParentRevision);
        Assert.Equal(work, await At(Work));
    }

    [Fact]
    public async Task A_catch_up_merges_the_parent_into_the_work_branch()
    {
        var work = await Change(Work, "a.txt", "ours", from: Main);
        var parent = await Change(Main, "b.txt", "theirs");

        var result = Ok(await Vcs.CatchUpAsync(Repo, Main, Work, work, "catch up", Ct));

        Assert.True(result.Changed);
        Assert.Equal(parent, result.ParentRevision);
        Assert.Equal(result.WorkRevision, await At(Work));
        Assert.True(Ok(await Vcs.DescendsFromAsync(Repo, result.WorkRevision, parent, Ct)));
        Assert.True(Ok(await Vcs.DescendsFromAsync(Repo, result.WorkRevision, work, Ct)));
        Assert.Equal("ours", await ReadOnRemoteAsync(result.WorkRevision, "a.txt"));
        Assert.Equal("theirs", await ReadOnRemoteAsync(result.WorkRevision, "b.txt"));
    }

    [Fact]
    public async Task A_catch_up_with_conflicts_lists_them_and_pushes_nothing()
    {
        var work = await Change(Work, "README.md", "ours\n", from: Main);
        await Change(Main, "README.md", "theirs\n");

        var result = Ok(await Vcs.CatchUpAsync(Repo, Main, Work, work, "catch up", Ct));

        Assert.True(result.Conflicted);
        Assert.Equal(["README.md"], result.Conflicts);
        Assert.False(result.Changed);
        Assert.Equal(work, await At(Work));
    }

    [Fact]
    public async Task A_catch_up_whose_work_branch_moved_is_refused_with_base_moved()
    {
        var work = await Change(Work, "a.txt", "1", from: Main);
        var moved = await Change(Work, "a.txt", "2");
        await Change(Main, "b.txt", "theirs");

        Refused(await Vcs.CatchUpAsync(Repo, Main, Work, work, "catch up", Ct), "base_moved");
        Assert.Equal(moved, await At(Work));
    }

    [Fact]
    public async Task A_catch_up_retried_after_it_landed_reports_the_landed_revision_as_changed()
    {
        var work = await Change(Work, "a.txt", "ours", from: Main);
        await Change(Main, "b.txt", "theirs");

        var first = Ok(await Vcs.CatchUpAsync(Repo, Main, Work, work, "catch up", Ct));
        var retry = Ok(await Vcs.CatchUpAsync(Repo, Main, Work, work, "catch up", Ct));

        Assert.True(retry.Changed);
        Assert.Equal(first.WorkRevision, retry.WorkRevision);
    }

    [Fact]
    public async Task A_merge_lands_exactly_the_tested_tree()
    {
        var target = await At(Main);
        var tested = await Change(Work, "a.txt", "tested", from: Main);

        var result = Ok(await Vcs.MergeAsync(Repo, tested, Main, target, MergeMethods.Merge, "merge the work", "key-merge", Ct));

        Assert.False(result.AlreadyMerged);
        Assert.Equal(target, result.TargetBefore);
        Assert.Equal(MergeMethods.Merge, result.Method);
        Assert.Equal(result.Revision, await At(Main));
        Assert.True(Ok(await Vcs.DescendsFromAsync(Repo, result.Revision, tested, Ct)));
        Assert.Equal("tested", await ReadOnRemoteAsync(result.Revision, "a.txt"));
    }

    [Fact]
    public async Task A_squash_lands_the_tested_tree_without_the_tested_history()
    {
        var target = await At(Main);
        await Change(Work, "a.txt", "1", from: Main);
        var tested = await Change(Work, "a.txt", "2");

        var result = Ok(await Vcs.MergeAsync(Repo, tested, Main, target, MergeMethods.Squash, "squash the work", "key-squash", Ct));

        Assert.False(result.AlreadyMerged);
        Assert.Equal(result.Revision, await At(Main));
        Assert.True(Ok(await Vcs.DescendsFromAsync(Repo, result.Revision, target, Ct)));
        Assert.False(Ok(await Vcs.DescendsFromAsync(Repo, result.Revision, tested, Ct)));
        Assert.Equal("2", await ReadOnRemoteAsync(result.Revision, "a.txt"));
    }

    [Fact]
    public async Task A_merge_whose_target_moved_is_refused_with_base_moved()
    {
        var target = await At(Main);
        var tested = await Change(Work, "a.txt", "tested", from: Main);
        var moved = await Change(Main, "b.txt", "a sibling merged");

        Refused(await Vcs.MergeAsync(Repo, tested, Main, target, MergeMethods.Merge, "merge", "key-moved", Ct), "base_moved");
        Assert.Equal(moved, await At(Main));
    }

    [Fact]
    public async Task A_merge_of_a_revision_that_does_not_contain_the_target_is_refused()
    {
        // Nothing untested lands: the tested head must already hold everything on the target.
        var tested = await Change(Work, "a.txt", "tested", from: Main);
        var target = await Change(Main, "b.txt", "not in the tested head");

        Refused(await Vcs.MergeAsync(Repo, tested, Main, target, MergeMethods.Merge, "merge", "key-stale", Ct), "base_moved");
        Assert.Equal(target, await At(Main));
    }

    [Theory]
    [InlineData(MergeMethods.Merge)]
    [InlineData(MergeMethods.Squash)]
    public async Task A_merge_retried_after_it_landed_reports_already_merged(string method)
    {
        // DR18: the refused retry checks whether the merge already landed, even after the target moved on.
        var target = await At(Main);
        var tested = await Change(Work, "a.txt", "tested", from: Main);
        var landed = Ok(await Vcs.MergeAsync(Repo, tested, Main, target, method, "merge", "key-retry-" + method, Ct));
        var after = await Change(Main, "b.txt", "a later merge");

        var retry = Ok(await Vcs.MergeAsync(Repo, tested, Main, target, method, "merge", "key-retry-" + method, Ct));

        Assert.True(retry.AlreadyMerged);
        Assert.Null(retry.TargetBefore);
        Assert.Equal(after, await At(Main));
        Assert.True(Ok(await Vcs.DescendsFromAsync(Repo, after, landed.Revision, Ct)));
    }
}
