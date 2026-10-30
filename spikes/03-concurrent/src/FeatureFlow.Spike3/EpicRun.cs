using System.Diagnostics;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.RealWorker;
using Temporalio.Client;

namespace FeatureFlow.Spike3;

/// <summary>One worker process the harness can kill (process tree) and restart.</summary>
public sealed class WorkerProc
{
    private readonly string name;
    private readonly string[] args;
    private readonly string log;
    private Process? process;

    public WorkerProc(string name, string[] args, string log)
    {
        this.name = name;
        this.args = args;
        this.log = log;
    }

    public int Pid => process?.Id ?? 0;

    public void Start()
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add(typeof(WorkerProc).Assembly.Location);
        psi.ArgumentList.Add("worker");
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        psi.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        psi.Environment["FF_AGENT_ALLOW"] = Texts.AgentAllow;
        psi.Environment.Remove("ANTHROPIC_API_KEY");
        process = Process.Start(psi)!;
        var writer = new StreamWriter(log, append: true) { AutoFlush = true };
        writer.WriteLine($"==== {DateTimeOffset.UtcNow:O} start pid={process.Id}");
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (writer) writer.WriteLine("ERR " + e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        Cli.Log($"WORKER {name} started pid={process.Id}");
    }

    public void Kill(string why)
    {
        if (process is null || process.HasExited)
            return;
        var pid = process.Id;
        process.Kill(entireProcessTree: true);
        process.WaitForExit(10000);
        File.AppendAllText(log, $"==== {DateTimeOffset.UtcNow:O} killed pid={pid}: {why}{Environment.NewLine}");
        Cli.Log($"WORKER {name} killed pid={pid}: {why}");
    }
}

public static class EpicRun
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private sealed record Ticket(string Key, string Id, string Step);

    private static readonly Ticket[] Tickets =
    {
        new("a", "EPIC3-A", "ticket_a"),
        new("b", "EPIC3-B", "ticket_b"),
        new("c", "EPIC3-C", "ticket_c"),
    };

    public static async Task<int> RunAsync(ITemporalClient client, string[] args)
    {
        var suffix = Cli.Arg(args, "--suffix") ?? "spike-3";
        var inject = !Cli.Flag(args, "--no-inject");
        var integration = $"ff/epic/{suffix}";
        var epicId = suffix;
        var lockId = $"lock:{Cli.Owner}/{Cli.Repo}:{integration}";
        var dir = Path.Combine(Cli.WorkDir, suffix);
        var records = Path.Combine(dir, "records");
        var logs = Path.Combine(dir, "logs");
        Directory.CreateDirectory(records);
        Directory.CreateDirectory(logs);
        var events = new List<object>();
        void Event(string kind, object detail)
        {
            lock (events) events.Add(new { at = DateTimeOffset.UtcNow, kind, detail });
            Cli.Log($"EVENT {kind} {JsonSerializer.Serialize(detail)}");
        }

        // 1. The integration branch, from fixture main.
        var ops = new GitHubOps(Path.Combine(dir, "harness"), $"{suffix}-harness");
        var created = await ops.CodeHost.CreateBranchAsync(ops.Repo, integration, "main", $"{suffix}-integration", CancellationToken.None);
        if (!created.IsOk)
        {
            Cli.Log($"cannot create {integration}: {created.Error!.Class}/{created.Error.Reason}; use a new --suffix");
            return 2;
        }
        Event("integration-branch", new { integration, sha = created.Value!.HeadSha });

        // 2. One clone per ticket and one for the epic (docs/17 7.2).
        var epicQueue = $"spike-3-epic-{suffix}";
        var workers = new Dictionary<string, WorkerProc>();
        var epicClone = GitHubOps.Clone(Path.Combine(dir, "epic"));
        workers["epic"] = new WorkerProc("epic", new[]
        {
            "--kind", "epic", "--queue", epicQueue, "--repo", epicClone, "--branch", integration, "--base", "main",
            "--ticket", "EPIC3", "--run-id", $"{suffix}-epic", "--records", records,
        }, Path.Combine(logs, "worker-epic.log"));
        var childQueues = new Dictionary<string, string>();
        foreach (var t in Tickets)
        {
            var clone = GitHubOps.Clone(Path.Combine(dir, $"ticket-{t.Key}"));
            Cli.Git(clone, "checkout", "--quiet", "--detach", $"origin/{integration}");
            var queue = $"spike-3-ticket-{t.Key}-{suffix}";
            childQueues[t.Step] = queue;
            workers[t.Key] = new WorkerProc(t.Key, new[]
            {
                "--kind", "ticket", "--queue", queue, "--repo", clone, "--branch", $"ff/ticket/{suffix}-{t.Key}",
                "--base", integration, "--ticket", t.Id, "--run-id", $"{suffix}-{t.Key}", "--records", records,
            }, Path.Combine(logs, $"worker-{t.Key}.log"));
        }
        foreach (var w in workers.Values)
            w.Start();
        await Task.Delay(TimeSpan.FromSeconds(8));

        // 3. The epic run.
        var organisation = OrganisationConfiguration.Load(Path.Combine(Cli.PlansDir, "organisation.yaml"));
        var ticketPlan = PlanYaml.Load(Path.Combine(Cli.PlansDir, "ticket-delivery.yaml"));
        var epicPlan = PlanYaml.Load(Path.Combine(Cli.PlansDir, "epic.yaml"),
            new Dictionary<string, string> { ["epic_id"] = suffix });
        var epicInput = new InterpreterInput
        {
            Plan = epicPlan,
            ChildPlans = new() { ["spike-3:templates/ticket-delivery"] = ticketPlan },
            ChildQueues = childQueues,
            ChildMergeLock = lockId,
            MergeLockQueue = epicQueue,
            AgentRunCeiling = organisation.AttemptCeilings.GetValueOrDefault("agent.run", 5),
            PerRunCeiling = organisation.PerRunAttemptCeiling ?? 40,
        };
        var epic = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(epicInput), new WorkflowOptions(epicId, epicQueue));
        Event("epic-started", new { epicId, lockId, childQueues });

        // 4. Gates, injections, progress.
        var arrivals = new Dictionary<string, int>();
        var seenArrival = new HashSet<string>();
        var heldMerge = new Dictionary<string, DateTimeOffset>();
        var lastCursors = new Dictionary<string, string>();
        DateTimeOffset? i2KilledAt = null, i1KilledAt = null;
        string? i2Ticket = null, i1Other = null;
        var i2Restarted = false;
        var i1Restarted = false;
        var i2Query = false;
        var started = DateTimeOffset.UtcNow;
        var deadline = started + TimeSpan.FromHours(4);
        var epicClosed = false;

        WorkflowHandle<InterpreterWorkflow> Child(Ticket t) => client.GetWorkflowHandle<InterpreterWorkflow>($"{epicId}/{t.Step}");

        async Task<InterpreterState?> TryState(WorkflowHandle<InterpreterWorkflow> h)
        {
            try { return await h.QueryAsync(w => w.State(), new() { Rpc = new() { Timeout = TimeSpan.FromSeconds(5) } }); }
            catch (Exception) { return null; }
        }

        async Task<bool> IsClosed(string id)
        {
            try { return (await client.GetWorkflowHandle(id).DescribeAsync()).Status != Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Running; }
            catch (Exception) { return false; }
        }

        async Task Decide(WorkflowHandle<InterpreterWorkflow> h, string ledgerRun, Step gate, string decision)
        {
            var ledger = new DecisionLedger(records, ledgerRun);
            ledger.Record(new DecisionRecord(gate.Id, gate.Gate!.DecisionType, decision,
                new DecisionActor(DecisionActorKind.Human, "stuart-parkinson", new[] { "approver" }), DateTimeOffset.UtcNow));
            await h.ExecuteUpdateAsync(w => w.SubmitDecisionAsync(new StepDecision(gate.Id, decision)));
            Event("decision", new { workflow = h.Id, gate = gate.Id, type = gate.Gate.DecisionType, decision });
        }

        while (!epicClosed && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(2000);
            epicClosed = await IsClosed(epicId);

            // Progress log: cursor changes per workflow.
            var states = new Dictionary<string, InterpreterState?>();
            foreach (var t in Tickets)
                states[t.Key] = await TryState(Child(t));
            var epicState = await TryState(epic);
            foreach (var (k, s) in states.Append(new("epic", epicState)))
            {
                if (s is null) continue;
                var cur = string.Join(",", s.Cursors) + (s.TerminalStatus is null ? "" : $" [{s.TerminalStatus}]");
                if (!lastCursors.TryGetValue(k, out var prev) || prev != cur)
                {
                    lastCursors[k] = cur;
                    Cli.Log($"PROGRESS {k}: {cur} (steps {s.StepsExecuted})");
                }
            }

            // Gates.
            var pendingMerge = new List<(Ticket T, Step Gate)>();
            foreach (var t in Tickets)
            {
                var s = states[t.Key];
                if (s is null || s.TerminalStatus is not null) continue;
                foreach (var gate in PlanReady.Ready(s).Where(g => g.Gate is not null))
                {
                    var key = $"{t.Key}:{gate.Id}:{s.StepsExecuted}";
                    if (gate.Id == "gate_merge")
                    {
                        pendingMerge.Add((t, gate));
                        continue;
                    }
                    if (!seenArrival.Add(key)) continue;
                    arrivals[$"{t.Key}:{gate.Id}"] = arrivals.GetValueOrDefault($"{t.Key}:{gate.Id}") + 1;
                    // Spike 3: an escalation is rejected, so the ticket ends failed
                    // (approval would only route to replan, which resumes nowhere).
                    var decision = gate.Gate!.DecisionType == "ticket.escalation" ? Outcomes.Rejected : Outcomes.Approved;
                    await Decide(Child(t), $"{suffix}-{t.Key}", gate, decision);
                }
            }

            // gate_merge: A and C are approved at the same moment (docs/17 3.3).
            foreach (var (t, gate) in pendingMerge)
                heldMerge.TryAdd(t.Key, DateTimeOffset.UtcNow);
            var toApprove = new List<(Ticket T, Step Gate)>();
            foreach (var (t, gate) in pendingMerge)
            {
                var firstRound = (t.Key is "a" or "c") && arrivals.GetValueOrDefault($"{t.Key}:gate_merge") == 0;
                if (!firstRound)
                {
                    toApprove.Add((t, gate));
                    continue;
                }
                var other = t.Key == "a" ? "c" : "a";
                var otherWaiting = pendingMerge.Any(p => p.T.Key == other) && arrivals.GetValueOrDefault($"{other}:gate_merge") == 0;
                var otherClosed = await IsClosed($"{epicId}/ticket_{other}");
                var waitedTooLong = DateTimeOffset.UtcNow - heldMerge[t.Key] > TimeSpan.FromMinutes(25);
                if (otherWaiting || otherClosed || waitedTooLong)
                {
                    toApprove.Add((t, gate));
                    if (!otherWaiting)
                        Event("merge-gate-alone", new { ticket = t.Key, otherClosed, waitedTooLong });
                }
            }
            if (toApprove.Count > 0)
            {
                foreach (var (t, _) in toApprove)
                {
                    arrivals[$"{t.Key}:gate_merge"] = arrivals.GetValueOrDefault($"{t.Key}:gate_merge") + 1;
                    heldMerge.Remove(t.Key);
                }
                if (toApprove.Count > 1)
                    Event("merge-gates-simultaneous", new { tickets = toApprove.Select(x => x.T.Key) });
                await Task.WhenAll(toApprove.Select(x => Decide(Child(x.T), $"{suffix}-{x.T.Key}", x.Gate, Outcomes.Approved)));
            }

            // Epic gates.
            if (epicState is not null && epicState.TerminalStatus is null)
            {
                foreach (var gate in PlanReady.Ready(epicState).Where(g => g.Gate is not null))
                {
                    if (!seenArrival.Add($"epic:{gate.Id}:{epicState.StepsExecuted}")) continue;
                    await Decide(epic, $"{suffix}-epic", gate, Outcomes.Approved);
                }
            }

            if (!inject)
                continue;

            // I2: kill the lock holder's worker just after the grant; down 60 s.
            LockStateView? lockState = null;
            try { lockState = await client.GetWorkflowHandle<LockWorkflow>(lockId).QueryAsync(l => l.State()); }
            catch (Exception) { }
            if (i2KilledAt is null && lockState?.Audit.FirstOrDefault(e => e.Kind == "granted") is { } grant)
            {
                i2Ticket = grant.HolderId.Split('/').Last().Replace("ticket_", "");
                workers[i2Ticket].Kill("I2: lock holder, just after the grant");
                i2KilledAt = DateTimeOffset.UtcNow;
                Event("I2-kill", new { holder = grant.HolderId, grantedAt = grant.At, killedAt = i2KilledAt });
            }
            if (i2KilledAt is not null && !i2Query && DateTimeOffset.UtcNow - i2KilledAt > TimeSpan.FromSeconds(30))
            {
                i2Query = true;
                Event("I2-query-during-outage", new { holder = lockState?.Holder, waiters = lockState?.Waiters });
            }
            if (i2KilledAt is not null && !i2Restarted && DateTimeOffset.UtcNow - i2KilledAt >= TimeSpan.FromSeconds(60))
            {
                i2Restarted = true;
                workers[i2Ticket!].Start();
                Event("I2-restart", new { ticket = i2Ticket });
            }

            // I1: kill the third ticket's worker as its implement starts (after
            // its first heartbeat, so a session exists to resume); keep it down
            // until the other in-flight ticket has merged, at least 5 minutes.
            var b = Tickets.Single(t => t.Key == "b");
            var bState = states["b"];
            if (i1KilledAt is null && bState is not null && bState.Cursors.Contains("implement"))
            {
                if (!heldMerge.ContainsKey("b-implement"))
                    heldMerge["b-implement"] = DateTimeOffset.UtcNow;
                if (DateTimeOffset.UtcNow - heldMerge["b-implement"] >= TimeSpan.FromSeconds(15))
                {
                    foreach (var other in new[] { "a", "c" })
                        if (!await IsClosed($"{epicId}/ticket_{other}"))
                            i1Other = other;
                    workers["b"].Kill("I1: third ticket, as its implement starts");
                    i1KilledAt = DateTimeOffset.UtcNow;
                    Event("I1-kill", new { ticket = "b", otherInFlight = i1Other, killedAt = i1KilledAt });
                }
            }
            if (i1KilledAt is not null && !i1Restarted && DateTimeOffset.UtcNow - i1KilledAt >= TimeSpan.FromMinutes(5))
            {
                var otherDone = i1Other is null || await IsClosed($"{epicId}/ticket_{i1Other}");
                if (otherDone)
                {
                    i1Restarted = true;
                    workers["b"].Start();
                    Event("I1-restart", new { ticket = "b", otherInFlight = i1Other, downSeconds = (DateTimeOffset.UtcNow - i1KilledAt.Value).TotalSeconds });
                }
            }
        }

        string result;
        try { result = await epic.GetResultAsync(); }
        catch (Exception ex) { result = $"threw {ex.GetType().Name}: {ex.Message}"; }
        Event("epic-closed", new { result });

        // 5. Evidence.
        await CollectAsync(client, suffix, epicId, lockId, records, dir);
        var outDir = Path.Combine(Cli.ResultsDir, suffix);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "harness-events.json"), JsonSerializer.Serialize(events, Indented));

        try { await client.GetWorkflowHandle<LockWorkflow>(lockId).SignalAsync(l => l.StopAsync()); } catch (Exception) { }
        await Task.Delay(3000);
        foreach (var w in workers.Values)
            w.Kill("run over");
        Cli.Log($"EPIC {epicId}: {result}");
        return result == Outcomes.Completed ? 0 : 1;
    }

    public static async Task CollectAsync(ITemporalClient client, string suffix, string epicId, string lockId, string records, string dir)
    {
        var outDir = Path.Combine(Cli.ResultsDir, suffix);
        var histories = Path.Combine(dir, "histories");
        Directory.CreateDirectory(outDir);
        Directory.CreateDirectory(histories);
        var temporal = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".temporalio", "bin", "temporal");
        var ids = new[] { epicId, $"{epicId}/ticket_a", $"{epicId}/ticket_b", $"{epicId}/ticket_c", lockId };
        var workflows = new List<object>();
        foreach (var id in ids)
        {
            var (_, json, _) = Cli.Run(temporal, null, "workflow", "show", "--workflow-id", id, "--address", Cli.DefaultAddress, "--output", "json");
            File.WriteAllText(Path.Combine(histories, id.Replace('/', '_').Replace(':', '_') + ".json"), json);
            try
            {
                var d = await client.GetWorkflowHandle(id).DescribeAsync();
                workflows.Add(new { id, status = d.Status.ToString(), start = d.StartTime, close = d.CloseTime, taskQueue = d.TaskQueue });
            }
            catch (Exception ex)
            {
                workflows.Add(new { id, error = ex.Message });
            }
        }
        File.WriteAllText(Path.Combine(outDir, "workflows.json"), JsonSerializer.Serialize(workflows, Indented));
        try
        {
            var state = await client.GetWorkflowHandle<LockWorkflow>(lockId).QueryAsync(l => l.State());
            File.WriteAllText(Path.Combine(outDir, "lock-audit.json"), JsonSerializer.Serialize(state, Indented));
        }
        catch (Exception ex)
        {
            Cli.Log($"lock query failed: {ex.Message}");
        }
        foreach (var file in Directory.GetFiles(records))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".hook-events.jsonl") || new FileInfo(file).Length > 400_000)
                continue;
            File.Copy(file, Path.Combine(outDir, name), overwrite: true);
        }
    }
}
