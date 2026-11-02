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
/// The VCS provider (docs/06 section 3.3), as far as M4 needs it: reading a reference on the remote, staging a
/// checkout into a sandbox, detecting and taking commits out of a working copy, and pushing with an expected
/// old revision. Catch-up and merge arrive in M5. Agents never call this provider.
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
}
