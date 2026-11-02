using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>A GitHub answer: its status, its body (parsed when JSON), and where a redirect points.</summary>
public sealed record GitHubResponse(HttpStatusCode Status, string Text, Uri? Location)
{
    public int Code => (int)Status;

    public JsonNode? Json => Text.Length == 0 ? null : JsonNode.Parse(Text);
}

/// <summary>
/// The REST and GraphQL client the GitHub code host and Actions providers share. One pooled connection handler for
/// the process (DR18: connections are reused; DR20: the platform trust store). Tokens come from the credential broker
/// only, scoped to one repository and the permissions the caller names, and are cached until five minutes before
/// they end. Error statuses map to docs/06 section 1.1; each provider method maps the success statuses it documents
/// and treats any other as <c>unexpected_response</c>.
/// </summary>
public sealed class GitHubApi(Func<ICredentialBroker?> broker, HttpMessageHandler? handler = null, string apiBase = "https://api.github.com")
{
    static readonly HttpMessageHandler Shared = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false };

    readonly HttpClient http = new(handler ?? Shared, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };

    // docs/06 section 1.2: an artifact get may take 5 minutes.
    readonly HttpClient downloads = new(handler ?? Shared, disposeHandler: false) { Timeout = TimeSpan.FromMinutes(5) };
    readonly Dictionary<string, Credential> tokens = [];
    readonly SemaphoreSlim gate = new(1, 1);

    public string ApiBase { get; } = apiBase.TrimEnd('/');

    public ICredentialBroker? Broker => broker();

    public async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) =>
        broker() is { } b ? await b.CheckAsync(cancellationToken) : ProviderResult<Unit>.Failure("unauthenticated", "no credential broker is configured for GitHub");

    async Task<ProviderResult<string>> TokenAsync(RepoRef repo, IReadOnlyDictionary<string, string> permissions, CancellationToken cancellationToken)
    {
        if (broker() is not { } b)
            return ProviderResult<string>.Failure("unauthenticated", "no credential broker is configured for GitHub");
        var scope = new CredentialScope([repo.FullName], permissions);
        var cacheKey = scope.ToString();
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (tokens.TryGetValue(cacheKey, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5))
                return ProviderResult<string>.Success(cached.Secret);
            var issued = await b.IssueAsync(Actor.Engine, "github", scope, TimeSpan.FromHours(1), cancellationToken);
            if (!issued.Ok)
                return new ProviderResult<string>(null, issued.Error);
            tokens[cacheKey] = issued.Value!;
            return ProviderResult<string>.Success(issued.Value!.Secret);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Sends a request for <paramref name="repo"/> with a token holding <paramref name="permissions"/>. Error statuses
    /// come back as their classified error; every other status, success or redirect, comes back as the response.
    /// </summary>
    public async Task<ProviderResult<GitHubResponse>> SendAsync(RepoRef repo, IReadOnlyDictionary<string, string> permissions, HttpMethod method, string path,
        object? body, CancellationToken cancellationToken, string accept = "application/vnd.github+json")
    {
        var token = await TokenAsync(repo, permissions, cancellationToken);
        if (!token.Ok)
            return new ProviderResult<GitHubResponse>(null, token.Error);
        using var request = new HttpRequestMessage(method, path.StartsWith("https://", StringComparison.Ordinal) ? path : ApiBase + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("feature-flow", "0.1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if ((int)response.StatusCode >= 400)
                return new ProviderResult<GitHubResponse>(null, Classify(response, text, method, path));
            return ProviderResult<GitHubResponse>.Success(new GitHubResponse(response.StatusCode, text, response.Headers.Location));
        }
        catch (HttpRequestException e)
        {
            return ProviderResult<GitHubResponse>.Failure("unreachable", $"GitHub cannot be reached: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderResult<GitHubResponse>.Failure("timeout", $"GitHub did not answer {method} {path} in time");
        }
    }

    /// <summary>
    /// Downloads what a redirect points to (logs and artifacts live in blob storage), without GitHub's token, streamed
    /// into a temporary file that is deleted when the stream is disposed. More than <paramref name="maxBytes"/> is
    /// refused with <c>validation_failed</c> (review finding 6).
    /// </summary>
    public async Task<ProviderResult<Stream>> DownloadAsync(Uri location, long maxBytes, CancellationToken cancellationToken)
    {
        var path = Path.Combine(Path.GetTempPath(), $"ff-download-{Guid.NewGuid():N}");
        var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            using var response = await downloads.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                await file.DisposeAsync();
                return ProviderResult<Stream>.Failure((int)response.StatusCode >= 500 ? "server_error" : "not_found", $"the download answered {(int)response.StatusCode}");
            }

            if (response.Content.Headers.ContentLength > maxBytes || !await CopyCappedAsync(await response.Content.ReadAsStreamAsync(cancellationToken), file, maxBytes, cancellationToken))
            {
                await file.DisposeAsync();
                return ProviderResult<Stream>.Failure("validation_failed", $"the download is larger than {maxBytes} bytes");
            }

            file.Position = 0;
            return ProviderResult<Stream>.Success(file);
        }
        catch (HttpRequestException e)
        {
            await file.DisposeAsync();
            return ProviderResult<Stream>.Failure("unreachable", $"the download cannot be reached: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await file.DisposeAsync();
            return ProviderResult<Stream>.Failure("timeout", "the download did not answer in time");
        }
    }

    /// <summary>Copies at most <paramref name="maxBytes"/>; false when the source holds more.</summary>
    public static async Task<bool> CopyCappedAsync(Stream source, Stream target, long maxBytes, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total += read;
            if (total > maxBytes)
                return false;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return true;
    }

    /// <summary>Every page of a list, following <c>Link: rel="next"</c>, up to <paramref name="maxPages"/>. <paramref name="items"/> picks the list out of a page.</summary>
    public async Task<ProviderResult<List<JsonNode>>> ListAsync(RepoRef repo, IReadOnlyDictionary<string, string> permissions, string path,
        Func<JsonNode, JsonArray?> items, CancellationToken cancellationToken, int maxPages = 10)
    {
        var all = new List<JsonNode>();
        var page = 1;
        var separator = path.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        while (true)
        {
            var response = await SendAsync(repo, permissions, HttpMethod.Get, $"{path}{separator}per_page=100&page={page}", null, cancellationToken);
            if (!response.Ok)
                return new ProviderResult<List<JsonNode>>(null, response.Error);
            if (response.Value!.Code != 200)
                return Unexpected<List<JsonNode>>(response.Value, $"GET {path}");
            var list = items(response.Value.Json!) ?? [];
            all.AddRange(list.Where(i => i is not null)!);
            if (list.Count < 100 || page >= maxPages)
                return ProviderResult<List<JsonNode>>.Success(all);
            page++;
        }
    }

    /// <summary>A GraphQL request. GraphQL reports errors in a 200's body; they are classified by their type.</summary>
    public async Task<ProviderResult<JsonNode>> GraphQlAsync(RepoRef repo, IReadOnlyDictionary<string, string> permissions, string query, object variables,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(repo, permissions, HttpMethod.Post, "/graphql", new { query, variables }, cancellationToken);
        if (!response.Ok)
            return new ProviderResult<JsonNode>(null, response.Error);
        if (response.Value!.Code != 200)
            return Unexpected<JsonNode>(response.Value, "POST /graphql");
        var json = response.Value.Json!;
        if (json["errors"] is JsonArray { Count: > 0 } errors)
        {
            var type = errors[0]?["type"]?.GetValue<string>();
            var message = errors[0]?["message"]?.GetValue<string>() ?? "GraphQL error";
            var reason = type switch
            {
                "NOT_FOUND" => "not_found",
                "FORBIDDEN" => "forbidden",
                "RATE_LIMITED" => "rate_limited",
                _ when message.Contains("conflict", StringComparison.OrdinalIgnoreCase) => "conflict",
                _ => "validation_failed",
            };
            return ProviderResult<JsonNode>.Failure(reason, $"GitHub refused the request: {message}");
        }

        return ProviderResult<JsonNode>.Success(json["data"]!);
    }

    public static ProviderResult<T> Unexpected<T>(GitHubResponse response, string what) =>
        ProviderResult<T>.Failure("unexpected_response", $"GitHub answered {response.Code} to {what}, which is not a documented success");

    /// <summary>docs/06 section 1.1. GitHub's message is kept (it names the fault, never a credential); the request body is not quoted.</summary>
    static ProviderError Classify(HttpResponseMessage response, string text, HttpMethod method, string path)
    {
        var message = "";
        try
        {
            message = JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? "";
        }
        catch (JsonException)
        {
        }

        var what = $"GitHub answered {(int)response.StatusCode} to {method} {Strip(path)}{(message.Length > 0 ? ": " + message : "")}";
        var retryAfter = response.Headers.RetryAfter?.Delta;
        var rateLimited = response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0"
                          || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase);
        return (int)response.StatusCode switch
        {
            401 => new ProviderError("unauthenticated", what),
            403 or 429 when rateLimited || (int)response.StatusCode == 429 => new ProviderError("rate_limited", what, retryAfter ?? ResetDelay(response)),
            // The App's installation lacks a permission: a configuration fault for a person, never retried (review finding 3).
            403 when message.Contains("Resource not accessible by", StringComparison.Ordinal) =>
                new ProviderError("rejected", $"configuration fault: the GitHub App has no permission for this. {what}"),
            403 => new ProviderError("forbidden", what),
            404 or 410 => new ProviderError("not_found", what),
            409 => new ProviderError("conflict", what),
            400 or 422 => new ProviderError("validation_failed", what),
            >= 500 => new ProviderError("server_error", what),
            _ => new ProviderError("unexpected_response", what),
        };
    }

    static TimeSpan? ResetDelay(HttpResponseMessage response) =>
        response.Headers.TryGetValues("x-ratelimit-reset", out var reset) && long.TryParse(reset.FirstOrDefault(), out var epoch)
            ? TimeSpan.FromSeconds(Math.Max(1, epoch - DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            : null;

    static string Strip(string path) => path.Split('?')[0];

    public static string Escape(string segment) => Uri.EscapeDataString(segment);

    /// <summary>A path whose segments are escaped one by one, so a branch or file name keeps its slashes.</summary>
    public static string EscapePath(string path) => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public static string Sha256(byte[] content) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(content));

    /// <summary>The permissions the code host provider asks for.</summary>
    public static readonly IReadOnlyDictionary<string, string> CodeHostPermissions = new Dictionary<string, string>
    {
        ["contents"] = "write",
        ["pull_requests"] = "write",
        ["checks"] = "read",
        ["actions"] = "read",
    };

    /// <summary>The permissions the Actions provider asks for: dispatching needs <c>actions: write</c>; the trigger tag needs <c>contents: write</c>.</summary>
    public static readonly IReadOnlyDictionary<string, string> CiPermissions = new Dictionary<string, string>
    {
        ["contents"] = "write",
        ["actions"] = "write",
    };
}
