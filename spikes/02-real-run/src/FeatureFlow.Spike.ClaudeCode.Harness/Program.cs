using System.Diagnostics;
using System.Globalization;
using FeatureFlow.Spike.AgentRuntime;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

// Spike 1's contracts already define an AgentProgress for the interpreter's
// heartbeat payload. The agent runtime's own progress record is the one this
// harness reads, so it is aliased rather than renamed: the shared interface
// shape is the sibling's to own.
using AgentProgress = FeatureFlow.Spike.AgentRuntime.AgentProgress;

namespace FeatureFlow.Spike.ClaudeCodeHarness;

/// <summary>
/// Runs the <c>claude-code</c> agent runtime instance against the real
/// <c>claude</c> CLI and the real fixture repository, and prints what actually
/// happened.
///
/// The six checks, the same six the Copilot CLI sibling proves so the two
/// capability tables can sit side by side in docs/16:
///
/// 1. <see cref="IAgentRuntimeProvider.CheckAsync"/> succeeds and reports the
///    real capability set.
/// 2. A task is started and a handle returned <b>without waiting</b>.
/// 3. Progress is polled and reported.
/// 4. The result comes from <see cref="IAgentRuntimeProvider.GetResultAsync"/>,
///    not from a hook.
/// 5. A retry <b>resumes</b> the same session, if the capability exists.
/// 6. A timeout is classified <see cref="ErrorClass.Work"/>, not
///    <see cref="ErrorClass.Infrastructure"/>.
///
/// What is real and what is not:
///
/// - **Real:** the <c>claude</c> binary, its process, its stream-json output,
///   its session files, its hooks, its exit codes, the git repository it runs
///   in, and the model endpoint it calls.
/// - **Not real:** the model behind the endpoint. No Anthropic credential exists
///   on this machine, so the endpoint is a local Anthropic-compatible router
///   (<c>tools/mock-model-router.py</c>). It is a test double for the *model*,
///   not for the runtime. Every claim this harness makes about the runtime is
///   observed from the real CLI.
/// </summary>
public static class Program
{
    private const string Owner = "ai-feature-flow";
    private const string Repo = "feature-flow-example";

    /// <summary>Where the fixture repository is checked out. Real, cloned from GitHub.</summary>
    private static readonly string FixtureDirectory =
        Environment.GetEnvironmentVariable("FF_FIXTURE_DIR")
        ?? @"D:\Dev\feature-flow-poc\.spike\fixture\feature-flow-example";

    /// <summary>The local model router. Configuration, not template content (docs/09 11.1).</summary>
    private static readonly string Endpoint =
        Environment.GetEnvironmentVariable("FF_MODEL_ENDPOINT") ?? "http://127.0.0.1:8791";

    private static readonly string Model =
        Environment.GetEnvironmentVariable("FF_MODEL") ?? "mock-claude-sonnet-4-6";

    private static readonly string ProbeDirectory =
        Environment.GetEnvironmentVariable("FF_HOOK_PROBE_DIR")
        ?? @"D:\Dev\feature-flow-poc\.spike\claude-hookprobe";

    private static int failures;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        WriteHeader("Spike 2, claude-code agent runtime instance, real run");
        Console.WriteLine($"repository:  {Owner}/{Repo}");
        Console.WriteLine($"checkout:    {FixtureDirectory}");
        Console.WriteLine($"endpoint:    {Endpoint}");
        Console.WriteLine($"model:       {Model}  (configuration, not template content)");
        Console.WriteLine($"time:        {DateTimeOffset.UtcNow:O}");
        Console.WriteLine();

        if (!Directory.Exists(FixtureDirectory))
        {
            Console.Error.WriteLine($"error: the fixture checkout is missing at {FixtureDirectory}");
            Console.Error.WriteLine("clone it: git clone https://github.com/ai-feature-flow/feature-flow-example <dir>");
            return 2;
        }

        var head = ReadHead();
        Console.WriteLine($"fixture HEAD: {head ?? "(not a git repository)"}");
        Console.WriteLine();

        // ---------------------------------------------------------------- setup
        // The model credential comes from the broker, which resolves it by
        // reference from a secrets file outside the repository (floor F2). No
        // credential value is ever printed; only its fingerprint and expiry.
        var secrets = new LocalFileSecretsProvider();
        var broker = new ModelGatewayCredentialBroker(
            secrets,
            grantable: new[]
            {
                GrantableScope.ForRepository($"{Owner}/{Repo}", "model:write"),
            });

        var options = new ClaudeCodeRuntimeOptions(
            Model: Model,
            Endpoint: Endpoint,
            HookProbeDirectory: ProbeDirectory,
            PermissionMode: "acceptEdits",
            ProbeOnCheck: true,
            ProbeTimeout: TimeSpan.FromSeconds(120));

        var provider = new ClaudeCodeAgentRuntimeProvider(broker, options);

        Console.WriteLine($"executable:  {provider.ExecutablePath ?? "(not found)"}");
        if (provider.ExecutablePath is null)
        {
            Console.Error.WriteLine("error: the claude CLI could not be resolved");
            return 2;
        }

        Console.WriteLine();

        // ------------------------------------------------------------- check 1
        WriteHeader("Check 1: CheckAsync succeeds and reports the real capability set");

        var check = await provider.CheckAsync(CancellationToken.None);
        if (!check.IsOk)
        {
            Fail("Check 1", $"CheckAsync failed: {check.Error!.Class}/{check.Error.Reason} {check.Error.Detail}");
            return Summary();
        }

        Console.WriteLine($"ok:          {check.Value!.Ok}");
        Console.WriteLine($"latency:     {check.Value.Latency!.Value.TotalMilliseconds:F0} ms");
        Console.WriteLine($"detail:      {check.Value.Detail}");
        Console.WriteLine();

        var descriptor = provider.Describe();
        Console.WriteLine($"name:        {descriptor.Name} v{descriptor.Version}");
        Console.WriteLine($"schema:      {descriptor.ConfigSchemaRef}");

        var capabilities = descriptor.Capabilities.OfType<AgentRuntimeCapabilities>().FirstOrDefault();
        if (capabilities is null)
        {
            Fail("Check 1", "no AgentRuntimeCapabilities on the descriptor");
            return Summary();
        }

        PrintCapabilityTable(capabilities, provider.Detection);

        // ------------------------------------------------------------- check 2
        WriteHeader("Check 2: a task is started and a handle returned without waiting");

        var sandbox = new SandboxHandle(
            Key: "spike2-claude-code-1",
            WorkingDirectory: FixtureDirectory,
            Os: "windows",
            Persistent: false);

        var task = new AgentTask(
            Prompt: "Inspect this .NET repository and report, in one sentence, what the " +
                    "WidgetStore class does. Do not modify any file.",
            Role: "implementer",
            WriteAccess: "branch",
            Tools: new[] { "Read", "Glob", "Grep" },
            Branch: "main",
            Repository: $"{Owner}/{Repo}");

        var limits = new AgentLimits(
            MaxWallClock: TimeSpan.FromMinutes(10),
            HeartbeatInterval: TimeSpan.FromSeconds(30));

        var key = "spike2-claude-code-start-1";

        var stopwatch = Stopwatch.StartNew();
        var started = await provider.StartAsync(sandbox, task, limits, key, CancellationToken.None);
        stopwatch.Stop();

        if (!started.IsOk)
        {
            Fail("Check 2", $"StartAsync failed: {started.Error!.Class}/{started.Error.Reason} {started.Error.Detail}");
            return Summary();
        }

        var session = started.Value!;
        Console.WriteLine($"session ref: {session.SessionRef}");
        Console.WriteLine($"process id:  {session.ProcessId?.ToString() ?? "(none)"}");
        Console.WriteLine($"resumed:     {session.Resumed}");
        Console.WriteLine($"StartAsync returned in {stopwatch.ElapsedMilliseconds} ms");

        // The proof that it did not wait: the process is still alive right after
        // StartAsync returned.
        var aliveAfterStart = IsAlive(session.ProcessId);
        Console.WriteLine($"process alive immediately after StartAsync: {aliveAfterStart}");

        if (stopwatch.ElapsedMilliseconds > 30_000)
            Fail("Check 2", $"StartAsync took {stopwatch.ElapsedMilliseconds} ms; docs/05 section 3 bounds it at 30s");
        if (!aliveAfterStart)
            Fail("Check 2", "the process had already exited, so StartAsync waited for it");

        // ------------------------------------------------------------- check 3
        WriteHeader("Check 3: progress is polled and reported");

        AgentProgress? lastProgress = null;
        var polls = 0;

        for (var i = 0; i < 60; i++)
        {
            var progress = await provider.GetProgressAsync(session, CancellationToken.None);
            if (!progress.IsOk)
            {
                Fail("Check 3", $"GetProgressAsync failed: {progress.Error!.Class}/{progress.Error.Reason}");
                break;
            }

            lastProgress = progress.Value!;
            polls++;

            Console.WriteLine(
                $"  poll {polls,2}: {lastProgress.HeartbeatDetail}");

            if (!lastProgress.Running)
                break;

            await Task.Delay(500);
        }

        if (lastProgress is null)
        {
            Fail("Check 3", "no progress was ever reported");
        }
        else
        {
            Console.WriteLine();
            Console.WriteLine($"polls:       {polls}");
            Console.WriteLine($"final:       {lastProgress.HeartbeatDetail}");
            Console.WriteLine($"turns:       {lastProgress.SessionTurns}");
            Console.WriteLine($"last commit: {lastProgress.LastCommit ?? "(none)"}");

            if (lastProgress.SessionTurns <= 0 && lastProgress.LastCommit is null)
                Fail("Check 3", "progress reported neither a turn count nor a commit");
        }

        // ------------------------------------------------------------- check 4
        WriteHeader("Check 4: the result comes from GetResultAsync, not from a hook");

        // Wait for the process to exit. The engine would poll inside one
        // long-running activity (docs/05 1.2.1); this harness does the same.
        AgentResult? result = null;
        for (var i = 0; i < 120; i++)
        {
            var attempt = await provider.GetResultAsync(session, CancellationToken.None);
            if (attempt.IsOk)
            {
                result = attempt.Value!;
                break;
            }

            if (attempt.Error!.Reason != "not_found")
            {
                Fail("Check 4", $"GetResultAsync failed: {attempt.Error.Class}/{attempt.Error.Reason} {attempt.Error.Detail}");
                break;
            }

            await Task.Delay(500);
        }

        if (result is null)
        {
            Fail("Check 4", "the session never produced a result");
        }
        else
        {
            Console.WriteLine($"outcome:     {result.Outcome}");
            Console.WriteLine($"session ref: {result.SessionRef}");
            Console.WriteLine($"turns:       {result.SessionTurns}");
            Console.WriteLine($"exit code:   {result.ExitCode?.ToString() ?? "(none)"}");
            Console.WriteLine($"timed out:   {result.TimedOut}");
            Console.WriteLine($"summary:     {Trim(result.Summary, 160)}");

            if (result.Usage is { } usage)
            {
                Console.WriteLine($"usage:       model={usage.Model} in={usage.InputTokens} " +
                                  $"out={usage.OutputTokens} cost={usage.CostUsd} source={usage.Source}");
            }
            else
            {
                Console.WriteLine("usage:       (none reported)");
            }

            // The point of check 4: the outcome was read from the runtime's own
            // result, and the hook log is only corroboration.
            var hookEvents = ReadHookEvents();
            Console.WriteLine($"hook events seen during the run: {(hookEvents.Count == 0 ? "(none)" : string.Join(", ", hookEvents))}");
            Console.WriteLine("the outcome above was read by GetResultAsync; hooks are a signal, not the source (review 05 P3)");

            if (result.Outcome != "completed")
                Fail("Check 4", $"expected outcome 'completed', got '{result.Outcome}'");
        }

        // ------------------------------------------------------------- check 5
        WriteHeader("Check 5: a retry resumes the same session");

        if (capabilities.SessionResume)
        {
            var resumeKey = "spike2-claude-code-resume-1";
            var resume = await provider.ResumeAsync(sandbox, session.SessionRef, limits, resumeKey, CancellationToken.None);

            if (!resume.IsOk)
            {
                Fail("Check 5", $"ResumeAsync failed: {resume.Error!.Class}/{resume.Error.Reason} {resume.Error.Detail}");
            }
            else
            {
                var resumed = resume.Value!;
                Console.WriteLine($"resumed handle: {resumed.SessionRef}");
                Console.WriteLine($"resumed flag:   {resumed.Resumed}");
                Console.WriteLine($"process id:     {resumed.ProcessId?.ToString() ?? "(none)"}");

                var sameSession = string.Equals(
                    resumed.SessionRef, session.SessionRef, StringComparison.Ordinal);
                Console.WriteLine($"same session as the first run: {sameSession}");

                if (!sameSession)
                    Fail("Check 5", "the resumed handle carries a different session reference");
                if (!resumed.Resumed)
                    Fail("Check 5", "the resumed handle is not flagged as resumed");

                // Let it finish, then read the result the same way.
                AgentResult? resumedResult = null;
                for (var i = 0; i < 120; i++)
                {
                    var attempt = await provider.GetResultAsync(resumed, CancellationToken.None);
                    if (attempt.IsOk)
                    {
                        resumedResult = attempt.Value!;
                        break;
                    }

                    await Task.Delay(500);
                }

                if (resumedResult is null)
                {
                    Fail("Check 5", "the resumed session never produced a result");
                }
                else
                {
                    Console.WriteLine($"resumed outcome: {resumedResult.Outcome}");
                    Console.WriteLine($"resumed turns:   {resumedResult.SessionTurns}");

                    // Session growth is the progress evidence docs/01 3.7 needs.
                    var grew = resumedResult.SessionTurns > (result?.SessionTurns ?? 0);
                    Console.WriteLine($"session grew since the first run: {grew} " +
                                      $"({result?.SessionTurns ?? 0} -> {resumedResult.SessionTurns})");
                }

                await provider.StopAsync(resumed, CancellationToken.None);
            }
        }
        else
        {
            Console.WriteLine("session_resume is NOT declared by this runtime; check 5 is not applicable");
            Console.WriteLine("fallback per docs/08 2.5: WIP commits on the branch, and a fresh session per attempt");
        }

        // ------------------------------------------------------------- check 6
        WriteHeader("Check 6: a timeout is classified Work, not Infrastructure");

        // A wall clock the session cannot beat: 2 seconds against a run that
        // takes longer. The provider stops the runtime on the poll path and
        // reports the outcome as "timeout", which docs/06 2.1 classes as work.
        //
        // The router is slowed first, so the session is genuinely still running
        // when the wall clock passes. Without that the check would depend on a
        // race between the poll loop and a fast model.
        await SetRouterDelayAsync(4000);

        var shortLimits = new AgentLimits(
            MaxWallClock: TimeSpan.FromSeconds(2),
            HeartbeatInterval: TimeSpan.FromSeconds(30));

        var timeoutKey = "spike2-claude-code-timeout-1";
        var timeoutStart = await provider.StartAsync(
            sandbox, task, shortLimits, timeoutKey, CancellationToken.None);

        if (!timeoutStart.IsOk)
        {
            Fail("Check 6", $"StartAsync failed: {timeoutStart.Error!.Class}/{timeoutStart.Error.Reason}");
        }
        else
        {
            var timeoutSession = timeoutStart.Value!;
            Console.WriteLine($"session ref: {timeoutSession.SessionRef}");
            Console.WriteLine($"wall clock:  {shortLimits.MaxWallClock!.Value.TotalSeconds:F0}s");

            // Poll progress, which is where the wall clock is enforced. The
            // engine does the same inside one long-running activity.
            for (var i = 0; i < 40; i++)
            {
                var progress = await provider.GetProgressAsync(timeoutSession, CancellationToken.None);
                if (progress.IsOk && !progress.Value!.Running)
                {
                    Console.WriteLine($"stopped after {i + 1} poll(s): {progress.Value.HeartbeatDetail}");
                    break;
                }

                await Task.Delay(250);
            }

            AgentResult? timeoutResult = null;
            for (var i = 0; i < 120; i++)
            {
                var attempt = await provider.GetResultAsync(timeoutSession, CancellationToken.None);
                if (attempt.IsOk)
                {
                    timeoutResult = attempt.Value!;
                    break;
                }

                await Task.Delay(500);
            }

            if (timeoutResult is null)
            {
                Fail("Check 6", "the timeout session never produced a result");
            }
            else
            {
                Console.WriteLine($"outcome:   {timeoutResult.Outcome}");
                Console.WriteLine($"timed out: {timeoutResult.TimedOut}");

                if (timeoutResult.Outcome != "timeout")
                    Fail("Check 6", $"expected outcome 'timeout', got '{timeoutResult.Outcome}'");
                else
                    Console.WriteLine("classified Work (timeout), not Infrastructure — docs/06 2.1");
            }

            await provider.StopAsync(timeoutSession, CancellationToken.None);
        }

        await SetRouterDelayAsync(0);

        // A missing session is not_found, class Work (docs/05 1.3). Proved
        // separately so the classification table has a second row.
        var missing = await provider.GetResultAsync(
            new AgentSession("no-such-session", sandbox, "no-such-key", DateTimeOffset.UtcNow),
            CancellationToken.None);

        Console.WriteLine();
        Console.WriteLine($"unknown session -> ok={missing.IsOk} " +
                          $"class={(missing.Error?.Class.ToString() ?? "-")} reason={missing.Error?.Reason ?? "-"}");

        if (missing.IsOk || missing.Error!.Class != ErrorClass.Work || missing.Error.Reason != "not_found")
            Fail("Check 6", "an unknown session must be not_found, class Work");

        // ------------------------------------------------------------- check 7
        // Not one of the six, but it is the W0 question (docs/08 2.4): do hooks
        // fire on Windows, per tool call? SessionStart and Stop alone would
        // only prove lifecycle hooks.
        WriteHeader("Check 7 (W0): per-tool-call hooks fire on Windows");

        await ProveToolCallHooksAsync(provider, sandbox, limits);

        await provider.StopAsync(session, CancellationToken.None);

        return Summary();
    }

    /// <summary>
    /// Drives one real tool call through the runtime and reports which hook
    /// events actually fired.
    ///
    /// The model router is armed with a tool call, so the CLI really invokes a
    /// tool and the hooks really run. The hook log is the evidence, not the
    /// provider's own claim.
    /// </summary>
    private static async Task ProveToolCallHooksAsync(
        IAgentRuntimeProvider provider, SandboxHandle sandbox, AgentLimits limits)
    {
        var logPath = Path.Combine(ProbeDirectory, "hook-events.log");
        if (File.Exists(logPath))
            File.Delete(logPath);

        var armed = await ArmToolCallAsync();
        Console.WriteLine($"tool call armed on the router: {armed}");

        var key = "spike2-claude-code-hookprobe-1";
        var started = await provider.StartAsync(
            sandbox,
            new AgentTask(
                Prompt: "Run the command git rev-parse --abbrev-ref HEAD and report the branch.",
                Role: "implementer",
                WriteAccess: "branch",
                Tools: new[] { "Bash" },
                Branch: "main",
                Repository: $"{Owner}/{Repo}"),
            limits,
            key,
            CancellationToken.None);

        if (!started.IsOk)
        {
            Fail("Check 7", $"StartAsync failed: {started.Error!.Class}/{started.Error.Reason}");
            return;
        }

        var hookSession = started.Value!;

        for (var i = 0; i < 120; i++)
        {
            var progress = await provider.GetProgressAsync(hookSession, CancellationToken.None);
            if (progress.IsOk && !progress.Value!.Running)
                break;

            await Task.Delay(250);
        }

        var events = File.Exists(logPath)
            ? File.ReadAllLines(logPath).Select(l => l.Trim()).Where(l => l.Length > 0).ToList()
            : new List<string>();

        Console.WriteLine($"hook events observed: {(events.Count == 0 ? "(none)" : string.Join(", ", events))}");

        foreach (var expected in new[] { "SessionStart", "PreToolUse", "PostToolUse", "Stop" })
        {
            var seen = events.Contains(expected);
            Console.WriteLine($"  {expected,-12} {(seen ? "fired" : "NOT observed")}");
        }

        // Per-tool-call policy is the capability that matters: it is what
        // enforces write_access on every tool call (docs/06 2.2).
        if (!events.Contains("PreToolUse"))
            Fail("Check 7", "PreToolUse did not fire, so per-tool-call policy is unavailable");

        await provider.StopAsync(hookSession, CancellationToken.None);
    }

    /// <summary>
    /// Tells the local model router to answer the next request with a tool call
    /// instead of prose. This is the only part of the harness that touches the
    /// model, and it is a control-plane call, not a claim about the runtime.
    /// </summary>
    /// <summary>
    /// Slows the local model router, so a wall-clock timeout is exercised
    /// against a session that is genuinely still working. Control plane only.
    /// </summary>
    private static async Task SetRouterDelayAsync(int milliseconds)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            await http.PostAsync($"{Endpoint.TrimEnd('/')}/control/delay?ms={milliseconds}", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Console.WriteLine($"  (could not set the router delay: {ex.GetType().Name})");
        }
    }

    /// <summary>
    /// Tells the local model router to answer the next request with a tool call
    /// instead of prose. This is the only part of the harness that touches the
    /// model, and it is a control-plane call, not a claim about the runtime.
    /// </summary>
    private static async Task<string> ArmToolCallAsync()
    {
        var command = Uri.EscapeDataString("git rev-parse --abbrev-ref HEAD");
        var url = $"{Endpoint.TrimEnd('/')}/control/tool-call?name=Bash&command={command}";

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            var response = await http.PostAsync(url, null);
            return await response.Content.ReadAsStringAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return $"(could not arm: {ex.GetType().Name})";
        }
    }

    // ------------------------------------------------------------------ output

    private static void PrintCapabilityTable(
        AgentRuntimeCapabilities capabilities, ClaudeCodeDetection? detection)
    {
        Console.WriteLine();
        Console.WriteLine("capabilities observed (docs/08 2.2 — what this runtime actually provided):");
        Console.WriteLine();
        Console.WriteLine("  capability            value   evidence");
        Console.WriteLine("  --------------------  ------  ---------------------------------------------");

        Row("structured_output", capabilities.StructuredOutput, "--json-schema in --help");
        Row("streaming", capabilities.Streaming, "stream-json in --help");
        Row("tool_allowlist", capabilities.ToolAllowlist, "--allowed-tools in --help");
        Row("hooks", capabilities.Hooks,
            detection?.HookEvents.Count > 0
                ? $"observed: {string.Join(",", detection!.HookEvents)}"
                : "--settings in --help (mechanism only, no event observed)");
        Row("session_resume", capabilities.SessionResume, "--resume in --help");
        Row("usage_reporting", capabilities.UsageReporting,
            capabilities.UsageReporting ? "usage block in the probe result" : "no usage block observed");
        Row("model_configuration", capabilities.ModelConfiguration, "--model in --help");
        Row("custom_endpoint", capabilities.CustomEndpoint, "ANTHROPIC_BASE_URL honoured by the probe");
        Console.WriteLine($"  max_context_tokens    {capabilities.MaxContextTokens,6}  contextWindow in the probe result");
        Console.WriteLine();

        var missing = capabilities.Missing(AgentRuntimeCapabilities.Names);
        Console.WriteLine($"missing: {(missing.Count == 0 ? "(none)" : string.Join(", ", missing))}");
        Console.WriteLine();

        static void Row(string name, bool value, string evidence) =>
            Console.WriteLine($"  {name,-20}  {(value ? "yes" : "no "),-6}  {evidence}");
    }

    private static List<string> ReadHookEvents()
    {
        var path = Path.Combine(ProbeDirectory, "hook-events.log");
        if (!File.Exists(path))
            return new List<string>();

        return File.ReadAllLines(path)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();
    }

    private static string? ReadHead()
    {
        try
        {
            var startInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = FixtureDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("rev-parse");
            startInfo.ArgumentList.Add("HEAD");

            using var git = Process.Start(startInfo);
            if (git is null)
                return null;

            var sha = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit(3000);
            return git.ExitCode == 0 ? sha : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsAlive(int? processId)
    {
        if (processId is null)
            return false;

        try
        {
            var process = Process.GetProcessById(processId.Value);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrEmpty(value))
            return "(none)";

        var flat = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
        return flat.Length <= max ? flat : flat[..max] + "...";
    }

    private static void WriteHeader(string text)
    {
        Console.WriteLine("=== " + text + " ===");
        Console.WriteLine();
    }

    private static void Fail(string check, string detail)
    {
        failures++;
        Console.WriteLine($"  FAIL {check}: {detail}");
    }

    private static int Summary()
    {
        Console.WriteLine();
        WriteHeader(failures == 0 ? "all checks passed" : $"{failures} check(s) failed");
        return failures == 0 ? 0 : 1;
    }
}
