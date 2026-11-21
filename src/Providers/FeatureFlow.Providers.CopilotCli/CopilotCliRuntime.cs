using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.CopilotCli;

/// <summary>Settings of a Copilot CLI runtime instance (docs/07 section 2): <c>model</c> and <c>executable</c>; the rest are fixed defaults (LOG 91).</summary>
public sealed record CopilotCliOptions(string Model, string Executable = "copilot", TimeSpan? HookHold = null, TimeSpan? ClientRetry = null)
    : AgentRuntimeOptions(Model, Executable, HookHold, ClientRetry);

/// <summary>
/// The GitHub Copilot CLI runtime (docs/06 section 3.2), run in a sandbox, ported from Spike 2's provider:
/// <c>copilot</c> in prompt mode with JSON-lines output, the prompt on standard input, a session id fixed from the step's
/// key so a continuation resumes the same conversation.
/// <para>
/// The model is reached through the model proxy of its instance's <c>model_access</c> as a custom provider of the
/// Anthropic Messages API, with the step's token as its key and <c>COPILOT_OFFLINE</c>, so the CLI holds no model or
/// GitHub credential and makes no other call (F6, LOG 293). Its settings come from the engine alone: <c>COPILOT_HOME</c>
/// is a root-owned sticky folder whose <c>settings.json</c> (the hooks) and <c>mcp-config.json</c> are root's and
/// read-only, so the agent can neither change nor replace them; the checkout is not trusted, so a repository's hooks,
/// MCP servers and instructions are not loaded; built-in MCP servers are off, and only the role's tools are available.
/// </para>
/// </summary>
public sealed class CopilotCliRuntime(CopilotCliOptions options, Func<ISandboxProvider> sandboxes, Func<IArtifactStoreProvider> store)
    : SandboxedAgentRuntime(options, sandboxes, store)
{
    public const string Home = "/ff/copilot";

    protected override string Kind => "copilot_cli";

    public override IReadOnlyDictionary<string, IReadOnlyList<string>> ToolClasses { get; } = new Dictionary<string, IReadOnlyList<string>>
    {
        [ToolClass.Read] = ["view"],
        [ToolClass.Search] = ["grep", "glob"],
        [ToolClass.Edit] = ["create", "edit"],
        [ToolClass.Shell] = ["bash"],
    };

    /// <summary>
    /// Copilot's hooks report its tools by Claude Code's names (<c>view</c> is <c>Read</c>, <c>create</c> is <c>Write</c>,
    /// <c>bash</c> is <c>Bash</c>; LOG 293); its own names, as offered, are read too (LOG 445).
    /// </summary>
    protected override IReadOnlyDictionary<string, string> CompanionTools { get; } = new Dictionary<string, string>
    {
        ["Read"] = "view",
        ["Grep"] = "grep",
        ["Glob"] = "glob",
        ["Write"] = "create",
        ["Edit"] = "edit",
        ["Bash"] = "bash",
    };

    protected override IReadOnlyList<string> PathFields { get; } = ["path", "file_path"];

    protected override string EngineToolPrefix => "engine-";

    protected override string TurnMarker => "{\"type\":\"assistant.message\"";

    /// <summary>The session id for a step's key: stable, so a continuation resumes the same conversation.</summary>
    public static string SessionId(string stepKey) => StableSessionId("copilot/" + stepKey);

    /// <summary>The user settings: the hook command for every hook Copilot fires, with its timeout (DR16), and nothing else.</summary>
    public JsonObject Settings()
    {
        JsonArray Hook(string name) => new(new JsonObject
        {
            ["type"] = "command",
            ["command"] = HookCommand(name),
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
                ["env"] = ClientEnvironment(task, limits),
                ["tools"] = new JsonArray("*"),
                ["timeout"] = (long)McpToolTimeout(limits).TotalMilliseconds,
            },
        },
    };

    /// <summary>The step's token is the custom provider's key; no model or GitHub credential (F6).</summary>
    protected override IReadOnlyDictionary<string, string> RuntimeEnvironment(AgentTask task, AgentLimits limits, EngineEndpoints engine, AgentPaths paths) =>
        new Dictionary<string, string>
        {
            ["COPILOT_HOME"] = Home,
            ["COPILOT_OFFLINE"] = "true",
            ["COPILOT_AUTO_UPDATE"] = "false",
            ["COPILOT_PROVIDER_TYPE"] = "anthropic",
            ["COPILOT_PROVIDER_BASE_URL"] = engine.ModelBaseUrl,
            ["COPILOT_PROVIDER_API_KEY"] = engine.StepToken,
            ["COPILOT_MODEL"] = task.Model ?? Options.Model,
        };

    /// <summary>COPILOT_HOME is root's and sticky: the CLI may add its own state, but nobody else's files can be removed or replaced.</summary>
    protected override async Task<ProviderResult<Unit>> PrepareAsync(ISandboxProvider sandboxes, SandboxHandle sandbox, string key, CancellationToken cancellationToken)
    {
        var home = await sandboxes.RunAsync(sandbox, new SandboxCommand(["sh", "-c", $"mkdir -p {Home} && chown root {Home} && chmod 1777 {Home}"], AsRoot: true),
            $"{key}/home", cancellationToken);
        if (!home.Ok)
            return new ProviderResult<Unit>(default, home.Error);
        return home.Value.Exit == 0 ? ProviderResult<Unit>.Success(default)
            : ProviderResult<Unit>.Failure("server_error", "the Copilot home could not be created: " + home.Value.Output);
    }

    protected override IEnumerable<(string Path, string Content)> Files(AgentTask task, AgentLimits limits) =>
    [
        ($"{Home}/settings.json", Settings().ToJsonString()),
        ($"{Home}/mcp-config.json", """{"mcpServers":{}}"""),
        ($"{AgentFolder}/mcp.json", McpConfig(task, limits).ToJsonString()),
    ];

    public override string CommandLine(AgentTask task, string promptFile, bool resume)
    {
        var tools = Tools(task);
        var paths = task.Paths!;
        var parts = new List<string>
        {
            Options.Executable, "--output-format", "json", resume ? "--resume" : "--session-id", SessionId(task.Step.IdempotencyKey),
            "--model", task.Model ?? Options.Model,
            "--no-ask-user", "--disable-builtin-mcps", "--no-custom-instructions", "--no-auto-update", "--disable-client-telemetry",
            // The sandbox is the boundary for files and network (a read-only checkout, a closed network): Copilot's own checks
            // of paths and URLs in a command would only refuse a build's caches and registries. Never --allow-all-tools or
            // --allow-all, which trust the folder and load the repository's hooks and MCP servers.
            "--allow-all-paths", "--allow-all-urls",
            "--add-dir", paths.Output, "--add-dir", Path.GetDirectoryName(paths.InputsManifest)!.Replace('\\', '/'),
            "--usage-output-file", $"{StateFolder}/copilot-usage-{task.Session}.json",
            "--log-dir", $"{StateFolder}/copilot-logs",
        };
        if (tools.Any(ToolClasses[ToolClass.Edit].Contains))
            parts.AddRange(["--allow-tool", "write"]);
        if (tools.Contains("bash"))
            parts.AddRange(["--allow-tool", "shell"]);
        parts.AddRange(["--allow-tool", "engine", "--additional-mcp-config", $"@{AgentFolder}/mcp.json"]);
        // Last, as it takes every value up to the next option.
        parts.Add("--available-tools");
        parts.AddRange(tools.DefaultIfEmpty("view"));
        parts.Add("engine");
        return "exec " + string.Join(" ", parts.Select(Quote)) + " < " + Quote(promptFile);
    }

    /// <summary>Copilot shows the agent only the exit code of a hook that exits 2, so a denial is its JSON decision with the reason (LOG 293).</summary>
    public override HookReply Deny(string reason) =>
        new(0, Output: new JsonObject { ["permissionDecision"] = "deny", ["permissionDecisionReason"] = reason }.ToJsonString());

    /// <summary>
    /// Copilot is not known to stop on <c>continue: false</c> (M8 follow-up 15), so the call is blocked by exit 2, which
    /// Copilot honours and shows the agent nothing of (LOG 293): no engine error reaches the agent (LOG 60).
    /// </summary>
    protected override HookReply Unreachable { get; } = new(2);

    /// <summary>The output and the usage file Copilot wrote, which is the agent's user's to write: it serves accounting only; the proxy records what was really used.</summary>
    protected override async Task<Transcript> ReadAsync(SandboxHandle sandbox, string output, int session, string key, CancellationToken cancellationToken)
    {
        var usage = await Sandboxes.RunAsync(sandbox, new SandboxCommand(["sh", "-c", $"cat {StateFolder}/copilot-usage-{session}.json 2>/dev/null; true"], AsRoot: true),
            $"{key}/usage/{Guid.NewGuid():N}", cancellationToken);
        return Parse(output, usage.Ok ? usage.Value.Output : "");
    }

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
}

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class CopilotCliProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } = [Runtime()];

    /// <summary>The runtime's factory. <paramref name="tune"/> lets a test change the fixed defaults (LOG 91).</summary>
    public static ProviderFactory Runtime(Func<CopilotCliOptions, CopilotCliOptions>? tune = null) =>
        new("agent_runtime", "copilot_cli", ["model", "executable", "model_access"], c =>
        {
            // The model is reached through the engine's proxy alone (F6); the CLI's own GitHub sign-in is never used.
            if (c.Instance.Settings["model_access"] is not YMap access || access["mode"].ScalarText() != "proxy")
                throw new InvalidOperationException($"providers.agent_runtime.instances.{c.Instance.Name} needs model_access with mode proxy (F6)");
            var options = new CopilotCliOptions(c.Setting("model") ?? "claude-haiku-4-5", c.Setting("executable") ?? "copilot");
            return new CopilotCliRuntime(
                tune?.Invoke(options) ?? options,
                () => c.Provider("sandbox") as ISandboxProvider ?? throw new InvalidOperationException("Copilot CLI runs in a sandbox: configure a sandbox provider"),
                () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("Copilot CLI needs an artifact store"));
        });
}
