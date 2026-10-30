using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;
using Temporalio.Worker;

namespace FeatureFlow.Spike3;

/// <summary>
/// Method sanity checks with no agents (docs/17 5, 5.1, 7.3 step 2): the lock
/// obvious and bypassed, I3, I4, and the Spike 1 replay fixtures.
/// </summary>
public static class Sanity
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static async Task<int> ReplaySpike1Async()
    {
        var dir = Path.GetFullPath(Path.Combine(Cli.SpikeDir, "..", "01-interpreter", "tests", "histories"));
        var replayer = new WorkflowReplayer(new WorkflowReplayerOptions().AddWorkflow<InterpreterWorkflow>());
        var failures = 0;
        var files = Directory.GetFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal).ToList();
        foreach (var file in files)
        {
            var name = Path.GetFileNameWithoutExtension(file);
            try
            {
                var history = Temporalio.Common.WorkflowHistory.FromJson(name, await File.ReadAllTextAsync(file));
                await replayer.ReplayWorkflowAsync(history);
                Console.WriteLine($"REPLAY {name}: PASS ({history.Events.Count} events)");
            }
            catch (Exception ex)
            {
                failures++;
                Console.WriteLine($"REPLAY {name}: FAIL {ex.GetType().Name}: {ex.Message}");
            }
        }
        Console.WriteLine($"REPLAY {files.Count - failures}/{files.Count} Spike 1 fixtures replay cleanly");
        return failures == 0 ? 0 : 1;
    }

    private sealed record Interval(string WorkflowId, string Step, string Kind, DateTimeOffset Start, DateTimeOffset End);

    private static List<Interval> ReadIntervals(string path) => File.Exists(path)
        ? File.ReadAllLines(path).Where(l => l.Length > 0).Select(l =>
        {
            var e = JsonDocument.Parse(l).RootElement;
            return new Interval(e.GetProperty("workflowId").GetString()!, e.GetProperty("step").GetString()!,
                e.GetProperty("kind").GetString()!, e.GetProperty("start").GetDateTimeOffset(),
                e.GetProperty("end").GetDateTimeOffset());
        }).ToList()
        : new List<Interval>();

    private static async Task<(TemporalWorker Worker, Task Running, CancellationTokenSource Cts, string Queue, string Intervals)>
        StartFakeWorkerAsync(ITemporalClient client, string name, int holderCheckSeconds = 30)
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var queue = $"spike-3-{name}-{stamp}";
        var dir = Path.Combine(Cli.WorkDir, "sanity");
        Directory.CreateDirectory(dir);
        var intervals = Path.Combine(dir, $"{name}-{stamp}.intervals.jsonl");
        var fake = new FakeActivities(intervals);
        var locks = new LockActivities(client, holderCheckSeconds);
        var worker = new TemporalWorker(client, new TemporalWorkerOptions(queue) { MaxConcurrentActivities = 20 }
            .AddWorkflow<InterpreterWorkflow>()
            .AddWorkflow<LockWorkflow>()
            .AddActivity(fake.RunAgentAsync)
            .AddActivity(fake.RunGenericStepAsync)
            .AddActivity(fake.ProposePatchAsync)
            .AddActivity(fake.WaitForDecisionAsync)
            .AddActivity(fake.RecordPatchResultAsync)
            .AddActivity(fake.ReadHistorySizeAsync)
            .AddActivity(locks.RequestLockAsync)
            .AddActivity(locks.IsWorkflowOpenAsync));
        var cts = new CancellationTokenSource();
        var running = worker.ExecuteAsync(cts.Token);
        await Task.Delay(500);
        return (worker, running, cts, queue, intervals);
    }

    private static async Task StopAsync(TemporalWorker worker, Task running, CancellationTokenSource cts)
    {
        cts.Cancel();
        try { await running; } catch (OperationCanceledException) { }
        worker.Dispose();
    }

    private static Plan OneStep(string kind, int seconds) => new()
    {
        Entry = "work",
        Steps =
        {
            new Step
            {
                Id = "work", Kind = kind, SimulatedDuration = seconds.ToString(),
                Routes = new()
                {
                    ["completed"] = RouteTarget.ToEnd("completed"),
                    ["failed"] = RouteTarget.ToEnd("failed"),
                },
            },
        },
    };

    // ------------------------------------------------------------ lock sanity

    public static async Task<int> LockAsync(ITemporalClient client)
    {
        var (worker, running, cts, queue, intervalsPath) = await StartFakeWorkerAsync(client, "sanity-lock");
        var stamp = queue.Split('-').Last();
        var report = new Dictionary<string, object?>();
        var ok = true;

        foreach (var bypass in new[] { false, true })
        {
            var label = bypass ? "bypassed" : "locked";
            var lockId = $"lock:sanity:{stamp}";
            Cli.Log($"LOCK sanity {label}: three holders, 20 s each, started together");
            var handles = new List<WorkflowHandle<InterpreterWorkflow, string>>();
            var starts = new Dictionary<string, DateTimeOffset>();
            var tasks = Enumerable.Range(1, 3).Select(async i =>
            {
                var id = $"sanity-{stamp}-{label}-{i}";
                var h = await client.StartWorkflowAsync(
                    (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput
                    {
                        Plan = OneStep("scm.merge_base_forward", 20),
                        MergeLock = bypass ? null : lockId,
                        MergeLockQueue = queue,
                    }),
                    new WorkflowOptions(id, queue));
                lock (starts) starts[id] = DateTimeOffset.UtcNow;
                return h;
            }).ToList();
            handles.AddRange(await Task.WhenAll(tasks));
            foreach (var h in handles)
                await h.GetResultAsync();

            var mine = ReadIntervals(intervalsPath).Where(x => x.WorkflowId.Contains($"-{label}-")).OrderBy(x => x.Start).ToList();
            var t0 = starts.Values.Min();
            var overlaps = new List<object>();
            for (var a = 0; a < mine.Count; a++)
                for (var b = a + 1; b < mine.Count; b++)
                {
                    var o = (Min(mine[a].End, mine[b].End) - Max(mine[a].Start, mine[b].Start)).TotalSeconds;
                    if (o > 0)
                        overlaps.Add(new { a = mine[a].WorkflowId, b = mine[b].WorkflowId, seconds = Math.Round(o, 1) });
                }
            var rows = mine.Select(x => new
            {
                holder = x.WorkflowId,
                waitedSeconds = Math.Round((x.Start - starts[x.WorkflowId]).TotalSeconds, 1),
                startS = Math.Round((x.Start - t0).TotalSeconds, 1),
                endS = Math.Round((x.End - t0).TotalSeconds, 1),
            }).ToList();
            foreach (var r in rows)
                Cli.Log($"  {r.holder}: waited {r.waitedSeconds}s, held [{r.startS}, {r.endS}]");
            Cli.Log($"  overlapping pairs: {overlaps.Count}");

            object? audit = null;
            if (!bypass)
            {
                var state = await client.GetWorkflowHandle<LockWorkflow>(lockId).QueryAsync(l => l.State());
                audit = state.Audit;
                foreach (var e in state.Audit)
                    Cli.Log($"  audit {e.At:HH:mm:ss.f} {e.Kind,-16} {e.HolderId} {e.Detail}");
                await client.GetWorkflowHandle<LockWorkflow>(lockId).SignalAsync(l => l.StopAsync());
            }

            var expected = bypass ? overlaps.Count > 0 : overlaps.Count == 0;
            ok &= expected && mine.Count == 3;
            report[label] = new { holders = rows, overlaps, audit, expectationMet = expected };
        }

        await StopAsync(worker, running, cts);
        Save("sanity-lock.json", report);
        Cli.Log($"LOCK sanity: {(ok ? "PASS" : "FAIL")} (locked: no overlap; bypassed: overlap)");
        return ok ? 0 : 1;
    }

    // ------------------------------------------------------------ I4

    public static async Task<int> I4Async(ITemporalClient client)
    {
        var (worker, running, cts, queue, _) = await StartFakeWorkerAsync(client, "i4", holderCheckSeconds: 30);
        var stamp = queue.Split('-').Last();
        var lockId = $"lock:i4:{stamp}";
        var lockHandle = client.GetWorkflowHandle<LockWorkflow>(lockId);

        var holder = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput
            {
                Plan = OneStep("scm.merge_base_forward", 300), MergeLock = lockId, MergeLockQueue = queue,
            }),
            new WorkflowOptions($"i4-{stamp}-holder", queue));
        await WaitAuditAsync(lockHandle, e => e.Kind == "granted" && e.HolderId == holder.Id);

        var waiter = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput
            {
                Plan = OneStep("scm.merge_base_forward", 2), MergeLock = lockId, MergeLockQueue = queue,
            }),
            new WorkflowOptions($"i4-{stamp}-waiter", queue));
        await WaitAuditAsync(lockHandle, e => e.Kind == "requested" && e.HolderId == waiter.Id);

        var terminatedAt = DateTimeOffset.UtcNow;
        await holder.TerminateAsync("I4: terminate a lock holder while another waits");
        Cli.Log($"I4 terminated {holder.Id} at {terminatedAt:HH:mm:ss.f}");
        await waiter.GetResultAsync();

        var state = await lockHandle.QueryAsync(l => l.State());
        foreach (var e in state.Audit)
            Cli.Log($"  audit {e.At:HH:mm:ss.f} {e.Kind,-16} {e.HolderId} {e.Detail}");
        var released = state.Audit.FirstOrDefault(e => e.Kind == "released" && e.HolderId == holder.Id);
        var granted = state.Audit.FirstOrDefault(e => e.Kind == "granted" && e.HolderId == waiter.Id);
        var delay = granted is null ? (double?)null : Math.Round((granted.At - terminatedAt).TotalSeconds, 1);
        var ok = released?.Detail?.Contains("holder closed") == true && delay is not null && delay <= 31;
        Cli.Log($"I4: released by liveness check={released?.Detail}; waiter granted {delay}s after terminate: {(ok ? "PASS" : "FAIL")}");
        await lockHandle.SignalAsync(l => l.StopAsync());
        await StopAsync(worker, running, cts);
        Save("i4.json", new { terminatedAt, audit = state.Audit, grantDelaySeconds = delay, pass = ok });
        return ok ? 0 : 1;
    }

    private static async Task WaitAuditAsync(WorkflowHandle<LockWorkflow> handle, Func<LockAuditEntry, bool> match)
    {
        for (var i = 0; i < 300; i++)
        {
            try
            {
                var s = await handle.QueryAsync(l => l.State());
                if (s.Audit.Any(match))
                    return;
            }
            catch (Exception)
            {
            }
            await Task.Delay(200);
        }
        throw new TimeoutException("lock audit entry not seen");
    }

    // ------------------------------------------------------------ I3

    public static async Task<int> I3Async(ITemporalClient client)
    {
        var (worker, running, cts, queue, _) = await StartFakeWorkerAsync(client, "i3");
        var stamp = queue.Split('-').Last();
        var parent = new Plan
        {
            Entry = "tickets",
            Steps =
            {
                new Step
                {
                    Id = "tickets", Kind = "parallel",
                    Inputs = new() { ["branches"] = new List<object?> { "c1", "c2", "c3" }, ["join"] = "all", ["max_concurrency"] = "2" },
                    Routes = new() { ["completed"] = RouteTarget.ToEnd("completed"), ["any_failed"] = RouteTarget.ToEnd("failed") },
                },
                new Step { Id = "c1", Kind = "run.child", Inputs = new() { ["plan_ref"] = "i3:ok20" } },
                new Step { Id = "c2", Kind = "run.child", Inputs = new() { ["plan_ref"] = "i3:fail2" } },
                new Step { Id = "c3", Kind = "run.child", Inputs = new() { ["plan_ref"] = "i3:ok10" } },
            },
        };
        var id = $"i3-{stamp}";
        var input = new InterpreterInput
        {
            Plan = parent,
            ChildPlans = new()
            {
                ["i3:ok20"] = OneStep("noop", 20),
                ["i3:fail2"] = OneStep("noop.fail", 2),
                ["i3:ok10"] = OneStep("noop", 10),
            },
        };
        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(input), new WorkflowOptions(id, queue));
        var result = await handle.GetResultAsync();
        var parentDesc = await handle.DescribeAsync();
        var outcome = (await handle.QueryAsync(w => w.State())).StepOutcomes["tickets"].Outcome;

        var rows = new List<(string Id, DateTime Start, DateTime? Close, string Status)>();
        foreach (var c in new[] { "c1", "c2", "c3" })
        {
            var d = await client.GetWorkflowHandle($"{id}/{c}").DescribeAsync();
            rows.Add((c, d.StartTime, d.CloseTime, d.Status.ToString()));
        }
        var t0 = parentDesc.StartTime;
        foreach (var r in rows)
            Cli.Log($"  {r.Id}: start {(r.Start - t0).TotalSeconds:F1}s close {(r.Close!.Value - t0).TotalSeconds:F1}s {r.Status}");
        Cli.Log($"  parent: join={outcome} result={result} close {(parentDesc.CloseTime!.Value - t0).TotalSeconds:F1}s");

        var c2 = rows.Single(r => r.Id == "c2");
        var c3 = rows.Single(r => r.Id == "c3");
        var lastChildClose = rows.Max(r => r.Close!.Value);
        var ok = rows.Single(r => r.Id == "c1").Status == "Completed" && c3.Status == "Completed" &&
                 c2.Status == "Completed" && // a failed plan still closes its workflow normally, result "failed"
                 outcome == Outcomes.AnyFailed && result == "failed" &&
                 parentDesc.CloseTime >= lastChildClose &&
                 (c3.Start - c2.Close!.Value).TotalSeconds < 3;
        Cli.Log($"I3: siblings completed, any_failed after all three terminal, c3 started {(c3.Start - c2.Close!.Value).TotalSeconds:F1}s after c2 closed: {(ok ? "PASS" : "FAIL")}");
        await StopAsync(worker, running, cts);
        Save("i3.json", new
        {
            parent = id, join = outcome, result,
            children = rows.Select(r => new { r.Id, startS = (r.Start - t0).TotalSeconds, closeS = (r.Close!.Value - t0).TotalSeconds, r.Status }),
            parentCloseS = (parentDesc.CloseTime!.Value - t0).TotalSeconds, pass = ok,
        });
        return ok ? 0 : 1;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    public static void Save(string name, object value)
    {
        Directory.CreateDirectory(Cli.ResultsDir);
        File.WriteAllText(Path.Combine(Cli.ResultsDir, name), JsonSerializer.Serialize(value, Indented));
    }
}
