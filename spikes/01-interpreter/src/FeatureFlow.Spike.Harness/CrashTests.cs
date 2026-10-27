using System.Diagnostics;
using System.Text;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// The crash tests. docs/08 2.1 exit criterion 1 and the measurements that go
/// with it: wall-clock time lost per crash, attempts consumed versus resumes,
/// and whether any step restarted that should have resumed.
///
/// Spike code: every verdict is printed with the evidence it came from, and a
/// test that cannot be made to pass says so rather than reporting a pass.
/// </summary>
public static class CrashTests
{
    private const string Address = "localhost:7233";
    private const string SandboxUrl = "http://127.0.0.1:5199";

    /// <summary>Where the worker project lives, so the harness can start it.</summary>
    private static string WorkerDir() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "FeatureFlow.Spike.Worker"));

    private static string ResultsDir()
    {
        var dir = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "results"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static async Task<int> RunAsync(ITemporalClient client, string queue, string[] args)
    {
        var only = args.Length > 1 ? args[1] : null;
        var sandbox = new SandboxClient(SandboxUrl);
        var report = new StringBuilder();
        var logDir = Path.Combine(ResultsDir(), "crash-logs");
        Directory.CreateDirectory(logDir);

        void Say(string line)
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        // A worker left over from restart.ps1 would pick up the retried activity
        // the moment this harness kills its own, which would hide the outage the
        // test is measuring. Take ownership of the queue first.
        var stray = await StopStrayWorkersAsync(queue, Say);
        Say($"# Spike 1 crash-resume results");
        Say("");
        Say($"Captured {DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ssZ}. The harness starts and kills " +
            $"the worker itself (Process.Kill(entireProcessTree: true) on the pid it started). " +
            $"Sandbox {SandboxUrl}, Temporal {Address}, queue {queue}. " +
            $"{stray} stray worker(s) stopped before the run.");
        Say("");

        var tests = new (string Name, Func<Task<Verdict>> Run)[]
        {
            ("1-resume-after-crash", () => ResumeAfterCrashAsync(client, queue, sandbox, logDir, Say)),
            ("2-repeated-kill-no-progress", () => RepeatedKillNoProgressAsync(client, queue, sandbox, logDir, Say)),
            ("3-hook-unreachable-infrastructure", () => HookUnreachableAsync(client, queue, sandbox, logDir, Say)),
            ("4-break-connection", () => BreakConnectionAsync(client, queue, sandbox, logDir, Say)),
        };

        var verdicts = new List<Verdict>();
        foreach (var (name, run) in tests)
        {
            if (only is not null && only != name && only != "all")
                continue;
            Say($"## {name}");
            Say("");
            Verdict verdict;
            try
            {
                verdict = await run();
            }
            catch (Exception ex)
            {
                verdict = new Verdict("INCONCLUSIVE", $"threw {ex.GetType().Name}: {ex.Message}");
                Say($"threw {ex.GetType().Name}: {ex.Message}");
            }
            verdicts.Add(verdict with { Name = name });
            Say("");
            Say($"**{verdict.Status}** — {verdict.Summary}");
            Say("");
        }

        // Measurement 5: wall-clock time lost per crash.
        Say("## 5-wall-clock-cost-per-crash");
        Say("");
        var costs = verdicts.SelectMany(v => v.CrashCosts).ToList();
        if (costs.Count == 0)
        {
            Say("No crash was measured.");
        }
        else
        {
            Say("| crash | kill -> step making progress again |");
            Say("|---|---|");
            foreach (var c in costs)
                Say($"| {c.Label} | {c.Seconds:F1}s |");
            Say("");
            Say($"n={costs.Count} min={costs.Min(c => c.Seconds):F1}s " +
                $"max={costs.Max(c => c.Seconds):F1}s mean={costs.Average(c => c.Seconds):F1}s");
        }
        Say("");

        // Measurement 6: did anything restart that should have resumed.
        Say("## 6-restarted-when-should-have-resumed");
        Say("");
        if (verdicts.Count == 0)
        {
            Say("No test ran.");
        }
        else
        {
            foreach (var v in verdicts)
                Say($"- {v.Name}: {(v.Restarts.Count == 0 ? "none" : string.Join("; ", v.Restarts))}");
        }
        Say("");

        var path = Path.Combine(ResultsDir(), "crash-resume.md");
        await File.WriteAllTextAsync(path, report.ToString());
        Console.WriteLine($"HARNESS wrote {path}");

        return verdicts.All(v => v.Status == "PASS") ? 0 : 1;
    }

    // ---------------------------------------------------------------- test 1

    /// <summary>
    /// Exit criterion 1: a worker crash mid-agent.run resumes rather than
    /// restarts. Evidence: same command handle, same sandboxKey, same session
    /// reference, and elapsedSeconds continuing rather than resetting to 0.
    /// </summary>
    private static async Task<Verdict> ResumeAfterCrashAsync(
        ITemporalClient client, string queue, SandboxClient sandbox, string logDir,
        Action<string> say)
    {
        const string stepId = "long_agent";
        await sandbox.ResetAsync();

        var plan = SingleAgentPlan(stepId, durationSeconds: 90, attempts: 3);
        var id = $"crash1-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        await using var worker = new WorkerProcess(
            WorkerDir(), queue, SandboxUrl, Address, 10,
            Path.Combine(logDir, "t1-worker.log"));
        var pid = await worker.StartAsync();
        say($"worker started pid={pid}");

        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, queue));

        var before = await WaitForCommandAsync(sandbox, stepId, minTurns: 3, say);
        if (before is null)
            return new Verdict("INCONCLUSIVE", "the sandbox never registered a command for the step");

        say($"before kill: {before.OneLine()}");
        var stateBefore = await sandbox.StateAsync();
        say($"before kill: /admin/state has {stateBefore.Commands.Count} command(s), " +
            $"hookDown=[{string.Join(",", stateBefore.HookDownStepIds)}]");

        // Kill the worker mid-step, then restart it.
        var killedAt = DateTimeOffset.UtcNow;
        worker.Kill();
        say($"worker {pid} killed at {killedAt:HH:mm:ss.fff}");

        var sw = Stopwatch.StartNew();
        var newPid = await worker.StartAsync();
        say($"worker restarted pid={newPid} at +{sw.Elapsed.TotalSeconds:F1}s after the kill");

        string result;
        try
        {
            result = await handle.GetResultAsync<string>();
        }
        catch (Exception ex)
        {
            result = $"threw {ex.GetType().Name}";
        }
        sw.Stop();

        var state = await handle.QueryAsync(w => w.State());
        var run = state.StepRuns.TryGetValue(stepId, out var r) ? r : null;
        var after = await sandbox.StateAsync();

        say($"result={result} outcome={run?.Outcome} attempts={run?.Attempts} " +
            $"resumes={run?.Resumes} infrastructureFailures={run?.InfrastructureFailures}");
        say($"workflow resume evidence: handle={run?.CommandHandle} key={run?.SandboxKey} " +
            $"session={run?.SessionRef}");
        say($"lastError={state.LastInfrastructureError}");
        foreach (var c in after.Commands)
            say($"after: {c.OneLine()}");

        var checks = new List<string>();
        var ok = true;

        var oneCommand = after.Commands.Count == 1;
        checks.Add($"exactly one command in /admin/state (no second command started): {oneCommand}");
        ok &= oneCommand;

        var first = after.Commands.FirstOrDefault();
        var sameHandle = first is not null && first.Handle == before.Handle;
        checks.Add($"same handle ({before.Handle} -> {first?.Handle}): {sameHandle}");
        ok &= sameHandle;

        var sameKey = first is not null && first.SandboxKey == before.SandboxKey;
        checks.Add($"same sandboxKey ({before.SandboxKey} -> {first?.SandboxKey}): {sameKey}");
        ok &= sameKey;

        var sameSession = first is not null && first.SessionRef == before.SessionRef;
        checks.Add($"same sessionRef ({before.SessionRef} -> {first?.SessionRef}): {sameSession}");
        ok &= sameSession;

        var elapsed = first?.ElapsedSeconds ?? 0;
        var continued = elapsed > before.ElapsedSeconds;
        checks.Add($"elapsedSeconds continued ({before.ElapsedSeconds:F2}s -> {elapsed:F2}s, " +
                   $"not reset to 0): {continued}");
        ok &= continued;

        var completed = run?.Outcome == Outcomes.Completed;
        checks.Add($"step outcome={run?.Outcome}: {completed}");
        ok &= completed;

        var noAttempt = (run?.Attempts ?? 99) == 1;
        checks.Add($"attempts={run?.Attempts} (a crash must not consume one): {noAttempt}");
        ok &= noAttempt;

        var resumed = (run?.Resumes ?? 0) >= 1;
        checks.Add($"resumes={run?.Resumes} (>=1 means the crash was seen as a resume): {resumed}");
        ok &= resumed;

        foreach (var c in checks)
            say($"- {c}");

        var restarts = new List<string>();
        if (!sameHandle || !oneCommand)
            restarts.Add($"{stepId}: command handle changed or a second command was started");
        if (run?.RestartedWhenShouldHaveResumed == true)
            restarts.Add($"{stepId}: workflow flagged RestartedWhenShouldHaveResumed");

        return new Verdict(ok ? "PASS" : "FAIL", string.Join("; ", checks))
        {
            Restarts = restarts,
            CrashCosts = new List<CrashCost> { new("test1 kill mid-agent.run", sw.Elapsed.TotalSeconds) },
        };
    }

    // ---------------------------------------------------------------- test 2

    /// <summary>
    /// docs/01 3.7: after 3 consecutive resumes with no progress, the next
    /// counts as a restart and the attempt allowance is consumed. The sandbox
    /// stall endpoint freezes progress; the worker is killed repeatedly.
    /// </summary>
    private static async Task<Verdict> RepeatedKillNoProgressAsync(
        ITemporalClient client, string queue, SandboxClient sandbox, string logDir,
        Action<string> say)
    {
        const string stepId = "stalled_agent";
        await sandbox.ResetAsync();

        var plan = SingleAgentPlan(stepId, durationSeconds: 3600, attempts: 3);
        var id = $"crash2-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        await using var worker = new WorkerProcess(
            WorkerDir(), queue, SandboxUrl, Address, 10,
            Path.Combine(logDir, "t2-worker.log"));
        var pid = await worker.StartAsync();
        say($"worker started pid={pid}");

        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, queue));

        var before = await WaitForCommandAsync(sandbox, stepId, minTurns: 2, say);
        if (before is null)
            return new Verdict("INCONCLUSIVE", "the sandbox never registered a command for the step");

        // Freeze progress. The command keeps running; turns and commits stop.
        await sandbox.StallAsync(before.Handle);
        say($"stalled {before.Handle} at turns={before.SessionTurns} commit={before.LastCommit}");

        var attemptsSeen = new List<int>();
        var costs = new List<CrashCost>();

        // Each kill is a resume. With no progress, every third one must consume
        // an attempt, so exhaustion at allowance 3 takes about nine kills.
        for (var i = 1; i <= 12; i++)
        {
            var state0 = await handle.QueryAsync(w => w.State());
            var run0 = state0.StepRuns.TryGetValue(stepId, out var r0) ? r0 : null;
            if (run0 is not null && run0.Outcome.Length > 0)
            {
                say($"kill {i}: step already terminal ({run0.Outcome}), stopping");
                break;
            }

            worker.Kill();
            await worker.StartAsync();

            // Wait for the step to make progress again: a new resume, a new
            // attempt, or a terminal outcome. This is what measurement 5 times.
            var sw = Stopwatch.StartNew();
            await WaitForResumeAsync(handle, stepId, run0?.Resumes ?? 0, run0?.Attempts ?? 0,
                TimeSpan.FromSeconds(180), say);
            sw.Stop();

            var state1 = await handle.QueryAsync(w => w.State());
            var run1 = state1.StepRuns.TryGetValue(stepId, out var r1) ? r1 : null;
            var turns = (await sandbox.CommandAsync(before.Handle))?.SessionTurns ?? -1;

            say($"kill {i}: resumes={run1?.Resumes} attempts={run1?.Attempts} " +
                $"outcome='{run1?.Outcome}' turns={turns} recovered in {sw.Elapsed.TotalSeconds:F1}s");
            say($"  lastError={state1.LastInfrastructureError}");

            attemptsSeen.Add(run1?.Attempts ?? 0);
            costs.Add(new CrashCost($"test2 kill {i}", sw.Elapsed.TotalSeconds));

            if (run1 is not null && run1.Outcome.Length > 0)
            {
                say($"kill {i}: step terminal ({run1.Outcome})");
                break;
            }
        }

        var final = await handle.QueryAsync(w => w.State());
        var run = final.StepRuns.TryGetValue(stepId, out var rf) ? rf : null;
        say($"final: outcome={run?.Outcome} attempts={run?.Attempts} resumes={run?.Resumes} " +
            $"terminal={final.TerminalStatus}");

        var checks = new List<string>();
        var ok = true;

        var attemptsRose = attemptsSeen.Count > 1 && attemptsSeen[^1] > attemptsSeen[0];
        checks.Add($"attempt count rose ({string.Join("->", attemptsSeen)}): {attemptsRose}");
        ok &= attemptsRose;

        var exhausted = run?.Outcome == Outcomes.AttemptsExhausted;
        checks.Add($"step reached attempts_exhausted (outcome={run?.Outcome}): {exhausted}");
        ok &= exhausted;

        // Resumes are free until three without progress, so exhaustion must
        // have taken more resumes than attempts.
        var moreResumes = (run?.Resumes ?? 0) > (run?.Attempts ?? 0);
        checks.Add($"resumes={run?.Resumes} > attempts={run?.Attempts} " +
                   $"(resumes free until 3 without progress): {moreResumes}");
        ok &= moreResumes;

        foreach (var c in checks)
            say($"- {c}");

        var restarts = new List<string>();
        if (run?.RestartedWhenShouldHaveResumed == true)
            restarts.Add($"{stepId}: workflow flagged RestartedWhenShouldHaveResumed");

        return new Verdict(ok ? "PASS" : "FAIL", string.Join("; ", checks))
        {
            Restarts = restarts,
            CrashCosts = costs,
        };
    }

    // ---------------------------------------------------------------- test 3

    /// <summary>
    /// R19: the hook endpoint going unreachable mid-step is infrastructure, not
    /// a denied tool call. It retries and does not consume an attempt.
    /// </summary>
    private static async Task<Verdict> HookUnreachableAsync(
        ITemporalClient client, string queue, SandboxClient sandbox, string logDir,
        Action<string> say)
    {
        const string stepId = "hook_agent";
        await sandbox.ResetAsync();

        var plan = SingleAgentPlan(stepId, durationSeconds: 60, attempts: 3);
        var id = $"crash3-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        await using var worker = new WorkerProcess(
            WorkerDir(), queue, SandboxUrl, Address, 10,
            Path.Combine(logDir, "t3-worker.log"));
        var pid = await worker.StartAsync();
        say($"worker started pid={pid}");

        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, queue));

        var cmd = await WaitForCommandAsync(sandbox, stepId, minTurns: 2, say);
        if (cmd is null)
            return new Verdict("INCONCLUSIVE", "the sandbox never registered a command for the step");

        say($"hook reachable before injection: {await sandbox.HookUpAsync(stepId)}");
        await sandbox.FailHookAsync(cmd.Handle);
        say($"fail-hook injected for {stepId}; hook reachable now: {await sandbox.HookUpAsync(stepId)}");

        // Let the activity notice and fail, then restore the hook so the step
        // can finish. The point is what the workflow does with the failure.
        await Task.Delay(TimeSpan.FromSeconds(6));
        var mid = await handle.QueryAsync(w => w.State());
        var midRun = mid.StepRuns.TryGetValue(stepId, out var mr) ? mr : null;
        say($"during outage: outcome='{midRun?.Outcome}' attempts={midRun?.Attempts} " +
            $"resumes={midRun?.Resumes} infrastructureFailures={midRun?.InfrastructureFailures}");
        say($"  lastError={mid.LastInfrastructureError}");

        await sandbox.RestoreHookAsync(stepId);
        say($"hook restored; hook reachable now: {await sandbox.HookUpAsync(stepId)}");

        string result;
        try
        {
            result = await handle.GetResultAsync<string>();
        }
        catch (Exception ex)
        {
            result = $"threw {ex.GetType().Name}";
        }

        var state = await handle.QueryAsync(w => w.State());
        var run = state.StepRuns.TryGetValue(stepId, out var r) ? r : null;
        say($"result={result} outcome={run?.Outcome} attempts={run?.Attempts} " +
            $"resumes={run?.Resumes} infrastructureFailures={run?.InfrastructureFailures}");
        say($"lastError={state.LastInfrastructureError}");

        var checks = new List<string>();
        var ok = true;

        // Not a work outcome: the step did not fail.
        var notFailed = run?.Outcome != Outcomes.Failed;
        checks.Add($"outcome={run?.Outcome} is not 'failed' (not treated as a denied tool call): {notFailed}");
        ok &= notFailed;

        // Infrastructure is free: no attempt consumed.
        var noAttempt = (run?.Attempts ?? 99) == 1;
        checks.Add($"attempts={run?.Attempts} (infrastructure retries are free): {noAttempt}");
        ok &= noAttempt;

        // It retried rather than giving up.
        var retried = (run?.InfrastructureFailures ?? 0) >= 1;
        checks.Add($"infrastructureFailures={run?.InfrastructureFailures} (it retried): {retried}");
        ok &= retried;

        var completed = run?.Outcome == Outcomes.Completed;
        checks.Add($"step completed once the hook came back: {completed}");
        ok &= completed;

        foreach (var c in checks)
            say($"- {c}");

        return new Verdict(ok ? "PASS" : "FAIL", string.Join("; ", checks));
    }

    // ---------------------------------------------------------------- test 4

    /// <summary>
    /// K3: break the worker-to-sandbox connection mid-command. The activity
    /// retries and re-attaches, and the command keeps running: elapsedSeconds
    /// and sessionTurns keep advancing across the outage.
    /// </summary>
    private static async Task<Verdict> BreakConnectionAsync(
        ITemporalClient client, string queue, SandboxClient sandbox, string logDir,
        Action<string> say)
    {
        const string stepId = "k3_agent";
        await sandbox.ResetAsync();

        var plan = SingleAgentPlan(stepId, durationSeconds: 60, attempts: 3);
        var id = $"crash4-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";

        await using var worker = new WorkerProcess(
            WorkerDir(), queue, SandboxUrl, Address, 10,
            Path.Combine(logDir, "t4-worker.log"));
        var pid = await worker.StartAsync();
        say($"worker started pid={pid}");

        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, queue));

        var cmd = await WaitForCommandAsync(sandbox, stepId, minTurns: 2, say);
        if (cmd is null)
            return new Verdict("INCONCLUSIVE", "the sandbox never registered a command for the step");

        say($"before outage: {cmd.OneLine()}");

        // Break the connection for 20s. The sandbox keeps the command running.
        await sandbox.BreakConnectionAsync(cmd.Handle, 20);
        say($"connection broken for 20s on {cmd.Handle}");

        // Sample /admin/state during the outage: the command must keep running
        // even though GET /commands/{handle} is 503.
        var during = new List<CommandView>();
        for (var i = 0; i < 4; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(4));
            var s = await sandbox.StateAsync();
            var c = s.Commands.FirstOrDefault(x => x.Handle == cmd.Handle);
            if (c is not null)
            {
                during.Add(c);
                say($"during outage: {c.OneLine()}");
            }
            var direct = await sandbox.CommandAsync(cmd.Handle);
            say($"  GET /commands/{cmd.Handle} -> {(direct is null ? "503 unreachable" : "200")}");
        }

        string result;
        try
        {
            result = await handle.GetResultAsync<string>();
        }
        catch (Exception ex)
        {
            result = $"threw {ex.GetType().Name}";
        }

        var state = await handle.QueryAsync(w => w.State());
        var run = state.StepRuns.TryGetValue(stepId, out var r) ? r : null;
        var after = await sandbox.StateAsync();
        say($"result={result} outcome={run?.Outcome} attempts={run?.Attempts} " +
            $"resumes={run?.Resumes} infrastructureFailures={run?.InfrastructureFailures}");
        say($"lastError={state.LastInfrastructureError}");
        foreach (var c in after.Commands)
            say($"after: {c.OneLine()}");

        var checks = new List<string>();
        var ok = true;

        // The command kept running across the outage.
        var advanced = during.Count >= 2 &&
                       during[^1].ElapsedSeconds > during[0].ElapsedSeconds &&
                       during[^1].SessionTurns > during[0].SessionTurns;
        checks.Add($"elapsed and turns advanced during the outage " +
                   $"({(during.Count > 0 ? $"{during[0].ElapsedSeconds:F2}s/{during[0].SessionTurns} turns" : "n/a")} -> " +
                   $"{(during.Count > 0 ? $"{during[^1].ElapsedSeconds:F2}s/{during[^1].SessionTurns} turns" : "n/a")}): {advanced}");
        ok &= advanced;

        // Same handle: re-attached, not restarted.
        var sameHandle = after.Commands.Count == 1 && after.Commands[0].Handle == cmd.Handle;
        checks.Add($"same handle after the outage ({cmd.Handle}): {sameHandle}");
        ok &= sameHandle;

        var completed = run?.Outcome == Outcomes.Completed;
        checks.Add($"step completed (outcome={run?.Outcome}): {completed}");
        ok &= completed;

        // Infrastructure is free: the outage must not consume an attempt.
        var noAttempt = (run?.Attempts ?? 99) == 1;
        checks.Add($"attempts={run?.Attempts} (outage is infrastructure, free): {noAttempt}");
        ok &= noAttempt;

        foreach (var c in checks)
            say($"- {c}");

        var restarts = new List<string>();
        if (!sameHandle)
            restarts.Add($"{stepId}: command handle changed across the outage");
        if (run?.RestartedWhenShouldHaveResumed == true)
            restarts.Add($"{stepId}: workflow flagged RestartedWhenShouldHaveResumed");

        return new Verdict(ok ? "PASS" : "FAIL", string.Join("; ", checks))
        {
            Restarts = restarts,
        };
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>A plan with one long agent.run step, so a kill always lands mid-step.</summary>
    private static Plan SingleAgentPlan(string stepId, int durationSeconds, int attempts)
    {
        var plan = new Plan { Entry = stepId, Revision = 1 };
        plan.Steps.Add(new Step
        {
            Id = stepId,
            Kind = "agent.run",
            SimulatedDuration = durationSeconds.ToString(),
            PollsInActivity = true,
            Limits = new StepLimits { Attempts = attempts, StartToClose = "PT4H", Heartbeat = "PT30S" },
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.Completed] = RouteTarget.ToEnd(Outcomes.Completed),
                [Outcomes.Failed] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.AttemptsExhausted] = RouteTarget.ToEnd(Outcomes.AttemptsExhausted),
            },
        });
        return plan;
    }

    /// <summary>Waits until the sandbox has a command for the step, with progress.</summary>
    private static async Task<CommandView?> WaitForCommandAsync(
        SandboxClient sandbox, string stepId, int minTurns, Action<string> say)
    {
        for (var i = 0; i < 240; i++)
        {
            var state = await sandbox.StateAsync();
            var cmd = state.Commands.FirstOrDefault(c => c.SandboxKey == $"sbx-{stepId}");
            if (cmd is not null && cmd.SessionTurns >= minTurns)
                return cmd;
            await Task.Delay(TimeSpan.FromMilliseconds(500));
        }
        say($"timed out waiting for a command for {stepId} with >= {minTurns} turns");
        return null;
    }

    /// <summary>
    /// Waits until the workflow records a new resume, a new attempt, or a
    /// terminal outcome for the step. This is "the step is making progress
    /// again" after a kill, and it is what measurement 5 times.
    /// </summary>
    private static async Task<bool> WaitForResumeAsync(
        WorkflowHandle<InterpreterWorkflow> handle, string stepId, int resumesBefore,
        int attemptsBefore, TimeSpan timeout, Action<string> say)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var state = await handle.QueryAsync(w => w.State());
                if (state.StepRuns.TryGetValue(stepId, out var run) &&
                    (run.Resumes > resumesBefore || run.Attempts > attemptsBefore ||
                     run.Outcome.Length > 0))
                {
                    return true;
                }
            }
            catch (Exception)
            {
                // The workflow may be mid-continue-as-new or briefly unreachable.
            }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
        say("timed out waiting for a resume after the kill");
        return false;
    }

    /// <summary>
    /// Stops any worker already polling the queue, so the only worker is the one
    /// this harness starts and kills.
    /// </summary>
    private static async Task<int> StopStrayWorkersAsync(string queue, Action<string> say)
    {
        var script = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "..", "find-worker.ps1"));
        if (!File.Exists(script))
        {
            say($"note: find-worker.ps1 not found at {script}; stray workers not stopped");
            return 0;
        }

        var psi = new ProcessStartInfo("powershell")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);
        psi.ArgumentList.Add("-Queue");
        psi.ArgumentList.Add(queue);

        using var process = Process.Start(psi);
        if (process is null)
            return 0;
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

        var stopped = 0;
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(line.Trim(), out var pid))
                continue;
            try
            {
                Process.GetProcessById(pid).Kill(entireProcessTree: true);
                stopped++;
                say($"stopped stray worker pid={pid}");
            }
            catch (Exception ex)
            {
                say($"stray worker pid={pid}: {ex.GetType().Name}");
            }
        }
        if (stopped > 0)
            await Task.Delay(TimeSpan.FromSeconds(2));
        return stopped;
    }
}

public sealed record Verdict(string Status, string Summary)
{
    public string Name { get; init; } = "";

    public List<string> Restarts { get; init; } = new();

    public List<CrashCost> CrashCosts { get; init; } = new();
}

public sealed record CrashCost(string Label, double Seconds);
