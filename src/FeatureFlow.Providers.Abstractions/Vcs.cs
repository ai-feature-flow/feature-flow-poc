namespace FeatureFlow.Providers.Abstractions;

/// <summary>A repository on the code host, <c>owner/name</c>.</summary>
public sealed record RepoRef(string FullName);

/// <summary>A working copy in a sandbox, reached through the sandbox provider (docs/06 section 3.3).</summary>
public sealed record WorkingCopyRef(SandboxHandle Sandbox, string Path);

/// <summary>Commits taken out of a working copy as a bundle in the artifact store, ending at <see cref="Head"/> (LOG 34).</summary>
public sealed record CommitBundle(ArtifactRef Bundle, string Head, int Commits);

/// <summary>A reference moved, from <see cref="OldRevision"/> (null when it was created) to <see cref="NewRevision"/>.</summary>
public sealed record PushResult(string Reference, string? OldRevision, string NewRevision);

/// <summary>
/// A catch-up's result (docs/04 section 7.1). <see cref="WorkRevision"/> is the work branch's revision now, which
/// contains <see cref="ParentRevision"/> unless there are <see cref="Conflicts"/>. <see cref="Changed"/> is true when
/// the work branch is no longer at the revision the caller expected: false is the "nothing to merge" case (GitHub's
/// 204 in Spike 3). With conflicts nothing was pushed, and the work branch is where it was.
/// </summary>
public sealed record CatchUpResult(bool Changed, string WorkRevision, string ParentRevision, IReadOnlyList<string> Conflicts)
{
    public bool Conflicted => Conflicts.Count > 0;
}

/// <summary>How a change lands on its target: a merge commit, or a single squash commit.</summary>
public static class MergeMethods
{
    public const string Merge = "merge";
    public const string Squash = "squash";
}

/// <summary>
/// A merge's result (docs/04 section 7.3): the target's revision that holds the change, the revision it replaced,
/// and the method. <see cref="AlreadyMerged"/> is true when the change had landed before this call (a retry after a
/// timeout, DR18); then <see cref="TargetBefore"/> is null, because this call replaced nothing.
/// </summary>
public sealed record MergeResult(string Revision, string? TargetBefore, string Method, bool AlreadyMerged);

/// <summary>
/// The VCS provider (docs/06 section 3.3): reading a reference on the remote, staging a checkout into a sandbox,
/// detecting and taking commits out of a working copy, pushing with an expected old revision, catching a work
/// branch up with its parent, and merging a tested revision into its target. Every write to the remote names the
/// revision it expects, so the remote refuses it with <c>base_moved</c> if anything moved. Agents never call this
/// provider. A branch reset (a <c>clean_up</c> route, docs/03 section 4.1) is <see cref="PushAsync"/> to an older
/// revision, expecting the current one.
/// </summary>
public interface IVcsProvider : IProvider
{
    /// <summary>The revision a reference names on the remote, or null when it does not exist.</summary>
    Task<ProviderResult<string?>> ResolveAsync(RepoRef repo, string reference, CancellationToken cancellationToken);

    /// <summary>
    /// Puts <paramref name="revision"/>, read from the remote's <paramref name="sourceReference"/>, into a sandbox as a
    /// checkout of <paramref name="branch"/> at the working copy's path, with no remote and no credential. The
    /// engine's identity reads; nothing of it enters the sandbox (F5).
    /// </summary>
    Task<ProviderResult<Unit>> CheckOutAsync(RepoRef repo, string sourceReference, string revision, string branch, WorkingCopyRef target,
        CancellationToken cancellationToken);

    Task<ProviderResult<string>> GetRevisionAsync(WorkingCopyRef copy, CancellationToken cancellationToken);

    /// <summary>Whether the working copy has changes not committed.</summary>
    Task<ProviderResult<bool>> IsDirtyAsync(WorkingCopyRef copy, CancellationToken cancellationToken);

    /// <summary>Commits every change; returns the new revision, or null when there was nothing to commit.</summary>
    Task<ProviderResult<string?>> CommitAllAsync(WorkingCopyRef copy, string message, CancellationToken cancellationToken);

    /// <summary>The commits after <paramref name="sinceRevision"/> up to the working copy's head, as a bundle.</summary>
    Task<ProviderResult<CommitBundle>> FetchCommitsAsync(WorkingCopyRef copy, string sinceRevision, CancellationToken cancellationToken);

    /// <summary>Whether <paramref name="revision"/> descends from <paramref name="ancestor"/> (or is it), on the remote's history.</summary>
    Task<ProviderResult<bool>> DescendsFromAsync(RepoRef repo, string revision, string ancestor, CancellationToken cancellationToken);

    /// <summary>The newest commit both revisions descend from, on the remote's history; null when they share none (LOG 193).</summary>
    Task<ProviderResult<string?>> MergeBaseAsync(RepoRef repo, string a, string b, CancellationToken cancellationToken);

    /// <summary>How many commits are in <paramref name="toRevision"/> and not in <paramref name="fromRevision"/>.</summary>
    Task<ProviderResult<int>> CountCommitsAsync(WorkingCopyRef copy, string fromRevision, string toRevision, CancellationToken cancellationToken);

    /// <summary>The paths changed between two revisions of a working copy.</summary>
    Task<ProviderResult<IReadOnlyList<string>>> ChangedFilesAsync(WorkingCopyRef copy, string fromRevision, string toRevision, CancellationToken cancellationToken);

    /// <summary>
    /// Moves <paramref name="reference"/> on the remote to <paramref name="revision"/>, taking the commits from
    /// <paramref name="commits"/> when the remote lacks them. Refused with <c>base_moved</c> unless the reference is at
    /// <paramref name="expectedOldRevision"/> (null: it must not exist).
    /// </summary>
    Task<ProviderResult<PushResult>> PushAsync(RepoRef repo, string reference, string revision, CommitBundle? commits, string? expectedOldRevision,
        CancellationToken cancellationToken);

    /// <summary>
    /// Merges <paramref name="parentReference"/> into <paramref name="workReference"/> on the remote, with
    /// <paramref name="message"/>, and pushes the work branch expecting <paramref name="expectedWorkRevision"/>.
    /// When the work branch already contains the parent, nothing is pushed and the result reports where the branch
    /// is: so a retry after a catch-up that landed succeeds, with <see cref="CatchUpResult.Changed"/> true. A work
    /// branch moved by someone else that does not contain the parent is refused with <c>base_moved</c>. Conflicts
    /// are a result, not an error: the conflicting paths are listed and nothing is pushed.
    /// </summary>
    Task<ProviderResult<CatchUpResult>> CatchUpAsync(RepoRef repo, string parentReference, string workReference, string expectedWorkRevision,
        string message, CancellationToken cancellationToken);

    /// <summary>
    /// Lands <paramref name="testedRevision"/> on <paramref name="targetReference"/>: a commit whose tree is exactly
    /// the tested revision's, with parents the target and the tested revision (<see cref="MergeMethods.Merge"/>) or
    /// the target alone (<see cref="MergeMethods.Squash"/>), and the message followed by the trailer
    /// <c>Feature-Flow-Merge: &lt;key&gt;</c>. It checks first whether the change already landed (the target contains
    /// the tested revision, or a commit on it carries the key's trailer), and then reports
    /// <see cref="MergeResult.AlreadyMerged"/> (DR18). Otherwise it is refused with <c>base_moved</c> unless the target
    /// is at <paramref name="expectedTargetRevision"/> and the tested revision contains it, so nothing untested lands.
    /// </summary>
    Task<ProviderResult<MergeResult>> MergeAsync(RepoRef repo, string testedRevision, string targetReference, string expectedTargetRevision,
        string method, string message, string key, CancellationToken cancellationToken);
}
