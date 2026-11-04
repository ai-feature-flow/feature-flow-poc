using System.Globalization;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// The GitHub Issues tracker provider (docs/06 section 3.6), over GitHub's REST API. An issue's key is
/// <c>owner/repo#12</c>; <see cref="IssueSpec.Project"/> is a repository, default the configured one. GitHub Issues has no
/// statuses: the configuration's <c>status_map</c> maps each library status to a label, or to <c>closed</c>, which closes
/// the issue. A transition sets the issue's state and leaves exactly one mapped label on it. Hierarchy is GitHub's
/// sub-issues; a link is a comment on the first issue naming the second; there are no custom fields. Creates and comments
/// write the key as a hidden marker in the body and adopt what a retry finds. Agents never hold this identity (F5).
/// </summary>
public sealed class GitHubIssuesTracker(GitHubApi api, string? repository, IReadOnlyDictionary<string, string>? statusMap = null) : ITrackerProvider
{
    /// <summary>The state a library status maps to when it closes the issue rather than labelling it.</summary>
    public const string Closed = "closed";

    /// <summary>std's statuses (<c>library/std/status_map.yaml</c>) when the configuration maps none.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultStatusMap = new Dictionary<string, string>
    {
        ["in_progress"] = "status: in progress",
        ["code_review"] = "status: code review",
        ["done"] = Closed,
    };

    static readonly IReadOnlyDictionary<string, string> Permissions = new Dictionary<string, string> { ["issues"] = "write", ["metadata"] = "read" };
    static readonly IReadOnlyDictionary<string, string> ReadPermissions = new Dictionary<string, string> { ["issues"] = "read", ["metadata"] = "read" };

    readonly IReadOnlyDictionary<string, string> map = statusMap ?? DefaultStatusMap;

    IEnumerable<string> StatusLabels => map.Values.Where(v => v != Closed);

    public ProviderDescription Describe() => new("tracker", "github_issues", "0.1.0", new TrackerCapabilities(true, true, true, true, true, false, true));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => api.CheckAsync(cancellationToken);

    // ---- Keys and markers ----

    sealed record Number(RepoRef Repo, int Value)
    {
        public IssueRef Ref => new($"{Repo.FullName}#{Value.ToString(CultureInfo.InvariantCulture)}");

        public string Path => $"/repos/{Repo.FullName}/issues/{Value.ToString(CultureInfo.InvariantCulture)}";
    }

    /// <summary><c>owner/repo#12</c>, or <c>#12</c> and <c>12</c> in the configured repository; null when it is neither.</summary>
    Number? Parse(IssueRef issue)
    {
        var key = issue.Key.Trim();
        var hash = key.LastIndexOf('#');
        var repo = hash > 0 ? key[..hash] : repository;
        var text = hash >= 0 ? key[(hash + 1)..] : key;
        if (repo is null || repo.Split('/').Length != 2 || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n <= 0)
            return null;
        return new Number(new RepoRef(repo), n);
    }

    static ProviderResult<T> NotFound<T>(IssueRef issue) => ProviderResult<T>.Failure("not_found", $"no issue {issue.Key}");

    RepoRef? Project(string? project) => (project ?? repository) is { } name ? new RepoRef(name) : null;

    static string WithMarker(string body, string key) => $"{body}\n\n{GitHubCodeHost.Marker(key)}";

    static string? KeyOf(string? body)
    {
        const string start = "<!-- feature-flow:";
        var at = body?.LastIndexOf(start, StringComparison.Ordinal) ?? -1;
        if (at < 0)
            return null;
        var end = body!.IndexOf(" -->", at, StringComparison.Ordinal);
        return end < 0 ? null : body[(at + start.Length)..end];
    }

    static string WithoutMarker(string? body)
    {
        var text = body ?? "";
        return KeyOf(text) is { } key && text.EndsWith(GitHubCodeHost.Marker(key), StringComparison.Ordinal)
            ? text[..^GitHubCodeHost.Marker(key).Length].TrimEnd('\n')
            : text;
    }

    // ---- Reading ----

    Task<ProviderResult<GitHubResponse>> Send(RepoRef repo, HttpMethod method, string path, object? body, CancellationToken cancellationToken) =>
        api.SendAsync(repo, method == HttpMethod.Get ? ReadPermissions : Permissions, method, path, body, cancellationToken);

    /// <summary>The issue's JSON (200); a pull request, or an issue GitHub no longer has (404, 410), is <c>not_found</c>.</summary>
    async Task<ProviderResult<JsonNode>> ReadAsync(Number number, CancellationToken cancellationToken)
    {
        var response = await Send(number.Repo, HttpMethod.Get, number.Path, null, cancellationToken);
        if (!response.Ok)
            return new ProviderResult<JsonNode>(null, response.Error);
        if (response.Value!.Code != 200)
            return GitHubApi.Unexpected<JsonNode>(response.Value, "GET the issue");
        var json = response.Value.Json!;
        return json["pull_request"] is not null ? NotFound<JsonNode>(number.Ref) : ProviderResult<JsonNode>.Success(json);
    }

    Issue View(Number number, JsonNode json)
    {
        var labels = (json["labels"] as JsonArray ?? []).Select(l => l?["name"]?.GetValue<string>() ?? "").Where(l => l.Length > 0).ToList();
        var state = json["state"]?.GetValue<string>() ?? "open";
        string status;
        if (state == Closed)
            status = map.FirstOrDefault(kv => kv.Value == Closed).Key ?? Closed;
        else
            status = map.FirstOrDefault(kv => kv.Value != Closed && labels.Contains(kv.Value)).Key ?? "open";
        return new Issue(number.Ref, json["title"]?.GetValue<string>() ?? "", WithoutMarker(json["body"]?.GetValue<string>()), status,
            labels.Where(l => !StatusLabels.Contains(l)).ToList(), ParentOf(json), new Dictionary<string, string>(), json["html_url"]?.GetValue<string>());
    }

    /// <summary>The parent from <c>parent_issue_url</c>, <c>https://api.github.com/repos/o/r/issues/5</c>.</summary>
    static IssueRef? ParentOf(JsonNode json)
    {
        if (json["parent_issue_url"]?.GetValue<string>() is not { } url)
            return null;
        var parts = new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is [.., "repos", var owner, var name, "issues", var n] ? new IssueRef($"{owner}/{name}#{n}") : null;
    }

    public async Task<ProviderResult<Issue>> GetIssueAsync(IssueRef issue, CancellationToken cancellationToken)
    {
        if (Parse(issue) is not { } number)
            return NotFound<Issue>(issue);
        var json = await ReadAsync(number, cancellationToken);
        return json.Ok ? ProviderResult<Issue>.Success(View(number, json.Value!)) : new ProviderResult<Issue>(null, json.Error);
    }

    /// <summary>
    /// Every criterion must hold. A parent's sub-issues are read from it; otherwise the project's issues, newest first, up to
    /// GitHub's ten pages of a hundred. Then the issues numbered above the newest that list shows are read one by one:
    /// GitHub's lists lag a create by seconds, and a retried create must find what the first made (<see cref="RecentAsync"/>).
    /// Labels and markers are matched here, not by GitHub's label filter, which lags more.
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<Issue>>> SearchAsync(IssueQuery query, CancellationToken cancellationToken)
    {
        ProviderResult<List<JsonNode>> listed;
        int newest;
        RepoRef repo;
        if (query.Parent is { } parent)
        {
            if (Parse(parent) is not { } p)
                return NotFound<IReadOnlyList<Issue>>(parent);
            repo = p.Repo;
            var top = await Send(repo, HttpMethod.Get, $"/repos/{repo.FullName}/issues?state=all&sort=created&direction=desc&per_page=1", null, cancellationToken);
            if (!top.Ok)
                return new ProviderResult<IReadOnlyList<Issue>>(null, top.Error);
            if (top.Value!.Code != 200)
                return GitHubApi.Unexpected<IReadOnlyList<Issue>>(top.Value, "GET the newest issue");
            newest = (top.Value.Json as JsonArray)?.FirstOrDefault()?["number"]?.GetValue<int>() ?? 0;
            listed = await api.ListAsync(p.Repo, ReadPermissions, p.Path + "/sub_issues", j => j as JsonArray, cancellationToken);
        }
        else
        {
            if (Project(query.Project) is not { } project)
                return ProviderResult<IReadOnlyList<Issue>>.Failure("validation_failed", "a search names no project and no repository is configured");
            repo = project;
            // Unfiltered: GitHub's label filter lags further behind than the list itself.
            listed = await api.ListAsync(repo, ReadPermissions, $"/repos/{repo.FullName}/issues?state=all&sort=created&direction=desc", j => j as JsonArray,
                cancellationToken);
            newest = listed.Ok ? listed.Value!.Select(j => j["number"]?.GetValue<int>() ?? 0).DefaultIfEmpty(0).Max() : 0;
        }

        if (listed.Ok)
        {
            var recent = await RecentAsync(repo, newest, cancellationToken);
            if (!recent.Ok)
                return new ProviderResult<IReadOnlyList<Issue>>(null, recent.Error);
            var seen = listed.Value!.Select(j => j["number"]?.GetValue<int>()).ToHashSet();
            listed.Value!.InsertRange(0, recent.Value!.Where(j => !seen.Contains(j["number"]?.GetValue<int>())
                                                                  && (query.Parent is null || ParentOf(j) == query.Parent)));
        }

        if (!listed.Ok)
            return new ProviderResult<IReadOnlyList<Issue>>(null, listed.Error);
        var found = new List<Issue>();
        foreach (var json in listed.Value!.Where(j => j["pull_request"] is null))
        {
            if (RefOf(json) is not { } number || query.Project is { } project && number.Repo.FullName != project)
                continue;
            var issue = View(number, json);
            if ((query.Label is null || issue.Labels.Contains(query.Label)) && (query.Marker is null || KeyOf(json["body"]?.GetValue<string>()) == query.Marker))
                found.Add(issue);
        }

        return ProviderResult<IReadOnlyList<Issue>>.Success(found);
    }

    /// <summary>
    /// The issues and pull requests numbered above <paramref name="newest"/>, the newest a list showed, read one by one (200)
    /// until GitHub has no such number (404); a deleted one (410) is passed over. At most <see cref="MaxRecent"/>.
    /// </summary>
    async Task<ProviderResult<List<JsonNode>>> RecentAsync(RepoRef repo, int newest, CancellationToken cancellationToken)
    {
        var number = newest;
        var recent = new List<JsonNode>();
        for (var i = 0; i < MaxRecent; i++)
        {
            var next = new Number(repo, ++number);
            var read = await Send(repo, HttpMethod.Get, next.Path, null, cancellationToken);
            if (read is { Ok: false, Error.Reason: "not_found" } && read.Error.Message.Contains(" 410 ", StringComparison.Ordinal))
                continue;
            if (read is { Ok: false, Error.Reason: "not_found" })
                break;
            if (!read.Ok)
                return new ProviderResult<List<JsonNode>>(null, read.Error);
            if (read.Value!.Code == 200 && read.Value.Json is { } json)
                recent.Add(json);
        }

        return ProviderResult<List<JsonNode>>.Success(recent);
    }

    /// <summary>How many numbers past the list's newest a search reads: more than the creates GitHub's lag can hide.</summary>
    const int MaxRecent = 50;

    /// <summary>An issue's key from its own JSON: <c>repository_url</c> and <c>number</c>.</summary>
    static Number? RefOf(JsonNode json)
    {
        var url = json["repository_url"]?.GetValue<string>();
        var parts = url is null ? [] : new Uri(url).AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts is [.., "repos", var owner, var name] && json["number"]?.GetValue<int>() is { } n ? new Number(new RepoRef($"{owner}/{name}"), n) : null;
    }

    // ---- Writing ----

    /// <summary>
    /// Adopts an issue in the project whose body carries the key's marker; otherwise creates one (201) and, with a parent,
    /// adds it as the parent's sub-issue (201). A missing parent is <c>not_found</c> and nothing is created.
    /// </summary>
    public async Task<ProviderResult<IssueRef>> CreateAsync(IssueSpec spec, string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.Title))
            return ProviderResult<IssueRef>.Failure("validation_failed", "an issue needs a title");
        if (spec.Fields is { Count: > 0 })
            return ProviderResult<IssueRef>.Failure("validation_failed", "GitHub Issues has no custom fields");
        if (Project(spec.Project) is not { } repo)
            return ProviderResult<IssueRef>.Failure("validation_failed", "the issue names no project and no repository is configured");
        Number? parent = null;
        if (spec.Parent is { } parentRef)
        {
            parent = Parse(parentRef);
            var read = parent is null ? NotFound<JsonNode>(parentRef) : await ReadAsync(parent, cancellationToken);
            if (!read.Ok)
                return new ProviderResult<IssueRef>(null, read.Error);
        }

        var existing = await SearchAsync(new IssueQuery(Marker: key, Project: repo.FullName), cancellationToken);
        if (!existing.Ok)
            return new ProviderResult<IssueRef>(null, existing.Error);
        Number number;
        long id;
        if (existing.Value!.FirstOrDefault() is { } adopted)
        {
            number = Parse(adopted.Ref)!;
            var json = await ReadAsync(number, cancellationToken);
            if (!json.Ok)
                return new ProviderResult<IssueRef>(null, json.Error);
            id = json.Value!["id"]!.GetValue<long>();
            if (parent is null || adopted.Parent == parent.Ref)
                return ProviderResult<IssueRef>.Success(number.Ref);
        }
        else
        {
            var body = new Dictionary<string, object> { ["title"] = spec.Title!, ["body"] = WithMarker(spec.Body ?? "", key) };
            if (spec.Labels is { } labels)
                body["labels"] = labels;
            var created = await Send(repo, HttpMethod.Post, $"/repos/{repo.FullName}/issues", body, cancellationToken);
            if (!created.Ok)
                return new ProviderResult<IssueRef>(null, created.Error);
            if (created.Value!.Code != 201 || created.Value.Json?["number"]?.GetValue<int>() is not { } n || created.Value.Json["id"]?.GetValue<long>() is not { } newId)
                return GitHubApi.Unexpected<IssueRef>(created.Value, "POST an issue");
            (number, id) = (new Number(repo, n), newId);
        }

        if (parent is not null)
        {
            var linked = await AddSubIssueAsync(parent, id, cancellationToken);
            if (!linked.Ok)
                return new ProviderResult<IssueRef>(null, linked.Error);
        }

        return ProviderResult<IssueRef>.Success(number.Ref);
    }

    async Task<ProviderResult<Unit>> AddSubIssueAsync(Number parent, long childId, CancellationToken cancellationToken)
    {
        var added = await Send(parent.Repo, HttpMethod.Post, parent.Path + "/sub_issues", new { sub_issue_id = childId, replace_parent = true }, cancellationToken);
        if (!added.Ok)
            return new ProviderResult<Unit>(default, added.Error);
        return added.Value!.Code == 201 ? ProviderResult<Unit>.Success(default) : GitHubApi.Unexpected<Unit>(added.Value, "POST a sub-issue");
    }

    /// <summary>Sets what <paramref name="spec"/> gives (200); the body keeps its marker, and given labels keep the issue's status label.</summary>
    public async Task<ProviderResult<IssueRef>> UpdateAsync(IssueRef issue, IssueSpec spec, CancellationToken cancellationToken)
    {
        if (Parse(issue) is not { } number)
            return NotFound<IssueRef>(issue);
        if (spec.Fields is { Count: > 0 })
            return ProviderResult<IssueRef>.Failure("validation_failed", "GitHub Issues has no custom fields");
        if (spec.Project is { } project && project != number.Repo.FullName)
            return ProviderResult<IssueRef>.Failure("validation_failed", "GitHub cannot move an issue to another repository through this provider");
        var read = await ReadAsync(number, cancellationToken);
        if (!read.Ok)
            return new ProviderResult<IssueRef>(null, read.Error);
        var json = read.Value!;
        var patch = new Dictionary<string, object>();
        if (spec.Title is { } title)
            patch["title"] = title;
        if (spec.Body is { } body)
            patch["body"] = KeyOf(json["body"]?.GetValue<string>()) is { } key ? WithMarker(body, key) : body;
        if (spec.Labels is { } labels)
            patch["labels"] = labels.Concat(Labels(json).Where(StatusLabels.Contains)).Distinct().ToList();
        if (patch.Count > 0)
        {
            var patched = await PatchAsync(number, patch, cancellationToken);
            if (!patched.Ok)
                return new ProviderResult<IssueRef>(null, patched.Error);
        }

        if (spec.Parent is { } parentRef && ParentOf(json) != parentRef)
        {
            if (Parse(parentRef) is not { } parent)
                return NotFound<IssueRef>(parentRef);
            var parentRead = await ReadAsync(parent, cancellationToken);
            if (!parentRead.Ok)
                return new ProviderResult<IssueRef>(null, parentRead.Error);
            var linked = await AddSubIssueAsync(parent, json["id"]!.GetValue<long>(), cancellationToken);
            if (!linked.Ok)
                return new ProviderResult<IssueRef>(null, linked.Error);
        }

        return ProviderResult<IssueRef>.Success(number.Ref);
    }

    static List<string> Labels(JsonNode json) =>
        (json["labels"] as JsonArray ?? []).Select(l => l?["name"]?.GetValue<string>() ?? "").Where(l => l.Length > 0).ToList();

    async Task<ProviderResult<Unit>> PatchAsync(Number number, object patch, CancellationToken cancellationToken)
    {
        var patched = await Send(number.Repo, HttpMethod.Patch, number.Path, patch, cancellationToken);
        if (!patched.Ok)
            return new ProviderResult<Unit>(default, patched.Error);
        return patched.Value!.Code == 200 ? ProviderResult<Unit>.Success(default) : GitHubApi.Unexpected<Unit>(patched.Value, "PATCH the issue");
    }

    /// <summary>
    /// A mapped status: <c>closed</c> closes the issue; a label reopens it if needed. Either way the other mapped labels are
    /// removed (one PATCH, 200). An issue already in the status is left as it is. An unmapped status is <c>validation_failed</c>.
    /// </summary>
    public async Task<ProviderResult<Unit>> TransitionAsync(IssueRef issue, string status, CancellationToken cancellationToken)
    {
        if (!map.TryGetValue(status, out var target))
            return ProviderResult<Unit>.Failure("validation_failed", $"status '{status}' is not mapped to a GitHub label or state ({string.Join(", ", map.Keys.Order(StringComparer.Ordinal))})");
        if (Parse(issue) is not { } number)
            return NotFound<Unit>(issue);
        var read = await ReadAsync(number, cancellationToken);
        if (!read.Ok)
            return new ProviderResult<Unit>(default, read.Error);
        var labels = Labels(read.Value!);
        var state = read.Value!["state"]?.GetValue<string>() ?? "open";
        var wantState = target == Closed ? Closed : "open";
        var wantLabels = labels.Where(l => !StatusLabels.Contains(l)).Concat(target == Closed ? [] : [target]).ToList();
        if (state == wantState && wantLabels.Order(StringComparer.Ordinal).SequenceEqual(labels.Order(StringComparer.Ordinal)))
            return ProviderResult<Unit>.Success(default);
        var patch = new Dictionary<string, object> { ["state"] = wantState, ["labels"] = wantLabels };
        if (wantState == Closed)
            patch["state_reason"] = "completed";
        return await PatchAsync(number, patch, cancellationToken);
    }

    /// <summary>GitHub Issues has no typed links: a comment on <paramref name="from"/> names <paramref name="to"/>, once per pair and type.</summary>
    public async Task<ProviderResult<Unit>> LinkAsync(IssueRef from, IssueRef to, string linkType, CancellationToken cancellationToken)
    {
        if (Parse(to) is not { } target)
            return NotFound<Unit>(to);
        var read = await ReadAsync(target, cancellationToken);
        if (!read.Ok)
            return new ProviderResult<Unit>(default, read.Error);
        var comment = await CommentAsync(from, $"{linkType}: {target.Ref.Key}", $"link/{linkType}/{target.Ref.Key}", cancellationToken);
        return comment.Ok ? ProviderResult<Unit>.Success(default) : new ProviderResult<Unit>(default, comment.Error);
    }

    /// <summary>A comment (201) with the key's marker; the issue's comments are searched for the marker first.</summary>
    public async Task<ProviderResult<CommentRef>> CommentAsync(IssueRef issue, string body, string key, CancellationToken cancellationToken)
    {
        if (Parse(issue) is not { } number)
            return NotFound<CommentRef>(issue);
        var read = await ReadAsync(number, cancellationToken);
        if (!read.Ok)
            return new ProviderResult<CommentRef>(null, read.Error);
        var comments = await api.ListAsync(number.Repo, ReadPermissions, number.Path + "/comments", j => j as JsonArray, cancellationToken);
        if (!comments.Ok)
            return new ProviderResult<CommentRef>(null, comments.Error);
        if (comments.Value!.FirstOrDefault(c => KeyOf(c["body"]?.GetValue<string>()) == key)?["id"]?.GetValue<long>() is { } existing)
            return ProviderResult<CommentRef>.Success(new CommentRef(existing.ToString(CultureInfo.InvariantCulture)));
        var posted = await Send(number.Repo, HttpMethod.Post, number.Path + "/comments", new { body = WithMarker(body, key) }, cancellationToken);
        if (!posted.Ok)
            return new ProviderResult<CommentRef>(null, posted.Error);
        return posted.Value!.Code == 201 && posted.Value.Json?["id"]?.GetValue<long>() is { } id
            ? ProviderResult<CommentRef>.Success(new CommentRef(id.ToString(CultureInfo.InvariantCulture)))
            : GitHubApi.Unexpected<CommentRef>(posted.Value, "POST a comment");
    }
}
