using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FeatureFlow.SandboxClient;

/// <summary>The engine stayed unreachable for the whole retry budget. Never shown to the agent (LOG 60).</summary>
public sealed class EngineUnreachableException(string message) : Exception(message);

/// <summary>
/// Calls the agent worker's endpoint for one step, with the step's token, moving to the next replica on any
/// connection error or server error and retrying for up to <see cref="Budget"/> of failures in a row
/// (docs/02 section 4.2). A client error (4xx) is an answer, not a failure.
/// </summary>
public sealed class EngineClient(IReadOnlyList<string> urls, string token, TimeSpan budget, ClientLog log, HttpMessageHandler? handler = null)
{
    readonly HttpClient http = new(handler ?? new SocketsHttpHandler(), disposeHandler: handler is null) { Timeout = Timeout.InfiniteTimeSpan };
    int index;

    public TimeSpan Budget { get; } = budget;

    /// <summary>The delay between attempts. Short in tests.</summary>
    public TimeSpan Pause { get; init; } = TimeSpan.FromSeconds(1);

    public string CurrentUrl => urls[index % urls.Count];

    public static EngineClient FromEnvironment(ClientLog log, TimeSpan budget) =>
        new((Environment.GetEnvironmentVariable("FF_ENGINE_URLS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Environment.GetEnvironmentVariable("FF_STEP_TOKEN") ?? "", budget, log);

    public async Task<JsonNode> SendAsync(HttpMethod method, string path, JsonNode? body, TimeSpan timeout, CancellationToken cancellationToken)
    {
        DateTimeOffset? failingSince = null;
        while (true)
        {
            var url = CurrentUrl;
            try
            {
                using var request = new HttpRequestMessage(method, url.TrimEnd('/') + path);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body is not null)
                    request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
                using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cancel.CancelAfter(timeout);
                using var response = await http.SendAsync(request, cancel.Token);
                var text = await response.Content.ReadAsStringAsync(cancel.Token);
                if ((int)response.StatusCode >= 500)
                    throw new HttpRequestException($"the endpoint answered {(int)response.StatusCode}");
                JsonNode? parsed;
                try
                {
                    parsed = JsonNode.Parse(text);
                }
                catch (JsonException)
                {
                    parsed = null;
                }

                if (parsed is not JsonObject)
                    throw new HttpRequestException($"the endpoint answered {(int)response.StatusCode} with no JSON object");
                if (failingSince is { } since)
                    log.Write("reconnected", ("replica", url), ("after_seconds", Math.Round((DateTimeOffset.UtcNow - since).TotalSeconds, 1)));
                return parsed;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
            {
                log.Write("connection_failed", ("replica", url), ("path", path), ("error", e.GetType().Name + ": " + e.Message));
                failingSince ??= DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - failingSince > Budget)
                    throw new EngineUnreachableException($"no engine replica answered for {Budget.TotalSeconds:F0} seconds");
                index++;
                await Task.Delay(Pause, cancellationToken);
            }
        }
    }
}

/// <summary>A JSON-lines log of the client's attempts, for tests and diagnosis. Holds no token.</summary>
public sealed class ClientLog(string? path)
{
    static readonly object Gate = new();

    public void Write(string kind, params (string Key, object? Value)[] fields)
    {
        if (path is null)
            return;
        var line = new JsonObject { ["t"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, ["event"] = kind };
        foreach (var (key, value) in fields)
            line[key] = value is null ? null : JsonValue.Create(value.ToString());
        lock (Gate)
            File.AppendAllText(path, line.ToJsonString() + "\n");
    }
}

/// <summary>
/// Ends the session in a resumable state when the engine stays unreachable (LOG 60): it leaves a marker the
/// agent worker reads as an infrastructure stop, then ends the runtime, its parent process. The agent is never
/// told an engine error, because SP2 showed an agent given one improvises a result and reports success.
/// </summary>
public static class SessionStop
{
    public static string MarkerPath => Path.Combine(Environment.GetEnvironmentVariable("FF_STATE") ?? "/ff/state", "engine_unreachable");

    public static void Mark(string why)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath)!);
            File.WriteAllText(MarkerPath, why + "\n");
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Ends the parent process (the runtime) on Linux, where the sandbox runs.</summary>
    public static void EndRuntime()
    {
        if (!OperatingSystem.IsLinux() || Environment.GetEnvironmentVariable("FF_STOP_PARENT") == "false")
            return;
        try
        {
            var stat = File.ReadAllText("/proc/self/stat");
            var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
            var parent = int.Parse(fields[1], System.Globalization.CultureInfo.InvariantCulture);
            if (parent > 1)
                System.Diagnostics.Process.GetProcessById(parent).Kill();
        }
        catch (Exception e) when (e is IOException or FormatException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }
}
