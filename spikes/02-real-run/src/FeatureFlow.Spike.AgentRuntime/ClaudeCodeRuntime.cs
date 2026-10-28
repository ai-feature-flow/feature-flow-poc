using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace FeatureFlow.Spike.AgentRuntime;

/// <summary>
/// Configuration for the <c>claude-code</c> named instance of the agent runtime
/// provider.
///
/// docs/09 section 11.1: the model and the endpoint are configuration, never
/// template content, and a role names neither. Nothing here holds a credential
/// value — the model credential comes from the broker at start time (floor F2,
/// docs/04 section 1).
/// </summary>
/// <param name="ExecutablePath">Explicit path to the CLI. Null means resolve it.</param>
/// <param name="Model">The model name. Configuration, never hardcoded.</param>
/// <param name="Endpoint">
/// An optional model-router base URL (bring-your-own-key). Claude Code speaks
/// the Anthropic Messages API, so this is passed as <c>ANTHROPIC_BASE_URL</c>.
/// </param>
/// <param name="HookSettingsPath">
/// A Claude Code settings JSON file that installs the engine's hook endpoint.
/// When set, <c>CheckAsync</c> proves hooks actually fire rather than only
/// declaring the mechanism.
/// </param>
/// <param name="HookProbeDirectory">
/// Where the capability probe writes its own throwaway settings file and hook
/// log. Null means the probe runs without hooks.
/// </param>
/// <param name="PermissionMode">The Claude Code permission mode. Default <c>acceptEdits</c>.</param>
/// <param name="SystemPromptSuffix">Appended to the runtime's own system prompt.</param>
/// <param name="ProbeOnCheck">Whether <c>CheckAsync</c> runs one real session to measure capabilities.</param>
/// <param name="ProbeTimeout">Bound on the probe. docs/05 1.2: a metadata read is 15s; a probe is a session, so it gets more.</param>
public sealed record ClaudeCodeRuntimeOptions(
    string? ExecutablePath = null,
    string? Model = null,
    string? Endpoint = null,
    string? HookSettingsPath = null,
    string? HookProbeDirectory = null,
    string? PermissionMode = null,
    string? SystemPromptSuffix = null,
    bool ProbeOnCheck = true,
    TimeSpan? ProbeTimeout = null)
{
    public TimeSpan EffectiveProbeTimeout => ProbeTimeout ?? TimeSpan.FromSeconds(120);

    public string EffectivePermissionMode =>
        string.IsNullOrWhiteSpace(PermissionMode) ? "acceptEdits" : PermissionMode!;
}

/// <summary>
/// What <c>CheckAsync</c> learned about the installed CLI. Every field is
/// observed, not assumed: docs/08 section 2.2 wants the capabilities a runtime
/// <em>actually</em> provides, and section 2.4 asks whether they work on Windows.
/// </summary>
/// <param name="Version">The CLI's own <c>--version</c> output.</param>
/// <param name="ExecutablePath">The binary that was probed.</param>
/// <param name="Capabilities">The observed capability set.</param>
/// <param name="HookEvents">Hook events the probe actually saw fire, in order.</param>
/// <param name="UsageObserved">Whether the probe's result carried a usage block.</param>
/// <param name="HookSettingsPath">
/// The settings file the probe wrote, so launched sessions install the same
/// hooks the probe measured. Null when hooks were not probed.
/// </param>
/// <param name="Detail">One line, safe to log.</param>
public sealed record ClaudeCodeDetection(
    string Version,
    string ExecutablePath,
    AgentRuntimeCapabilities Capabilities,
    IReadOnlyList<string> HookEvents,
    bool UsageObserved,
    string Detail,
    string? HookSettingsPath = null);

/// <summary>
/// Finds the Claude Code binary.
///
/// The npm install puts a <c>claude.cmd</c> shim on PATH. Launching a
/// <c>.cmd</c> through <see cref="Process"/> means launching <c>cmd.exe</c>,
/// whose process id is not the agent's, so <c>StopAsync</c> could not kill the
/// agent reliably. Resolving the native binary keeps process control honest.
/// </summary>
public static class ClaudeCodeExecutable
{
    /// <summary>Returns the path to a native <c>claude</c> binary, or null.</summary>
    public static string? Resolve(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return File.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

        foreach (var candidate in CandidatesOnPath())
            if (File.Exists(candidate))
                return candidate;

        foreach (var candidate in CandidatesInNpmTree())
            if (File.Exists(candidate))
                return candidate;

        return null;
    }

    private static IEnumerable<string> CandidatesOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
            yield break;

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string candidate;
            try
            {
                candidate = Path.Combine(dir.Trim('"'), "claude.exe");
            }
            catch (ArgumentException)
            {
                continue;
            }

            yield return candidate;
        }
    }

    private static IEnumerable<string> CandidatesInNpmTree()
    {
        var roots = new List<string>();

        var appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
            roots.Add(Path.Combine(appData, "npm"));

        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrEmpty(programFiles))
            roots.Add(Path.Combine(programFiles, "nodejs"));

        foreach (var root in roots)
        {
            yield return Path.Combine(
                root, "node_modules", "@anthropic-ai", "claude-code", "node_modules",
                "@anthropic-ai", "claude-code-win32-x64", "claude.exe");

            yield return Path.Combine(
                root, "node_modules", "@anthropic-ai", "claude-code", "bin", "claude.exe");
        }
    }
}

/// <summary>
/// Probes the installed CLI for its real capabilities.
///
/// Two sources, in order of strength:
///
/// 1. <c>--help</c>, which is the CLI's own statement of what it accepts. A flag
///    that is absent means the capability is absent; no guessing.
/// 2. One real session, which is the only way to learn the context window and
///    whether usage is reported, and — when a hook settings file is supplied —
///    whether hooks actually fire on this platform.
/// </summary>
public static class ClaudeCodeCapabilityDetector
{
    /// <summary>
    /// The conservative set, used before any probe has run. Everything false
    /// rather than optimistically true: a capability claimed and not delivered
    /// fails at step 40, which is what capability negotiation exists to prevent
    /// (docs/04 section 1.2).
    /// </summary>
    public static AgentRuntimeCapabilities Unknown { get; } = new(
        StructuredOutput: false,
        Streaming: false,
        ToolAllowlist: false,
        MaxContextTokens: 0,
        Hooks: false,
        SessionResume: false,
        UsageReporting: false,
        ModelConfiguration: false,
        CustomEndpoint: false);

    public static async Task<ClaudeCodeDetection?> DetectAsync(
        ClaudeCodeRuntimeOptions options,
        string executable,
        string? apiKey,
        CancellationToken ct)
    {
        var version = await RunAsync(executable, new[] { "--version" }, null, apiKey, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        if (version is null)
            return null;

        var help = await RunAsync(executable, new[] { "--help" }, null, apiKey, TimeSpan.FromSeconds(30), ct)
            .ConfigureAwait(false);
        if (help is null)
            return null;

        var structuredOutput = help.Contains("--json-schema", StringComparison.Ordinal);
        var streaming = help.Contains("stream-json", StringComparison.Ordinal);
        var toolAllowlist = help.Contains("--allowed-tools", StringComparison.Ordinal);
        var modelConfiguration = help.Contains("--model", StringComparison.Ordinal);
        var sessionResume = help.Contains("--resume", StringComparison.Ordinal);
        var hookMechanism = help.Contains("--settings", StringComparison.Ordinal);

        var hookEvents = new List<string>();
        var usageObserved = false;
        var maxContextTokens = 0;
        string? probeDetail = null;
        string? hookSettingsPath = options.HookSettingsPath;

        if (options.ProbeOnCheck)
        {
            string? settingsPath = hookSettingsPath;
            string? logPath = null;

            // A probe directory means the caller wants hooks proven, not just
            // declared. The settings file and its log are throwaway, but the
            // settings path is returned so launched sessions can install the
            // same hooks the probe measured.
            if (settingsPath is null && !string.IsNullOrWhiteSpace(options.HookProbeDirectory))
            {
                Directory.CreateDirectory(options.HookProbeDirectory!);
                logPath = Path.Combine(options.HookProbeDirectory!, "hook-events.log");
                settingsPath = Path.Combine(options.HookProbeDirectory!, "hook-settings.json");
                WriteHookSettings(logPath, settingsPath);
                hookSettingsPath = settingsPath;
            }
            else if (settingsPath is not null && !string.IsNullOrWhiteSpace(options.HookProbeDirectory))
            {
                logPath = Path.Combine(options.HookProbeDirectory!, "hook-events.log");
            }

            var args = new List<string> { "-p", "--output-format", "json", "--no-session-persistence" };
            if (!string.IsNullOrWhiteSpace(options.Model))
                args.AddRange(new[] { "--model", options.Model! });
            if (settingsPath is not null)
                args.AddRange(new[] { "--settings", settingsPath });
            args.Add("Reply with the single word: ok");

            var probe = await RunAsync(
                executable, args, options.Endpoint, apiKey, options.EffectiveProbeTimeout, ct)
                .ConfigureAwait(false);

            if (probe is not null)
            {
                usageObserved = probe.Contains("\"usage\"", StringComparison.Ordinal);
                maxContextTokens = ReadContextWindow(probe);
                probeDetail = $"probe: usage={usageObserved} contextWindow={maxContextTokens}";
            }

            if (logPath is not null && File.Exists(logPath))
            {
                hookEvents.AddRange(
                    File.ReadAllLines(logPath)
                        .Select(l => l.Trim())
                        .Where(l => l.Length > 0));
            }
        }

        var hooks = hookEvents.Count > 0 || hookMechanism;

        var capabilities = new AgentRuntimeCapabilities(
            StructuredOutput: structuredOutput,
            Streaming: streaming,
            ToolAllowlist: toolAllowlist,
            MaxContextTokens: maxContextTokens,
            Hooks: hooks,
            SessionResume: sessionResume,
            UsageReporting: usageObserved,
            ModelConfiguration: modelConfiguration,
            CustomEndpoint: true);   // ANTHROPIC_BASE_URL; evidenced by the harness

        var detail = $"claude {version.Trim()} at {executable}; " +
                     $"hooks={(hookEvents.Count > 0 ? string.Join(",", hookEvents) : (hookMechanism ? "mechanism only" : "none"))}" +
                     (probeDetail is null ? "" : $"; {probeDetail}");

        return new ClaudeCodeDetection(
            version.Trim(), executable, capabilities, hookEvents, usageObserved, detail, hookSettingsPath);
    }

    /// <summary>
    /// Writes a Claude Code settings file whose hooks append their own name to
    /// <paramref name="logPath"/>.
    ///
    /// Proving a hook fired is the whole point: docs/08 section 2.4 asks whether
    /// hooks work on Windows, and only an observed event answers that. The
    /// caller passes the result as
    /// <see cref="ClaudeCodeRuntimeOptions.HookSettingsPath"/> so launched
    /// sessions install the same hooks the probe measured.
    /// </summary>
    public static string WriteHookSettings(string logPath, string settingsPath)
    {
        var directory = Path.GetDirectoryName(settingsPath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var escaped = logPath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);

        string Hook(string name)
        {
            var command =
                "powershell -NoProfile -Command \\\"Add-Content -Path '" + escaped +
                "' -Value '" + name + "'\\\"";

            return "{ \"matcher\": \"\", \"hooks\": [ { \"type\": \"command\", \"command\": \"" + command + "\" } ] }";
        }

        var json =
            "{\n" +
            "  \"hooks\": {\n" +
            "    \"SessionStart\": [ " + Hook("SessionStart") + " ],\n" +
            "    \"PreToolUse\":   [ " + Hook("PreToolUse") + " ],\n" +
            "    \"PostToolUse\":  [ " + Hook("PostToolUse") + " ],\n" +
            "    \"Stop\":         [ " + Hook("Stop") + " ]\n" +
            "  }\n" +
            "}";

        File.WriteAllText(settingsPath, json);
        return settingsPath;
    }

    private static int ReadContextWindow(string resultJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(resultJson);
            if (!doc.RootElement.TryGetProperty("modelUsage", out var modelUsage))
                return 0;

            foreach (var property in modelUsage.EnumerateObject())
            {
                if (property.Value.TryGetProperty("contextWindow", out var window)
                    && window.TryGetInt32(out var tokens))
                {
                    return tokens;
                }
            }
        }
        catch (JsonException)
        {
            // A result we cannot parse is not a capability statement.
        }

        return 0;
    }

    private static async Task<string?> RunAsync(
        string executable,
        IReadOnlyList<string> args,
        string? endpoint,
        string? apiKey,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        if (!string.IsNullOrWhiteSpace(endpoint))
            startInfo.Environment["ANTHROPIC_BASE_URL"] = endpoint;
        if (!string.IsNullOrWhiteSpace(apiKey))
            startInfo.Environment["ANTHROPIC_API_KEY"] = apiKey;

        try
        {
            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            var output = await stdout.ConfigureAwait(false);
            await stderr.ConfigureAwait(false);

            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is OperationCanceledException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
