using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub.Tests;

/// <summary>
/// The fixture repository <c>ai-feature-flow/feature-flow-example</c> through the GitHub App, for the Live contract
/// suites. Each test works under its own prefix <c>ffc/&lt;stamp&gt;-&lt;id&gt;/</c>, from an orphan base holding only
/// <c>README.md</c>, so the fixture's <c>main</c> is never written, no workflow runs on the test's pushes, and runs
/// never collide. <see cref="CleanUpAsync"/> closes the pull requests and deletes the branches and tags it made.
/// Run with <c>FF_SECRETS_FILE</c> naming the App's settings; nothing of them is printed.
/// </summary>
public sealed class LiveFixture
{
    public static readonly RepoRef Repo = new("ai-feature-flow/feature-flow-example");

    static readonly IReadOnlyDictionary<string, string> Permissions = new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["actions"] = "write" };

    readonly List<string> runs = [];

    public LiveFixture()
    {
        var file = Environment.GetEnvironmentVariable("FF_SECRETS_FILE");
        Assert.True(file is not null && File.Exists(file), "the Live tests need FF_SECRETS_FILE naming the GitHub App's settings");
        var settings = JsonNode.Parse(File.ReadAllText(file!))!;
        AppId = settings["github-app-id"]!.ToString();
        KeyPath = Path.GetFullPath(settings["github-app-key"]!.ToString().Replace("~", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.Ordinal));
        Broker = new AppCredentialBroker(AppId, settings["github-installation-id"]!.ToString(),
            async ct => ProviderResult<string>.Success(await File.ReadAllTextAsync(KeyPath, ct)), [Repo.FullName]);
        Api = new GitHubApi(() => Broker);
        Prefix = $"ffc/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
    }

    string AppId { get; }

    string KeyPath { get; }

    public AppCredentialBroker Broker { get; }

    public GitHubApi Api { get; }

    /// <summary>The test's own branch prefix.</summary>
    public string Prefix { get; }

    /// <summary>The test's name for a branch: the scratch base for <c>main</c>, otherwise the name under the prefix.</summary>
    public string Branch(string name) => $"{Prefix}/{name}";

    public string Unbranch(string name) => name.StartsWith(Prefix + "/", StringComparison.Ordinal) ? name[(Prefix.Length + 1)..] : name;

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    async Task<JsonNode?> Call(HttpMethod method, string path, object? body, params int[] ok)
    {
        var response = (await Api.SendAsync(Repo, Permissions, method, $"/repos/{Repo.FullName}{path}", body, Ct)).Unwrap();
        Assert.True(ok.Contains(response.Code), $"{method} {path} answered {response.Code}");
        return response.Json;
    }

    public async Task<string?> ResolveAsync(string branch)
    {
        var response = await Api.SendAsync(Repo, Permissions, HttpMethod.Get, $"/repos/{Repo.FullName}/git/ref/heads/{GitHubApi.EscapePath(branch)}", null, Ct);
        return response.Ok ? response.Value!.Json!["object"]!["sha"]!.GetValue<string>() : null;
    }

    /// <summary>Commits <paramref name="changes"/> (null deletes) on top of <paramref name="parent"/>, or as an orphan, and returns the commit.</summary>
    public async Task<string> CommitAsync(string? parent, IReadOnlyDictionary<string, string?> changes, string message = "someone else's change")
    {
        var tree = new JsonArray(changes.Select(c => (JsonNode)new JsonObject
        {
            ["path"] = c.Key, ["mode"] = "100644", ["type"] = "blob",
            [c.Value is null ? "sha" : "content"] = c.Value is null ? null : JsonValue.Create(c.Value),
        }).ToArray());
        var treeBody = new JsonObject { ["tree"] = tree };
        if (parent is not null)
            treeBody["base_tree"] = (await Call(HttpMethod.Get, $"/git/commits/{parent}", null, 200))!["tree"]!["sha"]!.GetValue<string>();
        var newTree = (await Call(HttpMethod.Post, "/git/trees", treeBody.ToJsonString(), 201))!["sha"]!.GetValue<string>();
        var commit = new JsonObject { ["message"] = message, ["tree"] = newTree, ["parents"] = new JsonArray(parent is null ? [] : [JsonValue.Create(parent)]) };
        return (await Call(HttpMethod.Post, "/git/commits", commit.ToJsonString(), 201))!["sha"]!.GetValue<string>();
    }

    /// <summary>Moves (or creates) a branch to a commit, whatever it was before.</summary>
    public async Task SetBranchAsync(string branch, string revision)
    {
        if (await ResolveAsync(branch) is null)
            await Call(HttpMethod.Post, "/git/refs", new { @ref = "refs/heads/" + branch, sha = revision }, 201);
        else
            await Call(HttpMethod.Patch, $"/git/refs/heads/{GitHubApi.EscapePath(branch)}", new { sha = revision, force = true }, 200);
    }

    /// <summary>The scratch base: an orphan commit holding only <c>README.md</c>, at <c>&lt;prefix&gt;/main</c>.</summary>
    public async Task<string> CreateBaseAsync()
    {
        var root = await CommitAsync(null, new Dictionary<string, string?> { ["README.md"] = "# Contract fixture\n" }, "Contract base");
        await SetBranchAsync(Branch("main"), root);
        return root;
    }

    /// <summary>Merges a pull request as the App, which GitHub then reports as its merger, <c>&lt;app slug&gt;[bot]</c>.</summary>
    public async Task<string> MergeAsync(int number)
    {
        await Call(HttpMethod.Put, $"/pulls/{number}/merge", new { merge_method = "merge" }, 200);
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/app");
        request.Headers.Authorization = new("Bearer", AppJwt.Mint(AppId, await File.ReadAllTextAsync(KeyPath, Ct), DateTimeOffset.UtcNow));
        request.Headers.UserAgent.ParseAdd("feature-flow-tests");
        using var response = await http.SendAsync(request, Ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(Ct))!["slug"]!.GetValue<string>() + "[bot]";
    }

    public void ForgetRun(string id) => runs.Add(id);

    /// <summary>
    /// Closes every open pull request into or out of the prefix (reverts GitHub branched itself included), deletes their
    /// branches and every branch under the prefix, and cancels the CI runs the test started that are still going.
    /// </summary>
    public async Task CleanUpAsync()
    {
        var token = CancellationToken.None;
        async Task<GitHubResponse?> Try(HttpMethod method, string path, object? body = null)
        {
            var r = await Api.SendAsync(Repo, Permissions, method, $"/repos/{Repo.FullName}{path}", body, token);
            return r.Ok ? r.Value : null;
        }

        var branches = new HashSet<string>(StringComparer.Ordinal);
        foreach (var state in new[] { "open", "closed" })
            if (await Try(HttpMethod.Get, $"/pulls?state={state}&base={GitHubApi.Escape(Branch("main"))}&per_page=100") is { } list)
                foreach (var pr in list.Json as JsonArray ?? [])
                {
                    if (state == "open")
                        await Try(HttpMethod.Patch, $"/pulls/{pr!["number"]}", new { state = "closed" });
                    branches.Add(pr!["head"]!["ref"]!.GetValue<string>());
                }

        if (await Try(HttpMethod.Get, $"/git/matching-refs/heads/{Prefix}/") is { } refs)
            foreach (var r in refs.Json as JsonArray ?? [])
                branches.Add(r!["ref"]!.GetValue<string>()["refs/heads/".Length..]);
        foreach (var branch in branches.Where(b => b.StartsWith(Prefix, StringComparison.Ordinal) || b.StartsWith("revert-", StringComparison.Ordinal)))
            await Try(HttpMethod.Delete, $"/git/refs/heads/{GitHubApi.EscapePath(branch)}");

        foreach (var id in runs)
            if (await Try(HttpMethod.Get, $"/actions/runs/{id}") is { } run && run.Json?["status"]?.GetValue<string>() != "completed")
                await Try(HttpMethod.Post, $"/actions/runs/{id}/cancel");
    }

    /// <summary>A line for the results note, appended to the file <c>FF_LIVE_LOG</c> names. Never a credential.</summary>
    public static void Log(string line)
    {
        if (Environment.GetEnvironmentVariable("FF_LIVE_LOG") is { Length: > 0 } path)
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {line}\n");
    }

    public static string Json(object value) => JsonSerializer.Serialize(value);
}
