using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Jira;

/// <summary>
/// The Jira tracker provider (docs/06 section 3.6), over Jira Cloud's REST API v3. An issue's key is Jira's
/// (<c>ACME-12</c>); <see cref="IssueSpec.Project"/> is a project key, default the configured one. The configuration's
/// <c>status_map</c> maps each library status to a Jira status name; a transition takes the workflow's transition into that
/// status. Hierarchy is Jira's <c>parent</c>: a child of an epic is created with the configured issue type, a child of
/// any other issue as a subtask. A link is a Jira issue link whose type's name or description matches the library's link
/// type. Text is written as plain paragraphs. A create's idempotency key is a label <c>ff-key-&lt;hash&gt;</c> (JQL finds
/// labels exactly; it is left out of <see cref="Issue.Labels"/>), a comment's a hidden comment property. Jira's search
/// index lags a create by seconds, so a search also reads the issues numbered above the newest it lists, one by one (as the
/// GitHub Issues tracker does, LOG 184). There are no custom fields. Agents never hold this identity (F5).
/// </summary>
public sealed partial class JiraTracker(JiraApi api, string? project, IReadOnlyDictionary<string, string>? statusMap = null, string issueType = "Task")
    : ITrackerProvider
{
    /// <summary>std's statuses (<c>library/std/status_map.yaml</c>) when the configuration maps none: Jira's usual workflow names.</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultStatusMap = new Dictionary<string, string>
    {
        ["in_progress"] = "In Progress",
        ["code_review"] = "In Review",
        ["done"] = "Done",
    };

    /// <summary>The prefix of the label that carries a create's idempotency key.</summary>
    public const string MarkerPrefix = "ff-key-";

    /// <summary>The comment property that carries a comment's idempotency key.</summary>
    public const string CommentProperty = "feature-flow";

    const string Fields = "summary,description,status,labels,parent,issuetype";

    readonly IReadOnlyDictionary<string, string> map = statusMap ?? DefaultStatusMap;
    readonly Dictionary<string, string> subtaskTypes = new(StringComparer.Ordinal);
    JsonArray? linkTypes;

    public ProviderDescription Describe() => new("tracker", "jira", "0.1.0", new TrackerCapabilities(true, true, true, true, true, false, true));

    public async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        var me = await api.SendAsync(HttpMethod.Get, "/rest/api/3/myself", null, cancellationToken);
        if (!me.Ok)
            return new ProviderResult<Unit>(default, me.Error);
        return me.Value!.Code == 200 ? ProviderResult<Unit>.Success(default) : JiraApi.Unexpected<Unit>(me.Value, "GET myself");
    }

    // ---- Keys, markers and text ----

    [GeneratedRegex("^([A-Z][A-Z0-9_]*)-([1-9][0-9]*)$")]
    private static partial Regex KeyPattern();

    static (string Project, int Number)? Parse(IssueRef issue) =>
        KeyPattern().Match(issue.Key.Trim()) is { Success: true } m ? (m.Groups[1].Value, int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)) : null;

    static string Path(string key) => $"/rest/api/3/issue/{Uri.EscapeDataString(key)}";

    static ProviderResult<T> NotFound<T>(IssueRef issue) => ProviderResult<T>.Failure("not_found", $"no issue {issue.Key}");

    static ProviderResult<T> Failed<T, TFrom>(ProviderResult<TFrom> failed) => new(default, failed.Error);

    /// <summary>The label that marks an issue created with <paramref name="key"/>: labels hold no spaces and at most 255 characters, so the key is hashed.</summary>
    public static string MarkerLabel(string key) => MarkerPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..20];

    /// <summary>Plain text as Atlassian Document Format: one paragraph per line.</summary>
    public static JsonObject Document(string text) => new()
    {
        ["type"] = "doc",
        ["version"] = 1,
        ["content"] = new JsonArray(text.Split('\n').Select(line => (JsonNode)new JsonObject
        {
            ["type"] = "paragraph",
            ["content"] = line.Length == 0 ? new JsonArray() : new JsonArray(new JsonObject { ["type"] = "text", ["text"] = line }),
        }).ToArray()),
    };

    /// <summary>A document's text: each block on its own line, inline text and hard breaks within it.</summary>
    public static string Text(JsonNode? document)
    {
        var lines = new List<string>();
        Blocks(document, lines);
        return string.Join("\n", lines);

        static void Blocks(JsonNode? node, List<string> lines)
        {
            foreach (var child in node?["content"] as JsonArray ?? [])
            {
                if (child?["type"]?.GetValue<string>() is "paragraph" or "heading" or "codeBlock")
                    lines.Add(Inline(child));
                else
                    Blocks(child, lines);
            }
        }

        static string Inline(JsonNode? node) => string.Concat((node?["content"] as JsonArray ?? []).Select(n => n?["type"]?.GetValue<string>() switch
        {
            "text" => n["text"]?.GetValue<string>() ?? "",
            "hardBreak" => "\n",
            _ => Inline(n),
        }));
    }

    static List<string> Labels(JsonNode json) =>
        (json["fields"]?["labels"] as JsonArray ?? []).Select(l => l?.GetValue<string>() ?? "").Where(l => l.Length > 0).ToList();

    Issue View(JsonNode json)
    {
        var fields = json["fields"]!;
        var key = json["key"]!.GetValue<string>();
        var jiraStatus = fields["status"]?["name"]?.GetValue<string>() ?? "";
        var status = map.FirstOrDefault(kv => string.Equals(kv.Value, jiraStatus, StringComparison.OrdinalIgnoreCase)).Key ?? jiraStatus;
        var parent = fields["parent"]?["key"]?.GetValue<string>() is { } p ? new IssueRef(p) : null;
        return new Issue(new IssueRef(key), fields["summary"]?.GetValue<string>() ?? "", Text(fields["description"]), status,
            Labels(json).Where(l => !l.StartsWith(MarkerPrefix, StringComparison.Ordinal)).ToList(), parent, new Dictionary<string, string>(),
            $"{api.BaseUrl}/browse/{key}");
    }

    // ---- Reading ----

    async Task<ProviderResult<JsonNode>> ReadAsync(string key, CancellationToken cancellationToken, string fields = Fields)
    {
        var read = await api.SendAsync(HttpMethod.Get, $"{Path(key)}?fields={fields}", null, cancellationToken);
        if (!read.Ok)
            return Failed<JsonNode, JiraResponse>(read);
        return read.Value!.Code == 200 && read.Value.Json is { } json ? ProviderResult<JsonNode>.Success(json) : JiraApi.Unexpected<JsonNode>(read.Value, "GET the issue");
    }

    public async Task<ProviderResult<Issue>> GetIssueAsync(IssueRef issue, CancellationToken cancellationToken)
    {
        if (Parse(issue) is null)
            return NotFound<Issue>(issue);
        var json = await ReadAsync(issue.Key, cancellationToken);
        return json.Ok ? ProviderResult<Issue>.Success(View(json.Value!)) : Failed<Issue, JsonNode>(json);
    }

    /// <summary>
    /// Every criterion must hold. JQL lists the project's matching issues, newest first, up to ten pages of a hundred; then
    /// the issues numbered above the newest the index shows are read one by one, because the index lags a create by
    /// seconds and a retried create must find what the first made.
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<Issue>>> SearchAsync(IssueQuery query, CancellationToken cancellationToken)
    {
        string? inProject;
        if (query.Parent is { } parent)
        {
            if (Parse(parent) is not { } p)
                return NotFound<IReadOnlyList<Issue>>(parent);
            inProject = p.Project;
        }
        else
        {
            inProject = query.Project ?? project;
        }

        if (inProject is null)
            return ProviderResult<IReadOnlyList<Issue>>.Failure("validation_failed", "a search names no project and no project is configured");
        var clauses = new List<string> { $"project = {Quote(inProject)}" };
        if (query.Label is { } label)
            clauses.Add($"labels = {Quote(label)}");
        if (query.Parent is { } parentRef)
            clauses.Add($"parent = {Quote(parentRef.Key)}");
        if (query.Marker is { } marker)
            clauses.Add($"labels = {Quote(MarkerLabel(marker))}");
        var listed = await JqlAsync(string.Join(" AND ", clauses) + " ORDER BY created DESC", 10, cancellationToken);
        if (!listed.Ok)
            return Failed<IReadOnlyList<Issue>, List<JsonNode>>(listed);
        var newest = await JqlAsync($"project = {Quote(inProject)} ORDER BY created DESC", 1, cancellationToken, pageSize: Window);
        if (!newest.Ok)
            return Failed<IReadOnlyList<Issue>, List<JsonNode>>(newest);
        var indexed = newest.Value!.Select(j => Parse(new IssueRef(j["key"]!.GetValue<string>()))?.Number ?? 0).ToHashSet();
        var recent = await RecentAsync(inProject, indexed, cancellationToken);
        if (!recent.Ok)
            return Failed<IReadOnlyList<Issue>, List<JsonNode>>(recent);

        var seen = listed.Value!.Select(j => j["key"]!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
        var found = recent.Value!.Where(j => !seen.Contains(j["key"]!.GetValue<string>()) && Matches(j, query)).Concat(listed.Value!).Select(View).ToList();
        return ProviderResult<IReadOnlyList<Issue>>.Success(found);
    }

    static bool Matches(JsonNode json, IssueQuery query)
    {
        var labels = Labels(json);
        return (query.Label is null || labels.Contains(query.Label))
               && (query.Marker is null || labels.Contains(MarkerLabel(query.Marker)))
               && (query.Parent is null || json["fields"]?["parent"]?["key"]?.GetValue<string>() == query.Parent.Key);
    }

    static string Quote(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>Pages of <c>POST /search/jql</c> (200), following <c>nextPageToken</c>, up to <paramref name="maxPages"/>.</summary>
    async Task<ProviderResult<List<JsonNode>>> JqlAsync(string jql, int maxPages, CancellationToken cancellationToken, int pageSize = 100)
    {
        var all = new List<JsonNode>();
        string? next = null;
        for (var page = 0; page < maxPages; page++)
        {
            var body = new JsonObject { ["jql"] = jql, ["maxResults"] = pageSize, ["fields"] = new JsonArray(Fields.Split(',').Select(f => (JsonNode)f).ToArray()) };
            if (next is not null)
                body["nextPageToken"] = next;
            var response = await api.SendAsync(HttpMethod.Post, "/rest/api/3/search/jql", body.ToJsonString(), cancellationToken);
            if (!response.Ok)
                return Failed<List<JsonNode>, JiraResponse>(response);
            if (response.Value!.Code != 200 || response.Value.Json is not { } json)
                return JiraApi.Unexpected<List<JsonNode>>(response.Value, "POST search/jql");
            all.AddRange((json["issues"] as JsonArray ?? []).Where(i => i is not null)!);
            next = json["nextPageToken"]?.GetValue<string>();
            if (next is null || json["isLast"]?.GetValue<bool>() == true)
                break;
        }

        return ProviderResult<List<JsonNode>>.Success(all);
    }

    /// <summary>
    /// The project's issues the index may not show yet, read one by one (200): those numbered among the newest
    /// <see cref="Window"/> it does not list (Jira indexes out of order, so a later issue can be listed before an earlier
    /// one), then those numbered above the newest until <see cref="MaxMisses"/> numbers in a row are not found (a deleted
    /// or moved issue leaves a gap), at most <see cref="MaxRecent"/>. A gap's number that is not found is passed over.
    /// </summary>
    async Task<ProviderResult<List<JsonNode>>> RecentAsync(string inProject, IReadOnlySet<int> indexed, CancellationToken cancellationToken)
    {
        var newest = indexed.DefaultIfEmpty(0).Max();
        var recent = new List<JsonNode>();
        foreach (var gap in Enumerable.Range(Math.Max(1, newest - Window + 1), Math.Max(0, Math.Min(Window, newest))).Where(n => !indexed.Contains(n)))
        {
            var read = await ReadAsync($"{inProject}-{gap.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
            if (read is { Ok: false, Error.Reason: "not_found" })
                continue;
            if (!read.Ok)
                return Failed<List<JsonNode>, JsonNode>(read);
            recent.Add(read.Value!);
        }

        var misses = 0;
        for (var number = newest + 1; number <= newest + MaxRecent && misses < MaxMisses; number++)
        {
            var read = await ReadAsync($"{inProject}-{number.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
            if (read is { Ok: false, Error.Reason: "not_found" })
            {
                misses++;
                continue;
            }

            if (!read.Ok)
                return Failed<List<JsonNode>, JsonNode>(read);
            misses = 0;
            recent.Add(read.Value!);
        }

        return ProviderResult<List<JsonNode>>.Success(recent);
    }

    /// <summary>How many numbers past the index's newest a search reads: more than the creates the index's lag can hide.</summary>
    const int MaxRecent = 50;

    const int MaxMisses = 3;

    /// <summary>How many of the newest numbers a search checks against the index: more than the creates its lag can reorder.</summary>
    const int Window = 50;

    // ---- Writing ----

    /// <summary>
    /// Adopts the project's issue that carries the key's label; otherwise creates one (201), as a subtask when its parent
    /// is not an epic. A missing parent is <c>not_found</c> and nothing is created.
    /// </summary>
    public async Task<ProviderResult<IssueRef>> CreateAsync(IssueSpec spec, string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(spec.Title))
            return ProviderResult<IssueRef>.Failure("validation_failed", "an issue needs a title");
        if (spec.Fields is { Count: > 0 })
            return ProviderResult<IssueRef>.Failure("validation_failed", "the Jira tracker maps no custom fields");
        if ((spec.Project ?? project) is not { } inProject)
            return ProviderResult<IssueRef>.Failure("validation_failed", "the issue names no project and no project is configured");
        var type = issueType;
        if (spec.Parent is { } parent)
        {
            if (Parse(parent) is null)
                return NotFound<IssueRef>(parent);
            var read = await ReadAsync(parent.Key, cancellationToken, "issuetype");
            if (!read.Ok)
                return Failed<IssueRef, JsonNode>(read);
            var level = read.Value!["fields"]?["issuetype"]?["hierarchyLevel"]?.GetValue<int>() ?? 0;
            if (level < 0)
                return ProviderResult<IssueRef>.Failure("validation_failed", $"{parent.Key} is a subtask, which cannot have children in Jira");
            if (level == 0)
            {
                var subtask = await SubtaskTypeAsync(inProject, cancellationToken);
                if (!subtask.Ok)
                    return Failed<IssueRef, string>(subtask);
                type = subtask.Value!;
            }
        }

        var existing = await SearchAsync(new IssueQuery(Marker: key, Project: inProject), cancellationToken);
        if (!existing.Ok)
            return Failed<IssueRef, IReadOnlyList<Issue>>(existing);
        if (existing.Value!.FirstOrDefault() is { } adopted)
            return ProviderResult<IssueRef>.Success(adopted.Ref);

        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = inProject },
            ["summary"] = spec.Title,
            ["issuetype"] = new JsonObject { ["name"] = type },
            ["description"] = Document(spec.Body ?? ""),
            ["labels"] = new JsonArray((spec.Labels ?? []).Append(MarkerLabel(key)).Distinct().Select(l => (JsonNode)l).ToArray()),
        };
        if (spec.Parent is { } p2)
            fields["parent"] = new JsonObject { ["key"] = p2.Key };
        var created = await api.SendAsync(HttpMethod.Post, "/rest/api/3/issue", new JsonObject { ["fields"] = fields }.ToJsonString(), cancellationToken);
        if (!created.Ok)
            return Failed<IssueRef, JiraResponse>(created);
        return created.Value!.Code == 201 && created.Value.Json?["key"]?.GetValue<string>() is { } newKey
            ? ProviderResult<IssueRef>.Success(new IssueRef(newKey))
            : JiraApi.Unexpected<IssueRef>(created.Value, "POST an issue");
    }

    /// <summary>The project's subtask issue type (200 from the project), read once per project.</summary>
    async Task<ProviderResult<string>> SubtaskTypeAsync(string inProject, CancellationToken cancellationToken)
    {
        if (subtaskTypes.TryGetValue(inProject, out var known))
            return ProviderResult<string>.Success(known);
        var read = await api.SendAsync(HttpMethod.Get, $"/rest/api/3/project/{Uri.EscapeDataString(inProject)}", null, cancellationToken);
        if (!read.Ok)
            return Failed<string, JiraResponse>(read);
        if (read.Value!.Code != 200)
            return JiraApi.Unexpected<string>(read.Value, "GET the project");
        var name = (read.Value.Json?["issueTypes"] as JsonArray ?? []).FirstOrDefault(t => t?["subtask"]?.GetValue<bool>() == true)?["name"]?.GetValue<string>();
        if (name is null)
            return ProviderResult<string>.Failure("validation_failed", $"project {inProject} has no subtask issue type, so an issue that is not an epic cannot have children");
        subtaskTypes[inProject] = name;
        return ProviderResult<string>.Success(name);
    }

    /// <summary>Sets what <paramref name="spec"/> gives (204); given labels keep the issue's key label.</summary>
    public async Task<ProviderResult<IssueRef>> UpdateAsync(IssueRef issue, IssueSpec spec, CancellationToken cancellationToken)
    {
        if (Parse(issue) is not { } parsed)
            return NotFound<IssueRef>(issue);
        if (spec.Fields is { Count: > 0 })
            return ProviderResult<IssueRef>.Failure("validation_failed", "the Jira tracker maps no custom fields");
        if (spec.Project is { } other && other != parsed.Project)
            return ProviderResult<IssueRef>.Failure("validation_failed", "this provider cannot move an issue to another project");
        var read = await ReadAsync(issue.Key, cancellationToken);
        if (!read.Ok)
            return Failed<IssueRef, JsonNode>(read);
        var fields = new JsonObject();
        if (spec.Title is { } title)
            fields["summary"] = title;
        if (spec.Body is { } body)
            fields["description"] = Document(body);
        if (spec.Labels is { } labels)
            fields["labels"] = new JsonArray(labels.Concat(Labels(read.Value!).Where(l => l.StartsWith(MarkerPrefix, StringComparison.Ordinal)))
                .Distinct().Select(l => (JsonNode)l).ToArray());
        if (spec.Parent is { } parent && read.Value!["fields"]?["parent"]?["key"]?.GetValue<string>() != parent.Key)
        {
            var parentRead = Parse(parent) is null ? NotFound<JsonNode>(parent) : await ReadAsync(parent.Key, cancellationToken, "issuetype");
            if (!parentRead.Ok)
                return Failed<IssueRef, JsonNode>(parentRead);
            fields["parent"] = new JsonObject { ["key"] = parent.Key };
        }

        if (fields.Count == 0)
            return ProviderResult<IssueRef>.Success(issue);
        var put = await api.SendAsync(HttpMethod.Put, Path(issue.Key), new JsonObject { ["fields"] = fields }.ToJsonString(), cancellationToken);
        if (!put.Ok)
            return Failed<IssueRef, JiraResponse>(put);
        return put.Value!.Code == 204 ? ProviderResult<IssueRef>.Success(issue) : JiraApi.Unexpected<IssueRef>(put.Value, "PUT the issue");
    }

    /// <summary>
    /// A mapped status: the workflow's transition into the mapped Jira status (204). An issue already in it is left as it
    /// is. An unmapped status, or one the workflow offers no transition to from here, is <c>validation_failed</c>.
    /// </summary>
    public async Task<ProviderResult<Unit>> TransitionAsync(IssueRef issue, string status, CancellationToken cancellationToken)
    {
        if (!map.TryGetValue(status, out var target))
            return ProviderResult<Unit>.Failure("validation_failed", $"status '{status}' is not mapped to a Jira status ({string.Join(", ", map.Keys.Order(StringComparer.Ordinal))})");
        if (Parse(issue) is null)
            return NotFound<Unit>(issue);
        var read = await ReadAsync(issue.Key, cancellationToken, "status");
        if (!read.Ok)
            return Failed<Unit, JsonNode>(read);
        var current = read.Value!["fields"]?["status"]?["name"]?.GetValue<string>() ?? "";
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
            return ProviderResult<Unit>.Success(default);
        var transitions = await api.SendAsync(HttpMethod.Get, Path(issue.Key) + "/transitions", null, cancellationToken);
        if (!transitions.Ok)
            return Failed<Unit, JiraResponse>(transitions);
        if (transitions.Value!.Code != 200)
            return JiraApi.Unexpected<Unit>(transitions.Value, "GET the transitions");
        var list = transitions.Value.Json?["transitions"] as JsonArray ?? [];
        var chosen = list.FirstOrDefault(t => string.Equals(t?["to"]?["name"]?.GetValue<string>(), target, StringComparison.OrdinalIgnoreCase))
                     ?? list.FirstOrDefault(t => string.Equals(t?["name"]?.GetValue<string>(), target, StringComparison.OrdinalIgnoreCase));
        if (chosen?["id"]?.GetValue<string>() is not { } id)
            return ProviderResult<Unit>.Failure("validation_failed",
                $"Jira's workflow offers no transition from '{current}' to '{target}' for {issue.Key} (it offers {string.Join(", ", list.Select(t => t?["to"]?["name"]?.GetValue<string>()))})");
        var moved = await api.SendAsync(HttpMethod.Post, Path(issue.Key) + "/transitions", new JsonObject { ["transition"] = new JsonObject { ["id"] = id } }.ToJsonString(),
            cancellationToken);
        if (!moved.Ok)
            return Failed<Unit, JiraResponse>(moved);
        return moved.Value!.Code == 204 ? ProviderResult<Unit>.Success(default) : JiraApi.Unexpected<Unit>(moved.Value, "POST a transition");
    }

    /// <summary>
    /// A Jira issue link (201) of the link type whose name, outward or inward description is <paramref name="linkType"/>
    /// (<c>relates</c> is <c>Relates</c>, <c>blocks</c> is <c>Blocks</c>), reading as <paramref name="from"/> blocks
    /// <paramref name="to"/>. A link of that type between the two already there is left as it is.
    /// </summary>
    public async Task<ProviderResult<Unit>> LinkAsync(IssueRef from, IssueRef to, string linkType, CancellationToken cancellationToken)
    {
        if (Parse(from) is null)
            return NotFound<Unit>(from);
        if (Parse(to) is null)
            return NotFound<Unit>(to);
        var types = await LinkTypesAsync(cancellationToken);
        if (!types.Ok)
            return Failed<Unit, JsonArray>(types);
        bool Named(JsonNode? t, string field) => string.Equals(t?[field]?.GetValue<string>(), linkType, StringComparison.OrdinalIgnoreCase);
        var type = types.Value!.FirstOrDefault(t => Named(t, "name")) ?? types.Value!.FirstOrDefault(t => Named(t, "outward") || Named(t, "inward"));
        if (type?["name"]?.GetValue<string>() is not { } typeName)
            return ProviderResult<Unit>.Failure("validation_failed",
                $"Jira has no link type '{linkType}' ({string.Join(", ", types.Value!.Select(t => t?["name"]?.GetValue<string>()))})");
        var target = await ReadAsync(to.Key, cancellationToken, "summary");
        if (!target.Ok)
            return Failed<Unit, JsonNode>(target);
        var source = await ReadAsync(from.Key, cancellationToken, "issuelinks");
        if (!source.Ok)
            return Failed<Unit, JsonNode>(source);
        if ((source.Value!["fields"]?["issuelinks"] as JsonArray ?? []).Any(l => l?["type"]?["name"]?.GetValue<string>() == typeName
                && (l["inwardIssue"]?["key"]?.GetValue<string>() ?? l["outwardIssue"]?["key"]?.GetValue<string>()) == to.Key))
            return ProviderResult<Unit>.Success(default);
        // Jira reads a link as "inwardIssue <outward description> outwardIssue": sent so, from blocks to.
        var body = new JsonObject
        {
            ["type"] = new JsonObject { ["name"] = typeName },
            ["inwardIssue"] = new JsonObject { ["key"] = from.Key },
            ["outwardIssue"] = new JsonObject { ["key"] = to.Key },
        };
        var linked = await api.SendAsync(HttpMethod.Post, "/rest/api/3/issueLink", body.ToJsonString(), cancellationToken);
        if (!linked.Ok)
            return Failed<Unit, JiraResponse>(linked);
        return linked.Value!.Code == 201 ? ProviderResult<Unit>.Success(default) : JiraApi.Unexpected<Unit>(linked.Value, "POST an issue link");
    }

    async Task<ProviderResult<JsonArray>> LinkTypesAsync(CancellationToken cancellationToken)
    {
        if (linkTypes is not null)
            return ProviderResult<JsonArray>.Success(linkTypes);
        var read = await api.SendAsync(HttpMethod.Get, "/rest/api/3/issueLinkType", null, cancellationToken);
        if (!read.Ok)
            return Failed<JsonArray, JiraResponse>(read);
        if (read.Value!.Code != 200 || read.Value.Json?["issueLinkTypes"] is not JsonArray types)
            return JiraApi.Unexpected<JsonArray>(read.Value, "GET the issue link types");
        linkTypes = types;
        return ProviderResult<JsonArray>.Success(types);
    }

    /// <summary>A comment (201) carrying the key in a hidden comment property; the issue's comments are searched for it first.</summary>
    public async Task<ProviderResult<CommentRef>> CommentAsync(IssueRef issue, string body, string key, CancellationToken cancellationToken)
    {
        if (Parse(issue) is null)
            return NotFound<CommentRef>(issue);
        var comments = await CommentsAsync(issue.Key, cancellationToken);
        if (!comments.Ok)
            return Failed<CommentRef, List<JsonNode>>(comments);
        if (comments.Value!.FirstOrDefault(c => KeyOf(c) == key)?["id"]?.GetValue<string>() is { } existing)
            return ProviderResult<CommentRef>.Success(new CommentRef(existing));
        var payload = new JsonObject
        {
            ["body"] = Document(body),
            ["properties"] = new JsonArray(new JsonObject { ["key"] = CommentProperty, ["value"] = new JsonObject { ["key"] = key } }),
        };
        var posted = await api.SendAsync(HttpMethod.Post, Path(issue.Key) + "/comment", payload.ToJsonString(), cancellationToken);
        if (!posted.Ok)
            return Failed<CommentRef, JiraResponse>(posted);
        return posted.Value!.Code == 201 && posted.Value.Json?["id"]?.GetValue<string>() is { } id
            ? ProviderResult<CommentRef>.Success(new CommentRef(id))
            : JiraApi.Unexpected<CommentRef>(posted.Value, "POST a comment");
    }

    static string? KeyOf(JsonNode? comment) =>
        (comment?["properties"] as JsonArray ?? []).FirstOrDefault(p => p?["key"]?.GetValue<string>() == CommentProperty)?["value"]?["key"]?.GetValue<string>();

    /// <summary>Every comment on the issue with its properties (200), a hundred a page; a missing issue is <c>not_found</c>.</summary>
    public async Task<ProviderResult<List<JsonNode>>> CommentsAsync(string issueKey, CancellationToken cancellationToken)
    {
        var all = new List<JsonNode>();
        for (var page = 0; page < 10; page++)
        {
            var read = await api.SendAsync(HttpMethod.Get, $"{Path(issueKey)}/comment?expand=properties&maxResults=100&startAt={all.Count}", null, cancellationToken);
            if (!read.Ok)
                return Failed<List<JsonNode>, JiraResponse>(read);
            if (read.Value!.Code != 200 || read.Value.Json is not { } json)
                return JiraApi.Unexpected<List<JsonNode>>(read.Value, "GET the comments");
            var items = (json["comments"] as JsonArray ?? []).Where(c => c is not null).ToList();
            all.AddRange(items!);
            if (items.Count == 0 || all.Count >= (json["total"]?.GetValue<int>() ?? 0))
                break;
        }

        return ProviderResult<List<JsonNode>>.Success(all);
    }
}
