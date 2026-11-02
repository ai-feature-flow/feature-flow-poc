using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// The GitHub App credential broker (docs/06 section 3.13), ported from Spike 2. The App's private key is read
/// through the secrets provider or from a key file outside the repository; it is minted into a short JWT and
/// exchanged for an installation token scoped to the requested repositories and permissions, for at most an
/// hour.
/// <para>
/// It refuses, never narrows: a request for a repository it may not grant, a permission above the actor's
/// ceiling, or a lifetime over an hour is refused before any call to GitHub, as a work error (<c>rejected</c>),
/// so it is never retried. The engine may
/// read and write contents and pull requests, and dispatch workflows (actions); an agent step may only read contents (F5). No actor is ever
/// granted <c>administration</c>, so no credential can change branch protection.
/// </para>
/// </summary>
public sealed class AppCredentialBroker(
    string appId,
    string installationId,
    Func<CancellationToken, Task<ProviderResult<string>>> privateKey,
    IReadOnlyList<string> repositories,
    HttpMessageHandler? handler = null,
    string apiBase = "https://api.github.com",
    Func<DateTimeOffset>? clock = null) : ICredentialBroker
{
    public const string TargetSystem = "github";

    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(1);

    /// <summary>The highest access each actor kind may hold, by permission. Anything not listed is never granted.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Ceilings = new Dictionary<string, IReadOnlyDictionary<string, string>>
    {
        ["engine"] = new Dictionary<string, string> { ["contents"] = "write", ["pull_requests"] = "write", ["metadata"] = "read", ["checks"] = "read", ["actions"] = "write" },
        ["agent"] = new Dictionary<string, string> { ["contents"] = "read", ["metadata"] = "read" },
    };

    // DR20: the default handler validates against the platform's trust store.
    readonly HttpClient http = new(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { Timeout = TimeSpan.FromSeconds(15) };
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);

    public ProviderDescription Describe() => new("credential_broker", "github_app", "0.1.0", new NoCapabilities());

    public async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        var jwt = await JwtAsync(cancellationToken);
        if (!jwt.Ok)
            return new ProviderResult<Unit>(default, jwt.Error);
        var app = await SendAsync(HttpMethod.Get, "/app", jwt.Value!, null, cancellationToken);
        return app.Ok ? ProviderResult<Unit>.Success(default) : new ProviderResult<Unit>(default, app.Error);
    }

    /// <summary>The reason a request is refused, or null when it may be granted as asked.</summary>
    public string? Refusal(Actor actor, string targetSystem, CredentialScope scope, TimeSpan ttl)
    {
        if (!string.Equals(targetSystem, TargetSystem, StringComparison.OrdinalIgnoreCase))
            return $"this broker grants {TargetSystem} credentials, not {targetSystem}";
        if (ttl <= TimeSpan.Zero || ttl > MaxTtl)
            return $"a lifetime of {ttl.TotalMinutes:F0} minutes cannot be granted; at most {MaxTtl.TotalMinutes:F0}";
        if (scope.Repositories.Count == 0)
            return "a credential must name its repositories";
        if (scope.Repositories.FirstOrDefault(r => !repositories.Contains(r, StringComparer.OrdinalIgnoreCase)) is { } other)
            return $"{other} is not a repository this broker may grant";
        if (!Ceilings.TryGetValue(actor.Kind, out var ceiling))
            return $"no credential is granted to a {actor.Kind}";
        foreach (var (permission, access) in scope.Permissions)
            if (!ceiling.TryGetValue(permission, out var allowed) || Level(access) > Level(allowed))
                return $"{permission}:{access} cannot be granted to a {actor.Kind}";
        return null;
    }

    static int Level(string access) => access switch { "read" => 1, "write" => 2, _ => 3 };

    public async Task<ProviderResult<Credential>> IssueAsync(Actor actor, string targetSystem, CredentialScope scope, TimeSpan ttl, CancellationToken cancellationToken)
    {
        if (Refusal(actor, targetSystem, scope, ttl) is { } refusal)
            return new ProviderResult<Credential>(null, new ProviderError("rejected", refusal));
        var jwt = await JwtAsync(cancellationToken);
        if (!jwt.Ok)
            return new ProviderResult<Credential>(null, jwt.Error);
        var permissions = scope.Permissions.ToDictionary(p => p.Key, p => p.Value);
        permissions.TryAdd("metadata", "read");
        var body = JsonSerializer.Serialize(new
        {
            repositories = scope.Repositories.Select(r => r[(r.IndexOf('/') + 1)..]).ToArray(),
            permissions,
        });
        var response = await SendAsync(HttpMethod.Post, $"/app/installations/{Uri.EscapeDataString(installationId)}/access_tokens", jwt.Value!, body, cancellationToken);
        if (!response.Ok)
            return new ProviderResult<Credential>(null, response.Error);
        using var json = JsonDocument.Parse(response.Value!);
        if (!json.RootElement.TryGetProperty("token", out var token) || !json.RootElement.TryGetProperty("expires_at", out var expires))
            return ProviderResult<Credential>.Failure("unexpected_response", "GitHub's token response has no token or expiry");
        var expiresAt = DateTimeOffset.Parse(expires.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
        var limit = now() + ttl;
        return ProviderResult<Credential>.Success(new Credential(TargetSystem, scope, token.GetString()!, expiresAt < limit ? expiresAt : limit, actor));
    }

    public Task<ProviderResult<Credential>> RefreshAsync(Credential credential, CancellationToken cancellationToken) =>
        IssueAsync(credential.Actor, credential.TargetSystem, credential.Scope, MaxTtl, cancellationToken);

    async Task<ProviderResult<string>> JwtAsync(CancellationToken cancellationToken)
    {
        var key = await privateKey(cancellationToken);
        if (!key.Ok)
            return key;
        try
        {
            return ProviderResult<string>.Success(AppJwt.Mint(appId, key.Value!, now()));
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            return ProviderResult<string>.Failure("unauthenticated", "the GitHub App key does not read: " + e.GetType().Name);
        }
    }

    async Task<ProviderResult<string>> SendAsync(HttpMethod method, string path, string jwt, string? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, apiBase.TrimEnd('/') + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("feature-flow", "0.1.0"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (body is not null)
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
                return ProviderResult<string>.Success(text);
            return new ProviderResult<string>(null, Map(response.StatusCode, response.Headers.RetryAfter?.Delta));
        }
        catch (HttpRequestException e)
        {
            return ProviderResult<string>.Failure("unreachable", e.Message);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderResult<string>.Failure("timeout", "GitHub did not answer in time");
        }
    }

    /// <summary>docs/06 section 1.1. The response body is not quoted: it can echo request content.</summary>
    static ProviderError Map(HttpStatusCode status, TimeSpan? retryAfter) => (int)status switch
    {
        401 => new ProviderError("unauthenticated", "GitHub refused the App's credentials"),
        403 => new ProviderError("forbidden", "GitHub refused the request"),
        404 => new ProviderError("not_found", "GitHub has no such App installation"),
        422 or 400 => new ProviderError("validation_failed", "GitHub refused the token request as invalid"),
        429 => new ProviderError("rate_limited", "GitHub rate limit", retryAfter),
        >= 500 => new ProviderError("server_error", $"GitHub answered {(int)status}"),
        _ => new ProviderError("unexpected_response", $"GitHub answered {(int)status}"),
    };
}
