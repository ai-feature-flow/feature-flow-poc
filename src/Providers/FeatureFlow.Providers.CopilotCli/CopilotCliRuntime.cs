using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.CopilotCli;

/// <summary>
/// Settings of a Copilot CLI runtime instance (docs/07 section 2): <see cref="Model"/>, <see cref="Executable"/> and
/// <see cref="EngineTools"/>. <see cref="HookHold"/> and <see cref="ClientRetry"/> are fixed defaults, not settings (LOG 91);
/// only tests change them.
/// </summary>
public sealed record CopilotCliOptions(string Model, string Executable = "copilot", TimeSpan? HookHold = null, TimeSpan? ClientRetry = null, bool EngineTools = true)
{
    /// <summary>How long the hook command holds a call while no replica answers, before it stops the session (R19).</summary>
    public TimeSpan Hold => HookHold ?? TimeSpan.FromSeconds(120);

    /// <summary>How long the sandbox client retries an engine tool call before it ends the session (LOG 60).</summary>
    public TimeSpan Retry => ClientRetry ?? TimeSpan.FromMinutes(10);
}

/// <summary>
/// The GitHub Copilot CLI runtime (docs/06 section 3.2), run in a sandbox, ported from Spike 2's provider to M4's interfaces:
/// <c>copilot</c> in prompt mode with JSON-lines output, the prompt on standard input, a session id fixed from the step's
/// key so a continuation resumes the same conversation. Reporting goes through the engine's MCP tools (SP3), served by the
/// sandbox client; hooks call the hook endpoint through the same client.
/// <para>
/// The model is reached through the engine's model proxy as a custom provider of the Anthropic Messages API, with the
/// step's token as its key and <c>COPILOT_OFFLINE</c>, so the CLI holds no model or GitHub credential and makes no other
/// call (F6, LOG 293). Its settings come from the engine alone: <c>COPILOT_HOME</c> is a root-owned sticky folder whose
/// <c>settings.json</c> (the hooks) and <c>mcp-config.json</c> are root's and read-only, so the agent can neither change
/// nor replace them; the checkout is not trusted, so a repository's hooks, MCP servers and instructions are not loaded;
/// built-in MCP servers are off, and only the role's tools are available.
/// </para>
/// </summary>
public sealed class CopilotCliRuntime(CopilotCliOptions options, Func<ISandboxProvider> sandboxes, Func<IArtifactStoreProvider> store) : IAgentRuntimeProvider
{
    public const string AgentFolder = "/ff/agent";
    public const string Home = "/ff/copilot";
    public const string StateFolder = "/ff/state";
    readonly ConcurrentDictionary<string, (SandboxHandle Sandbox, long Cursor, int Turns, int Session)> progress = new();

    public ProviderDescription Describe() => new("agent_runtime", "copilot_cli", "0.1.0",
        new AgentRuntimeCapabilities(true, true, true, true, true, true, true, null));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    /// <summary>The Copilot tools of each abstract tool class a role lists (LOG 65).</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> ToolClasses = new Dictionary<string, IReadOnlyList<string>>
    {
        ["read"] = ["view"],
        ["search"] = ["grep", "glob"],
        ["edit"] = ["create", "edit"],
        ["shell"] = ["bash"],
    };

    /// <summary>The session id for a step's key: stable, so a continuation resumes the same conversation.</summary>
    public static string SessionId(string stepKey)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("copilot/" + stepKey))[..16];
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x40);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        return new Guid(bytes, bigEndian: true).ToString();
    }

    /// <summary>DR16: the engine MCP server's tool timeout exceeds the longest a question may wait.</summary>
    public static TimeSpan McpToolTimeout(AgentLimits limits) => (limits.QuestionsTimeout ?? AgentLimits.DefaultQuestionsTimeout) + TimeSpan.FromMinutes(15);

    /// <summary>DR16: the hook command's timeout exceeds the client's hold.</summary>
    public TimeSpan HookTimeout => options.Hold + TimeSpan.FromSeconds(60);

    static string CommandKey(AgentTask task) => $"{task.Step.IdempotencyKey}/session-{task.Session}";

    /// <summary>The user settings: the hook command for every hook Copilot fires, with its timeout (DR16), and nothing else.</summary>
    public JsonObject Settings()
    {
        JsonArray Hook(string name) => new(new JsonObject
        {
            ["type"] = "command",
            ["command"] = $"/ff/bin/ff-sandbox hook {name} copilot",
            ["timeoutSec"] = (int)HookTimeout.TotalSeconds,
        });
        return new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = Hook("pre_tool_use"),
                ["PostToolUse"] = Hook("post_tool_use"),
                ["Stop"] = Hook("stop"),
                ["SessionStart"] = Hook("session_start"),
            },
        };
    }

    /// <summary>The engine's MCP server, served by the sandbox client, with its tool timeout (DR16).</summary>
    public JsonObject McpConfig(AgentTask task, AgentLimits limits) => new()
    {
        ["mcpServers"] = new JsonObject
        {
            ["engine"] = new JsonObject
            {
                ["type"] = "local",
                ["command"] = "/ff/bin/ff-sandbox",
                ["args"] = new JsonArray("mcp"),
                ["env"] = new JsonObject(Environment(task, limits).Where(kv => kv.Key.StartsWith("FF_", StringComparison.Ordinal))
                    .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))),
                ["tools"] = new JsonArray("*"),
                ["timeout"] = (long)McpToolTimeout(limits).TotalMilliseconds,
            },
        },
    };

    /// <summary>
    /// The environment of the runtime and of the sandbox client it starts. It holds the step's token as the custom
    /// provider's key and the proxy's URL, never a model or GitHub credential (F6). <c>HOME</c> is the engine's (DR19).
    /// </summary>
    public IReadOnlyDictionary<string, string> Environment(AgentTask task, AgentLimits limits)
    {
        var engine = task.Engine ?? throw new InvalidOperationException("an agent task in a sandbox needs the engine's endpoints");
        var paths = task.Paths ?? throw new InvalidOperationException("an agent task in a sandbox needs its paths");
        return new Dictionary<string, string>
        {
            ["HOME"] = paths.Home,
            ["COPILOT_HOME"] = Home,
            ["COPILOT_OFFLINE"] = "true",
            ["COPILOT_AUTO_UPDATE"] = "false",
            ["COPILOT_PROVIDER_TYPE"] = "anthropic",
            ["COPILOT_PROVIDER_BASE_URL"] = engine.ModelBaseUrl,
            ["COPILOT_PROVIDER_API_KEY"] = engine.StepToken,
            ["COPILOT_MODEL"] = task.Model ?? options.Model,
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
    }

    /// <summary>The role's Copilot tools; a role with no write access gets no file-editing tool (its checkout is read-only anyway).</summary>
    public List<string> Tools(AgentTask task) => task.Tools.SelectMany(c => ToolClasses.GetValueOrDefault(c, []))
        .Where(t => task.WriteAccess != "none" || !ToolClasses["edit"].Contains(t)).Distinct().ToList();

    bool Engine(AgentTask task) => task.EngineTools && options.EngineTools;

    /// <summary>The command line, reading the prompt from a file on standard input so no task text sits in an argument.</summary>
    public string CommandLine(AgentTask task, string promptFile, bool resume)
    {
        var tools = Tools(task);
        var paths = task.Paths!;
        var parts = new List<string>
        {
            options.Executable, "--output-format", "json", resume ? "--resume" : "--session-id", SessionId(task.Step.IdempotencyKey),
            "--model", task.Model ?? options.Model,
            "--no-ask-user", "--disable-builtin-mcps", "--no-custom-instructions", "--no-auto-update", "--disable-client-telemetry",
            "--add-dir", paths.Output, "--add-dir", Path.GetDirectoryName(paths.InputsManifest)!.Replace('\\', '/'),
            "--usage-output-file", $"{StateFolder}/copilot-usage-{task.Session}.json",
            "--log-dir", $"{StateFolder}/copilot-logs",
        };
        if (tools.Any(ToolClasses["edit"].Contains))
            parts.AddRange(["--allow-tool", "write"]);
        if (tools.Contains("bash"))
            parts.AddRange(["--allow-tool", "shell"]);
        if (Engine(task))
            parts.AddRange(["--allow-tool", "engine", "--additional-mcp-config", $"@{AgentFolder}/mcp.json"]);
        // Last, as it takes every value up to the next option.
        parts.Add("--available-tools");
        parts.AddRange(tools.DefaultIfEmpty("view"));
        if (Engine(task))
            parts.Add("engine");
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
            return Session(sandbox, key, task.Session);

        // COPILOT_HOME is root's and sticky: the CLI may add its own state, but nobody else's files can be removed or replaced.
        var home = await sandboxes1.RunAsync(sandbox, new SandboxCommand(["sh", "-c", $"mkdir -p {Home} && chown root {Home} && chmod 1777 {Home}"], AsRoot: true),
            $"{key}/home", cancellationToken);
        if (!home.Ok)
            return new ProviderResult<AgentSession>(null, home.Error);
        if (home.Value.Exit != 0)
            return ProviderResult<AgentSession>.Failure("server_error", "the Copilot home could not be created: " + home.Value.Output);
        var promptFile = $"{AgentFolder}/prompt-{task.Session}.md";
        if (!Engine(task))
            // The runtime knows the tool is absent; the preamble's fallback rule applies, and the agent is told so.
            prompt += $"\n\nThe engine's report tool is not available in this session. Report by writing the JSON report to report.json in {task.Paths!.Output}.";
        var files = new List<(string, string)>
        {
            ($"{Home}/settings.json", Settings().ToJsonString()),
            ($"{Home}/mcp-config.json", """{"mcpServers":{}}"""),
            (promptFile, prompt),
        };
        if (Engine(task))
            files.Add(($"{AgentFolder}/mcp.json", McpConfig(task, limits).ToJsonString()));
        foreach (var (path, content) in files)
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
        return started.Ok ? Session(sandbox, key, task.Session) : new ProviderResult<AgentSession>(null, started.Error);
    }

    ProviderResult<AgentSession> Session(SandboxHandle sandbox, string key, int session)
    {
        progress.TryAdd(key, (sandbox, 0, 0, session));
        return ProviderResult<AgentSession>.Success(new AgentSession(key, "copilot_cli"));
    }

    public async Task<ProviderResult<AgentSession>> AttachAsync(SandboxHandle sandbox, AgentTask task, CancellationToken cancellationToken)
    {
        var key = CommandKey(task);
        var found = await sandboxes().AttachCommandAsync(sandbox, key, cancellationToken);
        return found.Ok ? Session(sandbox, key, task.Session) : new ProviderResult<AgentSession>(null, found.Error);
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
        var turns = state.Turns + status.Value!.Output.Split('\n').Count(l => l.StartsWith("{\"type\":\"assistant.message\"", StringComparison.Ordinal));
        progress[session.Id] = (state.Sandbox, status.Value.Cursor, turns, state.Session);
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
        // The usage file is the agent's user's to write: it serves accounting only; the proxy records what was really used.
        var usage = await sandbox.RunAsync(state.Sandbox, new SandboxCommand(["sh", "-c", $"cat {StateFolder}/copilot-usage-{state.Session}.json 2>/dev/null; true"], AsRoot: true),
            $"{session.Id}/usage/{Guid.NewGuid():N}", cancellationToken);
        var parsed = Parse(transcript, usage.Ok ? usage.Value.Output : "");
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
            SessionEnd.Failed => parsed.ApiError is not null ? $"the model answered {parsed.ApiStatus}: {parsed.ApiError}" : $"copilot exited {status.Value.ExitCode}: {parsed.Result}",
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
    /// How a model API error ends the session, as for Claude Code (LOG 98): no status (the connection failed after the CLI's
    /// own retries), 429 and 5xx are transient, so resumable; 401 and 403 are a configuration fault; any other 4xx is permanent.
    /// </summary>
    public static string ApiEnd(int? status) => status switch
    {
        null or 429 or >= 500 => SessionEnd.ModelUnreachable,
        401 or 403 => SessionEnd.ModelRefused,
        _ => SessionEnd.Failed,
    };

    /// <summary>
    /// Reads Copilot's JSON-lines output: the last assistant message, a session error and its status, and the files its tools
    /// opened; and the usage file it wrote, for the token counts its output lacks.
    /// </summary>
    public static Transcript Parse(string output, string usage)
    {
        string? result = null, apiError = null;
        int? apiStatus = null;
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

            var data = record?["data"];
            var type = record?["type"]?.GetValue<string>();
            if (type == "assistant.message" && data?["content"]?.ToString() is { Length: > 0 } content)
                result = content;
            else if (type == "tool.execution_start" && data?["toolName"]?.ToString() is "view" or "create" or "edit"
                     && data["arguments"]?["path"]?.ToString() is { } file)
                files.Add(file);
            else if (type == "session.error")
            {
                apiError = data?["message"]?.ToString() ?? "session error";
                apiStatus = data?["statusCode"] is JsonValue code && code.TryGetValue<int>(out var n) ? n
                    : data?["errorType"]?.ToString() == "authentication" ? 401 : null;
            }
        }

        long input = 0, outputTokens = 0;
        try
        {
            if (usage.Trim().Length > 0 && JsonNode.Parse(usage)?["modelMetrics"] is JsonObject metrics)
                foreach (var (_, model) in metrics)
                {
                    input += model?["usage"]?["inputTokens"]?.GetValue<long>() ?? 0;
                    outputTokens += model?["usage"]?["outputTokens"]?.GetValue<long>() ?? 0;
                }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
        }

        return new Transcript(result, apiError, input, outputTokens, files.Distinct().ToList(), apiStatus);
    }

    static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class CopilotCliProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } = [Runtime()];

    /// <summary>The runtime's factory. <paramref name="tune"/> lets a test change the fixed defaults (LOG 91).</summary>
    public static ProviderFactory Runtime(Func<CopilotCliOptions, CopilotCliOptions>? tune = null) =>
        new("agent_runtime", "copilot_cli", ["model", "executable", "model_access", "engine_tools"], c =>
        {
            // The model is reached through the engine's proxy alone (F6); the CLI's own GitHub sign-in is never used.
            if (c.Instance.Settings["model_access"] is not YMap access || access["mode"].ScalarText() != "proxy")
                throw new InvalidOperationException($"providers.agent_runtime.instances.{c.Instance.Name} needs model_access with mode proxy (F6)");
            var options = new CopilotCliOptions(c.Setting("model") ?? "claude-haiku-4-5", c.Setting("executable") ?? "copilot", EngineTools: c.Setting("engine_tools") != "false");
            return new CopilotCliRuntime(
                tune?.Invoke(options) ?? options,
                () => c.Provider("sandbox") as ISandboxProvider ?? throw new InvalidOperationException("Copilot CLI runs in a sandbox: configure a sandbox provider"),
                () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("Copilot CLI needs an artifact store"));
        });
}
