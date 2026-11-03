namespace FeatureFlow.Providers.Abstractions;

/// <summary>A pull request: its repository and number, the code host's stable identity for it.</summary>
public sealed record PullRequestRef(RepoRef Repo, int Number);

/// <summary>
/// What a pull request should look like: a whole desired state, so an update is last-write-wins and safe to repeat.
/// <see cref="Head"/> and <see cref="Base"/> are branch names, not references.
/// </summary>
public sealed record PullRequestSpec(string Title, string Body, string Head, string Base, bool Draft = false);

/// <summary>A pull request's state: <c>open</c>, <c>closed</c> (without merging) or <c>merged</c>.</summary>
public static class PullRequestStates
{
    public const string Open = "open";
    public const string Closed = "closed";
    public const string Merged = "merged";
}

/// <summary>
/// A pull request as the code host reports it now (docs/06 section 3.4). <see cref="MergedBy"/> is the identity that
/// merged it, so <c>scm.merge</c> can see a person's merge; <see cref="Mergeable"/> is null while the host has not
/// decided. Approvals and change requests are each reviewer's latest review.
/// </summary>
public sealed record PullRequestState(
    PullRequestRef PullRequest,
    string State,
    string Title,
    string Body,
    string Head,
    string HeadRevision,
    string Base,
    bool Draft,
    bool? Mergeable,
    IReadOnlyList<string> ApprovedBy,
    IReadOnlyList<string> ChangesRequestedBy,
    string? MergedBy,
    string? MergeRevision,
    string? Url);

/// <summary>
/// Whether a branch is protected, and how (check 24). The engine only reads it; its identity has no permission to
/// change it (F5). <see cref="ReviewRulesKnown"/> is false when the branch has protection whose review rules the engine
/// cannot read (on GitHub, classic branch protection, readable only with administration): then
/// <see cref="RequiresPullRequest"/> and <see cref="RequiredApprovals"/> describe only what could be read, and check 24
/// reports the rules as unknown rather than passing.
/// </summary>
public sealed record BranchProtection(string Branch, bool Protected, bool RequiresPullRequest, int RequiredApprovals,
    IReadOnlyList<string> RequiredChecks, bool RequiresMergeQueue, bool ReviewRulesKnown = true);

/// <summary>A check's progress: <c>queued</c>, <c>in_progress</c> or <c>completed</c>.</summary>
public static class CheckStates
{
    public const string Queued = "queued";
    public const string InProgress = "in_progress";
    public const string Completed = "completed";
}

/// <summary>How a completed check or CI run ended.</summary>
public static class Conclusions
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Cancelled = "cancelled";
    public const string Skipped = "skipped";
    public const string TimedOut = "timed_out";
    public const string Neutral = "neutral";
    public const string ActionRequired = "action_required";
}

/// <summary>
/// One check on a commit. <see cref="Conclusion"/> is set once it is completed. <see cref="Suite"/> names what reported
/// it, in the names <see cref="ICiProvider.GetExpectedRunsAsync"/> uses (a GitHub Actions workflow), or null for a
/// third-party check, so <c>ci.checks</c> can tell which expected runs have reported (docs/04 section 8.1).
/// </summary>
public sealed record CheckRun(string Name, string State, string? Conclusion, string? Suite, string? Url = null);

/// <summary>Every check reported on a commit so far, including late ones.</summary>
public sealed record CheckStatus(string Revision, IReadOnlyList<CheckRun> Checks);

/// <summary>A file read from the code host at a reference: its content, and the revision and content hash it was read at (RW11).</summary>
public sealed record RepoFile(string Path, string Revision, string Content, string Hash);

/// <summary>
/// The code host provider (docs/06 section 3.4), as far as M5 needs it: the default branch and its protection, a
/// file at a reference (the repository manifest), pull requests and their reverts, and the checks on a commit.
/// The default branch is always read from here, never from configuration or the plan (F1, F10). Creates take an
/// idempotency key and write it as a hidden marker; a create retried with the same key adopts the object it made.
/// </summary>
public interface ICodeHostProvider : IProvider
{
    Task<ProviderResult<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken cancellationToken);

    /// <summary>A branch's protection; an unprotected branch is <see cref="BranchProtection.Protected"/> false, not an error.</summary>
    Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken cancellationToken);

    /// <summary>The file at <paramref name="path"/> as of <paramref name="reference"/> (a branch name or a revision), or null when it does not exist there.</summary>
    Task<ProviderResult<RepoFile?>> ReadFileAsync(RepoRef repo, string path, string reference, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a pull request with <paramref name="key"/> as a hidden marker in its body. An open pull request with the
    /// same marker is adopted and updated to <paramref name="spec"/> instead, so a retry after a timeout opens no second
    /// one. Refused with <c>validation_failed</c> when the head has nothing the base lacks.
    /// </summary>
    Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key, CancellationToken cancellationToken);

    /// <summary>Sets title, body, base and draft. The marker in the body is kept.</summary>
    Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestSpec spec, CancellationToken cancellationToken);

    Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pullRequest, CancellationToken cancellationToken);

    /// <summary>Closes without merging, after adding <paramref name="comment"/> if given. Closing a closed or merged pull request succeeds.</summary>
    Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pullRequest, string? comment, CancellationToken cancellationToken);

    /// <summary>
    /// Opens a pull request that reverts a merged one, into the same base, idempotent by <paramref name="key"/>.
    /// Refused with <c>validation_failed</c> when the pull request is not merged.
    /// </summary>
    Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pullRequest, string key, CancellationToken cancellationToken);

    Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(RepoRef repo, string revision, CancellationToken cancellationToken);
}
