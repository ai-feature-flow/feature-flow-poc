using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Engine.Agent;

/// <summary>
/// The model proxy's settings (floor F6, <c>proxy</c> mode; LOG 68). <see cref="Auth"/> is <c>oauth</c> for a
/// subscription token, sent as a bearer token with the OAuth beta header, or <c>api_key</c>, sent as
/// <c>x-api-key</c>. The credential is read when a request is forwarded, never held by the sandbox.
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
    public const string OAuthBeta = "oauth-2025-04-20";

    static readonly HashSet<string> NotForwarded = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "authorization", "x-api-key", "content-length", "connection", "accept-encoding", "transfer-encoding", "keep-alive",
        "proxy-connection", "anthropic-beta", "expect",
    };

    readonly HttpClient http = new(handler ?? new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.None }, disposeHandler: handler is null)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    [GeneratedRegex("\"input_tokens\"\\s*:\\s*(\\d+)")]
    private static partial Regex InputTokens();

    [GeneratedRegex("\"output_tokens\"\\s*:\\s*(\\d+)")]
    private static partial Regex OutputTokens();

    /// <summary>Forwards one request whose step token the endpoint has checked. Returns the usage seen, for the step's record, and why the model could not be reached when it could not.</summary>
    /// <summary>
    /// The only requests forwarded (LOG 104): the Messages API and its token count, by POST, as relative paths. Anything
    /// else is refused before the credential is read, so an agent cannot point the proxy at another host.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedPaths = new HashSet<string>(StringComparer.Ordinal) { "v1/messages", "v1/messages/count_tokens" };

    /// <summary>The upstream URL for a request path, or null when the path is not allowed or would leave the upstream.</summary>
    public static Uri? Target(Uri upstream, string method, string path, string query)
    {
        if (method != "POST" || !AllowedPaths.Contains(path))
            return null;
        var target = new Uri(upstream, path + (query.StartsWith('?') && !query.Contains('#') ? query : ""));
        return target.Scheme == upstream.Scheme && target.Host == upstream.Host && target.Port == upstream.Port ? target : null;
    }

    public async Task<(int Status, long InputTokens, long OutputTokens, string? Model, string? Unreachable)> ForwardAsync(HttpContext context, string path, CancellationToken cancellationToken)
    {
        if (Target(options.Upstream, context.Request.Method, path, context.Request.QueryString.Value ?? "") is not { } target)
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
        if (options.Auth == "oauth")
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
            if (!betas.Contains(OAuthBeta))
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
