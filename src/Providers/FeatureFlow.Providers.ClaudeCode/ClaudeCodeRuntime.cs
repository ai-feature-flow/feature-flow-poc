using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.ClaudeCode;

/// <summary>
/// Settings of a Claude Code runtime instance (docs/07 section 2): <c>model</c> and <c>executable</c>. <see cref="MaxRetries"/>,
/// like the hold and the client's retry, is a fixed default, not a setting (LOG 91); only tests change it.
/// </summary>
public sealed record ClaudeCodeOptions(string Model, string Executable = "claude", int? MaxRetries = null, TimeSpan? HookHold = null, TimeSpan? ClientRetry = null)
    : AgentRuntimeOptions(Model, Executable, HookHold, ClientRetry);

/// <summary>
/// The Claude Code runtime (docs/06 section 3.2), run in a sandbox. Ported in spirit from Spike 2's provider:
/// <c>claude -p</c> with stream-json output and a session id fixed from the step's key so a new worker can resume it,
/// and usage read from the result record. Its settings come from the engine alone: user settings from the
/// engine-controlled home, no project settings, and only the engine's MCP server, so a repository cannot add hooks or tools.
/// </summary>
public sealed class ClaudeCodeRuntime(ClaudeCodeOptions options, Func<ISandboxProvider> sandboxes, Func<IArtifactStoreProvider> store)
    : SandboxedAgentRuntime(options, sandboxes, store)
{
    /// <summary>
    /// Claude Code's managed settings: root-owned and read-only in the sandbox, loaded before any other source, with
    /// <c>allowManagedHooksOnly</c> so no hook the agent writes into its own settings runs (LOG 100).
    /// </summary>
    public const string ManagedSettings = "/etc/claude-code/managed-settings.json";

    readonly int? maxRetries = options.MaxRetries;

    protected override string Kind => "claude_code";

    public override IReadOnlyDictionary<string, IReadOnlyList<string>> ToolClasses { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [ToolClass.Read] = ["Read", "LS", "TodoWrite"],
        [ToolClass.Search] = ["Grep", "Glob"],
        [ToolClass.Edit] = ["Edit", "Write", "MultiEdit", "NotebookEdit"],
        [ToolClass.Shell] = ["Bash"],
    };

    /// <summary>Tools Claude Code brings with those offered: a notebook read with Read, a background shell's output and kill with Bash (LOG 445).</summary>
    protected override IReadOnlyDictionary<string, string> CompanionTools { get; } = new Dictionary<string, string>
    {
        ["NotebookRead"] = "Read",
        ["BashOutput"] = "Bash",
        ["KillShell"] = "Bash",
        ["KillBash"] = "Bash",
    };

    protected override IReadOnlyList<string> PathFields { get; } = ["file_path", "notebook_path"];

    protected override string EngineToolPrefix => "mcp__engine__";

    protected override string TurnMarker => "{\"type\":\"assistant\"";

    /// <summary>The session id for a step's key: stable, so a new worker resumes the same conversation.</summary>
    public static string SessionId(string stepKey) => StableSessionId(stepKey);

    /// <summary>The settings file: the hook command for every hook, with its timeout (DR16), and nothing else.</summary>
    public JsonObject Settings()
    {
        JsonArray Hook(string name) => new(new JsonObject
        {
            ["matcher"] = "*",
            ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command",
                ["command"] = HookCommand(name),
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

    protected override IReadOnlyDictionary<string, string> RuntimeEnvironment(AgentTask task, AgentLimits limits, EngineEndpoints engine, AgentPaths paths)
    {
        var env = new Dictionary<string, string>
        {
            ["CLAUDE_CONFIG_DIR"] = paths.Home + "/.claude",
            ["ANTHROPIC_BASE_URL"] = engine.ModelBaseUrl,
            ["ANTHROPIC_AUTH_TOKEN"] = engine.StepToken,
            ["MCP_TOOL_TIMEOUT"] = ((long)McpToolTimeout(limits).TotalMilliseconds).ToString(CultureInfo.InvariantCulture),
            ["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1",
            ["DISABLE_AUTOUPDATER"] = "1",
        };
        if (maxRetries is { } retries)
            env["CLAUDE_CODE_MAX_RETRIES"] = retries.ToString(CultureInfo.InvariantCulture);
        return env;
    }

    protected override IEnumerable<(string Path, string Content)> Files(AgentTask task, AgentLimits limits)
    {
        var mcp = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["engine"] = new JsonObject
                {
                    ["command"] = "/ff/bin/ff-sandbox",
                    ["args"] = new JsonArray("mcp"),
                    ["env"] = ClientEnvironment(task, limits),
                },
            },
        };
        return [(ManagedSettings, Settings().ToJsonString()), ($"{AgentFolder}/mcp.json", mcp.ToJsonString())];
    }

    public override string CommandLine(AgentTask task, string promptFile, bool resume)
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
            Options.Executable, "-p", "--output-format", "stream-json", "--verbose", "--model", task.Model ?? Options.Model,
            resume ? "--resume" : "--session-id", SessionId(task.Step.IdempotencyKey),
            "--setting-sources", "user",
            "--tools", string.Join(",", tools.DefaultIfEmpty("Read")),
            "--allowedTools", string.Join(",", allowed),
            "--add-dir", paths.Output, Path.GetDirectoryName(paths.InputsManifest)!.Replace('\\', '/'),
            "--strict-mcp-config", "--mcp-config", $"{AgentFolder}/mcp.json",
        };
        return "exec " + string.Join(" ", parts.Select(Quote)) + " < " + Quote(promptFile);
    }

    /// <summary>Claude Code's hook protocol: exit 2 blocks the call and shows the agent standard error.</summary>
    public override HookReply Deny(string reason) => new(2, Error: reason);

    /// <summary>Claude Code stops the session on <c>continue: false</c>, before the call runs, and shows the person, not the agent, the reason.</summary>
    protected override HookReply Unreachable { get; } =
        new(0, Output: new JsonObject { ["continue"] = false, ["stopReason"] = "the engine is unreachable; the step can be resumed" }.ToJsonString());

    protected override Task<Transcript> ReadAsync(SandboxHandle sandbox, string output, int session, string key, CancellationToken cancellationToken) =>
        Task.FromResult(Parse(output));

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
}

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class ClaudeCodeProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } = [Runtime()];

    /// <summary>The runtime's factory. <paramref name="tune"/> lets a test change the fixed defaults (LOG 91).</summary>
    public static ProviderFactory Runtime(Func<ClaudeCodeOptions, ClaudeCodeOptions>? tune = null) =>
        new("agent_runtime", "claude_code", ["model", "executable", "model_access"], c =>
        {
            if (c.Instance.Settings["model_access"] is not YMap access || access["mode"].ScalarText() != "proxy")
                throw new InvalidOperationException($"providers.agent_runtime.instances.{c.Instance.Name} needs model_access with mode proxy (F6)");
            // The proxy speaks to an Anthropic API for Claude Code; `github` is Copilot CLI's (LOG 496).
            if (access["auth"].ScalarText() is { } auth && auth is not ("api_key" or "oauth"))
                throw new InvalidOperationException(
                    $"providers.agent_runtime.instances.{c.Instance.Name}: model_access.auth '{auth}' is not for claude_code, which takes api_key or oauth");
            var options = new ClaudeCodeOptions(c.Setting("model") ?? "haiku", c.Setting("executable") ?? "claude");
            return new ClaudeCodeRuntime(
                tune?.Invoke(options) ?? options,
                () => c.Provider("sandbox") as ISandboxProvider ?? throw new InvalidOperationException("Claude Code runs in a sandbox: configure a sandbox provider"),
                () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("Claude Code needs an artifact store"));
        });
}
