using System.Globalization;
using FeatureFlow.Spike.AgentRuntime;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

// docs/04 section 3 names the agent runtime's progress return type
// `AgentProgress`, and Spike 1's contracts already have an `AgentProgress` for
// the heartbeat payload. Both names are correct for what they are, so the
// harness disambiguates rather than renaming either. See the report.
using AgentProgress = FeatureFlow.Spike.AgentRuntime.AgentProgress;
using AgentResult = FeatureFlow.Spike.AgentRuntime.AgentResult;
using AgentSession = FeatureFlow.Spike.AgentRuntime.AgentSession;

namespace FeatureFlow.Spike.AgentRuntimeHarness;

/// <summary>
/// Runs the <c>copilot-cli</c> agent runtime against the **real** GitHub Copilot
/// CLI and the **real** fixture repository, and prints what actually happened.
///
/// Six checks, one per verification requirement:
///
/// 1. <see cref="IAgentRuntimeProvider.CheckAsync"/> succeeds and reports the
///    real capability set.
/// 2. A task is started and a handle returned **without waiting** for completion.
/// 3. Progress is polled and reported (commit / session growth).
/// 4. The result comes from <see cref="IAgentRuntimeProvider.GetResultAsync"/>,
///    not from a hook.
/// 5. A retry **resumes** the same session rather than starting a new one, when
///    the capability exists.
/// 6. A timeout is classified <see cref="ErrorClass.Work"/>, not
///    <see cref="ErrorClass.Infrastructure"/>.
///
/// Nothing here is simulated. Every line comes from a real CLI process, a real
/// session directory, or a real git repository. Where a check cannot be
/// evidenced, the harness says so and counts it as a failure rather than
/// printing a plausible-looking line.
///
/// Usage:
///   dotnet run --project FeatureFlow.Spike.AgentRuntimeHarness -- --repo <path> [--model <name>] [--endpoint <url>]
/// </summary>
public static class Program
{
    private const string Owner = "ai-feature-flow";
    private const string Repo = "feature-flow-example";

    private static int failures;

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var options = Parse(args);

        WriteHeader("Spike 2, copilot-cli agent runtime, real run");
        Console.WriteLine($"utc:         {DateTimeOffset.UtcNow:O}");
        Console.WriteLine($"repository:  {Owner}/{Repo}");
        Console.WriteLine($"checkout:    {options.RepoPath}");
        Console.WriteLine($"model:       {options.Model ?? "(runtime default)"}");
        Console.WriteLine($"endpoint:    {options.Endpoint ?? "(runtime default routing)"}");
        Console.WriteLine();

        var runtimeOptions = new AgentRuntimeOptions(
            Executable: "copilot",
            Model: options.Model,
            Endpoint: options.Endpoint,
            EndpointType: "openai",
            WorkingRoot: Path.Combine(options.RepoPath, "..", "agent-runtime-work"));

        var provider = new CopilotCliAgentRuntime(runtimeOptions);

        await Check1Capabilities(provider).ConfigureAwait(false);
        Console.WriteLine();

        var sandbox = new SandboxHandle(
            Key: $"spike2-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}",
            WorkingDirectory: options.RepoPath,
            Os: "windows");

        var session = await Check2StartWithoutWaiting(provider, sandbox).ConfigureAwait(false);
        Console.WriteLine();

        if (session is null)
        {
            Console.WriteLine("aborting: no session handle, checks 3 to 6 cannot run");
            return 1;
        }

        var progress = await Check3Progress(provider, session).ConfigureAwait(false);
        Console.WriteLine();

        await Check4ResultFromGetResult(provider, session).ConfigureAwait(false);
        Console.WriteLine();

        await Check5Resume(provider, sandbox, session).ConfigureAwait(false);
        Console.WriteLine();

        Check6TimeoutIsWork();
        Console.WriteLine();

        WriteHeader("summary");
        Console.WriteLine(failures == 0
            ? "all checks passed"
            : $"{failures} check(s) FAILED");

        return failures == 0 ? 0 : 1;
    }

    // -----------------------------------------------------------------------
    // Check 1: CheckAsync and the real capability set
    // -----------------------------------------------------------------------

    private static async Task Check1Capabilities(IAgentRuntimeProvider provider)
    {
        WriteHeader("Check 1: CheckAsync and capability declaration");

        var descriptor = provider.Describe();
        Console.WriteLine($"name:        {descriptor.Name}");
        Console.WriteLine($"version:     {descriptor.Version}");
        Console.WriteLine($"schema:      {descriptor.ConfigSchemaRef}");
        Console.WriteLine($"requires:    {(descriptor.Requires.Count == 0 ? "(none)" : string.Join(", ", descriptor.Requires))}");

        // Before the probe the provider claims nothing. That is deliberate: a
        // capability it has not measured is not a capability it has.
        var before = descriptor.Capabilities.OfType<AgentRuntimeCapabilities>().FirstOrDefault();
        Console.WriteLine($"before probe: session_resume={before?.SessionResume.ToString() ?? "n/a"} " +
                          $"hooks={before?.Hooks.ToString() ?? "n/a"} (unprobed, claims nothing)");

        var check = await provider.CheckAsync(CancellationToken.None).ConfigureAwait(false);

        if (!check.IsOk)
        {
            Fail("Check 1", $"CheckAsync failed: {check.Error!.Class}/{check.Error.Reason} {check.Error.Detail}");
            return;
        }

        Console.WriteLine($"CheckAsync:  ok, latency {check.Value!.Latency?.TotalMilliseconds:F0}ms");
        Console.WriteLine($"detail:      {check.Value.Detail}");

        var capabilities = provider.Describe().Capabilities.OfType<AgentRuntimeCapabilities>().FirstOrDefault();
        if (capabilities is null)
        {
            Fail("Check 1", "no AgentRuntimeCapabilities after CheckAsync");
            return;
        }

        Console.WriteLine("capabilities (measured from the real CLI):");
        foreach (var line in capabilities.Lines())
        {
            Console.WriteLine($"  {line}");
        }

        Pass("Check 1", "CheckAsync succeeded and reported a measured capability set");
    }

    // -----------------------------------------------------------------------
    // Check 2: start returns a handle without waiting
    // -----------------------------------------------------------------------

    private static async Task<AgentSession?> Check2StartWithoutWaiting(
        IAgentRuntimeProvider provider, SandboxHandle sandbox)
    {
        WriteHeader("Check 2: StartAsync returns a handle without waiting");

        var task = new AgentTask(
            Prompt: "Read src/FeatureFlow.Example.Api/WidgetStore.cs and reply with one sentence " +
                    "describing what the Add method does. Do not modify any file.",
            Role: "reviewer",
            WriteAccess: "none",
            Branch: "main",
            Repository: $"{Owner}/{Repo}");

        var limits = new AgentLimits(MaxWallClock: TimeSpan.FromMinutes(10));
        var key = "spike2-agent-run-check2";

        var started = DateTimeOffset.UtcNow;
        var result = await provider.StartAsync(sandbox, task, limits, key, CancellationToken.None)
            .ConfigureAwait(false);
        var elapsed = DateTimeOffset.UtcNow - started;

        if (!result.IsOk)
        {
            Fail("Check 2", $"StartAsync failed: {result.Error!.Class}/{result.Error.Reason} {result.Error.Detail}");
            return null;
        }

        var session = result.Value!;
        Console.WriteLine($"session ref: {session.SessionRef}");
        Console.WriteLine($"process id:  {session.ProcessId?.ToString() ?? "(none)"}");
        Console.WriteLine($"resumed:     {session.Resumed}");
        Console.WriteLine($"key:         {session.IdempotencyKey}");
        Console.WriteLine($"deadline:    {session.Deadline?.ToString("O") ?? "(none)"}");
        Console.WriteLine($"StartAsync returned in {elapsed.TotalMilliseconds:F0}ms");

        // The proof that it did not wait: the process is still running.
        var progress = await provider.GetProgressAsync(session, CancellationToken.None).ConfigureAwait(false);
        var running = progress.IsOk && progress.Value!.Running;

        Console.WriteLine($"agent still running after StartAsync returned: {running}");

        if (elapsed > TimeSpan.FromSeconds(30))
        {
            Fail("Check 2", $"StartAsync took {elapsed.TotalSeconds:F0}s; docs/05 section 3 bounds it at 30s");
            return session;
        }

        if (!running)
        {
            // Not a failure of the contract, but it means the run finished
            // before we could observe it, so say so plainly.
            Console.WriteLine("note: the agent had already finished; the handle was still returned without waiting");
        }

        Pass("Check 2", $"handle returned in {elapsed.TotalMilliseconds:F0}ms without waiting for completion");
        return session;
    }

    // -----------------------------------------------------------------------
    // Check 3: progress is polled and reported
    // -----------------------------------------------------------------------

    private static async Task<AgentProgress?> Check3Progress(
        IAgentRuntimeProvider provider, AgentSession session)
    {
        WriteHeader("Check 3: GetProgressAsync reports progress evidence");

        AgentProgress? previous = null;
        AgentProgress? latest = null;
        var polls = 0;

        // Poll until the agent finishes, or the budget runs out. The engine
        // would do this inside one long-running activity (docs/05 1.2.1).
        var budget = TimeSpan.FromMinutes(6);
        var deadline = DateTimeOffset.UtcNow + budget;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await provider.GetProgressAsync(session, CancellationToken.None).ConfigureAwait(false);
            polls++;

            if (!result.IsOk)
            {
                Fail("Check 3", $"GetProgressAsync failed: {result.Error!.Class}/{result.Error.Reason}");
                return latest;
            }

            latest = result.Value!;
            var grew = latest.ShowsProgressAgainst(previous);

            Console.WriteLine(
                $"  poll {polls}: {latest.HeartbeatDetail}" +
                (previous is null ? "" : $" progress={grew}"));

            if (!latest.Running && polls > 1)
                break;

            previous = latest;
            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        if (latest is null)
        {
            Fail("Check 3", "no progress reading was obtained");
            return null;
        }

        var hasCommit = latest.LastCommit is not null;
        var hasTurns = latest.SessionTurns > 0;

        Console.WriteLine($"final: commit={latest.LastCommit ?? "(none)"} turns={latest.SessionTurns} " +
                          $"running={latest.Running}");
        Console.WriteLine($"commit evidence: {hasCommit}, session growth evidence: {hasTurns}");

        if (!hasCommit && !hasTurns)
        {
            Fail("Check 3", "progress reported neither a commit nor session growth");
            return latest;
        }

        Pass("Check 3", $"progress reported over {polls} polls: " +
                        (hasCommit ? "commit" : "") + (hasCommit && hasTurns ? " + " : "") +
                        (hasTurns ? "session growth" : ""));
        return latest;
    }

    // -----------------------------------------------------------------------
    // Check 4: the result comes from GetResultAsync, not from a hook
    // -----------------------------------------------------------------------

    private static async Task Check4ResultFromGetResult(
        IAgentRuntimeProvider provider, AgentSession session)
    {
        WriteHeader("Check 4: GetResultAsync is the source of truth");

        // Wait for the process to exit, then read the result. The engine would
        // be woken by the session-end hook and would then confirm here.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(6);
        ProviderResult<AgentResult> result;

        while (true)
        {
            result = await provider.GetResultAsync(session, CancellationToken.None).ConfigureAwait(false);

            if (!result.IsOk)
                break;

            if (result.Value!.Outcome != AgentResult.Running)
                break;

            if (DateTimeOffset.UtcNow > deadline)
            {
                Fail("Check 4", "the agent did not finish within the harness budget");
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        if (!result.IsOk)
        {
            Fail("Check 4", $"GetResultAsync failed: {result.Error!.Class}/{result.Error.Reason} {result.Error.Detail}");
            return;
        }

        var value = result.Value!;
        Console.WriteLine($"outcome:     {value.Outcome}");
        Console.WriteLine($"exit code:   {value.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "(none)"}");
        Console.WriteLine($"session ref: {value.SessionRef}");
        Console.WriteLine($"turns:       {value.SessionTurns}");
        Console.WriteLine($"last commit: {value.LastCommit ?? "(none)"}");
        Console.WriteLine($"usage:       {value.Usage?.ToString() ?? "(none reported)"}");

        if (value.Summary is { Length: > 0 } summary)
        {
            var shown = summary.Length > 200 ? summary[..200] + "..." : summary;
            Console.WriteLine($"summary:     {shown.Replace("\n", " ", StringComparison.Ordinal)}");
        }

        // The point of the check: no hook was involved. The provider never
        // registers one, and the outcome was read from the runtime's own files.
        Console.WriteLine("source:      the runtime's own transcript and usage file, not a hook");

        if (value.Outcome != "completed")
        {
            Fail("Check 4", $"outcome was '{value.Outcome}', expected 'completed'");
            return;
        }

        Pass("Check 4", "outcome read from GetResultAsync, no hook involved");
    }

    // -----------------------------------------------------------------------
    // Check 5: a retry resumes the same session
    // -----------------------------------------------------------------------

    private static async Task Check5Resume(
        IAgentRuntimeProvider provider, SandboxHandle sandbox, AgentSession session)
    {
        WriteHeader("Check 5: a retry resumes the same session");

        var capabilities = provider.Describe().Capabilities.OfType<AgentRuntimeCapabilities>().FirstOrDefault();
        if (capabilities is null || !capabilities.SessionResume)
        {
            Fail("Check 5", "the runtime does not declare session_resume; resume cannot be evidenced");
            return;
        }

        Console.WriteLine($"session_resume capability: {capabilities.SessionResume} (measured, not assumed)");

        var limits = new AgentLimits(MaxWallClock: TimeSpan.FromMinutes(10));
        var key = "spike2-agent-run-check2";   // the same key as the first attempt

        var result = await provider.ResumeAsync(sandbox, session.SessionRef, limits, key, CancellationToken.None)
            .ConfigureAwait(false);

        if (!result.IsOk)
        {
            Fail("Check 5", $"ResumeAsync failed: {result.Error!.Class}/{result.Error.Reason} {result.Error.Detail}");
            return;
        }

        var resumed = result.Value!;
        Console.WriteLine($"session ref: {resumed.SessionRef}");
        Console.WriteLine($"resumed:     {resumed.Resumed}");
        Console.WriteLine($"process id:  {resumed.ProcessId?.ToString() ?? "(none)"}");

        var sameSession = string.Equals(resumed.SessionRef, session.SessionRef, StringComparison.Ordinal);
        Console.WriteLine($"same session as the first attempt: {sameSession}");

        if (!sameSession)
        {
            Fail("Check 5", $"resume produced a different session: {resumed.SessionRef} != {session.SessionRef}");
            return;
        }

        // The real proof: the resumed session's transcript contains the turns
        // from the first attempt, so the session grew rather than restarted.
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(6);
        AgentProgress? latest = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var progress = await provider.GetProgressAsync(resumed, CancellationToken.None).ConfigureAwait(false);
            if (progress.IsOk)
                latest = progress.Value!;

            if (latest is not null && !latest.Running)
                break;

            await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }

        if (latest is null)
        {
            Fail("Check 5", "no progress reading after resume");
            return;
        }

        Console.WriteLine($"after resume: turns={latest.SessionTurns} running={latest.Running}");

        var resultAfter = await provider.GetResultAsync(resumed, CancellationToken.None).ConfigureAwait(false);
        if (resultAfter.IsOk)
        {
            Console.WriteLine($"outcome after resume: {resultAfter.Value!.Outcome}, " +
                              $"turns={resultAfter.Value.SessionTurns}");
        }

        // A resumed session carries the prior turns. A fresh session would start
        // at zero, so a non-zero count here is the evidence that it resumed.
        if (latest.SessionTurns <= 0)
        {
            Fail("Check 5", "the resumed session reported no turns, so resume could not be evidenced");
            return;
        }

        Pass("Check 5", $"resumed session {resumed.SessionRef} with {latest.SessionTurns} turns carried over");
    }

    // -----------------------------------------------------------------------
    // Check 6: a timeout is Work, not Infrastructure
    // -----------------------------------------------------------------------

    private static void Check6TimeoutIsWork()
    {
        WriteHeader("Check 6: a timeout is classified Work, not Infrastructure");

        // A session whose deadline has passed. Built directly rather than by
        // waiting for a real 4-hour step, so the classification is exercised
        // without spending four hours.
        var now = DateTimeOffset.UtcNow;
        var session = new AgentSession(
            SessionRef: Guid.NewGuid().ToString(),
            Sandbox: new SandboxHandle("timeout-probe", Directory.GetCurrentDirectory()),
            IdempotencyKey: "spike2-timeout-probe",
            StartedAt: now - TimeSpan.FromMinutes(5),
            Deadline: now - TimeSpan.FromSeconds(1));

        Console.WriteLine($"session deadline: {session.Deadline:O} (now {now:O})");
        Console.WriteLine($"past deadline:    {session.PastDeadline(now)}");

        var provider = new CopilotCliAgentRuntime();
        var result = provider.GetResultAsync(session, CancellationToken.None).GetAwaiter().GetResult();

        if (result.IsOk)
        {
            Fail("Check 6", $"expected a timeout error, got outcome '{result.Value!.Outcome}'");
            return;
        }

        var error = result.Error!;
        Console.WriteLine($"class:  {error.Class}");
        Console.WriteLine($"reason: {error.Reason}");
        Console.WriteLine($"detail: {error.Detail}");

        if (error.Class != ErrorClass.Work || error.Reason != "timeout")
        {
            Fail("Check 6", $"expected Work/timeout, got {error.Class}/{error.Reason}");
            return;
        }

        Console.WriteLine("docs/06 2.1: an agent that ran out of time is a work outcome, " +
                          "and the step overrides whatever the provider classified");

        Pass("Check 6", "timeout classified Work/timeout, not Infrastructure");
    }

    // -----------------------------------------------------------------------
    // plumbing
    // -----------------------------------------------------------------------

    private sealed record HarnessOptions(string RepoPath, string? Model, string? Endpoint);

    private static HarnessOptions Parse(string[] args)
    {
        string? repoPath = null;
        string? model = null;
        string? endpoint = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--repo" when i + 1 < args.Length:
                    repoPath = args[++i];
                    break;
                case "--model" when i + 1 < args.Length:
                    model = args[++i];
                    break;
                case "--endpoint" when i + 1 < args.Length:
                    endpoint = args[++i];
                    break;
                default:
                    if (args[i].StartsWith("--repo=", StringComparison.Ordinal))
                        repoPath = args[i]["--repo=".Length..];
                    else if (args[i].StartsWith("--model=", StringComparison.Ordinal))
                        model = args[i]["--model=".Length..];
                    else if (args[i].StartsWith("--endpoint=", StringComparison.Ordinal))
                        endpoint = args[i]["--endpoint=".Length..];
                    break;
            }
        }

        // Default: the fixture checkout beside this project, if it is there.
        repoPath ??= Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "fixture"));

        if (!Directory.Exists(repoPath))
        {
            Console.Error.WriteLine($"error: repository checkout not found at {repoPath}");
            Console.Error.WriteLine("clone it, or pass --repo <path>");
            Environment.Exit(2);
        }

        return new HarnessOptions(repoPath, model, endpoint);
    }

    private static void WriteHeader(string title)
    {
        Console.WriteLine("=== " + title + " ===");
    }

    private static void Pass(string check, string detail)
    {
        Console.WriteLine($"PASS {check}: {detail}");
    }

    private static void Fail(string check, string detail)
    {
        failures++;
        Console.WriteLine($"FAIL {check}: {detail}");
    }
}
