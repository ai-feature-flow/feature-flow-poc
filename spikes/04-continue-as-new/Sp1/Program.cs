using System.Text.Json;
using Sp1;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Worker;

// SP1 harness: runs one parent under one mode and seed, answers its decisions
// while it hands over, then checks the pre-registered checks of
// docs/spikes/sp1-results.md section 3 against the parent's final state, a
// direct read of every child, and every run's history.

const string Address = "localhost:7633";
const string Queue = "sp1";

var mode = Arg("--mode") ?? Modes.Main;
var seed = int.Parse(Arg("--seed") ?? "1");
var timeout = TimeSpan.FromMinutes(int.Parse(Arg("--timeout-min") ?? (mode == Modes.Main ? "10" : "4")));
if (!Modes.All.Contains(mode))
    throw new ArgumentException($"unknown mode {mode}");

var client = await TemporalClient.ConnectAsync(new(Address));
using var worker = new TemporalWorker(client, new TemporalWorkerOptions(Queue)
    .AddWorkflow<ParentWorkflow>()
    .AddWorkflow<ChildWorkflow>()
    .AddAllActivities(new Activities(client)));
using var stopWorker = new CancellationTokenSource();
var workerTask = worker.ExecuteAsync(stopWorker.Token);

var id = $"sp1-{mode}-s{seed}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
await client.StartWorkflowAsync((ParentWorkflow w) => w.RunAsync(new ParentState { Mode = mode, Seed = seed }),
    new(id, Queue));
// Address by workflow id only, as the API does, so each call reaches the current run.
var parent = client.GetWorkflowHandle<ParentWorkflow>(id);
Console.WriteLine($"SP1 {id}: started, timeout {timeout.TotalMinutes} min");

var started = DateTime.UtcNow;
var random = new Random(seed * 7919);
var plans = Enumerable.Range(0, 20).Select(i => new AnswerPlan(
    $"d_{i:00}",
    random.Next(2) == 0 ? "approved" : "rejected",
    TimedToDrain: i % 2 == 0,
    DelayMillis: random.Next(0, 40_000))).ToList();
var answerResults = await Task.WhenAll(plans.Select(SendAnswerAsync));

string result;
try
{
    // A long poll ignores the RPC timeout, so the deadline is a cancellation token.
    var remaining = timeout - (DateTime.UtcNow - started);
    using var deadline = new CancellationTokenSource(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromSeconds(1));
    result = await parent.GetResultAsync<string>(followRuns: true,
        rpcOptions: new RpcOptions { CancellationToken = deadline.Token });
}
catch (Exception ex)
{
    result = $"<incomplete: {ex.GetType().Name}>";
}
var elapsed = DateTime.UtcNow - started;

var view = await parent.QueryAsync(w => w.View());
var s = view.State;

// Each child as Temporal reports it when read directly, independent of the parent.
var childReads = new Dictionary<string, string>();
foreach (var child in s.Children)
{
    var handle = client.GetWorkflowHandle(child.Id);
    try
    {
        var description = await handle.DescribeAsync();
        string? childResult = null;
        if (description.Status == WorkflowExecutionStatus.Completed)
            childResult = await handle.GetResultAsync<string>();
        childReads[child.Id] = $"{description.Status}:{childResult}";
    }
    catch (Exception ex)
    {
        childReads[child.Id] = $"<unreadable: {ex.GetType().Name}>";
    }
}

// Every run's history: workflow task failures, and activities scheduled versus completed.
var runs = new List<RunHistory>();
var expectedRuns = s.HandOvers.Count + 1;
for (var tries = 0; tries < 20 && runs.Count < expectedRuns; tries++)
{
    runs.Clear();
    await foreach (var execution in client.ListWorkflowsAsync($"WorkflowId = '{id}'"))
    {
        var history = await client.GetWorkflowHandle(id, execution.RunId).FetchHistoryAsync();
        int Count(EventType type) => history.Events.Count(e => e.EventType == type);
        runs.Add(new RunHistory(execution.RunId, history.Events.Count,
            Count(EventType.WorkflowTaskFailed),
            Count(EventType.ActivityTaskScheduled),
            Count(EventType.ActivityTaskCompleted),
            history.Events.Last().EventType.ToString()));
    }
    if (runs.Count < expectedRuns)
        await Task.Delay(500);
}

stopWorker.Cancel();
try { await workerTask; } catch (OperationCanceledException) { }

// ---- Checks ----

var handOversWithBoth = s.HandOvers.Count(h => h.RunningChildren > 0 && h.OpenDecisions > 0);
var staleThenAccepted = answerResults.Count(a => a.Accepted && a.Attempts.Any(x => x.Contains("stale")));

var childMismatches = s.Children
    .Where(c => !c.Ended || c.EndRecords != 1 || c.RecordedOutcome != childReads[c.Id])
    .Select(c => $"{c.Id}: ended={c.Ended} records={c.EndRecords} recorded={c.RecordedOutcome} direct={childReads[c.Id]}")
    .ToList();

var answerMismatches = new List<string>();
foreach (var plan in plans)
{
    var decision = s.Decisions.First(d => d.Id == plan.DecisionId);
    var records = s.AnswerLog.Count(r => r.DecisionId == plan.DecisionId);
    if (decision.Answer != plan.Outcome || records != 1)
        answerMismatches.Add($"{plan.DecisionId}: sent={plan.Outcome} recorded={decision.Answer} records={records}");
}
foreach (var a in answerResults.Where(a => a.ExtraKind == "conflict" && a.ExtraResult != "rejected:conflict"))
    answerMismatches.Add($"{a.DecisionId}: conflicting answer not rejected ({a.ExtraResult})");
foreach (var a in answerResults.Where(a => a.ExtraKind == "duplicate" && a.ExtraResult != "ok:duplicate"))
    answerMismatches.Add($"{a.DecisionId}: duplicate not acknowledged ({a.ExtraResult})");

var leftInFlight = runs.Where(r => r.LastEvent == nameof(EventType.WorkflowExecutionContinuedAsNew)
                                   && r.ActivitiesScheduled != r.ActivitiesCompleted).ToList();

var checks = new Dictionary<string, bool>
{
    ["E1 hand-overs with children running and decisions open >= 3"] = handOversWithBoth >= 3,
    ["C1 every child end recorded once, matching Temporal"] = childMismatches.Count == 0,
    ["C2 every answer recorded once as sent; conflicts rejected; duplicates acknowledged"] = answerMismatches.Count == 0,
    ["C3 no start while draining; nothing in flight at a hand-over"] =
        s.StartsWhileDraining == 0 && s.HandOvers.All(h => h.InFlight == 0) && leftInFlight.Count == 0,
    ["C4 completed within the timeout"] = result == "completed" && elapsed <= timeout,
    ["C5 no workflow task failures, unanswered decisions or failed child signals"] =
        runs.Sum(r => r.WorkflowTaskFailed) == 0 &&
        answerResults.All(a => a.Accepted) &&
        childReads.Values.All(v => !v.Contains("signal_failed")),
};

var summary = new
{
    id, mode, seed, result,
    elapsedSeconds = Math.Round(elapsed.TotalSeconds),
    handOvers = s.HandOvers.Count,
    handOversWithBoth,
    runsInHistory = runs.Count,
    signalsReceived = s.SignalsReceived,
    signalsWhileDraining = s.SignalsWhileDraining,
    staleThenAccepted,
    answersTimedToDrainThatSawDraining = answerResults.Count(a => a.SawDraining),
    activitiesStarted = s.ActivitiesStarted,
    activitiesCompleted = s.ActivitiesCompleted,
    workflowTaskFailed = runs.Sum(r => r.WorkflowTaskFailed),
    checks,
    childMismatches,
    answerMismatches,
    leftInFlight,
    distinctAnswerErrors = answerResults.SelectMany(a => a.Attempts).Where(x => !x.StartsWith("ok"))
        .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count()),
    answers = answerResults,
    handOverLog = s.HandOvers,
    runs,
};

var resultsDir = Path.Combine(AppContext.BaseDirectory, "../../../../results");
Directory.CreateDirectory(resultsDir);
var path = Path.GetFullPath(Path.Combine(resultsDir, $"{mode}-s{seed}.json"));
await File.WriteAllTextAsync(path, JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"SP1 {id}: result={result} elapsed={summary.elapsedSeconds}s handOvers={s.HandOvers.Count} " +
                  $"withBoth={handOversWithBoth} signalsWhileDraining={s.SignalsWhileDraining} " +
                  $"staleThenAccepted={staleThenAccepted} wtFailed={summary.workflowTaskFailed}");
foreach (var (name, ok) in checks)
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
foreach (var line in childMismatches.Concat(answerMismatches).Take(10))
    Console.WriteLine($"    {line}");
foreach (var (error, count) in summary.distinctAnswerErrors)
    Console.WriteLine($"    answer error x{count}: {error}");
Console.WriteLine($"  written {path}");
return checks.Values.All(v => v) ? 0 : 1;

// ---- Helpers ----

async Task<AnswerResult> SendAnswerAsync(AnswerPlan plan)
{
    await Task.Delay(plan.DelayMillis);
    var sawDraining = false;
    if (plan.TimedToDrain)
    {
        // Wait for a hand-over to begin, so the answer lands inside the window.
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            try
            {
                if ((await parent.QueryAsync(w => w.View())).Draining) { sawDraining = true; break; }
            }
            catch (Exception) { }
            await Task.Delay(50);
        }
    }

    var attempts = new List<string>();
    var accepted = await TryAnswerAsync(plan.DecisionId, plan.Outcome, attempts);

    // Five duplicates (an API retry after a lost response) and two conflicting answers.
    var index = int.Parse(plan.DecisionId[2..]);
    string? extraKind = null, extraResult = null;
    if (accepted && index < 5)
    {
        extraKind = "duplicate";
        var extra = new List<string>();
        await TryAnswerAsync(plan.DecisionId, plan.Outcome, extra);
        extraResult = extra.Last();
    }
    else if (accepted && index is 5 or 6)
    {
        extraKind = "conflict";
        var extra = new List<string>();
        await TryAnswerAsync(plan.DecisionId, plan.Outcome == "approved" ? "rejected" : "approved", extra);
        extraResult = extra.Last().Contains("conflict") ? "rejected:conflict" : extra.Last();
    }
    return new AnswerResult(plan.DecisionId, accepted, sawDraining, attempts, extraKind, extraResult);
}

// The API's retry: up to 20 attempts, 100 ms apart. A conflict is final.
async Task<bool> TryAnswerAsync(string decisionId, string outcome, List<string> attempts)
{
    for (var i = 0; i < 20; i++)
    {
        try
        {
            var reply = await parent.ExecuteUpdateAsync(w => w.AnswerAsync(new Answer(decisionId, outcome)));
            attempts.Add($"ok:{reply}");
            return true;
        }
        catch (Exception ex)
        {
            var message = $"{ex.GetType().Name}: {ex.Message}" +
                          (ex.InnerException is null ? "" : $" / {ex.InnerException.Message}");
            attempts.Add(message);
            if (message.Contains("conflict"))
                return false;
        }
        await Task.Delay(100);
    }
    return false;
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

record AnswerPlan(string DecisionId, string Outcome, bool TimedToDrain, int DelayMillis);

record AnswerResult(string DecisionId, bool Accepted, bool SawDraining, List<string> Attempts,
    string? ExtraKind, string? ExtraResult);

record RunHistory(string RunId, int Events, int WorkflowTaskFailed, int ActivitiesScheduled,
    int ActivitiesCompleted, string LastEvent);
