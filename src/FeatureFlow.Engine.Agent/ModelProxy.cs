using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Engine.Agent;

/// <summary>
/// The model proxy's settings (floor F6, <c>proxy</c> mode; LOG 68). <see cref="Auth"/> is <c>oauth</c> for a
/// subscription token, sent as a bearer token with the OAuth beta header; <c>github</c> for a GitHub token to the
/// Copilot API, sent as a bearer token alone (LOG 496); or <c>api_key</c>, sent as <c>x-api-key</c>. The credential
/// is read when a request is forwarded, never held by the sandbox.
/// </summary>
public sealed record ModelProxyOptions(Uri Upstream, string Auth, Func<CancellationToken, Task<string?>> Credential);

/// <summary>
/// Forwards an agent's model requests to the model's API, replacing the step's token with the real credential
/// and streaming the response back, as SP5's proxy did (<c>spikes/08-model-proxy</c>). It logs and records only
/// the path, status, model and token counts; never a credential. DR20: its client validates the upstream with
/// the platform's trust store (the default handler, no certificate callback).
/// </summary>
public sealed partial class ModelProxy(ModelProxyOptions options, HttpMessageHandler? handler = null)
{
    /// <summary>The model API this proxy forwards to.</summary>
    public Uri Upstream => options.Upstream;

    public const string OAuthBeta = "oauth-2025-04-20";

    /// <summary>The ways the credential can be sent, by <c>model_access.auth</c>; any other value is refused at composition.</summary>
    public static readonly IReadOnlyList<string> Auths = ["api_key", "oauth", "github"];

    static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "authorization", "x-api-key", "content-length", "connection", "accept-encoding", "transfer-encoding", "keep-alive",
        "proxy-connection", "anthropic-beta", "expect",
    };

    readonly HttpClient http = new(handler ?? new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None }, disposeHandler: handler is null)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    /// <summary>Anthropic's <c>input_tokens</c> or the OpenAI shape's <c>prompt_tokens</c> (Copilot's <c>chat/completions</c>).</summary>
    [GeneratedRegex("\"(?:input_tokens|prompt_tokens)\"\\s*:\\s*(\\d+)")]
    private static partial Regex InputTokens();

    [GeneratedRegex("\"(?:output_tokens|completion_tokens)\"\\s*:\\s*(\\d+)")]
    private static partial Regex OutputTokens();

    /// <summary>
    /// The only requests forwarded (LOG 104), as method and relative path: the Messages API and its token count, by POST.
    /// With <c>auth: github</c> also what Copilot CLI asks the Copilot API for (LOG 496): its model catalogue
    /// (<c>meta</c>, <c>models</c>) and <c>chat/completions</c>, used for a model that does not offer the Messages API.
    /// Anything else is refused before the credential is read, so an agent cannot point the proxy at another host.
    /// </summary>
    public static IReadOnlySet<string> Allowed(string auth) => auth == "github" ? GitHubRequests : AnthropicRequests;

    static readonly HashSet<string> AnthropicRequests = new(StringComparer.Ordinal) { "POST v1/messages", "POST v1/messages/count_tokens" };

    static readonly HashSet<string> GitHubRequests = new(AnthropicRequests, StringComparer.Ordinal) { "GET meta", "GET models", "POST chat/completions" };

    /// <summary>The upstream URL for a request, or null when the request is not allowed or would leave the upstream.</summary>
    public static Uri? Target(Uri upstream, string auth, string method, string path, string query) =>
        Allowed(auth).Contains($"{method} {path}") ? Within(upstream, path + (query.StartsWith('?') && !query.Contains('#') ? query : "")) : null;

    /// <summary>
    /// <paramref name="relative"/> resolved against the upstream, or null when it would leave the upstream's scheme, host or
    /// port. Today's allowed requests are relative literals, which cannot; this guards a future entry that is not one.
    /// </summary>
    public static Uri? Within(Uri upstream, string relative)
    {
        var target = new Uri(upstream, relative);
        return target.Scheme == upstream.Scheme && target.Host == upstream.Host && target.Port == upstream.Port ? target : null;
    }

    /// <summary>Forwards one request whose step token the endpoint has checked. Returns the usage seen, for the step's record, and why the model could not be reached when it could not.</summary>
    public async Task<(int Status, long InputTokens, long OutputTokens, string? Model, string? Unreachable)> ForwardAsync(HttpContext context, string path, CancellationToken cancellationToken)
    {
        if (Target(options.Upstream, options.Auth, context.Request.Method, path, context.Request.QueryString.Value ?? "") is not { } target)
        {
            context.Response.StatusCode = 404;
            await context.Response.WriteAsync("""{"type":"error","error":{"type":"not_found_error","message":"the model proxy does not forward this request"}}""", cancellationToken);
            return (404, 0, 0, null, null);
        }

        var credential = await options.Credential(cancellationToken);
        if (string.IsNullOrEmpty(credential))
        {
            context.Response.StatusCode = 503;
            await context.Response.WriteAsync("""{"type":"error","error":{"type":"api_error","message":"the model proxy has no credential configured"}}""", cancellationToken);
            return (503, 0, 0, null, null);
        }

        using var request = new HttpRequestMessage(new HttpMethod(context.Request.Method), target);
        var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, cancellationToken);
        var model = ModelOf(body);
        body.Position = 0;
        if (body.Length > 0 || context.Request.Method is "POST" or "PUT")
            request.Content = new StreamContent(body);
        foreach (var (name, values) in context.Request.Headers)
        {
            if (NotForwarded.Contains(name))
                continue;
            if (!request.Headers.TryAddWithoutValidation(name, values.ToArray()))
                request.Content?.Headers.TryAddWithoutValidation(name, values.ToArray());
        }

        var betas = context.Request.Headers["anthropic-beta"].SelectMany(v => (v ?? "").Split(',')).Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        if (options.Auth is "oauth" or "github")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            if (options.Auth == "oauth" && !betas.Contains(OAuthBeta))
                betas.Add(OAuthBeta);
        }
        else
            request.Headers.TryAddWithoutValidation("x-api-key", credential);
        if (betas.Count > 0)
            request.Headers.TryAddWithoutValidation("anthropic-beta", string.Join(",", betas));

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException e)
        {
            // The cause (a refused connection, a certificate the host does not trust) is for whoever runs the worker to fix.
            var cause = e.InnerException is { } inner ? $"{e.Message} {inner.Message}" : e.Message;
            Console.Error.WriteLine($"Model proxy: {target.Host} could not be reached: {cause}");
            context.Response.StatusCode = 502;
            await context.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(new
            {
                type = "error",
                error = new { type = "api_error", message = $"the model proxy could not reach the model: {cause}" },
            }), cancellationToken);
            return (502, 0, 0, model, cause);
        }

        using (response)
        {
            context.Response.StatusCode = (int)response.StatusCode;
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
                if (!name.Equals("transfer-encoding", StringComparison.OrdinalIgnoreCase) && !name.Equals("content-length", StringComparison.OrdinalIgnoreCase)
                    && !name.Equals("connection", StringComparison.OrdinalIgnoreCase))
                    context.Response.Headers[name] = values.ToArray();
            await using var upstream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[16384];
            var seen = new MemoryStream();
            int read;
            while ((read = await upstream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await context.Response.Body.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                await context.Response.Body.FlushAsync(cancellationToken);
                if (seen.Length < 4_000_000)
                    seen.Write(buffer, 0, read);
            }

            var text = System.Text.Encoding.UTF8.GetString(seen.ToArray());
            return ((int)response.StatusCode, Max(InputTokens(), text), Max(OutputTokens(), text), model, null);
        }
    }

    /// <summary>
    /// The model a request names, for the step's record (review R2, F3). It is the runtime's choice, not a limit the proxy
    /// enforces: the agent holds the step's token, so it could name another model; the record then shows it.
    /// </summary>
    static string? ModelOf(MemoryStream body)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(body.ToArray());
            return json.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object && json.RootElement.TryGetProperty("model", out var m)
                   && m.ValueKind == System.Text.Json.JsonValueKind.String ? m.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    static long Max(Regex regex, string text) =>
        regex.Matches(text).Select(m => long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(0).Max();
}
