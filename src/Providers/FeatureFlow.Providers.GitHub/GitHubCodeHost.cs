using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// The GitHub code host provider (docs/06 section 3.4), over GitHub's REST API, with GraphQL where REST has no
/// equivalent (draft state, reverts). Every method maps the success statuses GitHub documents for its endpoints;
/// any other success is <c>unexpected_response</c>, a work error, so it is never retried forever. Creates write the
/// key as a hidden marker in the body, <c>&lt;!-- feature-flow:KEY --&gt;</c>, and adopt what a retry finds.
/// </summary>
public sealed class GitHubCodeHost(GitHubApi api) : ICodeHostProvider
{
    static readonly IReadOnlyDictionary<string, string> Permissions = GitHubApi.CodeHostPermissions;

    public ProviderDescription Describe() => new("code_host", "github", "0.1.0", new CodeHostCapabilities(true, false, true, true, false, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => api.CheckAsync(cancellationToken);

    public static string Marker(string key) => $"<!-- feature-flow:{key} -->";

    static string WithMarker(string body, string key) => $"{body}\n\n{Marker(key)}";

    /// <summary>The marker a body carries, or null.</summary>
    static string? KeyOf(string? body)
    {
        const string start = "<!-- feature-flow:";
        var at = body?.LastIndexOf(start, StringComparison.Ordinal) ?? -1;
        if (at < 0)
            return null;
        var end = body!.IndexOf(" -->", at, StringComparison.Ordinal);
        return end < 0 ? null : body[(at + start.Length)..end];
    }

    Task<ProviderResult<GitHubResponse>> Send(RepoRef repo, HttpMethod method, string path, object? body, CancellationToken cancellationToken,
        string accept = "application/vnd.github+json") =>
        api.SendAsync(repo, Permissions, method, $"/repos/{repo.FullName}{path}", body, cancellationToken, accept);

    public async Task<ProviderResult<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken cancellationToken)
    {
        var response = await Send(repo, HttpMethod.Get, "", null, cancellationToken);
        if (!response.Ok)
            return new ProviderResult<string>(null, response.Error);
        return response.Value!.Code == 200 && response.Value.Json?["default_branch"]?.GetValue<string>() is { } branch
            ? ProviderResult<string>.Success(branch)
            : GitHubApi.Unexpected<string>(response.Value, "GET the repository");
    }

    /// <summary>
    /// Read from the branch (classic protection's status checks) and the rules that apply to it (rulesets), both
    /// readable without <c>administration</c>, which the engine never holds (F5). Classic protection's review
    /// requirements are visible only to an administrator, so a branch protected only classically reports
    /// <see cref="BranchProtection.Protected"/> with no review requirement.
    /// </summary>
    public async Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken cancellationToken)
    {
        var info = await Send(repo, HttpMethod.Get, $"/branches/{GitHubApi.EscapePath(branch)}", null, cancellationToken);
        if (!info.Ok)
            return new ProviderResult<BranchProtection>(null, info.Error);
        if (info.Value!.Code != 200)
            return GitHubApi.Unexpected<BranchProtection>(info.Value, "GET the branch");
        var rules = await Send(repo, HttpMethod.Get, $"/rules/branches/{GitHubApi.EscapePath(branch)}?per_page=100", null, cancellationToken);
        if (!rules.Ok)
            return new ProviderResult<BranchProtection>(null, rules.Error);
        if (rules.Value!.Code != 200)
            return GitHubApi.Unexpected<BranchProtection>(rules.Value, "GET the branch's rules");

        var json = info.Value.Json!;
        var list = rules.Value.Json as JsonArray ?? [];
        var checks = new SortedSet<string>(StringComparer.Ordinal);
        if (json["protection"]?["required_status_checks"] is JsonObject classic)
        {
            foreach (var c in classic["contexts"] as JsonArray ?? [])
                checks.Add(c!.GetValue<string>());
            foreach (var c in classic["checks"] as JsonArray ?? [])
                checks.Add(c!["context"]!.GetValue<string>());
        }

        var approvals = 0;
        foreach (var rule in list)
        {
            var type = rule?["type"]?.GetValue<string>();
            if (type == "pull_request")
                approvals = Math.Max(approvals, rule!["parameters"]?["required_approving_review_count"]?.GetValue<int>() ?? 0);
            if (type == "required_status_checks")
                foreach (var c in rule!["parameters"]?["required_status_checks"] as JsonArray ?? [])
                    checks.Add(c!["context"]!.GetValue<string>());
        }

        bool Has(string type) => list.Any(r => r?["type"]?.GetValue<string>() == type);
        var protectedBranch = json["protected"]?.GetValue<bool>() == true || list.Count > 0;
        return ProviderResult<BranchProtection>.Success(new BranchProtection(branch, protectedBranch, Has("pull_request"), approvals, checks.ToList(), Has("merge_queue")));
    }

    /// <summary>
    /// The reference is resolved to a commit first, so the file and the revision it reports are read at the same
    /// commit even while the branch moves.
    /// </summary>
    public async Task<ProviderResult<RepoFile?>> ReadFileAsync(RepoRef repo, string path, string reference, CancellationToken cancellationToken)
    {
        var revision = await ResolveAsync(repo, reference, cancellationToken);
        if (!revision.Ok)
            return new ProviderResult<RepoFile?>(null, revision.Error);
        var content = await ReadAtAsync(api, repo, Permissions, path, revision.Value!, cancellationToken);
        if (!content.Ok)
            return new ProviderResult<RepoFile?>(null, content.Error);
        return ProviderResult<RepoFile?>.Success(content.Value is { } bytes
            ? new RepoFile(path, revision.Value!, Encoding.UTF8.GetString(bytes), GitHubApi.Sha256(bytes))
            : null);
    }

    /// <summary>A branch name or revision as the commit it names: 200 with the SHA; 404 or 422 when there is no such commit.</summary>
    async Task<ProviderResult<string>> ResolveAsync(RepoRef repo, string reference, CancellationToken cancellationToken)
    {
        var response = await Send(repo, HttpMethod.Get, $"/commits/{GitHubApi.EscapePath(reference)}", null, cancellationToken, "application/vnd.github.sha");
        if (!response.Ok)
            return response.Error!.Reason == "validation_failed"
                ? ProviderResult<string>.Failure("not_found", $"no commit {reference} in {repo.FullName}")
                : new ProviderResult<string>(null, response.Error);
        return response.Value!.Code == 200 && response.Value.Text.Trim() is { Length: 40 } sha
            ? ProviderResult<string>.Success(sha)
            : GitHubApi.Unexpected<string>(response.Value, "GET the commit");
    }

    /// <summary>
    /// A file's bytes at a revision, or null when it does not exist there. The contents API answers 200 with the file
    /// in base64 up to 1 MB, and with an empty content for larger files, which are then read raw. A directory is a
    /// list, refused as <c>validation_failed</c>.
    /// </summary>
    internal static async Task<ProviderResult<byte[]?>> ReadAtAsync(GitHubApi api, RepoRef repo, IReadOnlyDictionary<string, string> permissions, string path,
        string revision, CancellationToken cancellationToken)
    {
        var at = $"/repos/{repo.FullName}/contents/{GitHubApi.EscapePath(path)}?ref={revision}";
        var response = await api.SendAsync(repo, permissions, HttpMethod.Get, at, null, cancellationToken);
        if (!response.Ok)
            return response.Error!.Reason == "not_found" ? ProviderResult<byte[]?>.Success(null) : new ProviderResult<byte[]?>(null, response.Error);
        if (response.Value!.Code != 200)
            return GitHubApi.Unexpected<byte[]?>(response.Value, "GET the file");
        var json = response.Value.Json!;
        if (json is JsonArray || json["type"]?.GetValue<string>() != "file")
            return ProviderResult<byte[]?>.Failure("validation_failed", $"{path} is not a file");
        var encoded = json["content"]?.GetValue<string>() ?? "";
        if (json["encoding"]?.GetValue<string>() == "base64" && (encoded.Length > 0 || json["size"]?.GetValue<long>() == 0))
            return ProviderResult<byte[]?>.Success(Convert.FromBase64String(encoded.Replace("\n", "", StringComparison.Ordinal)));
        var raw = await api.SendAsync(repo, permissions, HttpMethod.Get, at, null, cancellationToken, "application/vnd.github.raw");
        if (!raw.Ok)
            return new ProviderResult<byte[]?>(null, raw.Error);
        return raw.Value!.Code == 200 ? ProviderResult<byte[]?>.Success(Encoding.UTF8.GetBytes(raw.Value.Text)) : GitHubApi.Unexpected<byte[]?>(raw.Value, "GET the raw file");
    }

    /// <summary>
    /// Adopts an open pull request from the same head carrying the key's marker; otherwise creates one (201). GitHub
    /// refuses with 422 a head with nothing the base lacks (<c>validation_failed</c>), and a second open pull request
    /// for the same head and base, which is a <c>conflict</c>.
    /// </summary>
    public async Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key, CancellationToken cancellationToken)
    {
        var owner = repo.FullName.Split('/')[0];
        var open = await api.ListAsync(repo, Permissions, $"/repos/{repo.FullName}/pulls?state=open&head={GitHubApi.Escape(owner + ":" + spec.Head)}",
            j => j as JsonArray, cancellationToken);
        if (!open.Ok)
            return new ProviderResult<PullRequestRef>(null, open.Error);
        if (open.Value!.FirstOrDefault(p => KeyOf(p["body"]?.GetValue<string>()) == key) is { } existing)
            return await UpdatePullRequestAsync(new PullRequestRef(repo, existing["number"]!.GetValue<int>()), spec, cancellationToken);

        var created = await Send(repo, HttpMethod.Post, "/pulls",
            new Dictionary<string, object> { ["title"] = spec.Title, ["body"] = WithMarker(spec.Body, key), ["head"] = spec.Head, ["base"] = spec.Base, ["draft"] = spec.Draft },
            cancellationToken);
        if (!created.Ok)
            return created.Error!.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                ? ProviderResult<PullRequestRef>.Failure("conflict", created.Error.Message)
                : new ProviderResult<PullRequestRef>(null, created.Error);
        return created.Value!.Code == 201 && created.Value.Json?["number"]?.GetValue<int>() is { } number
            ? ProviderResult<PullRequestRef>.Success(new PullRequestRef(repo, number))
            : GitHubApi.Unexpected<PullRequestRef>(created.Value, "POST a pull request");
    }

    /// <summary>
    /// Title, body and base through REST (200); draft through GraphQL, the only way GitHub changes it, and only when it
    /// differs. The marker already in the body is kept.
    /// </summary>
    public async Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestSpec spec, CancellationToken cancellationToken)
    {
        var current = await GetRawAsync(pullRequest, cancellationToken);
        if (!current.Ok)
            return new ProviderResult<PullRequestRef>(null, current.Error);
        var key = KeyOf(current.Value!["body"]?.GetValue<string>());
        var body = key is null ? spec.Body : WithMarker(spec.Body, key);
        var patched = await Send(pullRequest.Repo, HttpMethod.Patch, $"/pulls/{pullRequest.Number}",
            new Dictionary<string, object> { ["title"] = spec.Title, ["body"] = body, ["base"] = spec.Base }, cancellationToken);
        if (!patched.Ok)
            return new ProviderResult<PullRequestRef>(null, patched.Error);
        if (patched.Value!.Code != 200)
            return GitHubApi.Unexpected<PullRequestRef>(patched.Value, "PATCH the pull request");

        if (current.Value["draft"]?.GetValue<bool>() != spec.Draft && current.Value["state"]?.GetValue<string>() == "open")
        {
            var mutation = spec.Draft
                ? "mutation($id: ID!) { convertPullRequestToDraft(input: { pullRequestId: $id }) { pullRequest { isDraft } } }"
                : "mutation($id: ID!) { markPullRequestReadyForReview(input: { pullRequestId: $id }) { pullRequest { isDraft } } }";
            var drafted = await api.GraphQlAsync(pullRequest.Repo, Permissions, mutation, new { id = current.Value["node_id"]!.GetValue<string>() }, cancellationToken);
            if (!drafted.Ok)
                return new ProviderResult<PullRequestRef>(null, drafted.Error);
        }

        return ProviderResult<PullRequestRef>.Success(pullRequest);
    }

    async Task<ProviderResult<JsonNode>> GetRawAsync(PullRequestRef pullRequest, CancellationToken cancellationToken)
    {
        var response = await Send(pullRequest.Repo, HttpMethod.Get, $"/pulls/{pullRequest.Number}", null, cancellationToken);
        if (!response.Ok)
            return new ProviderResult<JsonNode>(null, response.Error);
        return response.Value!.Code == 200 ? ProviderResult<JsonNode>.Success(response.Value.Json!) : GitHubApi.Unexpected<JsonNode>(response.Value, "GET the pull request");
    }

    /// <summary>
    /// The pull request (200) and its reviews (200, every page). Each reviewer's latest approval, change request or
    /// dismissal counts; a comment changes nothing, as on GitHub.
    /// </summary>
    public async Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pullRequest, CancellationToken cancellationToken)
    {
        var raw = await GetRawAsync(pullRequest, cancellationToken);
        if (!raw.Ok)
            return new ProviderResult<PullRequestState>(null, raw.Error);
        var reviews = await api.ListAsync(pullRequest.Repo, Permissions, $"/repos/{pullRequest.Repo.FullName}/pulls/{pullRequest.Number}/reviews",
            j => j as JsonArray, cancellationToken);
        if (!reviews.Ok)
            return new ProviderResult<PullRequestState>(null, reviews.Error);

        var latest = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var review in reviews.Value!)
            if (review["user"]?["login"]?.GetValue<string>() is { } login && review["state"]?.GetValue<string>() is "APPROVED" or "CHANGES_REQUESTED" or "DISMISSED")
                latest[login] = review["state"]!.GetValue<string>();

        var p = raw.Value!;
        var merged = p["merged"]?.GetValue<bool>() == true || p["merged_at"] is JsonValue;
        var state = merged ? PullRequestStates.Merged : p["state"]?.GetValue<string>() == "closed" ? PullRequestStates.Closed : PullRequestStates.Open;
        return ProviderResult<PullRequestState>.Success(new PullRequestState(
            pullRequest,
            state,
            p["title"]?.GetValue<string>() ?? "",
            p["body"]?.GetValue<string>() ?? "",
            p["head"]!["ref"]!.GetValue<string>(),
            p["head"]!["sha"]!.GetValue<string>(),
            p["base"]!["ref"]!.GetValue<string>(),
            p["draft"]?.GetValue<bool>() == true,
            p["mergeable"] is JsonValue m ? m.GetValue<bool>() : null,
            latest.Where(kv => kv.Value == "APPROVED").Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList(),
            latest.Where(kv => kv.Value == "CHANGES_REQUESTED").Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList(),
            merged ? p["merged_by"]?["login"]?.GetValue<string>() : null,
            merged ? p["merge_commit_sha"]?.GetValue<string>() : null,
            p["html_url"]?.GetValue<string>()));
    }

    /// <summary>The comment (201) and the close (200) only while it is open, so a retry adds no second comment.</summary>
    public async Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pullRequest, string? comment, CancellationToken cancellationToken)
    {
        var raw = await GetRawAsync(pullRequest, cancellationToken);
        if (!raw.Ok)
            return new ProviderResult<Unit>(default, raw.Error);
        if (raw.Value!["state"]?.GetValue<string>() != "open")
            return ProviderResult<Unit>.Success(default);
        if (comment is not null)
        {
            var commented = await Send(pullRequest.Repo, HttpMethod.Post, $"/issues/{pullRequest.Number}/comments", new { body = comment }, cancellationToken);
            if (!commented.Ok)
                return new ProviderResult<Unit>(default, commented.Error);
            if (commented.Value!.Code != 201)
                return GitHubApi.Unexpected<Unit>(commented.Value, "POST a comment");
        }

        var closed = await Send(pullRequest.Repo, HttpMethod.Patch, $"/pulls/{pullRequest.Number}", new { state = "closed" }, cancellationToken);
        if (!closed.Ok)
            return new ProviderResult<Unit>(default, closed.Error);
        return closed.Value!.Code == 200 ? ProviderResult<Unit>.Success(default) : GitHubApi.Unexpected<Unit>(closed.Value, "PATCH the pull request closed");
    }

    /// <summary>
    /// GitHub's own revert (GraphQL <c>revertPullRequest</c>, as the "Revert" button), which makes the branch and the
    /// pull request. A revert opened earlier with the key is adopted: pull requests into the base created since the
    /// merge are searched for the marker, newest first.
    /// </summary>
    public async Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pullRequest, string key, CancellationToken cancellationToken)
    {
        var raw = await GetRawAsync(pullRequest, cancellationToken);
        if (!raw.Ok)
            return new ProviderResult<PullRequestRef>(null, raw.Error);
        var p = raw.Value!;
        if (p["merged_at"] is not JsonValue mergedAt)
            return ProviderResult<PullRequestRef>.Failure("validation_failed", $"pull request {pullRequest.Number} is not merged");

        var since = DateTimeOffset.Parse(mergedAt.GetValue<string>(), CultureInfo.InvariantCulture);
        var baseBranch = p["base"]!["ref"]!.GetValue<string>();
        for (var page = 1; page <= 10; page++)
        {
            var list = await Send(pullRequest.Repo, HttpMethod.Get, $"/pulls?state=all&base={GitHubApi.Escape(baseBranch)}&sort=created&direction=desc&per_page=100&page={page}",
                null, cancellationToken);
            if (!list.Ok)
                return new ProviderResult<PullRequestRef>(null, list.Error);
            if (list.Value!.Code != 200)
                return GitHubApi.Unexpected<PullRequestRef>(list.Value, "GET the pull requests");
            var items = list.Value.Json as JsonArray ?? [];
            if (items.FirstOrDefault(i => KeyOf(i?["body"]?.GetValue<string>()) == key) is { } found)
                return ProviderResult<PullRequestRef>.Success(new PullRequestRef(pullRequest.Repo, found["number"]!.GetValue<int>()));
            if (items.Count < 100 || items.Any(i => DateTimeOffset.Parse(i!["created_at"]!.GetValue<string>(), CultureInfo.InvariantCulture) < since))
                break;
        }

        const string mutation = """
            mutation($id: ID!, $title: String!, $body: String!) {
              revertPullRequest(input: { pullRequestId: $id, title: $title, body: $body }) { revertPullRequest { number } }
            }
            """;
        var title = $"Revert \"{p["title"]?.GetValue<string>()}\"";
        var reverted = await api.GraphQlAsync(pullRequest.Repo, Permissions, mutation,
            new { id = p["node_id"]!.GetValue<string>(), title, body = WithMarker($"Reverts #{pullRequest.Number}", key) }, cancellationToken);
        if (!reverted.Ok)
            return new ProviderResult<PullRequestRef>(null, reverted.Error);
        return reverted.Value!["revertPullRequest"]?["revertPullRequest"]?["number"]?.GetValue<int>() is { } number
            ? ProviderResult<PullRequestRef>.Success(new PullRequestRef(pullRequest.Repo, number))
            : ProviderResult<PullRequestRef>.Failure("unexpected_response", "GitHub's revert answered without a pull request");
    }

    /// <summary>
    /// Check runs (200, every page) and commit statuses (200). A check run reported by GitHub Actions is named with
    /// its workflow file, from the run whose check suite it belongs to, so <c>ci.checks</c> can match it to an
    /// expected run; any other app's check has no suite. A commit GitHub does not know is <c>not_found</c>.
    /// </summary>
    public async Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(RepoRef repo, string revision, CancellationToken cancellationToken)
    {
        var runs = await api.ListAsync(repo, Permissions, $"/repos/{repo.FullName}/commits/{revision}/check-runs", j => j["check_runs"] as JsonArray, cancellationToken);
        if (!runs.Ok)
            return NotFoundIfInvalid<CheckStatus>(runs.Error!, revision, repo);
        var statuses = await Send(repo, HttpMethod.Get, $"/commits/{revision}/status?per_page=100", null, cancellationToken);
        if (!statuses.Ok)
            return NotFoundIfInvalid<CheckStatus>(statuses.Error!, revision, repo);
        if (statuses.Value!.Code != 200)
            return GitHubApi.Unexpected<CheckStatus>(statuses.Value, "GET the commit's statuses");

        var suites = new Dictionary<long, string>();
        if (runs.Value!.Any(r => r["app"]?["slug"]?.GetValue<string>() == "github-actions"))
        {
            var workflows = await api.ListAsync(repo, Permissions, $"/repos/{repo.FullName}/actions/runs?head_sha={revision}", j => j["workflow_runs"] as JsonArray,
                cancellationToken);
            if (!workflows.Ok)
                return new ProviderResult<CheckStatus>(null, workflows.Error);
            foreach (var w in workflows.Value!)
                if (w["check_suite_id"]?.GetValue<long>() is { } suite && w["path"]?.GetValue<string>() is { } path)
                    suites[suite] = WorkflowName(path);
        }

        var checks = runs.Value!.Select(r =>
        {
            var status = r["status"]?.GetValue<string>();
            var state = status == "completed" ? CheckStates.Completed : status == "in_progress" ? CheckStates.InProgress : CheckStates.Queued;
            var suite = r["check_suite"]?["id"]?.GetValue<long>() is { } id && suites.TryGetValue(id, out var name) ? name : null;
            return new CheckRun(r["name"]!.GetValue<string>(), state, state == CheckStates.Completed ? Conclusion(r["conclusion"]?.GetValue<string>()) : null, suite,
                r["html_url"]?.GetValue<string>());
        }).ToList();
        foreach (var s in statuses.Value.Json?["statuses"] as JsonArray ?? [])
        {
            var state = s!["state"]?.GetValue<string>();
            checks.Add(state == "pending"
                ? new CheckRun(s["context"]!.GetValue<string>(), CheckStates.InProgress, null, null, s["target_url"]?.GetValue<string>())
                : new CheckRun(s["context"]!.GetValue<string>(), CheckStates.Completed, state == "success" ? Conclusions.Success : Conclusions.Failure, null,
                    s["target_url"]?.GetValue<string>()));
        }

        return ProviderResult<CheckStatus>.Success(new CheckStatus(revision, checks));
    }

    static ProviderResult<T> NotFoundIfInvalid<T>(ProviderError error, string revision, RepoRef repo) =>
        error.Reason == "validation_failed" ? ProviderResult<T>.Failure("not_found", $"no commit {revision} in {repo.FullName}") : new ProviderResult<T>(default, error);

    /// <summary>A workflow's name in checks and expected runs: its file name, <c>unit.yml</c> for <c>.github/workflows/unit.yml</c>.</summary>
    public static string WorkflowName(string path) => path[(path.LastIndexOf('/') + 1)..];

    /// <summary>GitHub's conclusions, with <c>startup_failure</c> a failure and <c>stale</c> cancelled.</summary>
    public static string Conclusion(string? conclusion) => conclusion switch
    {
        "success" => Conclusions.Success,
        "failure" or "startup_failure" => Conclusions.Failure,
        "cancelled" or "stale" => Conclusions.Cancelled,
        "skipped" => Conclusions.Skipped,
        "timed_out" => Conclusions.TimedOut,
        "neutral" => Conclusions.Neutral,
        "action_required" => Conclusions.ActionRequired,
        _ => Conclusions.Failure,
    };
}
