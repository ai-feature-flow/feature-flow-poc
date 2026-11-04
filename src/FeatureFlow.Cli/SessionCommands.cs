using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;

namespace FeatureFlow.Cli;

/// <summary>
/// <c>ff session</c>: a person's side of an interactive session (docs/04 section 4.4), over the API and authenticated as
/// the person (the token from <c>--token-file</c> or <c>FF_API_TOKEN</c>). <c>ff session mcp</c> serves the same fetch,
/// check and submit to the person's agent tool as an MCP server on stdio. A check and a submission name a full commit,
/// by default the working copy's <c>HEAD</c>, which must be pushed to the session's branch.
/// </summary>
public static class SessionCommands
{
    public const string Usage = """
               ff session list <run id>
               ff session fetch <session id> [--out <folder>]
               ff session check <session id> <step> [--revision <commit>] [--timeout <seconds>]
               ff session submit <session id> [--revision <commit>]
               ff session abandon <session id>
               ff session mcp
               (every ff session command takes [--api <url>] [--token-file <file>])
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, TextReader? input = null)
    {
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        var positional = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Length)
                options[args[i]] = args[++i];
            else
                positional.Add(args[i]);
        }

        var api = options.GetValueOrDefault("--api") ?? Environment.GetEnvironmentVariable("FF_API_URL") ?? "http://127.0.0.1:8480";
        var token = options.GetValueOrDefault("--token-file") is { } file ? (File.Exists(file) ? File.ReadAllText(file).Trim() : null) : Environment.GetEnvironmentVariable("FF_API_TOKEN");
        if (token is null)
        {
            error.WriteLine("no API token: give --token-file <file> or set FF_API_TOKEN");
            return 2;
        }

        using var http = new HttpClient { BaseAddress = new Uri(api.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(5) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var client = new SessionClient(http);
        var timeout = TimeSpan.FromSeconds(double.Parse(options.GetValueOrDefault("--timeout") ?? "600", System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            var (exit, text) = positional switch
            {
                ["list", var run] => await client.ListAsync(run),
                ["fetch", var sid] => await client.FetchAsync(sid, options.GetValueOrDefault("--out")),
                ["check", var sid, var step] => await client.CheckAsync(sid, step, options.GetValueOrDefault("--revision"), timeout),
                ["submit", var sid] => await client.SubmitAsync(sid, options.GetValueOrDefault("--revision")),
                ["abandon", var sid] => await client.AbandonAsync(sid),
                ["mcp"] => (await new SessionMcpServer(client).RunAsync(input ?? Console.In, output), ""),
                _ => (-1, ""),
            };
            if (exit == -1)
            {
                error.WriteLine("usage: " + Usage.TrimStart());
                return 2;
            }

            (exit == 0 ? output : error).Write(text);
            return exit;
        }
        catch (HttpRequestException e)
        {
            error.WriteLine($"the API at {api} did not answer: {e.Message}");
            return 3;
        }
    }
}

/// <summary>The API calls behind <c>ff session</c> and its MCP tools: each returns an exit code and the text to show.</summary>
public sealed class SessionClient(HttpClient http)
{
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>How long a check refused during the run's hand-over is tried again; a hand-over takes moments.</summary>
    public TimeSpan HandOverRetry { get; init; } = TimeSpan.FromSeconds(30);

    public TimeSpan RetryInterval { get; init; } = TimeSpan.FromSeconds(1);

    public async Task<(int, string)> ListAsync(string runId)
    {
        var (reply, failure) = await GetAsync($"runs/{Uri.EscapeDataString(runId)}/sessions");
        if (reply is null)
            return (1, failure);
        var text = new StringBuilder();
        foreach (var s in reply["sessions"]!.AsArray())
            text.AppendLine($"session {s!["sessionId"]} {s["status"]} at {s["stepId"]}: {s["repository"]} {s["branch"]}{(s["person"]?.ToString() is { Length: > 0 } p ? " by " + p : "")}");
        return (0, text.ToString());
    }

    /// <summary>The workspace: where to work, the files to write, the checks, the prompt; with <paramref name="folder"/>, also written there with the drafts and the context.</summary>
    public async Task<(int, string)> FetchAsync(string sid, string? folder)
    {
        if (NotAnId(sid) is { } refused)
            return refused;
        var (reply, failure) = await GetAsync($"sessions/{Uri.EscapeDataString(sid)}");
        if (reply is null)
            return (1, failure);
        var w = reply["workspace"]!;
        var text = new StringBuilder();
        text.AppendLine($"session {sid} ({reply["session"]!["status"]}): {w["repository"]} branch {w["branch"]} from {w["baseRevision"]}");
        foreach (var f in w["files"]!.AsArray())
            text.AppendLine($"  write {Join(w["folder"]!.ToString(), f!["path"]!.ToString())} as {f["artifact"]} ({f["type"]}){(f["seedRef"] is null ? "" : ", from a draft")}");
        text.AppendLine($"  checks: {string.Join(", ", w["checksFrom"]!.AsArray().Select(c => c!.ToString()))}; submitting answers {w["submitOutcome"]}");
        text.AppendLine();
        text.AppendLine(w["instructions"]!.ToString());
        text.AppendLine();
        text.AppendLine(w["prompt"]!.ToString());
        if (folder is not null)
        {
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Combine(folder, "session.json"), reply.ToJsonString(Indented));
            await File.WriteAllTextAsync(Path.Combine(folder, "prompt.md"), w["prompt"]!.ToString());
            foreach (var (path, content) in reply["drafts"]!.AsObject())
                await WriteAsync(folder, "drafts", path, content!.ToString());
            foreach (var (name, content) in reply["context"]!.AsObject())
                await WriteAsync(folder, "context", name, content!.ToString());
            text.AppendLine($"(written to {folder}: session.json, prompt.md, drafts/ and context/)");
        }

        return (0, text.ToString());
    }

    /// <summary>Runs a check on a commit and waits for its result: 0 when it passed.</summary>
    public async Task<(int, string)> CheckAsync(string sid, string step, string? revision, TimeSpan timeout)
    {
        if (NotAnId(sid) is { } refused)
            return refused;
        if (Revision(revision) is not { } commit)
            return (2, "no commit to check: give --revision <full commit id>, or run this in a git working copy\n");
        var clock = Stopwatch.StartNew();
        var (started, failure) = await PostAsync($"sessions/{Uri.EscapeDataString(sid)}/checks", new JsonObject { ["step"] = step, ["revision"] = commit });
        while (started is null && failure.Contains(SessionNames.HandingOver, StringComparison.Ordinal) && clock.Elapsed < HandOverRetry)
        {
            await Task.Delay(RetryInterval);
            (started, failure) = await PostAsync($"sessions/{Uri.EscapeDataString(sid)}/checks", new JsonObject { ["step"] = step, ["revision"] = commit });
        }

        if (started is null)
            return (1, failure);
        var checkId = started["check_id"]!.ToString();
        while (clock.Elapsed < timeout)
        {
            var (list, _) = await GetAsync($"sessions/{Uri.EscapeDataString(sid)}/checks");
            var check = list?["checks"]!.AsArray().FirstOrDefault(c => c!["check"]!["checkId"]!.ToString() == checkId);
            var status = check?["check"]!["status"]!.ToString();
            if (status is "passed" or "failed" && check!["detail"] is { } detail)
            {
                var text = $"check {checkId} of {step} at {commit}: {status}\n  {detail}\n";
                if (check["result"] is { } result)
                    text += result + "\n";
                return (status == "passed" ? 0 : 1, text);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }

        return (4, $"check {checkId} did not finish within {timeout.TotalSeconds:0} s; see ff session fetch {sid} and the run's timeline\n");
    }

    public async Task<(int, string)> SubmitAsync(string sid, string? revision)
    {
        if (NotAnId(sid) is { } refused)
            return refused;
        if (Revision(revision) is not { } commit)
            return (2, "no commit to submit: give --revision <full commit id>, or run this in a git working copy\n");
        var (reply, failure) = await PostAsync($"sessions/{Uri.EscapeDataString(sid)}/submit", new JsonObject { ["revision"] = commit });
        return reply is null ? (1, failure) : (0, $"submitted {commit}: {reply["result"]}\n");
    }

    public async Task<(int, string)> AbandonAsync(string sid)
    {
        if (NotAnId(sid) is { } refused)
            return refused;
        var (reply, failure) = await PostAsync($"sessions/{Uri.EscapeDataString(sid)}/abandon", new JsonObject());
        return reply is null ? (1, failure) : (0, $"abandoned: {reply["result"]}\n");
    }

    /// <summary>A session id comes from text an agent may have read (LOG 242): anything not of its form is refused, never sent.</summary>
    static (int, string)? NotAnId(string sid) => SessionNames.IsId(sid) ? null : (2, $"'{sid}' is not a session id (<run id>.<wait id>)\n");

    /// <summary>The commit named, or the working copy's HEAD; null when there is neither.</summary>
    static string? Revision(string? given)
    {
        if (given is not null)
            return given;
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse HEAD") { RedirectStandardOutput = true, RedirectStandardError = true })!;
            var head = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return git.ExitCode == 0 && head.Length > 0 ? head : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    static string Join(string folder, string path) => folder.Length == 0 ? path : $"{folder}/{path}";

    static async Task WriteAsync(string folder, string area, string name, string content)
    {
        var root = Path.GetFullPath(Path.Combine(folder, area));
        var path = Path.GetFullPath(Path.Combine(root, name));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, content);
    }

    async Task<(JsonNode?, string)> GetAsync(string path)
    {
        using var response = await http.GetAsync(path);
        return await ReadAsync(response);
    }

    async Task<(JsonNode?, string)> PostAsync(string path, JsonObject body)
    {
        using var response = await http.PostAsync(path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
        return await ReadAsync(response);
    }

    static async Task<(JsonNode?, string)> ReadAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode)
            return (JsonNode.Parse(text), "");
        string message;
        try
        {
            message = JsonNode.Parse(text)?["error"]?.ToString() ?? text;
        }
        catch (JsonException)
        {
            message = text;
        }

        return (null, $"{(int)response.StatusCode}: {message}\n");
    }
}

/// <summary>
/// The engine's MCP server for a person's agent tool (docs/04 section 4.4): stdio JSON-RPC with tools <c>session_fetch</c>,
/// <c>session_check</c> and <c>session_submit</c>, each the <c>ff session</c> command of that name, authenticated as the
/// person through the API.
/// </summary>
public sealed class SessionMcpServer(SessionClient client)
{
    public async Task<int> RunAsync(TextReader input, TextWriter output)
    {
        while (await input.ReadLineAsync() is { } line)
        {
            if (line.Trim().Length == 0)
                continue;
            JsonNode? message;
            try
            {
                message = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            var id = message?["id"]?.DeepClone();
            JsonNode? result = message?["method"]?.GetValue<string>() switch
            {
                "initialize" => new JsonObject
                {
                    ["protocolVersion"] = message?["params"]?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "feature-flow-session", ["version"] = "0.1.0" },
                },
                "tools/list" => new JsonObject { ["tools"] = Tools() },
                "tools/call" => await CallAsync(message!["params"]!),
                "ping" => new JsonObject(),
                _ => null,
            };
            if (id is null)
                continue;
            var reply = result is null
                ? new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = $"method not found: {message?["method"]}" } }
                : new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
            await output.WriteLineAsync(reply.ToJsonString());
            await output.FlushAsync();
        }

        return 0;
    }

    async Task<JsonNode> CallAsync(JsonNode call)
    {
        var args = call["arguments"] as JsonObject ?? [];
        string? Arg(string name) => args[name]?.GetValue<string>();
        var (exit, text) = call["name"]?.GetValue<string>() switch
        {
            "session_fetch" => await client.FetchAsync(Arg("session_id") ?? "", null),
            "session_check" => await client.CheckAsync(Arg("session_id") ?? "", Arg("step") ?? "", Arg("revision"), TimeSpan.FromMinutes(10)),
            "session_submit" => await client.SubmitAsync(Arg("session_id") ?? "", Arg("revision")),
            var other => (2, $"no tool {other}"),
        };
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
            ["isError"] = exit != 0,
        };
    }

    static JsonArray Tools()
    {
        static JsonObject Tool(string name, string description, params (string Name, string Description, bool Required)[] parameters) => new()
        {
            ["name"] = name,
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject(parameters.Select(p =>
                    KeyValuePair.Create(p.Name, (JsonNode?)new JsonObject { ["type"] = "string", ["description"] = p.Description }))),
                ["required"] = new JsonArray(parameters.Where(p => p.Required).Select(p => (JsonNode?)p.Name).ToArray()),
            },
        };

        const string Revision = "The full id of a commit pushed to the session's branch; default the working copy's HEAD";
        return
        [
            Tool("session_fetch", "Fetch the session: the repository, branch and folder to work in, the files to write, the checks you may run, and the prompt.",
                ("session_id", "The session's id", true)),
            Tool("session_check", "Run the check of a later step on a commit, as that step will run it after submission, and wait for its result.",
                ("session_id", "The session's id", true), ("step", "A step the session's checks name", true), ("revision", Revision, false)),
            Tool("session_submit", "Submit the session's files at a commit. The engine imports and validates them; the decision is answered as you.",
                ("session_id", "The session's id", true), ("revision", Revision, false)),
        ];
    }
}
