using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.CodeHost;

/// <summary>
/// The code host provider. docs/04 section 4 for the signatures, docs/05
/// section 4 for the per-method contracts.
///
/// Three rules shape every method here:
///
/// 1. **Every method returns <see cref="ProviderResult{T}"/>.** Exceptions are
///    only for programming errors. The provider classifies, because it knows
///    its system (docs/01 section 4.3).
/// 2. **No method waits.** No provider method waits on human or external
///    completion. Waiting is an engine mechanism: durable timers, polling, or
///    inbound events (docs/04 section 1.3).
/// 3. **Transport-neutral.** Plain serialisable records only. No Octokit or
///    other SDK types in the interface (docs/04 section 4.5).
/// </summary>
public interface ICodeHostProvider : IProvider
{
    // Branches

    /// <summary>
    /// Creates a branch from <paramref name="fromRef"/>. Idempotent by key: a
    /// second call with the same key adopts the existing branch rather than
    /// creating a duplicate (docs/05 section 4.1).
    /// </summary>
    Task<ProviderResult<BranchRef>> CreateBranchAsync(
        RepoRef repo, string name, string fromRef, string idempotencyKey, CancellationToken ct);

    /// <summary>
    /// Deletes a branch. Idempotent: deleting an already-deleted branch
    /// succeeds. Used by the verification harness to leave the fixture
    /// repository as it found it.
    /// </summary>
    Task<ProviderResult<Unit>> DeleteBranchAsync(RepoRef repo, string name, CancellationToken ct);

    // Pull requests

    /// <summary>
    /// Opens a pull request and writes the hidden marker into its body. On
    /// timeout, find by marker then adopt (docs/05 section 4.2).
    /// </summary>
    Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(
        RepoRef repo, PullRequestSpec spec, string idempotencyKey, CancellationToken ct);

    /// <summary>Last write wins. Safe to repeat.</summary>
    Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(
        PullRequestRef pr, PullRequestSpec spec, CancellationToken ct);

    /// <summary>
    /// Returns state, head SHA, approvals and mergeability. Non-blocking: this
    /// is what the engine polls while a human decides on the code host.
    /// </summary>
    Task<ProviderResult<PullRequestState>> GetPullRequestAsync(
        PullRequestRef pr, CancellationToken ct);

    /// <summary>Idempotent. Used when an abort explicitly asks for it.</summary>
    Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pr, CancellationToken ct);

    /// <summary>
    /// Opens a pull request that reverts <paramref name="pr"/>. Serves the
    /// revert option of a partial delivery (docs/06 section 6.6).
    /// </summary>
    Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(
        PullRequestRef pr, string idempotencyKey, CancellationToken ct);

    // Merging

    /// <summary>
    /// Merges the integration base forward into a ticket branch. Reports
    /// whether the branch changed, because checks rerun only if it did
    /// (review 06 C3).
    /// </summary>
    Task<ProviderResult<MergeResult>> MergeBaseForwardAsync(
        RepoRef repo, string branch, string into, CancellationToken ct);

    /// <summary>
    /// Merges a pull request. <c>rejected</c> means branch protection or policy
    /// refused it, which is a work outcome: a human must approve on the code
    /// host and the engine waits (Q5).
    /// </summary>
    Task<ProviderResult<MergeResult>> MergeAsync(
        PullRequestRef pr, string method, CancellationToken ct);

    /// <summary>
    /// Enqueues into the merge queue and returns. It does not wait — the
    /// engine polls <see cref="GetPullRequestAsync"/> or receives an inbound
    /// event (docs/05 section 4.3).
    /// </summary>
    Task<ProviderResult<MergeResult>> EnqueueMergeAsync(PullRequestRef pr, CancellationToken ct);

    /// <summary>Read-only. Used to decide whether a human must approve.</summary>
    Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(
        RepoRef repo, string branch, CancellationToken ct);

    // Checks

    /// <summary>
    /// Takes <see cref="RepoRef"/> as well as the SHA: a SHA alone is not
    /// unique across repositories (review 05 P2). Must include checks that
    /// register late.
    /// </summary>
    Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(
        RepoRef repo, string commitSha, CancellationToken ct);

    // Review comments

    /// <summary>Returns threads, not flat comments, so a thread can be resolved.</summary>
    Task<ProviderResult<IReadOnlyList<ReviewThread>>> ListReviewCommentsAsync(
        PullRequestRef pr, CancellationToken ct);

    /// <summary>Writes the marker. Used to post decline reasons.</summary>
    Task<ProviderResult<CommentRef>> AddReviewCommentAsync(
        PullRequestRef pr, ReviewComment comment, CancellationToken ct);

    Task<ProviderResult<CommentRef>> ReplyToReviewCommentAsync(
        CommentRef comment, string body, CancellationToken ct);

    /// <summary>Idempotent. Closes a thread after a fix or a decline.</summary>
    Task<ProviderResult<Unit>> ResolveReviewThreadAsync(ReviewThread thread, CancellationToken ct);

    // Diffs

    /// <summary>
    /// Returns an <see cref="ArtifactRef"/>, not the diff. A large aggregate
    /// diff never enters workflow state (DP10).
    /// </summary>
    Task<ProviderResult<ArtifactRef>> GetDiffAsync(
        RepoRef repo, string fromRef, string toRef, CancellationToken ct);

    // File reads

    /// <summary>
    /// Reads a file from a branch and returns its content hash. This is the
    /// R16 check: the manifest is read per step from the branch, never copied
    /// into the plan (docs/07 section 5.1.2 and section 9).
    /// </summary>
    Task<ProviderResult<FileContent>> ReadFileAsync(
        RepoRef repo, string path, string reference, CancellationToken ct);

    // Idempotency

    /// <summary>
    /// Searches branches and pull requests by hidden marker. Returns empty,
    /// not <c>not_found</c>, when nothing matches (docs/05 section 4.6).
    /// </summary>
    Task<ProviderResult<FoundObjects>> FindByMarkerAsync(
        RepoRef repo, string marker, CancellationToken ct);
}
