namespace FeatureFlow.Spike.CodeHost;

/// <summary>
/// A repository on a code host. Transport-neutral: the host is data, not an
/// SDK connection, so a reference survives serialisation into workflow state.
/// </summary>
public sealed record RepoRef(string Owner, string Name, string Host = "github.com")
{
    public string FullName => $"{Owner}/{Name}";

    public override string ToString() => $"{Host}/{FullName}";
}

/// <summary>
/// A branch and the commit it pointed at when the reference was read.
/// </summary>
public sealed record BranchRef(RepoRef Repo, string Name, string HeadSha);

/// <summary>
/// A pull request. The number is the stable remote identity; the head SHA is
/// advisory and is refreshed by <see cref="ICodeHostProvider.GetPullRequestAsync"/>.
/// </summary>
public sealed record PullRequestRef(RepoRef Repo, int Number, string? HeadSha = null);

/// <summary>
/// What a pull request should look like. A full desired state, not a diff, so
/// <c>UpdatePullRequestAsync</c> is last-write-wins and safe to repeat
/// (docs/05 section 4.2).
/// </summary>
public sealed record PullRequestSpec(
    string Title,
    string Head,
    string Base,
    string Body = "",
    bool Draft = false,
    IReadOnlyList<string>? Labels = null);

/// <summary>
/// The observed state of a pull request. This is what "the human approves on
/// the code host and the engine waits" reads (docs/05 section 4.2, Q5).
/// </summary>
public sealed record PullRequestState(
    PullRequestRef Pr,
    string State,
    bool Merged,
    bool Draft,
    string HeadSha,
    string HeadBranch,
    string BaseBranch,
    bool Mergeable,
    string MergeableState,
    int Approvals,
    IReadOnlyList<string> ApprovedBy,
    string? Url = null);

/// <summary>
/// The outcome of a merge. <see cref="BranchChanged"/> and
/// <see cref="NewHeadSha"/> are load-bearing: review 03 decided checks rerun
/// only if the merge changed the branch (docs/05 section 4.3, review 06 C3).
/// </summary>
public sealed record MergeResult(
    bool Merged,
    bool BranchChanged,
    string? NewHeadSha,
    string? Sha = null,
    string? Message = null);

/// <summary>
/// What a protected branch requires. Used to decide whether a human must
/// approve on the code host (docs/05 section 4.3).
/// </summary>
public sealed record BranchProtection(
    string Branch,
    bool Enabled,
    bool RequiredReviews,
    int RequiredApprovingReviews,
    bool RequiresQueue,
    IReadOnlyList<string> RequiredChecks,
    bool EnforceAdmins);

/// <summary>
/// The combined check-runs and commit statuses for one commit in one
/// repository.
/// </summary>
/// <param name="State">
/// <c>success</c>, <c>failure</c>, <c>pending</c> (something is still
/// running), or <c>none</c> (nothing has registered yet — checks may still
/// register late, docs/05 section 4.4).
/// </param>
public sealed record CheckStatus(
    RepoRef Repo,
    string CommitSha,
    string State,
    int TotalCount,
    IReadOnlyList<CheckRun> CheckRuns,
    IReadOnlyList<CommitStatus> Statuses);

public sealed record CheckRun(
    long Id,
    string Name,
    string Status,
    string? Conclusion,
    string? DetailsUrl = null,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null);

public sealed record CommitStatus(
    long Id,
    string Context,
    string State,
    string? Description = null,
    string? TargetUrl = null);

/// <summary>
/// One review thread. Threads, not flat comments, so a thread can be resolved
/// (docs/05 section 4.5).
/// </summary>
public sealed record ReviewThread(
    string Id,
    string? Path,
    int? Line,
    bool Resolved,
    bool Outdated,
    IReadOnlyList<ReviewComment> Comments);

/// <summary>
/// A review comment to post. <see cref="Path"/> null means a pull-request level
/// comment rather than an inline one.
/// </summary>
public sealed record ReviewComment(
    string Body,
    string? Path = null,
    int? Line = null,
    string? CommitSha = null,
    string? Side = "RIGHT",
    string? Marker = null);

public sealed record CommentRef(string Id, string? Url = null);

/// <summary>
/// A reference to stored content. The content itself never enters workflow
/// state (DP10).
/// </summary>
public sealed record ArtifactRef(
    string Uri,
    string? ContentHash = null,
    long? SizeBytes = null);

/// <summary>
/// A file read from a branch, with the hash of its content.
///
/// <para>
/// This is the R16 mechanism: each step reads <c>feature-flow.yaml</c> from the
/// branch it is working on and records the hash, so a mid-run edit is visible
/// rather than silently stale (docs/07 section 9, docs/01 R16).
/// </para>
/// </summary>
public sealed record FileContent(
    string Path,
    string Ref,
    string Content,
    string ContentHash,
    long SizeBytes,
    string? BlobSha = null);

/// <summary>
/// What <see cref="ICodeHostProvider.FindByMarkerAsync"/> found. Empty is a
/// normal answer, not an error (docs/05 section 4.6).
/// </summary>
public sealed record FoundObjects(
    IReadOnlyList<BranchRef> Branches,
    IReadOnlyList<PullRequestRef> PullRequests)
{
    public static FoundObjects Empty { get; } =
        new(Array.Empty<BranchRef>(), Array.Empty<PullRequestRef>());

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsEmpty => Branches.Count == 0 && PullRequests.Count == 0;
}

/// <summary>
/// The code host capability record. docs/04 section 4. Capabilities are typed
/// records, never strings, so a step can ask a precise question.
/// </summary>
public sealed record CodeHostCapabilities(
    bool PullRequest,
    bool ReviewComments,
    bool CheckStatus,
    IReadOnlyList<string> MergeMethods,   // "merge", "squash", "rebase"
    bool BranchProtection,
    bool MergeQueue,                      // optional; only some repositories enable it
    bool InboundEvents);                  // can receive webhooks (docs/04 section 1.3)
