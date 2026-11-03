namespace FeatureFlow.Providers.Abstractions;

/// <summary>A tracker issue, by the tracker's key for it (<c>run.tracker_issue</c>): <c>ACME-12</c>, or <c>owner/repo#12</c> for GitHub Issues.</summary>
public sealed record IssueRef(string Key);

/// <summary>A comment the engine added, by the remote's id for it: on a pull request or a tracker issue.</summary>
public sealed record CommentRef(string Id);

/// <summary>
/// What to create or change on an issue. On an update a null field is left as it is. <see cref="Project"/> is where a
/// created issue goes (a Jira project, a GitHub repository); null means the provider's configured default.
/// <see cref="Fields"/> are the library's field names, mapped by provider configuration.
/// </summary>
public sealed record IssueSpec(
    string? Title = null,
    string? Body = null,
    IReadOnlyList<string>? Labels = null,
    IssueRef? Parent = null,
    IReadOnlyDictionary<string, string>? Fields = null,
    string? Project = null);

/// <summary>
/// An issue as the tracker reports it now. <see cref="Status"/> is the library status name its tracker status maps to
/// (<c>library/std/status_map.yaml</c>), else the tracker's own name. <see cref="Body"/> is as given, without the hidden marker.
/// </summary>
public sealed record Issue(
    IssueRef Ref,
    string Title,
    string Body,
    string Status,
    IReadOnlyList<string> Labels,
    IssueRef? Parent,
    IReadOnlyDictionary<string, string> Fields,
    string? Url = null);

/// <summary>A search: every given criterion must hold. <see cref="Marker"/> is a create's idempotency key.</summary>
public sealed record IssueQuery(string? Label = null, IssueRef? Parent = null, string? Marker = null, string? Project = null);

/// <summary>
/// The tracker provider (docs/06 section 3.6), behind <c>tracker.update</c>, declarative <c>tracker</c> updates on steps and
/// planners' toolsets. Statuses are the library's names (<c>in_progress</c>, <c>code_review</c>, <c>done</c>), mapped to the
/// tracker's own in provider configuration. Creates take an idempotency key and write it as a hidden marker; a create or a
/// comment retried with the same key adopts what the first made. Agents have no tracker identity (F5).
/// </summary>
public interface ITrackerProvider : IProvider
{
    /// <summary>An issue by key; <c>not_found</c> when there is none.</summary>
    Task<ProviderResult<Issue>> GetIssueAsync(IssueRef issue, CancellationToken cancellationToken);

    Task<ProviderResult<IReadOnlyList<Issue>>> SearchAsync(IssueQuery query, CancellationToken cancellationToken);

    /// <summary>Creates an issue; one created earlier with the same <paramref name="key"/> is returned instead. A title is required.</summary>
    Task<ProviderResult<IssueRef>> CreateAsync(IssueSpec spec, string key, CancellationToken cancellationToken);

    /// <summary>Sets the fields <paramref name="spec"/> gives; the rest are left as they are.</summary>
    Task<ProviderResult<IssueRef>> UpdateAsync(IssueRef issue, IssueSpec spec, CancellationToken cancellationToken);

    /// <summary>
    /// Moves the issue to a library status. Moving it to the status it has succeeds. A status the configuration does not
    /// map, or the tracker's workflow does not allow from here, is <c>validation_failed</c>.
    /// </summary>
    Task<ProviderResult<Unit>> TransitionAsync(IssueRef issue, string status, CancellationToken cancellationToken);

    /// <summary>Links two issues; linking them again with the same type succeeds and adds nothing.</summary>
    Task<ProviderResult<Unit>> LinkAsync(IssueRef from, IssueRef to, string linkType, CancellationToken cancellationToken);

    /// <summary>Adds a comment; one added earlier with the same <paramref name="key"/> is returned instead.</summary>
    Task<ProviderResult<CommentRef>> CommentAsync(IssueRef issue, string body, string key, CancellationToken cancellationToken);
}
