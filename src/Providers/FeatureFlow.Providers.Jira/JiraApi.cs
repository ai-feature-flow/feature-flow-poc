using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Jira;

/// <summary>A Jira answer: its status and its body, parsed when JSON.</summary>
public sealed record JiraResponse(int Code, string Text)
{
    public JsonNode? Json => Text.Length == 0 ? null : JsonNode.Parse(Text);
}

/// <summary>
/// The Jira Cloud REST client (v3). The credential is a secret reference resolved by the secrets provider, holding
/// <c>&lt;account email&gt;:&lt;API token&gt;</c> for basic authentication; it is cached, and resolved again for the one
/// retry a configuration fault (401, 403) gets after <see cref="FreshCredentialDelay"/> (docs/06 section 1.1). Error
/// statuses map to docs/06 section 1.1; each tracker method maps the success statuses Jira documents and treats any other
/// as <c>unexpected_response</c>. The credential is never put in a message.
/// </summary>
public sealed class JiraApi(Func<CancellationToken, Task<ProviderResult<string>>> credential, string baseUrl, HttpMessageHandler? handler = null)
{
    static readonly HttpMessageHandler Shared = new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), AllowAutoRedirect = false };

    readonly HttpClient http = new(handler ?? Shared, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
    string? cached;

    public string BaseUrl { get; } = baseUrl.TrimEnd('/');

    /// <summary>The wait before the fresh-credential retry. Tests shorten it.</summary>
    public TimeSpan FreshCredentialDelay { get; init; } = ProviderError.FreshCredentialDelay;

    public async Task<ProviderResult<JiraResponse>> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var first = await SendOnceAsync(method, path, body, fresh: false, cancellationToken);
        if (first.Error is not { IsConfigurationFault: true })
            return first;
        await Task.Delay(FreshCredentialDelay, cancellationToken);
        return await SendOnceAsync(method, path, body, fresh: true, cancellationToken);
    }

    async Task<ProviderResult<string>> AuthorizationAsync(bool fresh, CancellationToken cancellationToken)
    {
        if (!fresh && cached is not null)
            return ProviderResult<string>.Success(cached);
        var resolved = await credential(cancellationToken);
        if (!resolved.Ok)
            return ProviderResult<string>.Failure("unauthenticated", $"the Jira credential does not resolve: {resolved.Error!.Reason}");
        if (resolved.Value!.IndexOf(':', StringComparison.Ordinal) <= 0)
            return ProviderResult<string>.Failure("unauthenticated", "the Jira secret must hold <account email>:<API token>");
        cached = Convert.ToBase64String(Encoding.UTF8.GetBytes(resolved.Value));
        return ProviderResult<string>.Success(cached);
    }

    async Task<ProviderResult<JiraResponse>> SendOnceAsync(HttpMethod method, string path, object? body, bool fresh, CancellationToken cancellationToken)
    {
        var authorization = await AuthorizationAsync(fresh, cancellationToken);
        if (!authorization.Ok)
            return new ProviderResult<JiraResponse>(null, authorization.Error);
        // The credential is only ever sent to the configured site: a path is always relative to it.
        if (!path.StartsWith('/'))
            throw new ArgumentException($"{path} is not a path on {BaseUrl}", nameof(path));
        using var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", authorization.Value);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("feature-flow", "0.1.0"));
        if (body is not null)
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        try
        {
            using var response = await http.SendAsync(request, cancellationToken);
            var text = await response.Content.ReadAsStringAsync(cancellationToken);
            if ((int)response.StatusCode >= 400)
                return new ProviderResult<JiraResponse>(null, Classify(response, text, method, path));
            return ProviderResult<JiraResponse>.Success(new JiraResponse((int)response.StatusCode, text));
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Net.Sockets.SocketException)
        {
            return ProviderResult<JiraResponse>.Failure("unreachable", $"Jira cannot be reached: {e.Message}");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ProviderResult<JiraResponse>.Failure("timeout", $"Jira did not answer {method} {Strip(path)} in time");
        }
    }

    public static ProviderResult<T> Unexpected<T>(JiraResponse response, string what) =>
        ProviderResult<T>.Failure("unexpected_response", $"Jira answered {response.Code} to {what}, which is not a documented success");

    /// <summary>docs/06 section 1.1. Jira's error messages are kept (they name the fault, never a credential); the request body is not quoted.</summary>
    static ProviderError Classify(HttpResponseMessage response, string text, HttpMethod method, string path)
    {
        var messages = new List<string>();
        try
        {
            if (JsonNode.Parse(text) is JsonObject json)
            {
                messages.AddRange((json["errorMessages"] as JsonArray ?? []).Select(m => m?.GetValue<string>() ?? "").Where(m => m.Length > 0));
                if (json["errors"] is JsonObject errors)
                    messages.AddRange(errors.Select(e => $"{e.Key}: {e.Value}"));
            }
        }
        catch (JsonException)
        {
        }

        var code = (int)response.StatusCode;
        var what = $"Jira answered {code} to {method} {Strip(path)}{(messages.Count > 0 ? ": " + string.Join("; ", messages) : "")}";
        return code switch
        {
            401 => new ProviderError("unauthenticated", what),
            403 => new ProviderError("forbidden", what),
            404 => new ProviderError("not_found", what),
            409 => new ProviderError("conflict", what),
            429 => new ProviderError("rate_limited", what, response.Headers.RetryAfter?.Delta),
            400 or 422 => new ProviderError("validation_failed", what),
            >= 500 => new ProviderError("server_error", what),
            _ => new ProviderError("unexpected_response", what),
        };
    }

    static string Strip(string path) => path.Split('?')[0];
}
