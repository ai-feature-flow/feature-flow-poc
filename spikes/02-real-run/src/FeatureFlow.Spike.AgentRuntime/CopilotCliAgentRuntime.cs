using System.Globalization;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.AgentRuntime;

/// <summary>
/// Configuration for one named agent runtime instance.
///
/// **Model and endpoint are configuration, never template content** (docs/09
/// 11.1, brief lesson 5). A role names a capability; the organisation layer maps
/// the role to an instance and a model. There is no default model here: an
/// installation that runs only an open model through a router sets
/// <see cref="Model"/> and <see cref="Endpoint"/>, and nothing else changes.
/// </summary>
/// <param name="Executable">The runtime command. "copilot" for this instance.</param>
/// <param name="Model">The model to run. Configuration, not content.</param>
/// <param name="Endpoint">
/// An optional OpenAI-compatible router endpoint (bring-your-own-key). When set,
/// the runtime is pointed at it instead of its own default routing, so a role
/// can run on an open model through a router.
/// </param>
/// <param name="EndpointType">"openai" (default), "azure" or "anthropic".</param>
/// <param name="EndpointKeyReference">
/// A **reference** to the router's API key in the secrets provider, never the
/// value (floor F2). Null when the endpoint needs no key, as a local one does not.
/// </param>
/// <param name="SessionStateDirectory">
/// Where the runtime keeps its sessions. Used to read session growth for
/// progress evidence, and to check whether a session reference still exists.
/// </param>
/// <param name="WorkingRoot">Where per-session scratch files are written.</param>
/// <param name="ExtraArguments">Additional arguments, for an installation that needs them.</param>
public sealed record AgentRuntimeOptions(
    string Executable = "copilot",
    string? Model = null,
    string? Endpoint = null,
    string EndpointType = "openai",
    SecretReference? EndpointKeyReference = null,
    string? SessionStateDirectory = null,
    string? WorkingRoot = null,
    IReadOnlyList<string>? ExtraArguments = null)
{
    /// <summary>Whether a custom endpoint is configured.</summary>
    public bool HasEndpoint => !string.IsNullOrWhiteSpace(Endpoint);
}

/// <summary>
/// The GitHub Copilot CLI agent runtime, as one **named instance**
/// (<c>copilot-cli</c>) of <see cref="IAgentRuntimeProvider"/>.
///
/// Four things this implementation is careful about:
///
/// 1. **Start and poll, never await.** <see cref="StartAsync"/> spawns the
///    process and returns. It never waits for the agent to finish, because a
///    step may run for hours and a blocked call is indistinguishable from a
///    stuck one (docs/04 1.3, review 06 C1).
/// 2. **Resume is detected, not assumed.** <see cref="CheckAsync"/> asks the
///    real CLI whether it has <c>--resume</c> and <c>--session-id</c>, and
///    <see cref="AgentRuntimeCapabilities.SessionResume"/> reports what it
///    actually found. A retry resumes only when the capability is real.
/// 3. **Classification is never string matching.** Exit codes, process state and
///    the deadline are classified; exception text is never inspected. An agent
///    that ran out of time is <see cref="ErrorClass.Work"/> with reason
///    <c>timeout</c>, because docs/06 2.1 says the step overrides whatever the
///    provider classified — and the provider classifies it correctly to begin
///    with.
/// 4. **Nothing here assumes Copilot.** The instance name, the flags and the
///    session layout are all in <see cref="AgentRuntimeOptions"/> and this class,
///    so a second runtime is a new class, not an edit to the engine.
/// </summary>
public sealed class CopilotCliAgentRuntime : IAgentRuntimeProvider
{
    /// <summary>The named instance. docs/09 11.1: role mapping names an instance; a role never names a product.</summary>
    public const string InstanceName = "copilot-cli";

    /// <summary>
    /// What a resume says. The prior session already holds the task, so the
    /// continuation prompt is deliberately minimal.
    /// </summary>
    public const string ContinuePrompt = "Continue where you left off.";

    private readonly AgentRuntimeOptions options;
    private readonly IAgentRuntimeProcess process;
    private readonly ISecretsProvider? secrets;
    private readonly SecretScrubber scrubber;
    private readonly Func<DateTimeOffset> clock;

    private AgentRuntimeCapabilities? probed;

    public CopilotCliAgentRuntime(
        AgentRuntimeOptions? options = null,
        IAgentRuntimeProcess? process = null,
        ISecretsProvider? secrets = null,
        SecretScrubber? scrubber = null,
        Func<DateTimeOffset>? clock = null)
    {
        this.options = options ?? new AgentRuntimeOptions();
        this.process = process ?? new LocalAgentRuntimeProcess();
        this.secrets = secrets;
        this.scrubber = scrubber ?? new SecretScrubber();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The configuration this instance was built with. Safe to log; it holds no secret value.</summary>
    public AgentRuntimeOptions Options => options;

    /// <summary>
    /// The capabilities measured by the last <see cref="CheckAsync"/>, or null
    /// before one has run. Capability reporting is evidence-backed, not declared
    /// (docs/08 2.2).
    /// </summary>
    public AgentRuntimeCapabilities? Probed => probed;

    public ProviderDescriptor Describe() => new(
        Name: InstanceName,
        Version: "0.1.0",
        Capabilities: new object[] { probed ?? Unprobed() },
        Requires: Array.Empty<string>(),
        ConfigSchemaRef: "feature-flow/schemas/agent-runtime/copilot-cli/v1");

    /// <summary>
    /// Before a probe the provider claims nothing. Claiming a capability it has
    /// not measured is exactly the failure docs/08 2.2 exists to prevent.
    /// </summary>
    private static AgentRuntimeCapabilities Unprobed() => new(
        StructuredOutput: false,
        Streaming: false,
        ToolAllowlist: false,
        MaxContextTokens: 0,
        Hooks: false,
        SessionResume: false,
        UsageReporting: false,
        ModelConfiguration: false,
        CustomEndpoint: false);

    /// <summary>
    /// Probes the real CLI and reports what it actually supports.
    ///
    /// Two bounded calls: <c>--version</c> and <c>--help</c>. The help text is
    /// parsed for the flags the provider would need, so
    /// <see cref="AgentRuntimeCapabilities.SessionResume"/> is true only because
    /// the installed CLI really has <c>--resume</c> and <c>--session-id</c>.
    /// </summary>
    public async Task<ProviderResult<CheckResult>> CheckAsync(CancellationToken ct)
    {
        var started = clock();

        CommandOutput version;
        CommandOutput help;

        try
        {
            version = await process.RunAsync(
                Directory.GetCurrentDirectory(), options.Executable, new[] { "--version" }, ct)
                .ConfigureAwait(false);

            help = await process.RunAsync(
                Directory.GetCurrentDirectory(), options.Executable, new[] { "--help" }, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // Classified by exception type, never by its text (docs/04 section 1).
            return ProviderResult<CheckResult>.Infrastructure(
                "unreachable", TimeSpan.FromSeconds(5), $"'{options.Executable}' could not be started");
        }

        if (version.ExitCode != 0)
        {
            return ProviderResult<CheckResult>.Infrastructure(
                "unreachable",
                TimeSpan.FromSeconds(5),
                $"'{options.Executable} --version' exited {version.ExitCode}: " +
                scrubber.Scrub(version.StandardError).Trim());
        }

        if (help.ExitCode != 0)
        {
            return ProviderResult<CheckResult>.Infrastructure(
                "unreachable",
                TimeSpan.FromSeconds(5),
                $"'{options.Executable} --help' exited {help.ExitCode}");
        }

        var text = help.StandardOutput;

        // Hooks are probed, not inferred. The CLI only loads repo-level hooks
        // when the working directory is trusted, so the probe writes a hook
        // file into a scratch directory, runs one trivial prompt there, and
        // checks whether the hook actually fired. A capability that is declared
        // but does not fire is worse than one that is absent, because the
        // engine would rely on it (docs/08 2.2).
        var hooksFired = await ProbeHooksAsync(ct).ConfigureAwait(false);

        probed = new AgentRuntimeCapabilities(
            StructuredOutput: Has(text, "--output-format"),
            Streaming: Has(text, "--stream"),
            ToolAllowlist: Has(text, "--allow-tool") && Has(text, "--deny-tool"),
            MaxContextTokens: 0,
            Hooks: hooksFired,
            SessionResume: Has(text, "--resume") && Has(text, "--session-id"),
            UsageReporting: Has(text, "--usage-output-file"),
            ModelConfiguration: Has(text, "--model"),
            CustomEndpoint: true);

        var detail =
            $"{options.Executable} {version.StandardOutput.Trim()}; " +
            $"hooks={probed.Hooks} (probed) resume={probed.SessionResume} " +
            $"usage={probed.UsageReporting} model={probed.ModelConfiguration} " +
            $"endpoint={(options.HasEndpoint ? options.Endpoint : "(default routing)")}";

        return ProviderResult<CheckResult>.Ok(new CheckResult(
            Ok: true, Detail: detail, Latency: clock() - started));
    }

    private static bool Has(string helpText, string flag) =>
        helpText.Contains(flag, StringComparison.Ordinal);

    /// <summary>
    /// Whether the runtime's hooks actually fire. Writes a hook that appends to
    /// a file, runs one trivial prompt in a scratch directory, and checks
    /// whether the file appeared.
    ///
    /// The probe is bounded: it is one short prompt, and a failure of any kind
    /// means "hooks did not fire", which is the safe answer.
    /// </summary>
    private async Task<bool> ProbeHooksAsync(CancellationToken ct)
    {
        var probeRoot = Path.Combine(WorkingRoot, "hook-probe");
        var hooksDirectory = Path.Combine(probeRoot, ".github", "hooks");
        var marker = Path.Combine(probeRoot, "hook-fired.txt");

        try
        {
            Directory.CreateDirectory(hooksDirectory);
            File.Delete(marker);

            // Repo-level hooks are only loaded when the working directory is a
            // git repository: the CLI resolves a gitRoot and loads
            // .github/hooks from there. A scratch directory that is not a repo
            // silently loads nothing, which would make the probe report "no
            // hooks" for a runtime that does have them.
            if (!Directory.Exists(Path.Combine(probeRoot, ".git")))
            {
                await process.RunAsync(probeRoot, "git", new[] { "init", "--quiet" }, ct).ConfigureAwait(false);
            }

            // The event names are the ones the CLI accepts; it logs and ignores
            // any it does not recognise, so an unknown name is harmless here.
            var hook = """
            {
              "hooks": {
                "SessionStart": [ { "type": "command", "command": "cmd /c echo fired >> hook-fired.txt" } ]
              }
            }
            """;

            await File.WriteAllTextAsync(Path.Combine(hooksDirectory, "ff-probe.json"), hook, ct)
                .ConfigureAwait(false);

            var environment = await BuildEnvironmentAsync(ct).ConfigureAwait(false);
            if (environment.Error is not null)
                return false;

            var sessionId = Guid.NewGuid().ToString();
            var arguments = new List<string>
            {
                "--prompt", "Reply with the word ok.",
                "--silent",
                "--no-color",
                "--allow-all-tools",
                "--output-format", "json",
                "--session-id", sessionId,
            };

            if (!string.IsNullOrWhiteSpace(options.Model))
            {
                arguments.Add("--model");
                arguments.Add(options.Model);
            }

            var launch = process.Launch(probeRoot, options.Executable, arguments, environment.Value!);

            // Bounded: the probe waits only as long as a trivial prompt needs.
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(2);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (File.Exists(marker))
                    return true;

                if (!process.IsRunning(launch.ProcessId))
                    break;

                await Task.Delay(TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            }

            return File.Exists(marker);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidOperationException
                                      or System.ComponentModel.Win32Exception)
        {
            // A probe that cannot run means hooks did not fire, not that the
            // runtime is broken. The safe answer is false.
            return false;
        }
    }

    public async Task<ProviderResult<AgentSession>> StartAsync(
        SandboxHandle sandbox, AgentTask task, AgentLimits limits, string idempotencyKey, CancellationToken ct)
    {
        if (sandbox is null)
            return ProviderResult<AgentSession>.Work("validation_failed", "sandbox is required");
        if (task is null)
            return ProviderResult<AgentSession>.Work("validation_failed", "task is required");
        if (string.IsNullOrWhiteSpace(task.Prompt))
            return ProviderResult<AgentSession>.Work("validation_failed", "task prompt is empty");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return ProviderResult<AgentSession>.Work("validation_failed", "idempotency key is required");

        // A new session needs a UUID the runtime will accept, so a later resume
        // can name it. Generated here rather than read back from the runtime,
        // because StartAsync must return the reference without waiting for output.
        var sessionId = Guid.NewGuid().ToString();

        return await LaunchAsync(
            sandbox, sessionId, task.Prompt, limits, idempotencyKey, resumed: false, ct).ConfigureAwait(false);
    }

    public async Task<ProviderResult<AgentSession>> ResumeAsync(
        SandboxHandle sandbox, string sessionRef, AgentLimits limits, string idempotencyKey, CancellationToken ct)
    {
        if (sandbox is null)
            return ProviderResult<AgentSession>.Work("validation_failed", "sandbox is required");
        if (string.IsNullOrWhiteSpace(sessionRef))
            return ProviderResult<AgentSession>.Work("validation_failed", "session reference is required");
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return ProviderResult<AgentSession>.Work("validation_failed", "idempotency key is required");

        // Resume is a capability, not an assumption. If the installed CLI does
        // not have it, say so with a named missing capability rather than
        // silently starting a new session, which would lose the prior context
        // and look identical to a fresh attempt in the record.
        if (probed is not null && !probed.SessionResume)
        {
            return ProviderResult<AgentSession>.Work(
                "capability_missing", "this runtime does not declare session_resume");
        }

        if (!SessionExists(sessionRef))
        {
            return ProviderResult<AgentSession>.Work("not_found", $"no session '{sessionRef}' to resume");
        }

        return await LaunchAsync(
            sandbox, sessionRef, ContinuePrompt, limits, idempotencyKey, resumed: true, ct).ConfigureAwait(false);
    }

    public async Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken ct)
    {
        if (session is null)
            return ProviderResult<AgentProgress>.Work("validation_failed", "session is required");

        var running = session.ProcessId is int pid && process.IsRunning(pid);
        var turns = await CountTurnsAsync(session, ct).ConfigureAwait(false);
        var commit = await HeadCommitAsync(session.Sandbox.WorkingDirectory, ct).ConfigureAwait(false);

        return ProviderResult<AgentProgress>.Ok(new AgentProgress(
            LastCommit: commit,
            SessionTurns: turns,
            SessionRef: session.SessionRef,
            Running: running,
            Elapsed: clock() - session.StartedAt));
    }

    public async Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken ct)
    {
        if (session is null)
            return ProviderResult<AgentResult>.Work("validation_failed", "session is required");

        var running = session.ProcessId is int pid && process.IsRunning(pid);

        // The result is read from the runtime's own output, not from a hook.
        // docs/05 3.1: the session-end hook is a signal; GetResultAsync confirms.
        var usage = await ReadUsageAsync(session, ct).ConfigureAwait(false);
        var turns = await CountTurnsAsync(session, ct).ConfigureAwait(false);
        var commit = await HeadCommitAsync(session.Sandbox.WorkingDirectory, ct).ConfigureAwait(false);
        var summary = await ReadSummaryAsync(session, ct).ConfigureAwait(false);

        if (running)
        {
            // Not finished. Not an error: the step polls again.
            return ProviderResult<AgentResult>.Ok(new AgentResult(
                Outcome: AgentResult.Running,
                SessionRef: session.SessionRef,
                LastCommit: commit,
                SessionTurns: turns,
                Usage: usage));
        }

        var exitCode = session.ProcessId is int id ? process.ExitCode(id) : null;

        // docs/06 2.1: an agent that ran out of time is a work outcome, not
        // infrastructure. The step overrides whatever the provider classified,
        // and the provider classifies it correctly to begin with.
        if (session.PastDeadline(clock()))
        {
            return ProviderResult<AgentResult>.Fail(new ProviderError(
                ErrorClass.Work,
                "timeout",
                null,
                session.SessionRef,
                $"session exceeded its wall-clock limit of {Format(session.Deadline - session.StartedAt)}"));
        }

        if (exitCode is not null and not 0)
        {
            return ProviderResult<AgentResult>.Fail(new ProviderError(
                ErrorClass.Work,
                "failed",
                null,
                session.SessionRef,
                $"runtime exited {exitCode}: {await LastRuntimeError(session, ct).ConfigureAwait(false) ?? "(no error reported)"}"));
        }

        return ProviderResult<AgentResult>.Ok(new AgentResult(
            Outcome: "completed",
            SessionRef: session.SessionRef,
            Summary: summary,
            LastCommit: commit,
            SessionTurns: turns,
            Usage: usage,
            ExitCode: exitCode));
    }

    /// <summary>
    /// The runtime's own error message from its transcript, if it recorded one.
    /// Reported as detail only; classification never depends on matching its
    /// text (docs/04 section 1).
    /// </summary>
    private async Task<string?> LastRuntimeError(AgentSession session, CancellationToken ct)
    {
        var text = await process.ReadFileAsync(
            session.TranscriptPath ?? TranscriptPathFor(session.SessionRef), ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(text))
            return null;

        string? last = null;

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);

                if (!document.RootElement.TryGetProperty("type", out var type)
                    || type.GetString() != "session.error")
                {
                    continue;
                }

                if (document.RootElement.TryGetProperty("data", out var data)
                    && data.TryGetProperty("message", out var message))
                {
                    last = message.GetString();
                }
            }
            catch (JsonException)
            {
                // Skip a partially written line.
            }
        }

        return last is null ? null : scrubber.Scrub(last);
    }

    public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken ct)
    {
        if (session is null)
            return Task.FromResult(ProviderResult<Unit>.Work("validation_failed", "session is required"));

        if (session.ProcessId is not int pid)
            return Task.FromResult(ProviderResult<Unit>.Ok(Unit.Value));

        try
        {
            var target = System.Diagnostics.Process.GetProcessById(pid);
            if (!target.HasExited)
            {
                target.Kill(entireProcessTree: true);
            }

            // Idempotent: stopping a stopped session succeeds (docs/05 section 3).
            return Task.FromResult(ProviderResult<Unit>.Ok(Unit.Value));
        }
        catch (ArgumentException)
        {
            return Task.FromResult(ProviderResult<Unit>.Ok(Unit.Value));
        }
        catch (InvalidOperationException)
        {
            return Task.FromResult(ProviderResult<Unit>.Ok(Unit.Value));
        }
    }

    // ---------- launching ----------

    private async Task<ProviderResult<AgentSession>> LaunchAsync(
        SandboxHandle sandbox,
        string sessionId,
        string prompt,
        AgentLimits limits,
        string idempotencyKey,
        bool resumed,
        CancellationToken ct)
    {
        var arguments = BuildArguments(sessionId, prompt, limits);

        var environment = await BuildEnvironmentAsync(ct).ConfigureAwait(false);
        if (environment.Error is not null)
            return ProviderResult<AgentSession>.Fail(environment.Error);

        var now = clock();
        var deadline = limits.MaxWallClock is TimeSpan budget ? now + budget : (DateTimeOffset?)null;

        try
        {
            // The runtime writes its usage file here; it does not create the
            // directory itself.
            Directory.CreateDirectory(WorkingRoot);

            var launch = process.Launch(
                sandbox.WorkingDirectory, options.Executable, arguments, environment.Value!);

            return ProviderResult<AgentSession>.Ok(new AgentSession(
                SessionRef: sessionId,
                Sandbox: sandbox,
                IdempotencyKey: idempotencyKey,
                StartedAt: now,
                Resumed: resumed,
                ProcessId: launch.ProcessId,
                TranscriptPath: TranscriptPathFor(sessionId),
                UsagePath: UsagePathFor(sessionId),
                Deadline: deadline));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return ProviderResult<AgentSession>.Infrastructure(
                "unreachable", TimeSpan.FromSeconds(5), $"could not start '{options.Executable}'");
        }
    }

    private List<string> BuildArguments(string sessionId, string prompt, AgentLimits limits)
    {
        // --trust-folder and --session-id are mutually exclusive in the real
        // CLI: passing both exits 1 with "the argument '--trust-folder' cannot
        // be used with '--session-id <id>'". A session id is the more valuable
        // of the two, because resume needs it, so trust-folder is dropped when
        // one is set. Trust is then supplied by --allow-all-tools, which is
        // what non-interactive mode requires anyway.
        var arguments = new List<string>
        {
            "--prompt", prompt,
            "--silent",
            "--no-color",
            "--allow-all-tools",
            "--output-format", "json",
            "--session-id", sessionId,
            "--usage-output-file", UsagePathFor(sessionId),
        };

        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            arguments.Add("--model");
            arguments.Add(options.Model);
        }

        if (limits.MaxTurns is int turns)
        {
            arguments.Add("--max-autopilot-continues");
            arguments.Add(turns.ToString(CultureInfo.InvariantCulture));
        }

        if (limits.MaxCredits is double credits)
        {
            arguments.Add("--max-ai-credits");
            arguments.Add(credits.ToString(CultureInfo.InvariantCulture));
        }

        if (options.ExtraArguments is { Count: > 0 } extra)
        {
            arguments.AddRange(extra);
        }

        return arguments;
    }

    private async Task<ProviderResult<IReadOnlyDictionary<string, string>>> BuildEnvironmentAsync(CancellationToken ct)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal);

        // Repo-level hooks only load when the working directory is trusted.
        // --trust-folder would do it, but the real CLI rejects it alongside
        // --session-id ("the argument '--trust-folder' cannot be used with
        // '--session-id <id>'"), and resume needs the session id. This
        // environment variable is the documented equivalent, and it is what
        // makes the Hooks capability real rather than declared.
        environment["COPILOT_ALLOW_ALL"] = "true";

        if (!options.HasEndpoint)
            return ProviderResult<IReadOnlyDictionary<string, string>>.Ok(environment);

        environment["COPILOT_PROVIDER_BASE_URL"] = options.Endpoint!;
        environment["COPILOT_PROVIDER_TYPE"] = options.EndpointType;

        if (options.EndpointKeyReference is not SecretReference keyRef)
            return ProviderResult<IReadOnlyDictionary<string, string>>.Ok(environment);

        if (secrets is null)
        {
            return ProviderResult<IReadOnlyDictionary<string, string>>.Work(
                "validation_failed", "an endpoint key reference needs a secrets provider");
        }

        var resolved = await secrets.ResolveAsync(keyRef, ct).ConfigureAwait(false);
        if (resolved.Error is not null)
            return ProviderResult<IReadOnlyDictionary<string, string>>.Fail(resolved.Error);

        // The value goes into the child process environment only. It is
        // registered with the scrubber so it cannot reach a log, and it is never
        // written to configuration (floor F2).
        scrubber.Register(resolved.Value!.Value);
        environment["COPILOT_PROVIDER_API_KEY"] = resolved.Value!.Value;

        return ProviderResult<IReadOnlyDictionary<string, string>>.Ok(environment);
    }

    // ---------- paths ----------

    private string SessionStateDirectory =>
        options.SessionStateDirectory
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot", "session-state");

    private string WorkingRoot =>
        options.WorkingRoot ?? Path.Combine(Path.GetTempPath(), "feature-flow-agent-runtime");

    private string TranscriptPathFor(string sessionId) =>
        Path.Combine(SessionStateDirectory, sessionId, "events.jsonl");

    private string UsagePathFor(string sessionId) =>
        Path.Combine(WorkingRoot, $"usage-{sessionId}.json");

    private bool SessionExists(string sessionRef) =>
        Directory.Exists(Path.Combine(SessionStateDirectory, sessionRef));

    // ---------- reading the runtime's own output ----------

    /// <summary>
    /// Counts assistant turns in the session transcript. This is the session
    /// growth the attempt-budget rule needs (docs/01 3.7): a resume is free only
    /// if the session has grown.
    /// </summary>
    private async Task<int> CountTurnsAsync(AgentSession session, CancellationToken ct)
    {
        var text = await process.ReadFileAsync(
            session.TranscriptPath ?? TranscriptPathFor(session.SessionRef), ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(text))
            return 0;

        var turns = 0;

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("type", out var type)
                    && type.GetString() == "assistant.turn_end")
                {
                    turns++;
                }
            }
            catch (JsonException)
            {
                // A partially written line is normal while the runtime is
                // running. Skip it rather than failing the read.
            }
        }

        return turns;
    }

    /// <summary>
    /// Reads the runtime's own usage output. docs/08 2.5: without hooks, cost
    /// comes from the runtime's own usage output.
    /// </summary>
    private async Task<AgentUsage?> ReadUsageAsync(AgentSession session, CancellationToken ct)
    {
        var text = await process.ReadFileAsync(
            session.UsagePath ?? UsagePathFor(session.SessionRef), ct).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(text))
            return null;

        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;

            long? input = null;
            long? output = null;
            double? credits = null;
            string? model = null;

            if (root.TryGetProperty("currentModel", out var currentModel))
                model = currentModel.GetString();

            if (root.TryGetProperty("modelMetrics", out var metrics)
                && metrics.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in metrics.EnumerateObject())
                {
                    model ??= property.Name;

                    if (!property.Value.TryGetProperty("usage", out var usage))
                        continue;

                    if (usage.TryGetProperty("inputTokens", out var inTokens) && inTokens.TryGetInt64(out var i))
                        input = (input ?? 0) + i;

                    if (usage.TryGetProperty("outputTokens", out var outTokens) && outTokens.TryGetInt64(out var o))
                        output = (output ?? 0) + o;
                }
            }

            if (root.TryGetProperty("totalPremiumRequestCost", out var premium) && premium.TryGetDouble(out var p))
                credits = p;

            return new AgentUsage(
                Model: model,
                InputTokens: input,
                OutputTokens: output,
                Credits: credits,
                CostUsd: null,
                Source: "runtime_usage_output");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<string?> ReadSummaryAsync(AgentSession session, CancellationToken ct)
    {
        var text = await process.ReadFileAsync(
            session.TranscriptPath ?? TranscriptPathFor(session.SessionRef), ct).ConfigureAwait(false);

        if (string.IsNullOrEmpty(text))
            return null;

        string? last = null;

        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0)
                continue;

            try
            {
                using var document = JsonDocument.Parse(line);

                if (!document.RootElement.TryGetProperty("type", out var type)
                    || type.GetString() != "assistant.message")
                {
                    continue;
                }

                if (document.RootElement.TryGetProperty("data", out var data)
                    && data.TryGetProperty("content", out var content))
                {
                    last = content.GetString();
                }
            }
            catch (JsonException)
            {
                // Skip a partially written line.
            }
        }

        return last is null ? null : scrubber.Scrub(last);
    }

    private async Task<string?> HeadCommitAsync(string workingDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            return null;

        try
        {
            var output = await process.RunAsync(
                workingDirectory, "git", new[] { "rev-parse", "HEAD" }, ct).ConfigureAwait(false);

            return output.ExitCode == 0 ? output.StandardOutput.Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static string Format(TimeSpan? value) =>
        value is TimeSpan span ? $"{span.TotalSeconds:F0}s" : "(none)";
}
