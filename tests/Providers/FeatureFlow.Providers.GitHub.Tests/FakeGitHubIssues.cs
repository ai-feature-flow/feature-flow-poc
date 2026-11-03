using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>
/// GitHub Issues in memory, as its REST API answers the calls the tracker provider makes: issues (with pull requests
/// sharing their numbers, as on GitHub), labels created on first use, comments and sub-issues. Repository <c>o/r</c>.
/// </summary>
public sealed partial class FakeGitHubIssues : HttpMessageHandler
{
    const string Base = "https://api.example";

    sealed class Item
    {
        public required int Number;
        public required long Id;
        public string Title = "";
        public string Body = "";
        public string State = "open";
        public List<string> Labels = [];
        public int? Parent;
        public bool PullRequest;
        public readonly List<(long Id, string Body)> Comments = [];
    }

    readonly Lock gate = new();
    readonly List<Item> items = [];
    long ids = 1000;

    public FakeGitHubIssues() => items.Add(new Item { Number = 1, Id = ++ids, Title = "A pull request", PullRequest = true });

    public List<(HttpMethod Method, string Path)> Calls { get; } = [];

    /// <summary>How many of the newest items the lists leave out, as GitHub's lists lag a create by seconds; reading one by number does not lag.</summary>
    public int Unlisted { get; set; }

    IEnumerable<Item> Listed => items.OrderByDescending(i => i.Number).Skip(Unlisted);

    /// <summary>The bodies of an issue's comments, as a person sees them on GitHub (the marker is an HTML comment).</summary>
    public IReadOnlyList<string> Comments(int number)
    {
        lock (gate)
            return items.Single(i => i.Number == number).Comments.Select(c => c.Body).ToList();
    }

    public (string State, IReadOnlyList<string> Labels) Read(int number)
    {
        lock (gate)
        {
            var item = items.Single(i => i.Number == number);
            return (item.State, item.Labels.ToList());
        }
    }

    public GitHubApi Api() => new(() => new FakeGitHub.Broker(), this, Base) { FreshTokenDelay = FakeGitHub.FreshTokenDelay };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        var query = request.RequestUri.Query;
        var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
        lock (gate)
        {
            Calls.Add((request.Method, path + query));
            if (Page(query) > 1)
                return Json(200, new JsonArray());
            var m = Route().Match(path);
            if (!m.Success)
                throw new InvalidOperationException($"no route for {request.Method} {path}");
            var number = m.Groups["n"].Success ? int.Parse(m.Groups["n"].Value) : (int?)null;
            var tail = m.Groups["tail"].Value;
            var item = number is { } n ? items.FirstOrDefault(i => i.Number == n) : null;
            if (number is not null && item is null)
                return Json(404, new JsonObject { ["message"] = "Not Found" });

            switch (request.Method.Method, number is null, tail)
            {
                case ("GET", true, ""):
                    var label = Regex.Match(query, "labels=([^&]*)") is { Success: true } l ? Uri.UnescapeDataString(l.Groups[1].Value) : null;
                    return Json(200, new JsonArray(Listed.Where(i => label is null || i.Labels.Contains(label)).Select(View).ToArray()));
                case ("POST", true, ""):
                    var created = new Item { Number = items.Max(i => i.Number) + 1, Id = ++ids, Title = body!["title"]!.GetValue<string>(), Body = body["body"]?.GetValue<string>() ?? "" };
                    created.Labels = (body["labels"] as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToList();
                    items.Add(created);
                    return Json(201, View(created));
                case ("GET", false, ""):
                    return Json(200, View(item!));
                case ("PATCH", false, ""):
                    if (body!["title"] is { } t)
                        item!.Title = t.GetValue<string>();
                    if (body["body"] is { } b)
                        item!.Body = b.GetValue<string>();
                    if (body["state"] is { } s)
                        item!.State = s.GetValue<string>();
                    if (body["labels"] is JsonArray labels)
                        item!.Labels = labels.Select(x => x!.GetValue<string>()).ToList();
                    return Json(200, View(item!));
                case ("GET", false, "/comments"):
                    return Json(200, new JsonArray(item!.Comments.Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["body"] = c.Body }).ToArray()));
                case ("POST", false, "/comments"):
                    var comment = (++ids, body!["body"]!.GetValue<string>());
                    item!.Comments.Add(comment);
                    return Json(201, new JsonObject { ["id"] = comment.Item1, ["body"] = comment.Item2 });
                case ("GET", false, "/sub_issues"):
                    return Json(200, new JsonArray(Listed.Where(i => i.Parent == number).Select(View).ToArray()));
                case ("POST", false, "/sub_issues"):
                    var child = items.FirstOrDefault(i => i.Id == body!["sub_issue_id"]!.GetValue<long>());
                    if (child is null)
                        return Json(422, new JsonObject { ["message"] = "Validation Failed" });
                    child.Parent = number;
                    return Json(201, View(item!));
                default:
                    throw new InvalidOperationException($"no route for {request.Method} {path}");
            }
        }
    }

    static int Page(string query) => Regex.Match(query, "[?&]page=(\\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 1;

    JsonNode View(Item i)
    {
        var json = new JsonObject
        {
            ["id"] = i.Id,
            ["number"] = i.Number,
            ["title"] = i.Title,
            ["body"] = i.Body,
            ["state"] = i.State,
            ["labels"] = new JsonArray(i.Labels.Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()),
            ["repository_url"] = $"{Base}/repos/o/r",
            ["html_url"] = $"https://github.example/o/r/issues/{i.Number}",
            ["parent_issue_url"] = i.Parent is { } p ? $"{Base}/repos/o/r/issues/{p}" : null,
        };
        if (i.PullRequest)
            json["pull_request"] = new JsonObject { ["url"] = $"{Base}/repos/o/r/pulls/{i.Number}" };
        return json;
    }

    static HttpResponseMessage Json(int status, JsonNode body) =>
        new((HttpStatusCode)status) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    [GeneratedRegex("^/repos/o/r/issues(?:/(?<n>\\d+))?(?<tail>/comments|/sub_issues)?$")]
    private static partial Regex Route();
}
