using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// Copilot code review as a findings source (docs/06 section 3.7, kind <c>copilot_review</c>): <c>findings.record</c> asks
/// Copilot to review a pull request's head and reads its review comments as a <c>findings_report</c>. Copilot is asked by
/// requesting <c>copilot-pull-request-reviewer[bot]</c> as a reviewer; it then posts one review for the head it saw.
/// Stateless: the handle names the pull request and revision, and the pull request says where the request is.
/// </summary>
/// <remarks>
/// Copilot gives no rule ids or severities. Every comment is rule <c>copilot-review</c>, severity <c>medium</c>, basis
/// <c>reasoned</c>, and blocking only when the instance sets <c>blocking: true</c>; the fingerprint's snippet is the lines
/// the comment is on, from its diff hunk, so a re-review of unchanged lines matches (LOG 165).
/// </remarks>
public sealed class CopilotReviewSource(GitHubApi api, Func<IArtifactStoreProvider> store, bool blocking = false) : IFindingsSourceProvider
{
    public const string Reviewer = "copilot-pull-request-reviewer[bot]";
    public const string Rule = "copilot-review";

    static readonly IReadOnlyDictionary<string, string> Write = new Dictionary<string, string> { ["pull_requests"] = "write" };
    static readonly IReadOnlyDictionary<string, string> Read = new Dictionary<string, string> { ["pull_requests"] = "read" };

    public ProviderDescription Describe() => new("findings_source", "copilot_review", "0.1.0", new FindingsSourceCapabilities(false, false, true));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => api.CheckAsync(cancellationToken);

    /// <summary>What a handle names: the pull request and revision, or that Copilot cannot be asked here (<see cref="Refused"/>).</summary>
    sealed record Target(RepoRef Repo, int PullRequest, string Revision, string? Refused)
    {
        public string Id => $"copilot:{Repo.FullName}#{PullRequest.ToString(CultureInfo.InvariantCulture)}@{Revision}" + (Refused is null ? "" : ":" + Refused);

        public static Target? Parse(string id)
        {
            if (!id.StartsWith("copilot:", StringComparison.Ordinal))
                return null;
            var rest = id["copilot:".Length..];
            var hash = rest.LastIndexOf('#');
            var at = rest.IndexOf('@', Math.Max(hash, 0));
            if (hash <= 0 || at < 0 || !int.TryParse(rest[(hash + 1)..at], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
                return null;
            var tail = rest[(at + 1)..];
            var colon = tail.IndexOf(':');
            var revision = colon < 0 ? tail : tail[..colon];
            return GitHubApi.IsRevision(revision) ? new Target(new RepoRef(rest[..hash]), number, revision, colon < 0 ? null : tail[(colon + 1)..]) : null;
        }
    }

    /// <summary>
    /// Asks Copilot for a review of the target's pull request, unless it already reviewed this revision or is already
    /// asked, so a repeated request asks once. With no pull request, or when GitHub refuses Copilot as a reviewer (not
    /// enabled for the repository), the handle says so and the request is <c>unavailable</c>.
    /// </summary>
    public async Task<ProviderResult<CollectHandle>> RequestAsync(FindingsTarget target, string key, CancellationToken cancellationToken)
    {
        if (!GitHubApi.IsRevision(target.Revision))
            return ProviderResult<CollectHandle>.Failure("validation_failed", $"{target.Revision} is not a commit SHA");
        if (target.PullRequest is not { } pr)
            return Handle(new Target(target.Repository, 0, target.Revision, "no-pull-request"));
        var handle = new Target(pr.Repo, pr.Number, target.Revision, null);

        var reviewed = await CopilotReviewAsync(handle, cancellationToken);
        if (!reviewed.Ok)
            return new ProviderResult<CollectHandle>(null, reviewed.Error);
        if (reviewed.Value is not null)
            return Handle(handle);
        var asked = await AskedAsync(handle, cancellationToken);
        if (!asked.Ok)
            return new ProviderResult<CollectHandle>(null, asked.Error);
        if (asked.Value)
            return Handle(handle);

        var requested = await api.SendAsync(pr.Repo, Write, HttpMethod.Post, $"/repos/{pr.Repo.FullName}/pulls/{pr.Number}/requested_reviewers",
            new { reviewers = new[] { Reviewer } }, cancellationToken);
        if (requested is { Ok: false, Error.Reason: "validation_failed" })
            // 422: Copilot is not a reviewer this repository can ask for.
            return Handle(handle with { Refused = "refused" });
        if (!requested.Ok)
            return new ProviderResult<CollectHandle>(null, requested.Error);
        if (requested.Value!.Code != 201)
            return GitHubApi.Unexpected<CollectHandle>(requested.Value, "POST requested_reviewers");
        // GitHub may accept the request and leave Copilot out, as it does a reviewer it cannot ask.
        return Handle(Names(requested.Value.Json?["requested_reviewers"]).Any(IsCopilot) ? handle : handle with { Refused = "ignored" });
    }

    static ProviderResult<CollectHandle> Handle(Target target) => ProviderResult<CollectHandle>.Success(new CollectHandle(target.Id));

    /// <summary><c>ready</c> once Copilot reviewed the revision; <c>pending</c> while it is asked; otherwise <c>unavailable</c>.</summary>
    public async Task<ProviderResult<CollectStatus>> GetStatusAsync(CollectHandle handle, CancellationToken cancellationToken)
    {
        if (Target.Parse(handle.Id) is not { } target)
            return ProviderResult<CollectStatus>.Failure("not_found", $"{handle.Id} is not a Copilot review request");
        if (target.Refused is { } refused)
            return ProviderResult<CollectStatus>.Success(new CollectStatus(CollectStates.Unavailable, refused switch
            {
                "no-pull-request" => "Copilot reviews pull requests, and none is open for this change",
                "refused" => $"GitHub refused Copilot as a reviewer of {target.Repo.FullName} (422): Copilot code review is not enabled there",
                _ => $"GitHub accepted the review request on {target.Repo.FullName} but did not add Copilot: Copilot code review is not enabled there",
            }));

        var reviewed = await CopilotReviewAsync(target, cancellationToken);
        if (!reviewed.Ok)
            return new ProviderResult<CollectStatus>(null, reviewed.Error);
        if (reviewed.Value is not null)
            return ProviderResult<CollectStatus>.Success(new CollectStatus(CollectStates.Ready));
        var asked = await AskedAsync(target, cancellationToken);
        if (!asked.Ok)
            return new ProviderResult<CollectStatus>(null, asked.Error);
        return ProviderResult<CollectStatus>.Success(asked.Value
            ? new CollectStatus(CollectStates.Pending)
            : new CollectStatus(CollectStates.Unavailable, $"Copilot is not asked to review {target.Repo.FullName}#{target.PullRequest} and has not reviewed {target.Revision[..10]}"));
    }

    /// <summary>Copilot's review comments on the revision, as a findings report in the artifact store.</summary>
    public async Task<ProviderResult<ArtifactRef>> ReadFindingsAsync(CollectHandle handle, CancellationToken cancellationToken)
    {
        if (Target.Parse(handle.Id) is not { } target)
            return ProviderResult<ArtifactRef>.Failure("not_found", $"{handle.Id} is not a Copilot review request");
        var reviewed = target.Refused is null ? await CopilotReviewAsync(target, cancellationToken) : ProviderResult<long?>.Success(null);
        if (!reviewed.Ok)
            return new ProviderResult<ArtifactRef>(null, reviewed.Error);
        if (reviewed.Value is not { } review)
            return ProviderResult<ArtifactRef>.Failure("validation_failed", $"{handle.Id} is not ready: Copilot has not reviewed {target.Revision[..10]}");
        var comments = await api.ListAsync(target.Repo, Read,
            $"/repos/{target.Repo.FullName}/pulls/{target.PullRequest}/reviews/{review.ToString(CultureInfo.InvariantCulture)}/comments", j => j as JsonArray, cancellationToken);
        if (!comments.Ok)
            return new ProviderResult<ArtifactRef>(null, comments.Error);
        var report = new FindingsReport(1, comments.Value!.Select(c => FindingOf(target.Repo, c)).ToList(), "copilot");
        return await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(ArtifactShapes.WriteLibrary(report))), cancellationToken);
    }

    /// <summary>One review comment as a finding: its first line as the title, the comment as the description, a suggestion as the correction.</summary>
    ReportedFinding FindingOf(RepoRef repo, JsonNode comment)
    {
        var body = comment["body"]?.GetValue<string>() ?? "";
        var title = body.Split('\n').Select(l => l.Trim().TrimStart('#', '*', '>', ' ').TrimEnd('*').Trim()).FirstOrDefault(l => l.Length > 0) ?? "Copilot review comment";
        if (title.Length > 120)
            title = title[..117] + "...";
        return new ReportedFinding(Rule, title, body.Length > 0 ? body : title, "medium", blocking, FindingBases.Reasoned,
            new FindingLocation(repo.FullName, comment["path"]?.GetValue<string>() ?? "", null, Snippet(comment)), Suggestion(body));
    }

    /// <summary>The lines a comment is on: the end of its diff hunk, one line or <c>start_line</c> to <c>line</c>, without the diff markers.</summary>
    static string? Snippet(JsonNode comment)
    {
        if (comment["diff_hunk"]?.GetValue<string>() is not { Length: > 0 } hunk)
            return null;
        var line = comment["line"]?.GetValue<int?>() ?? comment["original_line"]?.GetValue<int?>();
        var start = comment["start_line"]?.GetValue<int?>() ?? comment["original_start_line"]?.GetValue<int?>() ?? line;
        var count = line is { } l && start is { } s && l >= s ? l - s + 1 : 1;
        var lines = hunk.Split('\n').Where(x => !x.StartsWith("@@", StringComparison.Ordinal)).ToList();
        return string.Join('\n', lines.TakeLast(count).Select(x => x.Length > 0 && x[0] is '+' or '-' or ' ' ? x[1..] : x));
    }

    static string? Suggestion(string body)
    {
        const string fence = "```suggestion";
        var at = body.IndexOf(fence, StringComparison.Ordinal);
        if (at < 0)
            return null;
        var start = body.IndexOf('\n', at);
        var end = start < 0 ? -1 : body.IndexOf("```", start, StringComparison.Ordinal);
        return start < 0 || end < 0 ? null : body[(start + 1)..end].TrimEnd('\n');
    }

    /// <summary>The id of Copilot's latest review of the revision, or null.</summary>
    async Task<ProviderResult<long?>> CopilotReviewAsync(Target target, CancellationToken cancellationToken)
    {
        var reviews = await api.ListAsync(target.Repo, Read, $"/repos/{target.Repo.FullName}/pulls/{target.PullRequest}/reviews", j => j as JsonArray, cancellationToken);
        if (!reviews.Ok)
            return new ProviderResult<long?>(null, reviews.Error);
        var id = reviews.Value!
            .Where(r => IsCopilot(r["user"]?["login"]?.GetValue<string>()) && r["commit_id"]?.GetValue<string>() == target.Revision)
            .Select(r => r["id"]?.GetValue<long>())
            .Max();
        return ProviderResult<long?>.Success(id);
    }

    /// <summary>Whether Copilot is among the pull request's requested reviewers.</summary>
    async Task<ProviderResult<bool>> AskedAsync(Target target, CancellationToken cancellationToken)
    {
        var response = await api.SendAsync(target.Repo, Read, HttpMethod.Get, $"/repos/{target.Repo.FullName}/pulls/{target.PullRequest}/requested_reviewers", null,
            cancellationToken);
        if (!response.Ok)
            return new ProviderResult<bool>(false, response.Error);
        return response.Value!.Code == 200
            ? ProviderResult<bool>.Success(Names(response.Value.Json?["users"]).Any(IsCopilot))
            : GitHubApi.Unexpected<bool>(response.Value, "GET requested_reviewers");
    }

    static IEnumerable<string?> Names(JsonNode? users) => (users as JsonArray ?? []).Select(u => u?["login"]?.GetValue<string>());

    /// <summary>Copilot's reviewer login: the bot's, or <c>Copilot</c> as some responses show it.</summary>
    static bool IsCopilot(string? login) =>
        login is not null && (login.StartsWith("copilot-pull-request-reviewer", StringComparison.OrdinalIgnoreCase) || login.Equals("Copilot", StringComparison.OrdinalIgnoreCase));
}
