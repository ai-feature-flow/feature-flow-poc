using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Providers;

/// <summary>
/// The two GitHub REST calls the credential broker needs. Transport-neutral in
/// the sense that matters here: plain <see cref="HttpClient"/>, no GitHub SDK,
/// and the results come back as our own records.
/// </summary>
public class GitHubAppClient
{
    public const string DefaultApiBase = "https://api.github.com";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient http;
    private readonly string apiBase;

    public GitHubAppClient(HttpClient? http = null, string apiBase = DefaultApiBase)
    {
        this.http = http ?? new HttpClient();
        this.apiBase = apiBase.TrimEnd('/');
    }

    /// <summary>
    /// GET /app with the App JWT. This is the proof that the JWT was accepted:
    /// the endpoint is only reachable as the App itself.
    /// </summary>
    public virtual async Task<ProviderResult<AppInfo>> GetAppAsync(string jwt, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{apiBase}/app");
        AddHeaders(request, jwt);

        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<AppInfo>.Fail(response.Error);

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;

        return ProviderResult<AppInfo>.Ok(new AppInfo(
            Id: root.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
            Name: root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Slug: root.TryGetProperty("slug", out var slug) ? slug.GetString() : null,
            Owner: root.TryGetProperty("owner", out var owner) &&
                   owner.TryGetProperty("login", out var login)
                ? login.GetString()
                : null,
            HtmlUrl: root.TryGetProperty("html_url", out var url) ? url.GetString() : null));
    }

    /// <summary>
    /// POST /app/installations/{id}/access_tokens. The request carries the
    /// narrowed scope, so the token GitHub mints is already limited to one
    /// repository and the requested permissions.
    /// </summary>
    public virtual async Task<ProviderResult<InstallationToken>> CreateInstallationTokenAsync(
        string jwt,
        string installationId,
        InstallationTokenRequest request,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object>();
        if (request.Repositories.Count > 0)
            payload["repositories"] = request.Repositories;
        if (request.Permissions.Count > 0)
            payload["permissions"] = request.Permissions;

        using var httpRequest = new HttpRequestMessage(
            HttpMethod.Post,
            $"{apiBase}/app/installations/{Uri.EscapeDataString(installationId)}/access_tokens")
        {
            Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json"),
        };

        AddHeaders(httpRequest, jwt);

        var response = await SendAsync(httpRequest, ct).ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<InstallationToken>.Fail(response.Error);

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("token", out var token) ||
            !root.TryGetProperty("expires_at", out var expiresAt))
        {
            return ProviderResult<InstallationToken>.Infrastructure("server_error");
        }

        var permissions = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("permissions", out var perms) &&
            perms.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in perms.EnumerateObject())
                permissions[property.Name] = property.Value.GetString() ?? "";
        }

        var repositories = new List<string>();
        if (root.TryGetProperty("repositories", out var repos) &&
            repos.ValueKind == JsonValueKind.Array)
        {
            foreach (var repo in repos.EnumerateArray())
            {
                if (repo.TryGetProperty("full_name", out var fullName))
                    repositories.Add(fullName.GetString() ?? "");
            }
        }

        var selection = root.TryGetProperty("repository_selection", out var sel)
            ? sel.GetString()
            : null;

        return ProviderResult<InstallationToken>.Ok(new InstallationToken(
            Token: token.GetString() ?? "",
            ExpiresAt: ParseTimestamp(expiresAt.GetString()),
            Permissions: permissions,
            Repositories: repositories,
            RepositorySelection: selection));
    }

    private static void AddHeaders(HttpRequestMessage request, string jwt)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("feature-flow-spike2", "0.1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    private async Task<RawResponse> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            return response.IsSuccessStatusCode
                ? RawResponse.Ok(body)
                : RawResponse.Fail(MapError(response.StatusCode, response.Headers, body));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            return RawResponse.Fail(new ProviderError(ErrorClass.Infrastructure, "timeout"));
        }
        catch (HttpRequestException)
        {
            return RawResponse.Fail(new ProviderError(ErrorClass.Infrastructure, "unreachable"));
        }
    }

    /// <summary>
    /// Maps an HTTP status to the common error codes in docs/05 section 1.3.
    /// Note the class: a 403 from the remote is Infrastructure there, because it
    /// is almost always a configuration fault. The broker's own scope refusal is
    /// Work — see <see cref="GitHubAppCredentialBroker"/>.
    /// </summary>
    private static ProviderError MapError(
        HttpStatusCode status,
        System.Net.Http.Headers.HttpResponseHeaders headers,
        string body)
    {
        switch (status)
        {
            case HttpStatusCode.Unauthorized:
                return new ProviderError(ErrorClass.Infrastructure, "unauthenticated");

            case HttpStatusCode.Forbidden:
                return new ProviderError(ErrorClass.Infrastructure, "forbidden", RetryAfterFrom(headers));

            case HttpStatusCode.NotFound:
                return new ProviderError(ErrorClass.Work, "not_found");

            case HttpStatusCode.Conflict:
                return new ProviderError(ErrorClass.Work, "conflict");

            case HttpStatusCode.UnprocessableEntity:
            case HttpStatusCode.BadRequest:
                return new ProviderError(ErrorClass.Work, "validation_failed");

            case (HttpStatusCode)429:
                return new ProviderError(ErrorClass.Infrastructure, "rate_limited", RetryAfterFrom(headers));

            default:
                return status >= HttpStatusCode.InternalServerError
                    ? new ProviderError(ErrorClass.Infrastructure, "server_error")
                    : new ProviderError(ErrorClass.Infrastructure, "internal_error");
        }
    }

    private static TimeSpan? RetryAfterFrom(System.Net.Http.Headers.HttpResponseHeaders headers)
    {
        if (headers.TryGetValues("retry-after", out var values))
        {
            var text = string.Join(",", values);
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(seconds);
        }

        if (headers.TryGetValues("x-ratelimit-reset", out var resetValues))
        {
            var text = string.Join(",", resetValues);
            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch))
            {
                var wait = DateTimeOffset.FromUnixTimeSeconds(epoch) - DateTimeOffset.UtcNow;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
        }

        return null;
    }

    private static DateTimeOffset ParseTimestamp(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return DateTimeOffset.UtcNow;

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : DateTimeOffset.UtcNow;
    }

    private readonly struct RawResponse
    {
        private RawResponse(ProviderError? error, string body)
        {
            Error = error;
            Body = body;
        }

        public ProviderError? Error { get; }

        public string Body { get; }

        public static RawResponse Ok(string body) => new(null, body);

        public static RawResponse Fail(ProviderError error) => new(error, "");
    }
}

/// <summary>What GET /app returns that we care about.</summary>
public sealed record AppInfo(
    long Id,
    string Name,
    string? Slug = null,
    string? Owner = null,
    string? HtmlUrl = null);

/// <summary>What POST /app/installations/{id}/access_tokens returns.</summary>
public sealed record InstallationToken(
    string Token,
    DateTimeOffset ExpiresAt,
    IReadOnlyDictionary<string, string> Permissions,
    IReadOnlyList<string> Repositories,
    string? RepositorySelection);

/// <summary>
/// The narrowed scope sent to GitHub. Both fields are optional in the API; when
/// omitted the token gets the installation's full grant, which is exactly what
/// R6 forbids, so the broker always sends at least the repository.
/// </summary>
public sealed record InstallationTokenRequest(
    IReadOnlyList<string> Repositories,
    IReadOnlyDictionary<string, string> Permissions);
