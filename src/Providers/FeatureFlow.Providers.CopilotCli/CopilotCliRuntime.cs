using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.CopilotCli;

/// <summary>
/// Settings of a Copilot CLI runtime instance (docs/07 section 2): <c>model</c> and <c>executable</c>, and <see cref="GitHub"/>,
/// whether its <c>model_access.auth</c> is <c>github</c>; the rest are fixed defaults (LOG 91).
/// </summary>
public sealed record CopilotCliOptions(string Model, string Executable = "copilot", TimeSpan? HookHold = null, TimeSpan? ClientRetry = null, bool GitHub = false)
    : AgentRuntimeOptions(Model, Executable, HookHold, ClientRetry);

/// <summary>
/// The GitHub Copilot CLI runtime (docs/06 section 3.2), run in a sandbox, ported from Spike 2's provider:
/// <c>copilot</c> in prompt mode with JSON-lines output, the prompt on standard input, a session id fixed from the step's
/// key so a continuation resumes the same conversation.
/// <para>
/// The model is reached only through the model proxy of its instance's <c>model_access</c>, with the step's token as the
/// CLI's only credential (F6, LOG 293, 497). With <c>auth: github</c> the CLI runs natively, on Copilot quota: the step's
/// token, prefixed <c>ghs_</c>, is its GitHub token and the proxy is its Copilot API (<c>COPILOT_API_URL</c>), so its
/// own requests reach the proxy, which holds the real GitHub token. Otherwise (<c>api_key</c>, <c>oauth</c>) the proxy is
/// a custom provider of the Anthropic Messages API, with the step's token as its key and <c>COPILOT_OFFLINE</c>.
/// </para>
/// <para>
/// Its settings come from the engine alone: <c>COPILOT_HOME</c> is the step's own root-owned sticky folder, whose
/// <c>settings.json</c> (the hooks) and <c>mcp-config.json</c> are root's and read-only, so the agent can neither change nor
/// replace them. It is made fresh at the step's first session, and a continuation keeps only the session's state, so
/// nothing the agent adds reaches another step or a later start-up; the checkout is not trusted, so a repository's hooks,
/// MCP servers and instructions are not loaded; built-in MCP servers are off, and only the role's tools are available.
/// </para>
/// </summary>
public sealed class CopilotCliRuntime(CopilotCliOptions options, Func<ISandboxProvider> sandboxes, Func<IArtifactStoreProvider> store)
    : SandboxedAgentRuntime(options, sandboxes, store)
{
    /// <summary>The folder of every step's <c>COPILOT_HOME</c>.</summary>
    public const string Homes = "/ff/copilot";

    /// <summary>
    /// A step's <c>COPILOT_HOME</c>, by its key (M8 follow-up 15). The sandbox is the step's and is removed with it; the path is
    /// the step's too, so no other step's sessions could read what an agent left in it.
    /// </summary>
    public static string Home(string stepKey) =>
        Homes + "/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(stepKey)))[..16];

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

    protected override TranscriptFormat Format => TranscriptFormat.CopilotCli;

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

    /// <summary>
    /// The step's token is the CLI's only credential (F6): with <c>auth: github</c>, prefixed <c>ghs_</c>, its GitHub token,
    /// which outranks <c>GH_TOKEN</c> and <c>GITHUB_TOKEN</c>, with the proxy as its Copilot API and offline mode off, as the
    /// CLI refuses offline without a custom provider (LOG 497); otherwise the custom provider's key, offline.
    /// </summary>
    protected override IReadOnlyDictionary<string, string> RuntimeEnvironment(AgentTask task, AgentLimits limits, EngineEndpoints engine, AgentPaths paths)
    {
        var env = new Dictionary<string, string>
        {
            ["COPILOT_HOME"] = Home(task.Step.IdempotencyKey),
            ["COPILOT_AUTO_UPDATE"] = "false",
            ["COPILOT_MODEL"] = task.Model ?? Options.Model,
        };
        if (options.GitHub)
        {
            env["COPILOT_GITHUB_TOKEN"] = EngineEndpoints.GitHubTokenPrefix + engine.StepToken;
            env["COPILOT_API_URL"] = engine.ModelBaseUrl;
            env["COPILOT_TELEMETRY_DISABLED"] = "true";
        }
        else
        {
            env["COPILOT_OFFLINE"] = "true";
            env["COPILOT_PROVIDER_TYPE"] = "anthropic";
            env["COPILOT_PROVIDER_BASE_URL"] = engine.ModelBaseUrl;
            env["COPILOT_PROVIDER_API_KEY"] = engine.StepToken;
        }

        return env;
    }

    /// <summary>What a continuation keeps of the step's <c>COPILOT_HOME</c>: only what <c>--resume</c> reads (LOG 497).</summary>
    public static readonly IReadOnlyList<string> Resumed = ["session-state", "session-store.db*", "config.json"];

    /// <summary>
    /// The step's <c>COPILOT_HOME</c> is root's and sticky: the CLI may add its own state, but nobody else's files can be
    /// removed or replaced. A new session gets it empty; a continuation keeps only the session's state (<see cref="Resumed"/>),
    /// so nothing else the agent added, such as a <c>settings.local.json</c> or a skill, reaches the CLI's start-up.
    /// </summary>
    protected override async Task<ProviderResult<Unit>> PrepareAsync(ISandboxProvider sandboxes, SandboxHandle sandbox, AgentTask task, bool resume, string key,
        CancellationToken cancellationToken)
    {
        var home = Home(task.Step.IdempotencyKey);
        var clear = resume
            ? $"mkdir -p {home} && find {home} -mindepth 1 -maxdepth 1 {string.Join(" ", Resumed.Select(n => $"! -name '{n}'"))} -exec rm -rf {{}} + && "
            : $"rm -rf {home} && mkdir -p {home} && ";
        var made = await sandboxes.RunAsync(sandbox, new SandboxCommand(["sh", "-c", clear + $"chown root {home} && chmod 1777 {home}"], AsRoot: true),
            $"{key}/home", cancellationToken);
        if (!made.Ok)
            return new ProviderResult<Unit>(default, made.Error);
        return made.Value.Exit == 0 ? ProviderResult<Unit>.Success(default)
            : ProviderResult<Unit>.Failure("server_error", "the Copilot home could not be created: " + made.Value.Output);
    }

    protected override IEnumerable<(string Path, string Content)> Files(AgentTask task, AgentLimits limits) =>
    [
        ($"{Home(task.Step.IdempotencyKey)}/settings.json", Settings().ToJsonString()),
        ($"{Home(task.Step.IdempotencyKey)}/mcp-config.json", """{"mcpServers":{}}"""),
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
        // Copilot asks its write permission for a shell redirection into a file too, so a role with the shell gets it, or
        // with --no-ask-user it cannot write its own output folder. The edit tools stay out of --available-tools; the
        // sandbox is the boundary (a read-only checkout for a role without edit, a writable output folder).
        if (tools.Any(ToolClasses[ToolClass.Edit].Contains) || tools.Contains("bash"))
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
            // The model is reached through the engine's proxy alone (F6); with auth github the CLI's GitHub token is the step's.
            if (c.Instance.Settings["model_access"] is not YMap access || access["mode"].ScalarText() != "proxy")
                throw new InvalidOperationException($"providers.agent_runtime.instances.{c.Instance.Name} needs model_access with mode proxy (F6)");
            var options = new CopilotCliOptions(c.Setting("model") ?? "claude-haiku-4-5", c.Setting("executable") ?? "copilot",
                GitHub: access["auth"].ScalarText() == "github");
            return new CopilotCliRuntime(
                tune?.Invoke(options) ?? options,
                () => c.Provider("sandbox") as ISandboxProvider ?? throw new InvalidOperationException("Copilot CLI runs in a sandbox: configure a sandbox provider"),
                () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("Copilot CLI needs an artifact store"));
        });
}
