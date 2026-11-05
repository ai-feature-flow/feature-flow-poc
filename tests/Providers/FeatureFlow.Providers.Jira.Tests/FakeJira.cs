using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FeatureFlow.Providers.Jira.Tests;

/// <summary>
/// Jira Cloud's REST API v3 in memory, for the parts the tracker uses: issues, JQL search, transitions, issue links and
/// comments with properties. Its workflow lets any status move to any other, by transitions named unlike their statuses. The search index lags: the
/// <see cref="Unlisted"/> newest issues are left out of every JQL result, as Jira's index lags a create. A request without
/// the basic credential <see cref="Credential"/> is refused with 401; <see cref="Answer"/> overrides a route.
/// </summary>
public sealed partial class FakeJira : HttpMessageHandler
{
    public const string Base = "https://jira.example";

    public sealed class FakeIssue
    {
        public required string Key { get; init; }
        public required string Project { get; init; }
        public string Summary { get; set; } = "";
        public JsonNode? Description { get; set; }
        public string Status { get; set; } = "To Do";
        public List<string> Labels { get; set; } = [];
        public string? Parent { get; set; }
        public string Type { get; set; } = "Task";
        /// <summary>Links as Jira reads them: <c>Source</c> blocks <c>Target</c>.</summary>
        public List<(string Type, string Source, string Target)> Links { get; } = [];
        public List<(string Id, JsonNode Body, string? Key)> Comments { get; } = [];
    }

    readonly Dictionary<string, int> next = [];
    readonly List<(HttpMethod Method, string Path, int Status, string Body)> answers = [];
    int ids = 10000;

    public string Credential { get; set; } = "bot@example.com:api-token";

    public string[] Statuses { get; set; } = ["To Do", "In Progress", "In Review", "Done"];

    public int Unlisted { get; set; }

    /// <summary>Issues the index has not caught up with yet, whatever their age: Jira indexes out of order under load.</summary>
    public HashSet<string> Unindexed { get; } = [];

    public List<FakeIssue> Issues { get; } = [];

    public List<(HttpMethod Method, string Path, string Body)> Calls { get; } = [];

    /// <summary>Answers <paramref name="method"/> on a path starting with <paramref name="path"/> with this status and body, before any route.</summary>
    public FakeJira Answer(HttpMethod method, string path, int status, string body = "")
    {
        answers.Add((method, path, status, body));
        return this;
    }

    public FakeIssue Issue(string key) => Issues.Single(i => i.Key == key);

    public JiraApi Api(string? credential = null) =>
        new(_ => Task.FromResult(Abstractions.ProviderResult<string>.Success(credential ?? Credential)), Base, this) { FreshCredentialDelay = TimeSpan.FromMilliseconds(10) };

    static HttpResponseMessage Json(int status, JsonNode? body = null) =>
        new((HttpStatusCode)status) { Content = new StringContent(body?.ToJsonString() ?? "", Encoding.UTF8, "application/json") };

    static HttpResponseMessage Error(int status, string message) => Json(status, new JsonObject { ["errorMessages"] = new JsonArray(message) });

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.PathAndQuery;
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request.Method, path, body));
        if (request.Headers.Authorization is not { Scheme: "Basic" } auth || Encoding.UTF8.GetString(Convert.FromBase64String(auth.Parameter!)) != Credential)
            return Error(401, "Client must be authenticated to access this resource.");
        foreach (var (method, prefix, status, text) in answers)
            if (method == request.Method && path.StartsWith(prefix, StringComparison.Ordinal))
                return new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
        var json = body.Length == 0 ? null : JsonNode.Parse(body);
        var route = path.Split('?')[0];
        var query = request.RequestUri.Query;
        return (request.Method.Method, route) switch
        {
            ("GET", "/rest/api/3/myself") => Json(200, new JsonObject { ["accountId"] = "bot" }),
            ("GET", "/rest/api/3/issueLinkType") => Json(200, JsonNode.Parse("""
                { "issueLinkTypes": [ { "name": "Blocks", "inward": "is blocked by", "outward": "blocks" },
                                      { "name": "Relates", "inward": "relates to", "outward": "relates to" } ] }
                """)),
            ("POST", "/rest/api/3/search/jql") => Search(json!),
            ("POST", "/rest/api/3/issue") => Create(json!),
            ("POST", "/rest/api/3/issueLink") => Link(json!),
            _ when ProjectRoute().Match(route) is { Success: true } p => Json(200, JsonNode.Parse($$"""
                { "key": "{{p.Groups[1].Value}}", "issueTypes": [ { "name": "Epic", "subtask": false, "hierarchyLevel": 1 },
                  { "name": "Task", "subtask": false, "hierarchyLevel": 0 }, { "name": "Subtask", "subtask": true, "hierarchyLevel": -1 } ] }
                """)),
            _ when IssueRoute().Match(route) is { Success: true } m => Issues.FirstOrDefault(i => i.Key == m.Groups[1].Value) is not { } issue
                ? Error(404, "Issue does not exist or you do not have permission to see it.")
                : (request.Method.Method, m.Groups[2].Value) switch
                {
                    ("GET", "") => Json(200, View(issue)),
                    ("PUT", "") => Update(issue, json!),
                    ("GET", "/transitions") => Json(200, new JsonObject
                    {
                        ["transitions"] = new JsonArray(Statuses.Where(s => s != issue.Status).Select((s, n) => (JsonNode)new JsonObject
                            { ["id"] = (n + 11).ToString(System.Globalization.CultureInfo.InvariantCulture), ["name"] = "Move to " + s, ["to"] = new JsonObject { ["name"] = s } }).ToArray()),
                    }),
                    ("POST", "/transitions") => Transition(issue, json!),
                    ("GET", "/comment") => Comments(issue, query),
                    ("POST", "/comment") => Comment(issue, json!),
                    _ => throw new InvalidOperationException($"no route for {request.Method} {path}"),
                },
            _ => throw new InvalidOperationException($"no route for {request.Method} {path}"),
        };
    }

    [GeneratedRegex("^/rest/api/3/issue/([A-Z][A-Z0-9_]*-[0-9]+)(/transitions|/comment)?$")]
    private static partial Regex IssueRoute();

    [GeneratedRegex("^/rest/api/3/project/([A-Z][A-Z0-9_]*)$")]
    private static partial Regex ProjectRoute();

    [GeneratedRegex("^(project|labels|parent) = \"((?:[^\"\\\\]|\\\\.)*)\"$")]
    private static partial Regex Clause();

    JsonObject View(FakeIssue issue)
    {
        var fields = new JsonObject
        {
            ["summary"] = issue.Summary,
            ["description"] = issue.Description?.DeepClone(),
            ["status"] = new JsonObject { ["name"] = issue.Status },
            ["labels"] = new JsonArray(issue.Labels.Select(l => (JsonNode)l).ToArray()),
            ["issuetype"] = new JsonObject { ["name"] = issue.Type, ["hierarchyLevel"] = issue.Type switch { "Epic" => 1, "Subtask" => -1, _ => 0 } },
            ["issuelinks"] = new JsonArray(issue.Links.Select(l => (JsonNode)new JsonObject
            {
                ["type"] = new JsonObject { ["name"] = l.Type },
                [l.Source == issue.Key ? "outwardIssue" : "inwardIssue"] = new JsonObject { ["key"] = l.Source == issue.Key ? l.Target : l.Source },
            }).ToArray()),
        };
        if (issue.Parent is { } parent)
            fields["parent"] = new JsonObject { ["key"] = parent };
        return new JsonObject { ["id"] = issue.Key.GetHashCode().ToString(System.Globalization.CultureInfo.InvariantCulture), ["key"] = issue.Key, ["fields"] = fields };
    }

    HttpResponseMessage Search(JsonNode body)
    {
        var jql = body["jql"]!.GetValue<string>();
        var where = jql.Split(" ORDER BY ")[0];
        var clauses = where.Split(" AND ").Select(c => Clause().Match(c)).ToList();
        if (clauses.Any(c => !c.Success))
            return Error(400, $"the fake does not read this JQL: {jql}");
        var max = body["maxResults"]?.GetValue<int>() ?? 50;
        var indexed = Issues.Take(Math.Max(0, Issues.Count - Unlisted)).Where(i => !Unindexed.Contains(i.Key));
        var found = indexed.Where(i => clauses.All(c =>
        {
            var value = Regex.Unescape(c.Groups[2].Value);
            return c.Groups[1].Value switch
            {
                "project" => i.Project == value,
                "labels" => i.Labels.Contains(value),
                _ => i.Parent == value,
            };
        })).Reverse().Take(max).Select(i => (JsonNode)View(i)).ToArray();
        return Json(200, new JsonObject { ["issues"] = new JsonArray(found), ["isLast"] = true });
    }

    HttpResponseMessage Create(JsonNode body)
    {
        var fields = body["fields"]!;
        var project = fields["project"]!["key"]!.GetValue<string>();
        var type = fields["issuetype"]!["name"]!.GetValue<string>();
        var parent = fields["parent"]?["key"]?.GetValue<string>();
        if (parent is not null && !Issues.Any(i => i.Key == parent))
            return Json(400, new JsonObject { ["errors"] = new JsonObject { ["parent"] = "Could not find issue by id or key." } });
        if (parent is not null && (Issue(parent).Type == "Epic") == (type == "Subtask"))
            return Json(400, new JsonObject { ["errors"] = new JsonObject { ["issuetype"] = $"A {type} cannot have parent {parent}." } });
        var number = next[project] = next.GetValueOrDefault(project) + 1;
        var issue = new FakeIssue
        {
            Key = $"{project}-{number}",
            Project = project,
            Summary = fields["summary"]!.GetValue<string>(),
            Description = fields["description"]?.DeepClone(),
            Labels = (fields["labels"] as JsonArray ?? []).Select(l => l!.GetValue<string>()).ToList(),
            Parent = parent,
            Type = type,
        };
        Issues.Add(issue);
        return Json(201, new JsonObject { ["id"] = (++ids).ToString(System.Globalization.CultureInfo.InvariantCulture), ["key"] = issue.Key });
    }

    HttpResponseMessage Update(FakeIssue issue, JsonNode body)
    {
        var fields = body["fields"]!;
        if (fields["summary"] is { } summary)
            issue.Summary = summary.GetValue<string>();
        if (fields["description"] is { } description)
            issue.Description = description.DeepClone();
        if (fields["labels"] is JsonArray labels)
            issue.Labels = labels.Select(l => l!.GetValue<string>()).ToList();
        if (fields["parent"]?["key"]?.GetValue<string>() is { } parent)
            issue.Parent = parent;
        return Json(204);
    }

    HttpResponseMessage Transition(FakeIssue issue, JsonNode body)
    {
        var id = int.Parse(body["transition"]!["id"]!.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture);
        issue.Status = Statuses.Where(s => s != issue.Status).ElementAt(id - 11);
        return Json(204);
    }

    HttpResponseMessage Link(JsonNode body)
    {
        // As Jira does: the inward issue is the subject of the outward description ("inwardIssue blocks outwardIssue").
        var source = body["inwardIssue"]!["key"]!.GetValue<string>();
        var target = body["outwardIssue"]!["key"]!.GetValue<string>();
        if (Issues.FirstOrDefault(i => i.Key == source) is not { } from || Issues.FirstOrDefault(i => i.Key == target) is not { } to)
            return Error(404, "Issue does not exist.");
        var link = (body["type"]!["name"]!.GetValue<string>(), source, target);
        from.Links.Add(link);
        to.Links.Add(link);
        return Json(201);
    }

    HttpResponseMessage Comments(FakeIssue issue, string query) =>
        Json(200, new JsonObject
        {
            ["total"] = issue.Comments.Count,
            ["comments"] = new JsonArray(issue.Comments.Select(c =>
            {
                var comment = new JsonObject { ["id"] = c.Id, ["body"] = c.Body.DeepClone() };
                if (query.Contains("expand=properties", StringComparison.Ordinal))
                    comment["properties"] = c.Key is null ? new JsonArray() : new JsonArray(new JsonObject { ["key"] = JiraTracker.CommentProperty, ["value"] = new JsonObject { ["key"] = c.Key } });
                return (JsonNode)comment;
            }).ToArray()),
        });

    HttpResponseMessage Comment(FakeIssue issue, JsonNode body)
    {
        var key = (body["properties"] as JsonArray ?? []).FirstOrDefault(p => p?["key"]?.GetValue<string>() == JiraTracker.CommentProperty)?["value"]?["key"]?.GetValue<string>();
        var id = (++ids).ToString(System.Globalization.CultureInfo.InvariantCulture);
        issue.Comments.Add((id, body["body"]!.DeepClone(), key));
        return Json(201, new JsonObject { ["id"] = id });
    }
}
