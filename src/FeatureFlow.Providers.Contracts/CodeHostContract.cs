using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The code host provider's contract (docs/06 section 3.4): default branch, protection, file reads, pull requests
/// and their reverts, checks. A provider's test project subclasses it with a fresh repository per test.
/// </summary>
public abstract class CodeHostContract : ContractBase
{
    protected abstract ICodeHostProvider CodeHost { get; }

    /// <summary>A repository whose default branch, <c>main</c>, holds <c>README.md</c>.</summary>
    protected abstract RepoRef Repo { get; }

    /// <summary>
    /// Commits <paramref name="changes"/> on top of <paramref name="branch"/> (a branch name) as someone other than
    /// the engine; a branch that does not exist starts from <paramref name="from"/>, default <c>main</c>. Returns the revision.
    /// </summary>
    protected abstract Task<string> CommitOnRemoteAsync(string branch, IReadOnlyDictionary<string, string?> changes, string? from = null);

    /// <summary>A person merges the pull request on the code host. Returns the identity the host will report as its merger.</summary>
    protected abstract Task<string> MergeAsPersonAsync(PullRequestRef pullRequest);

    Task<string> Change(string branch, string path, string content, string? from = null) =>
        CommitOnRemoteAsync(branch, new Dictionary<string, string?> { [path] = content }, from);

    async Task<PullRequestRef> OpenAsync(string head, string key)
    {
        await Change(head, "work.txt", head);
        return Ok(await CodeHost.OpenPullRequestAsync(Repo, new PullRequestSpec($"Work on {head}", "The body.", head, "main"), key, Ct));
    }

    [Fact]
    public async Task The_default_branch_is_the_hosts()
    {
        Assert.Equal("main", Ok(await CodeHost.GetDefaultBranchAsync(Repo, Ct)));
    }

    [Fact]
    public async Task Protection_is_read_for_the_default_branch()
    {
        var protection = Ok(await CodeHost.GetBranchProtectionAsync(Repo, "main", Ct));
        Assert.Equal("main", protection.Branch);
    }

    [Fact]
    public async Task A_file_is_read_at_a_reference_with_its_revision_and_hash()
    {
        var revision = await Change("ff/manifest", "feature-flow.yaml", "tiers: []\n");

        var file = Ok(await CodeHost.ReadFileAsync(Repo, "feature-flow.yaml", "ff/manifest", Ct))!;
        var atRevision = Ok(await CodeHost.ReadFileAsync(Repo, "feature-flow.yaml", revision, Ct))!;

        Assert.Equal("tiers: []\n", file.Content);
        Assert.Equal(revision, file.Revision);
        Assert.Equal(file.Hash, atRevision.Hash);
        Assert.Null(Ok(await CodeHost.ReadFileAsync(Repo, "feature-flow.yaml", "main", Ct)));
    }

    [Fact]
    public async Task Opening_a_pull_request_twice_with_one_key_opens_one()
    {
        var first = await OpenAsync("ff/twice", "key-twice");
        var second = Ok(await CodeHost.OpenPullRequestAsync(Repo, new PullRequestSpec("Retitled", "The body.", "ff/twice", "main"), "key-twice", Ct));

        Assert.Equal(first, second);
        Assert.Equal("Retitled", Ok(await CodeHost.GetPullRequestAsync(first, Ct)).Title);
    }

    [Fact]
    public async Task A_pull_request_reports_its_head_base_and_state()
    {
        var head = await Change("ff/state", "work.txt", "1");
        var pr = Ok(await CodeHost.OpenPullRequestAsync(Repo, new PullRequestSpec("State", "The body.", "ff/state", "main"), "key-state", Ct));

        var state = Ok(await CodeHost.GetPullRequestAsync(pr, Ct));

        Assert.Equal(PullRequestStates.Open, state.State);
        Assert.Equal(("ff/state", head, "main"), (state.Head, state.HeadRevision, state.Base));
        Assert.Null(state.MergedBy);
        Assert.Contains("key-state", state.Body);
    }

    [Fact]
    public async Task A_pull_request_with_nothing_to_merge_is_refused()
    {
        var merged = await OpenAsync("ff/empty", "key-empty");
        await MergeAsPersonAsync(merged);

        Refused(await CodeHost.OpenPullRequestAsync(Repo, new PullRequestSpec("Again", "", "ff/empty", "main"), "key-empty-again", Ct), "validation_failed");
    }

    [Fact]
    public async Task An_update_sets_title_and_body_and_keeps_the_marker()
    {
        var pr = await OpenAsync("ff/update", "key-update");

        Ok(await CodeHost.UpdatePullRequestAsync(pr, new PullRequestSpec("New title", "New body.", "ff/update", "main"), Ct));
        var state = Ok(await CodeHost.GetPullRequestAsync(pr, Ct));

        Assert.Equal("New title", state.Title);
        Assert.Contains("New body.", state.Body);
        Assert.Contains("key-update", state.Body);
    }

    [Fact]
    public async Task A_persons_merge_is_reported_with_who_merged()
    {
        var pr = await OpenAsync("ff/person", "key-person");

        var who = await MergeAsPersonAsync(pr);
        var state = Ok(await CodeHost.GetPullRequestAsync(pr, Ct));

        Assert.Equal(PullRequestStates.Merged, state.State);
        Assert.Equal(who, state.MergedBy);
        Assert.NotNull(state.MergeRevision);
    }

    [Fact]
    public async Task Closing_twice_succeeds()
    {
        var pr = await OpenAsync("ff/close", "key-close");

        Ok(await CodeHost.ClosePullRequestAsync(pr, "Superseded.", Ct));
        Ok(await CodeHost.ClosePullRequestAsync(pr, null, Ct));

        Assert.Equal(PullRequestStates.Closed, Ok(await CodeHost.GetPullRequestAsync(pr, Ct)).State);
    }

    [Fact]
    public async Task A_revert_of_a_merged_pull_request_is_opened_once_into_the_same_base()
    {
        var pr = await OpenAsync("ff/revert", "key-revert");
        await MergeAsPersonAsync(pr);

        var revert = Ok(await CodeHost.CreateRevertPullRequestAsync(pr, "key-revert-of", Ct));
        var again = Ok(await CodeHost.CreateRevertPullRequestAsync(pr, "key-revert-of", Ct));
        var state = Ok(await CodeHost.GetPullRequestAsync(revert, Ct));

        Assert.Equal(revert, again);
        Assert.NotEqual(pr, revert);
        Assert.Equal((PullRequestStates.Open, "main"), (state.State, state.Base));
        Assert.Null(Ok(await CodeHost.ReadFileAsync(Repo, "work.txt", state.HeadRevision, Ct)));
    }

    [Fact]
    public async Task A_revert_of_an_unmerged_pull_request_is_refused()
    {
        var pr = await OpenAsync("ff/unmerged", "key-unmerged");
        Refused(await CodeHost.CreateRevertPullRequestAsync(pr, "key-unmerged-revert", Ct), "validation_failed");
    }

    [Fact]
    public async Task An_unknown_pull_request_is_not_found()
    {
        Refused(await CodeHost.GetPullRequestAsync(new PullRequestRef(Repo, 999_999), Ct), "not_found");
    }

    [Fact]
    public async Task A_commit_with_no_checks_reports_none()
    {
        var revision = await Change("ff/checks", "a.txt", "1");
        var status = Ok(await CodeHost.GetCheckStatusAsync(Repo, revision, Ct));

        Assert.Equal(revision, status.Revision);
        Assert.Empty(status.Checks);
    }
}
