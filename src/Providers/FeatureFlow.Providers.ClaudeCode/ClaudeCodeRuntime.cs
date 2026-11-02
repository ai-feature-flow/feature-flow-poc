using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.ClaudeCode;

/// <summary>Settings of a Claude Code runtime instance (docs/07 section 2).</summary>
public sealed record ClaudeCodeOptions(string Model, string Executable = "claude", int? MaxRetries = null, TimeSpan? HookHold = null, TimeSpan? ClientRetry = null,
    bool EngineTools = true)
{
    /// <summary>How long the hook command holds a call while no replica answers, before it stops the session (R19).</summary>
    public TimeSpan Hold => HookHold ?? TimeSpan.FromSeconds(120);

    /// <summary>How long the sandbox client retries an engine tool call before it ends the session (LOG 60).</summary>
    public TimeSpan Retry => ClientRetry ?? TimeSpan.FromMinutes(10);
}

/// <summary>
/// The Claude Code runtime (docs/06 section 3.2), run in a sandbox. Ported in spirit from Spike 2's provider:
/// <c>claude -p</c> with stream-json output, a session id fixed from the step's key so a new worker can resume it,
/// and usage read from the result record. Reporting goes through the engine's MCP tools (SP3), served by the
/// sandbox client; hooks call the hook endpoint through the same client.
/// <para>
/// The runtime is given only the step's token and the model proxy's URL, never a model credential (F6, SP5). Its
/// settings come from the engine alone: user settings from the engine-controlled home, no project settings, and
/// only the engine's MCP server, so a repository cannot add hooks or tools.
/// </para>
/// </summary>
public sealed class ClaudeCodeRuntime(ClaudeCodeOptions options, Func<ISandboxProvider> sandboxes, Func<IArtifactStoreProvider> store) : IAgentRuntimeProvider
{
    public const string AgentFolder = "/ff/agent";

    /// <summary>
    /// Claude Code's managed settings: root-owned and read-only in the sandbox, loaded before any other source, with
    /// <c>allowManagedHooksOnly</c> so no hook the agent writes into its own settings runs (LOG 100).
    /// </summary>
    public const string ManagedSettings = "/etc/claude-code/managed-settings.json";
    public const string StateFolder = "/ff/state";
    readonly ConcurrentDictionary<string, (SandboxHandle Sandbox, long Cursor, int Turns)> progress = new();

    public ProviderDescription Describe() => new("agent_runtime", "claude_code", "0.1.0",
        new AgentRuntimeCapabilities(true, true, true, true, true, true, true, null));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    /// <summary>The tools of each abstract tool class a role lists (LOG 65).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ToolClasses = new Dictionary<string, IReadOnlyList<string>>
    {
        ["read"] = ["Read", "LS", "TodoWrite"],
        ["search"] = ["Grep", "Glob"],
        ["edit"] = ["Edit", "Write", "MultiEdit", "NotebookEdit"],
        ["shell"] = ["Bash"],
    };

    /// <summary>The session id for a step's key: stable, so a new worker resumes the same conversation.</summary>
    public static string SessionId(string stepKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(stepKey))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    /// <summary>DR16: the runtime's MCP tool timeout exceeds the longest a question may wait.</summary>
    public static TimeSpan McpToolTimeout(AgentLimits limits) => (limits.QuestionsTimeout ?? TimeSpan.FromHours(4)) + TimeSpan.FromMinutes(15);

    /// <summary>DR16: the hook command's timeout exceeds the client's hold.</summary>
    public TimeSpan HookTimeout => options.Hold + TimeSpan.FromSeconds(60);

    static string CommandKey(AgentTask task) => $"{task.Step.IdempotencyKey}/session-{task.Session}";

    /// <summary>The settings file: the hook command for every hook, with its timeout (DR16), and nothing else.</summary>
    public JsonObject Settings()
    {
        JsonArray Hook(string name) => new(new JsonObject
        {
            ["matcher"] = "*",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = $"/ff/bin/ff-sandbox hook {name}",
                ["timeout"] = (int)HookTimeout.TotalSeconds,
            }),
        });
        return new JsonObject
        {
            ["allowManagedHooksOnly"] = true,
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = Hook("pre_tool_use"),
                ["PostToolUse"] = Hook("post_tool_use"),
                ["Stop"] = Hook("stop"),
                ["SessionStart"] = Hook("session_start"),
            },
        };
    }

    /// <summary>
    /// The environment of the runtime and of the sandbox client it starts. It holds the step's token and the
    /// proxy's URL, never a model credential (F6). <c>HOME</c> is the engine's (DR19).
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment(AgentTask task, AgentLimits limits)
    {
        var engine = task.Engine ?? throw new InvalidOperationException("an agent task in a sandbox needs the engine's endpoints");
        var paths = task.Paths ?? throw new InvalidOperationException("an agent task in a sandbox needs its paths");
        var env = new Dictionary<string, string>
        {
            ["HOME"] = paths.Home,
            ["CLAUDE_CONFIG_DIR"] = paths.Home + "/.claude",
            ["ANTHROPIC_BASE_URL"] = engine.ModelBaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = engine.StepToken,
            ["MCP_TOOL_TIMEOUT"] = ((long)McpToolTimeout(limits).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            ["DISABLE_AUTOUPDATER"] = "1",
            ["FF_ENGINE_URLS"] = string.Join(",", engine.AgentUrls),
            ["FF_STEP_TOKEN"] = engine.StepToken,
            ["FF_OUTPUT"] = paths.Output,
            ["FF_CHECKOUT"] = paths.Checkout,
            ["FF_STATE"] = StateFolder,
            ["FF_CLIENT_LOG"] = StateFolder + "/client.jsonl",
            ["FF_QUESTIONS"] = task.Questions ? "true" : "false",
            ["FF_WRITE"] = task.WriteAccess == "none" ? "false" : "true",
            ["FF_HOOK_HOLD_SECONDS"] = ((int)options.Hold.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["FF_RETRY_SECONDS"] = ((int)options.Retry.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (options.MaxRetries is { } retries)
            env["CLAUDE_CODE_MAX_RETRIES"] = retries.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return env;
    }

    public List<string> Tools(AgentTask task) => task.Tools.SelectMany(c => ToolClasses.GetValueOrDefault(c, []))
        .Where(t => task.WriteAccess != "none" || !ToolClasses["edit"].Contains(t)).Distinct().ToList();

    /// <summary>The command line, reading the prompt from a file so no task text sits in an argument.</summary>
    public string CommandLine(AgentTask task, string promptFile, bool resume)
    {
        var tools = Tools(task);
        var allowed = tools.Append("mcp__engine__report");
        if (task.Questions)
            allowed = allowed.Append("mcp__engine__ask_human");
        if (task.WriteAccess != "none")
            allowed = allowed.Append("mcp__engine__checkpoint");
        var paths = task.Paths!;
        var parts = new List<string>
        {
            options.Executable, "-p", "--output-format", "stream-json", "--verbose", "--model", options.Model,
            resume ? "--resume" : "--session-id", SessionId(task.Step.IdempotencyKey),
            "--setting-sources", "user",
            "--tools", string.Join(",", tools.DefaultIfEmpty("Read")),
            "--allowedTools", string.Join(",", allowed),
            "--add-dir", paths.Output, Path.GetDirectoryName(paths.InputsManifest)!.Replace('\\', '/'),
        };
        if (task.EngineTools && options.EngineTools)
            parts.AddRange(["--strict-mcp-config", "--mcp-config", $"{AgentFolder}/mcp.json"]);
        else
            parts.Add("--strict-mcp-config");
        return "exec " + string.Join(" ", parts.Select(Quote)) + " < " + Quote(promptFile);
    }

    public Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken) =>
        LaunchAsync(sandbox, task, limits, task.TaskText ?? "", resume: false, cancellationToken);

    public Task<ProviderResult<AgentSession>> ContinueAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, string message, CancellationToken cancellationToken) =>
        LaunchAsync(sandbox, task, limits, message, resume: true, cancellationToken);

    async Task<ProviderResult<AgentSession>> LaunchAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, string prompt, bool resume,
        CancellationToken cancellationToken)
    {
        var sandboxes1 = sandboxes();
        var key = CommandKey(task);
        if ((await sandboxes1.AttachCommandAsync(sandbox, key, cancellationToken)) is { Ok: true })
            return Session(sandbox, key);

        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["engine"] = new JsonObject
                {
                    ["command"] = "/ff/bin/ff-sandbox",
                    ["args"] = new JsonArray("mcp"),
                    ["env"] = new JsonObject(Environment(task, limits).Where(kv => kv.Key.StartsWith("FF_", StringComparison.Ordinal))
                        .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
                },
            },
        };
        var promptFile = $"{AgentFolder}/prompt-{task.Session}.md";
        if (!(task.EngineTools && options.EngineTools))
            // The runtime knows the tool is absent; the preamble's fallback rule applies, and the agent is told so.
            prompt += $"\n\nThe engine's report tool is not available in this session. Report by writing the JSON report to report.json in {task.Paths!.Output}.";
        foreach (var (path, content) in new[] { (ManagedSettings, Settings().ToJsonString()), ($"{AgentFolder}/mcp.json", mcp.ToJsonString()), (promptFile, prompt) })
        {
            var put = await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), cancellationToken);
            if (!put.Ok)
                return new ProviderResult<AgentSession>(null, put.Error);
            var copied = await sandboxes1.CopyInAsync(sandbox, put.Value!, path, readOnly: true, cancellationToken);
            if (!copied.Ok)
                return new ProviderResult<AgentSession>(null, copied.Error);
        }

        var command = new SandboxCommand(["sh", "-c", CommandLine(task, promptFile, resume)], Environment(task, limits), task.Paths!.Checkout);
        var started = await sandboxes1.StartCommandAsync(sandbox, command, key, cancellationToken);
        return started.Ok ? Session(sandbox, key) : new ProviderResult<AgentSession>(null, started.Error);
    }

    ProviderResult<AgentSession> Session(SandboxHandle sandbox, string key)
    {
        progress.TryAdd(key, (sandbox, 0, 0));
        return ProviderResult<AgentSession>.Success(new AgentSession(key, "claude_code"));
    }

    public async Task<ProviderResult<AgentSession>> AttachAsync(SandboxHandle sandbox, AgentTask task, CancellationToken cancellationToken)
    {
        var key = CommandKey(task);
        var found = await sandboxes().AttachCommandAsync(sandbox, key, cancellationToken);
        return found.Ok ? Session(sandbox, key) : new ProviderResult<AgentSession>(null, found.Error);
    }

    public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult<AgentSession>.Failure("capability_missing", "resume with a message: ContinueAsync"));

    public async Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!progress.TryGetValue(session.Id, out var state))
            return ProviderResult<AgentProgress>.Failure("not_found", $"no session {session.Id}");
        var status = await sandboxes().GetCommandStatusAsync(new CommandHandle(state.Sandbox, session.Id), state.Cursor, cancellationToken);
        if (!status.Ok)
            return new ProviderResult<AgentProgress>(null, status.Error);
        var turns = state.Turns + status.Value!.Output.Split('\n').Count(l => l.StartsWith("{\"type\":\"assistant\"", StringComparison.Ordinal));
        progress[session.Id] = (state.Sandbox, status.Value.Cursor, turns);
        return ProviderResult<AgentProgress>.Success(new AgentProgress(!status.Value.Running, turns));
    }

    public async Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!progress.TryGetValue(session.Id, out var state))
            return ProviderResult<AgentResult>.Failure("not_found", $"no session {session.Id}");
        var sandbox = sandboxes();
        var status = await sandbox.GetCommandStatusAsync(new CommandHandle(state.Sandbox, session.Id), 0, cancellationToken);
        if (!status.Ok)
            return new ProviderResult<AgentResult>(null, status.Error);
        var transcript = status.Value!.Output;
        var put = await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(transcript)), cancellationToken);
        var marker = await sandbox.RunAsync(state.Sandbox, new SandboxCommand(["sh", "-c", $"cat {StateFolder}/engine_unreachable 2>/dev/null; rm -f {StateFolder}/engine_unreachable"], AsRoot: true),
            $"{session.Id}/marker/{Guid.NewGuid():N}", cancellationToken);
        var report = await sandbox.RunAsync(state.Sandbox, new SandboxCommand(["sh", "-c", "cat \"$FF_OUTPUT/report.json\" 2>/dev/null; true"],
            new Dictionary<string, string> { ["FF_OUTPUT"] = "/ff/out" }, AsRoot: true), $"{session.Id}/report/{Guid.NewGuid():N}", cancellationToken);
        var parsed = Parse(transcript);
        var end = marker.Ok && marker.Value.Output.Trim().Length > 0 ? SessionEnd.EngineUnreachable
            : parsed.ApiError is not null ? ApiEnd(parsed.ApiStatus)
            : status.Value.Lost ? SessionEnd.Lost
            : status.Value.ExitCode == 0 ? SessionEnd.Completed
            : SessionEnd.Failed;
        var detail = end switch
        {
            SessionEnd.EngineUnreachable => "the sandbox client could not reach the engine",
            SessionEnd.Lost => "the session's process was lost",
            SessionEnd.ModelRefused => $"the model answered {parsed.ApiStatus}: {parsed.ApiError}",
            SessionEnd.ModelUnreachable => "the model could not be reached: " + parsed.ApiError,
            SessionEnd.Failed => parsed.ApiError is not null ? $"the model answered {parsed.ApiStatus}: {parsed.ApiError}" : $"claude exited {status.Value.ExitCode}: {parsed.Result}",
            _ => parsed.Result,
        };
        return ProviderResult<AgentResult>.Success(new AgentResult(null, end, detail, parsed.InputTokens, parsed.OutputTokens, parsed.Files,
            put.Ok ? put.Value!.Id : null, report.Ok && report.Value.Output.Trim().Length > 0 ? report.Value.Output.Trim() : null)
        {
            QuietWindow = options.Hold < options.Retry ? options.Hold : options.Retry,
        });
    }

    public async Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!progress.TryGetValue(session.Id, out var state))
            return ProviderResult<Unit>.Success(default);
        return await sandboxes().CancelCommandAsync(new CommandHandle(state.Sandbox, session.Id), cancellationToken);
    }

    public sealed record Transcript(string? Result, string? ApiError, long InputTokens, long OutputTokens, IReadOnlyList<string> Files, int? ApiStatus = null);

    /// <summary>
    /// How a model API error ends the session (LOG 98): no status (the connection failed), 429 and 5xx are transient,
    /// so resumable; 401 and 403 are a configuration fault; any other 4xx (a request too large, a prompt too long) is
    /// permanent, a failed session.
    /// </summary>
    public static string ApiEnd(int? status) => status switch
    {
        null or 429 or >= 500 => SessionEnd.ModelUnreachable,
        401 or 403 => SessionEnd.ModelRefused,
        _ => SessionEnd.Failed,
    };

    /// <summary>Reads Claude Code's stream-json output: the result record, usage, and the files its tools opened.</summary>
    public static Transcript Parse(string output)
    {
        string? result = null, apiError = null;
        int? apiStatus = null;
        long input = 0, outputTokens = 0;
        var files = new List<string>();
        foreach (var line in output.Split('\n'))
        {
            if (!line.StartsWith('{'))
                continue;
            JsonNode? record;
            try
            {
                record = JsonNode.Parse(line);
            }
            catch (JsonException)
            {
                continue;
            }

            switch (record?["type"]?.GetValue<string>())
            {
                case "assistant":
                    foreach (var block in record["message"]?["content"]?.AsArray() ?? [])
                        if (block?["type"]?.GetValue<string>() == "tool_use" && block["name"]?.GetValue<string>() is "Read" or "Edit" or "Write" or "MultiEdit"
                            && block["input"]?["file_path"]?.GetValue<string>() is { } file)
                            files.Add(file);
                    break;
                case "result":
                    result = record["result"]?.ToString();
                    var terminal = record["terminal_reason"]?.ToString();
                    apiError = terminal == "api_error" ? record["api_error"]?.ToString() ?? result ?? "api_error" : null;
                    apiStatus = terminal == "api_error" && record["api_error_status"] is JsonValue code && code.TryGetValue<int>(out var n) ? n : null;
                    input += record["usage"]?["input_tokens"]?.GetValue<long>() ?? 0;
                    outputTokens += record["usage"]?["output_tokens"]?.GetValue<long>() ?? 0;
                    break;
            }
        }

        return new Transcript(result, apiError, input, outputTokens, files.Distinct().ToList(), apiStatus);
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class ClaudeCodeProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("agent_runtime", "claude_code", ["model", "executable", "model_access", "max_retries", "hook_hold", "client_retry", "engine_tools"], c =>
        {
            if (c.Instance.Settings["model_access"] is not YMap access || access["mode"].ScalarText() != "proxy")
                throw new InvalidOperationException($"providers.agent_runtime.instances.{c.Instance.Name} needs model_access with mode proxy (F6)");
            return new ClaudeCodeRuntime(
                new ClaudeCodeOptions(c.Setting("model") ?? "haiku", c.Setting("executable") ?? "claude",
                    c.Setting("max_retries") is { } r ? int.Parse(r, System.Globalization.CultureInfo.InvariantCulture) : null,
                    c.Setting("hook_hold") is { } h ? TimeSpan.FromSeconds(double.Parse(h, System.Globalization.CultureInfo.InvariantCulture)) : null,
                    c.Setting("client_retry") is { } cr ? TimeSpan.FromSeconds(double.Parse(cr, System.Globalization.CultureInfo.InvariantCulture)) : null,
                    c.Setting("engine_tools") != "false"),
                () => c.Provider("sandbox") as ISandboxProvider ?? throw new InvalidOperationException("Claude Code runs in a sandbox: configure a sandbox provider"),
                () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("Claude Code needs an artifact store"));
        }),
    ];
}
