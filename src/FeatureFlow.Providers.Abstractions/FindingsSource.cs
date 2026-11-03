namespace FeatureFlow.Providers.Abstractions;

/// <summary>What an external reviewer is asked to review: a repository at an exact revision, and its pull request if there is one.</summary>
public sealed record FindingsTarget(RepoRef Repository, string Revision, PullRequestRef? PullRequest = null);

/// <summary>A review request a findings source accepted, by the source's own id for it.</summary>
public sealed record CollectHandle(string Id);

/// <summary>Where a review request is: <c>pending</c>, <c>ready</c> to read, or <c>unavailable</c>.</summary>
public static class CollectStates
{
    public const string Pending = "pending";
    public const string Ready = "ready";

    /// <summary>The source cannot review this target (not enabled, the request ignored): <c>findings.record</c>'s <c>unavailable</c> outcome.</summary>
    public const string Unavailable = "unavailable";
}

/// <summary>A review request's state, with the source's reason when it is <c>unavailable</c>.</summary>
public sealed record CollectStatus(string State, string? Reason = null);

/// <summary>
/// A findings source (docs/06 section 3.7): an external reviewer behind <c>findings.record</c>'s <c>source</c> (RW5), such as
/// Copilot review on a pull request, SonarCloud or a SARIF file from CI. Requests are started and polled, never waited on
/// inside a call. What it returns is a <see cref="FindingsReport"/> artifact, the same type an agent reviewer produces,
/// so the ledger treats both alike.
/// </summary>
public interface IFindingsSourceProvider : IProvider
{
    /// <summary>Asks for a review of <paramref name="target"/>. A request repeated with the same <paramref name="key"/> returns the same handle.</summary>
    Task<ProviderResult<CollectHandle>> RequestAsync(FindingsTarget target, string key, CancellationToken cancellationToken);

    /// <summary>The request's state. An unknown handle is <c>not_found</c>.</summary>
    Task<ProviderResult<CollectStatus>> GetStatusAsync(CollectHandle handle, CancellationToken cancellationToken);

    /// <summary>
    /// The findings, stored in the artifact store as a <see cref="FindingsReport"/> (library shape) whose findings are all
    /// in the target's repository. Refused with <c>validation_failed</c> unless the request is <c>ready</c>.
    /// </summary>
    Task<ProviderResult<ArtifactRef>> ReadFindingsAsync(CollectHandle handle, CancellationToken cancellationToken);
}
