using System.Diagnostics;
using System.Text;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

// ArtifactRef is owned by the code host project (docs/04 section 8). The agent
// runtime needs only the type, and referencing the whole code host would drag in
// its own duplicate ICredentialBroker seam, so it is aliased here. When the
// shared type moves to FeatureFlow.Spike.Contracts, drop the alias.
using ArtifactRef = FeatureFlow.Spike.CodeHost.ArtifactRef;

namespace FeatureFlow.Spike.AgentRuntime;

/// <summary>
/// The <c>claude-code</c> named instance of the agent runtime provider.
///
/// docs/04 section 3 for the signatures, docs/05 section 3 for the per-method
/// contracts. It is one instance among several, selected by role mapping
/// (docs/09 11.1), so nothing here may be assumed by the engine and nothing
/// here names itself in the interface.
///
/// Five rules shape the implementation:
///
/// 1. **Start and poll, never await.** <see cref="StartAsync"/> spawns the CLI
///    and returns a handle. <see cref="GetProgressAsync"/> and
///    <see cref="GetResultAsync"/> read state and return. R18/R20, review 06 C1.
/// 2. **Every method returns <see cref="ProviderResult{T}"/>.** Classification
///    is by exit code, process state and JSON shape — never by matching text in
///    an exception message (docs/05 1.3, and the seam Spike 1 left open).
/// 3. **Resume is a capability, not an assumption.** Whether the CLI accepts
///    <c>--resume</c> is detected, reported through
///    <see cref="AgentRuntimeCapabilities.SessionResume"/>, and honoured only
///    when present.
/// 4. **Credentials come from the broker.** The model key is issued per step and
///    passed to the child process through its environment. It is never read from
///    configuration and never written to a file (docs/04 section 1, floor F2).
/// 5. **Model and endpoint are configuration.** Neither is hardcoded, and
///    neither appears in the task (docs/09 11.1, brief lesson 5).
/// </summary>
public sealed class ClaudeCodeAgentRuntimeProvider : IAgentRuntimeProvider
{
    /// <summary>The instance name the organisation layer maps a role to (docs/09 11.1).</summary>
    public const string InstanceName = "claude-code";

    /// <summary>docs/05 section 3: StartAsync and ResumeAsync time out at 30s.</summary>
    public static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    /// <summary>docs/05 section 3: GetProgressAsync is a metadata read, 15s.</summary>
    public static readonly TimeSpan ProgressTimeout = TimeSpan.FromSeconds(15);

    /// <summary>docs/05 section 3: GetResultAsync 30s.</summary>
    public static readonly TimeSpan ResultTimeout = TimeSpan.FromSeconds(30);

    private readonly ICredentialBroker broker;
    private readonly ClaudeCodeRuntimeOptions options;
    private readonly Func<DateTimeOffset> clock;
    private readonly string? executable;

    private readonly Dictionary<string, RunningSession> sessions = new(StringComparer.Ordinal);
    private readonly object gate = new();

    private ClaudeCodeDetection? detection;

    public ClaudeCodeAgentRuntimeProvider(
        ICredentialBroker broker,
        ClaudeCodeRuntimeOptions? options = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.options = options ?? new ClaudeCodeRuntimeOptions();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.executable = ClaudeCodeExecutable.Resolve(this.options.ExecutablePath);
    }

    /// <summary>What the last <see cref="CheckAsync"/> observed. Null before it runs.</summary>
    public ClaudeCodeDetection? Detection => detection;

    /// <summary>The binary this instance will launch. Null when it could not be found.</summary>
    public string? ExecutablePath => executable;

    // ------------------------------------------------------------------ IProvider

    public ProviderDescriptor Describe()
    {
        var capabilities = detection?.Capabilities ?? ClaudeCodeCapabilityDetector.Unknown;

        return new ProviderDescriptor(
            Name: InstanceName,
            Version: detection?.Version ?? "unknown",
            Capabilities: new object[] { capabilities },
            Requires: Array.Empty<string>(),
            ConfigSchemaRef: "feature-flow/schemas/agentRuntime/claude-code/v1");
    }

    public async Task<ProviderResult<CheckResult>> CheckAsync(CancellationToken ct)
    {
        var started = clock();

        if (executable is null)
            return ProviderResult<CheckResult>.Infrastructure(
                "not_found", detail: "the claude CLI is not installed or not on PATH");

        // The model credential is issued per step, never read from configuration
        // (docs/04 section 1). CheckAsync takes one so the probe can run a real
        // session rather than only parsing --help.
        var credential = await IssueModelCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<CheckResult>.Fail(credential.Error!);

        var detected = await ClaudeCodeCapabilityDetector.DetectAsync(
            options, executable, credential.Value!.Secret, ct).ConfigureAwait(false);

        if (detected is null)
            return ProviderResult<CheckResult>.Infrastructure(
                "unreachable", detail: $"claude at {executable} did not answer --version or --help");

        detection = detected;

        return ProviderResult<CheckResult>.Ok(new CheckResult(
            Ok: true,
            Detail: detected.Detail,
            Latency: clock() - started));
    }

    // -------------------------------------------------------- IAgentRuntimeProvider

    public async Task<ProviderResult<AgentSession>> StartAsync(
        SandboxHandle sandbox, AgentTask task, AgentLimits limits, string idempotencyKey, CancellationToken ct)
    {
        if (sandbox is null || task is null || string.IsNullOrWhiteSpace(idempotencyKey))
            return ProviderResult<AgentSession>.Work("validation_failed");

        if (string.IsNullOrWhiteSpace(task.Prompt))
            return ProviderResult<AgentSession>.Work("validation_failed", "the task has no prompt");

        if (executable is null)
            return ProviderResult<AgentSession>.Infrastructure("not_found", detail: "the claude CLI is not installed");

        // Idempotency by key: a second start with the same key returns the
        // session already running rather than starting a duplicate (docs/05 1.1).
        lock (gate)
        {
            if (sessions.TryGetValue(idempotencyKey, out var existing))
                return ProviderResult<AgentSession>.Ok(existing.ToSession());
        }

        var credential = await IssueModelCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<AgentSession>.Fail(credential.Error!);

        var sessionRef = Guid.NewGuid().ToString();
        var started = clock();

        var running = Launch(
            sandbox: sandbox,
            sessionRef: sessionRef,
            prompt: task.Prompt,
            tools: task.Tools,
            resumeSessionRef: null,
            apiKey: credential.Value!.Secret,
            startedAt: started,
            idempotencyKey: idempotencyKey,
            resumed: false,
            limits: limits);

        if (running is null)
            return ProviderResult<AgentSession>.Infrastructure(
                "unreachable", detail: $"could not start claude at {executable}");

        lock (gate)
        {
            sessions[idempotencyKey] = running;
        }

        return ProviderResult<AgentSession>.Ok(running.ToSession());
    }

    public async Task<ProviderResult<AgentSession>> ResumeAsync(
        SandboxHandle sandbox, string sessionRef, AgentLimits limits, string idempotencyKey, CancellationToken ct)
    {
        if (sandbox is null || string.IsNullOrWhiteSpace(sessionRef) || string.IsNullOrWhiteSpace(idempotencyKey))
            return ProviderResult<AgentSession>.Work("validation_failed");

        var capabilities = detection?.Capabilities;
        if (capabilities is null)
            return ProviderResult<AgentSession>.Work(
                "capability_missing", "CheckAsync has not run, so session_resume is not established");

        if (!capabilities.SessionResume)
            return ProviderResult<AgentSession>.Work("capability_missing", "session_resume");

        if (executable is null)
            return ProviderResult<AgentSession>.Infrastructure("not_found", detail: "the claude CLI is not installed");

        lock (gate)
        {
            if (sessions.TryGetValue(idempotencyKey, out var existing))
                return ProviderResult<AgentSession>.Ok(existing.ToSession());
        }

        var credential = await IssueModelCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<AgentSession>.Fail(credential.Error!);

        var started = clock();

        var running = Launch(
            sandbox: sandbox,
            sessionRef: sessionRef,
            prompt: ContinuePrompt,
            tools: null,
            resumeSessionRef: sessionRef,
            apiKey: credential.Value!.Secret,
            startedAt: started,
            idempotencyKey: idempotencyKey,
            resumed: true,
            limits: limits);

        if (running is null)
            return ProviderResult<AgentSession>.Infrastructure(
                "unreachable", detail: $"could not start claude at {executable}");

        lock (gate)
        {
            sessions[idempotencyKey] = running;
        }

        return ProviderResult<AgentSession>.Ok(running.ToSession());
    }

    public Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken ct)
    {
        if (session is null)
            return Task.FromResult(ProviderResult<AgentProgress>.Work("validation_failed"));

        RunningSession? running;
        lock (gate)
        {
            sessions.TryGetValue(session.IdempotencyKey, out running);
        }

        if (running is null)
            return Task.FromResult(ProviderResult<AgentProgress>.Work("not_found", "no session for this handle"));

        // Non-blocking by construction: this reads the process state and the
        // transcript file. It never waits for the agent (docs/05 section 3).
        var progress = running.ReadProgress(clock());
        return Task.FromResult(ProviderResult<AgentProgress>.Ok(progress));
    }

    public async Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken ct)
    {
        if (session is null)
            return ProviderResult<AgentResult>.Work("validation_failed");

        RunningSession? running;
        lock (gate)
        {
            sessions.TryGetValue(session.IdempotencyKey, out running);
        }

        if (running is null)
            return ProviderResult<AgentResult>.Work("not_found", "no session for this handle");

        // The source of truth is the runtime's own result, not a hook (review 05
        // P3). If the process is still running there is no result yet: the
        // caller polls, it does not block here.
        if (!running.HasExited)
            return ProviderResult<AgentResult>.Work("not_found", "the session has not finished");

        var result = await running.ReadResultAsync(ct).ConfigureAwait(false);
        return ProviderResult<AgentResult>.Ok(result);
    }

    public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken ct)
    {
        if (session is null)
            return Task.FromResult(ProviderResult<Unit>.Work("validation_failed"));

        RunningSession? running;
        lock (gate)
        {
            sessions.TryGetValue(session.IdempotencyKey, out running);
        }

        // Idempotent: stopping a session that is already gone succeeds
        // (docs/05 section 3).
        running?.Stop();

        return Task.FromResult(ProviderResult<Unit>.Ok(Unit.Value));
    }

    // ------------------------------------------------------------------ internals

    /// <summary>
    /// What a resumed session is told. The prior transcript is already in the
    /// session, so the prompt only has to ask it to continue.
    /// </summary>
    private const string ContinuePrompt =
        "Continue the work in progress. Do not restart from the beginning.";

    private Task<ProviderResult<Credential>> IssueModelCredentialAsync(CancellationToken ct)
    {
        // Scoped to one repository and short-lived, from the broker. The
        // provider never reads a credential from configuration (docs/04 1).
        //
        // Judgement call: CredentialScope's grammar allows only
        // read/write/admin as the access level, so "model:invoke" does not
        // parse. "model:write" is used because invoking a model consumes
        // quota. Flagged in the summary as a grammar gap for a model gateway.
        return broker.IssueAsync(
            Actor.Engine("agent-runtime"),
            "anthropic",
            "repo:ai-feature-flow/feature-flow-example model:write",
            TimeSpan.FromMinutes(60),
            ct);
    }

    private RunningSession? Launch(
        SandboxHandle sandbox,
        string sessionRef,
        string prompt,
        IReadOnlyList<string>? tools,
        string? resumeSessionRef,
        string apiKey,
        DateTimeOffset startedAt,
        string idempotencyKey,
        bool resumed,
        AgentLimits limits)
    {
        var args = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--verbose",
            "--permission-mode", options.EffectivePermissionMode,
            "--permission-prompts", "none",
        };

        if (!string.IsNullOrWhiteSpace(options.Model))
            args.AddRange(new[] { "--model", options.Model! });

        // --session-id and --resume are mutually exclusive unless --fork-session
        // is given, and --fork-session would mint a *new* session id, which is
        // the opposite of a resume. So a resume passes only --resume, and the
        // runtime reuses the id it already has.
        if (resumeSessionRef is not null)
            args.AddRange(new[] { "--resume", resumeSessionRef });
        else
            args.AddRange(new[] { "--session-id", sessionRef });

        if (tools is { Count: > 0 })
            args.AddRange(new[] { "--allowed-tools", string.Join(",", tools) });

        if (!string.IsNullOrWhiteSpace(options.SystemPromptSuffix))
            args.AddRange(new[] { "--append-system-prompt", options.SystemPromptSuffix! });

        // The hook settings the probe measured, so a launched session installs
        // the same hooks. Without this the capability would be declared from
        // the probe and then silently absent in the real run.
        var hookSettings = options.HookSettingsPath ?? detection?.HookSettingsPath;
        if (!string.IsNullOrWhiteSpace(hookSettings))
            args.AddRange(new[] { "--settings", hookSettings! });

        // The prompt goes on stdin, so a prompt containing quotes, newlines or
        // shell metacharacters cannot be reinterpreted as arguments.
        args.Add("-");

        var startInfo = new ProcessStartInfo(executable!)
        {
            WorkingDirectory = sandbox.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        // The credential reaches the child through its environment only. It is
        // never written to a file and never appears in the argument list, where
        // it would be visible to anything reading the process table.
        startInfo.Environment["ANTHROPIC_API_KEY"] = apiKey;
        if (!string.IsNullOrWhiteSpace(options.Endpoint))
            startInfo.Environment["ANTHROPIC_BASE_URL"] = options.Endpoint;

        try
        {
            var process = Process.Start(startInfo);
            if (process is null)
                return null;

            var running = new RunningSession(
                process: process,
                sessionRef: sessionRef,
                sandbox: sandbox,
                idempotencyKey: idempotencyKey,
                startedAt: startedAt,
                resumed: resumed,
                clock: clock,
                limits: limits);

            running.BeginReading();
            running.WritePrompt(prompt);

            return running;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// One running CLI process, and the state read off it.
    ///
    /// Output is read on a background task into a buffer, so no provider call
    /// ever blocks on the agent's stdout. That is what makes
    /// <see cref="GetProgressAsync"/> non-blocking in fact, not only in intent.
    /// </summary>
    private sealed class RunningSession
    {
        private readonly Process process;
        private readonly SandboxHandle sandbox;
        private readonly DateTimeOffset startedAt;
        private readonly bool resumed;
        private readonly Func<DateTimeOffset> clock;
        private readonly AgentLimits limits;
        private readonly StringBuilder output = new();
        private readonly object outputGate = new();

        private int turns;
        private int timedOut;
        private string? lastCommit;
        private readonly string? transcriptPath;

        public RunningSession(
            Process process,
            string sessionRef,
            SandboxHandle sandbox,
            string idempotencyKey,
            DateTimeOffset startedAt,
            bool resumed,
            Func<DateTimeOffset> clock,
            AgentLimits limits)
        {
            this.process = process;
            this.sandbox = sandbox;
            this.startedAt = startedAt;
            this.resumed = resumed;
            this.clock = clock;
            this.limits = limits;
            SessionRef = sessionRef;
            IdempotencyKey = idempotencyKey;
            transcriptPath = FindTranscriptPath(sessionRef);
        }

        public string SessionRef { get; }

        public string IdempotencyKey { get; }

        public bool HasExited => process.HasExited;

        public AgentSession ToSession() => new(
            SessionRef: SessionRef,
            Sandbox: sandbox,
            IdempotencyKey: IdempotencyKey,
            StartedAt: startedAt,
            Resumed: resumed,
            ProcessId: SafeProcessId(),
            TranscriptPath: transcriptPath,
            UsagePath: null);

        /// <summary>
        /// Where Claude Code keeps the session transcript.
        ///
        /// <c>~/.claude/projects/&lt;slug&gt;/&lt;session-id&gt;.jsonl</c>, where the slug
        /// is the working directory with the separators flattened. This is the
        /// runtime's own record, and it is what makes session growth measurable
        /// across processes: <c>num_turns</c> in a result is per-process, so a
        /// resumed session reports 1 again even though the session has two
        /// turns. docs/01 3.7 needs the cumulative count.
        /// </summary>
        private static string? FindTranscriptPath(string sessionRef)
        {
            try
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (string.IsNullOrEmpty(home))
                    return null;

                var projects = Path.Combine(home, ".claude", "projects");
                if (!Directory.Exists(projects))
                    return null;

                var file = Directory.EnumerateFiles(projects, sessionRef + ".jsonl", SearchOption.AllDirectories)
                    .FirstOrDefault();

                return file;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The cumulative turn count for this session, read from the transcript.
        /// Falls back to the turns seen on this process's own output, which is
        /// the right answer for a session that has never been resumed.
        /// </summary>
        private int CumulativeTurns()
        {
            var fromTranscript = CountTranscriptTurns();
            return fromTranscript > 0 ? fromTranscript : Volatile.Read(ref turns);
        }

        private int CountTranscriptTurns()
        {
            if (transcriptPath is null || !File.Exists(transcriptPath))
                return 0;

            try
            {
                var count = 0;
                using var stream = new FileStream(
                    transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);

                while (reader.ReadLine() is { } line)
                {
                    // One assistant record per turn. Counting them is counting
                    // turns, and it survives a resume because the file does.
                    if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
                        count++;
                }

                return count;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return 0;
            }
        }

        public void BeginReading()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    // One line per event. Each assistant message is a turn, which
                    // is the session-growth evidence docs/01 3.7 asks for.
                    while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
                    {
                        lock (outputGate)
                        {
                            output.AppendLine(line);
                        }

                        if (line.Contains("\"type\":\"assistant\"", StringComparison.Ordinal))
                            Interlocked.Increment(ref turns);
                    }
                }
                catch (Exception)
                {
                    // A closed pipe is the process exiting, not an error.
                }
            });

            _ = Task.Run(async () =>
            {
                try
                {
                    var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    lock (outputGate)
                    {
                        output.AppendLine(stderr);
                    }
                }
                catch (Exception)
                {
                    // Same: the process went away.
                }
            });
        }

        public void WritePrompt(string prompt)
        {
            try
            {
                process.StandardInput.Write(prompt);
                process.StandardInput.Flush();
                process.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException)
            {
                // The process died before it read the prompt. GetResultAsync
                // reports it; there is nothing to recover here.
            }
        }

        public AgentProgress ReadProgress(DateTimeOffset now)
        {
            var elapsed = now - startedAt;

            // The wall clock is enforced here, on the poll path, because that is
            // where the engine would enforce it: docs/05 1.2.1 puts the poll loop
            // inside one long-running activity, and the activity is what decides
            // a step has run out of time. The provider stops the runtime so a
            // timed-out agent does not keep spending after the step has moved on.
            if (!process.HasExited && TimedOutAgainst(now))
            {
                Stop();
                Volatile.Write(ref timedOut, 1);
            }

            // A new commit on the branch is the primary progress evidence
            // (docs/01 3.6). It is read from the working directory, which is the
            // sandbox checkout, and a failure to read it is not an error: the
            // turn count is the fallback.
            var commit = ReadHeadCommit();
            if (commit is not null)
                lastCommit = commit;

            return new AgentProgress(
                LastCommit: lastCommit,
                SessionTurns: CumulativeTurns(),
                SessionRef: SessionRef,
                Running: !process.HasExited,
                Elapsed: elapsed,
                CommitCount: 0);
        }

        public Task<AgentResult> ReadResultAsync(CancellationToken ct)
        {
            string text;
            lock (outputGate)
            {
                text = output.ToString();
            }

            var exitCode = SafeExitCode();

            // Timed out if the poll path stopped it, or if it finished after the
            // wall clock had already passed. Either way it is a work outcome
            // (docs/06 2.1), not infrastructure.
            var hitWallClock = Volatile.Read(ref timedOut) == 1 || TimedOutAgainst(clock());

            var parsed = ParseResult(text);

            // An agent that ran out of time is a work outcome, not
            // infrastructure (docs/06 2.1). The provider reports it as work so
            // the step does not have to override.
            if (hitWallClock)
            {
                return Task.FromResult(new AgentResult(
                    Outcome: "timeout",
                    SessionRef: SessionRef,
                    Summary: "the session exceeded its wall-clock limit",
                    LastCommit: lastCommit ?? parsed.LastCommit,
                    SessionTurns: CumulativeTurns(),
                    Usage: parsed.Usage,
                    ExitCode: exitCode,
                    TimedOut: true));
            }

            var outcome = exitCode == 0 ? "completed" : "failed";

            return Task.FromResult(new AgentResult(
                Outcome: outcome,
                SessionRef: SessionRef,
                Summary: parsed.Summary,
                LastCommit: lastCommit ?? parsed.LastCommit,
                SessionTurns: CumulativeTurns(),
                Usage: parsed.Usage,
                ExitCode: exitCode,
                TimedOut: false));
        }

        public void Stop()
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
                // Already gone. Stopping is idempotent (docs/05 section 3).
            }
        }

        private bool TimedOutAgainst(DateTimeOffset now) =>
            limits.MaxWallClock is { } max && now - startedAt >= max;

        private string? ReadHeadCommit()
        {
            try
            {
                var gitDir = Path.Combine(sandbox.WorkingDirectory, ".git");
                if (!Directory.Exists(gitDir) && !File.Exists(gitDir))
                    return null;

                var startInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = sandbox.WorkingDirectory,
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
                git.WaitForExit(2000);

                return git.ExitCode == 0 && sha.Length > 0 ? sha : null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                return null;
            }
        }

        private int? SafeExitCode()
        {
            try
            {
                return process.HasExited ? process.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        private int? SafeProcessId()
        {
            try
            {
                return process.Id;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        /// <summary>
        /// Reads the runtime's own final result line. Classification is by exit
        /// code and JSON shape, never by matching text in a message (docs/05 1.3).
        /// </summary>
        private static ParsedResult ParseResult(string text)
        {
            foreach (var line in text.Split('\n'))
            {
                var trimmed = line.Trim();
                if (!trimmed.StartsWith("{", StringComparison.Ordinal) || !trimmed.EndsWith("}", StringComparison.Ordinal))
                    continue;

                try
                {
                    using var doc = JsonDocument.Parse(trimmed);
                    var root = doc.RootElement;

                    if (!root.TryGetProperty("type", out var type)
                        || !string.Equals(type.GetString(), "result", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var summary = root.TryGetProperty("result", out var result)
                        ? result.GetString()
                        : null;

                    var turns = root.TryGetProperty("num_turns", out var numTurns) && numTurns.TryGetInt32(out var t)
                        ? t
                        : (int?)null;

                    var usage = ReadUsage(root);

                    return new ParsedResult(summary, turns, null, usage);
                }
                catch (JsonException)
                {
                    // Not a result line. Keep looking.
                }
            }

            return new ParsedResult(null, null, null, null);
        }

        private static AgentUsage? ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage))
                return null;

            long? input = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv) ? iv : null;
            long? output = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov) ? ov : null;

            decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.TryGetDecimal(out var cv)
                ? cv
                : null;

            string? model = null;
            if (root.TryGetProperty("modelUsage", out var modelUsage))
            {
                foreach (var property in modelUsage.EnumerateObject())
                {
                    model = property.Name;
                    break;
                }
            }

            // docs/08 2.5: without hooks, cost comes from the runtime's own
            // usage output. That is what this is, and the source says so.
            return new AgentUsage(
                Model: model,
                InputTokens: input,
                OutputTokens: output,
                Credits: null,
                CostUsd: cost,
                Source: "runtime_usage_output");
        }

        private sealed record ParsedResult(
            string? Summary,
            int? Turns,
            string? LastCommit,
            AgentUsage? Usage);
    }
}
