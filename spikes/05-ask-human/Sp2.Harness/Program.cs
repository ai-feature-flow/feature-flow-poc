using System.Diagnostics;
using System.Text.Json;
using Sp2;
using Temporalio.Client;
using Temporalio.Worker;

// SP2 harness: runs one scenario of docs/spikes/sp2-results.md section 3. It
// hosts the orchestration worker, starts and kills replicas, runs Claude Code as
// the agent, answers as the person, then evaluates the pre-registered checks.
//
// Usage: Sp2.Harness --scenario S1|S2|S3|S4|L1 --run <n> [--variant main|n1|n2] [--ports 7751,7752]

const string Address = "localhost:7733";
const string Queue = "sp2";

var scenario = Arg("--scenario") ?? "S1";
var runNumber = Arg("--run") ?? "1";
var variant = Arg("--variant") ?? "main";
var ports = (Arg("--ports") ?? "7751,7752").Split(',').Select(int.Parse).ToArray();

var baseDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));
var runDir = Path.Combine(baseDir, "runs", $"{scenario}-{variant}-{runNumber}");
if (Directory.Exists(runDir))
    Directory.Delete(runDir, recursive: true);
var sandboxDir = Path.Combine(runDir, "sandbox");
var storeDir = Path.Combine(runDir, "store");
Directory.CreateDirectory(sandboxDir);
Directory.CreateDirectory(storeDir);
var clientLog = Path.Combine(runDir, "client.jsonl");
var hookLog = Path.Combine(runDir, "hook.jsonl");
var harnessLog = Path.Combine(runDir, "harness.jsonl");

var client = await TemporalClient.ConnectAsync(new(Address));
using var worker = new TemporalWorker(client, new TemporalWorkerOptions(Queue)
    .AddWorkflow<StepWorkflow>()
    .AddAllActivities(new StepActivities()));
using var stopWorker = new CancellationTokenSource();
var workerTask = worker.ExecuteAsync(stopWorker.Token);

var stepId = $"sp2-{scenario}-{variant}-{runNumber}-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
var stepInput = new StepInput
{
    StoreDir = storeDir,
    QuestionTimeoutSeconds = scenario is "S3" or "S3b" ? 90 : 7200,
};
await client.StartWorkflowAsync((StepWorkflow w) => w.RunAsync(stepInput), new(stepId, Queue));
var step = client.GetWorkflowHandle<StepWorkflow>(stepId);
Log("started", new { stepId, scenario, variant });

var replicaUrls = ports.Select(p => $"http://localhost:{p}").ToArray();
var replicas = new Dictionary<string, Process>();
var nonce = $"{new[] { "teal", "amber", "violet" }[Random.Shared.Next(3)]}-{Random.Shared.Next(10000, 100000)}";

if (scenario != "S4")
{
    foreach (var url in replicaUrls)
        await StartReplicaAsync(url);
}

var agent = StartAgent();
var agentDeadline = DateTime.UtcNow.AddMinutes(scenario == "L1" ? 35 : 10);
string? lateAnswerResult = null;
var answeredWhileNoneRunning = false;

try
{
    switch (scenario)
    {
        case "S1":
        case "L1":
        {
            var opener = await WaitForOpenerAsync();
            await KillReplicaAsync(opener);
            await Task.Delay(3000);
            await StartReplicaAsync(opener);
            await Task.Delay(3000);
            var other = replicaUrls.First(u => u != opener);
            await KillReplicaAsync(other);
            if (scenario == "L1")
            {
                await Task.Delay(TimeSpan.FromMinutes(10));
                await KillReplicaAsync(opener);
                await StartReplicaAsync(other);
                await Task.Delay(TimeSpan.FromMinutes(10));
            }
            else
            {
                await Task.Delay(10_000);
            }
            await AnswerAsync();
            break;
        }
        case "S2":
        {
            await WaitForOpenerAsync();
            foreach (var url in replicaUrls)
                await KillReplicaAsync(url);
            answeredWhileNoneRunning = replicas.Values.All(p => p.HasExited);
            await AnswerAsync();
            Log("answered_while_none_running", new { answeredWhileNoneRunning });
            await Task.Delay(60_000);
            await StartReplicaAsync(replicaUrls[0]);
            break;
        }
        case "S3":
        case "S3b":
        {
            var opener = await WaitForOpenerAsync();
            await Task.Delay(30_000);
            await KillReplicaAsync(opener);
            await Task.Delay(3000);
            await StartReplicaAsync(opener);
            if (scenario == "S3b")
            {
                // Answer late while the step is still running, so the validator decides.
                await WaitUntilAsync(() => step.QueryAsync(w => w.State()).Result.Questions.Single().Status == "timed_out",
                    TimeSpan.FromMinutes(3), "question timed out");
                Log("timed_out_seen", null);
            }
            else
            {
                await WaitForAgentAsync();
            }
            try
            {
                var question = (await step.QueryAsync(w => w.State())).Questions.Single();
                lateAnswerResult = "accepted:" + await step.ExecuteUpdateAsync(w =>
                    w.AnswerAsync(new AnswerIn(question.Id, nonce)));
            }
            catch (Exception ex)
            {
                lateAnswerResult = "rejected:" + (ex.InnerException?.Message ?? ex.Message);
            }
            Log("late_answer", new { lateAnswerResult });
            break;
        }
        case "S4":
        {
            await WaitUntilAsync(() => ReadJsonLines(hookLog).Any(e => e.GetProperty("event").GetString() == "attempt_failed"),
                TimeSpan.FromMinutes(3), "first failed hook attempt");
            Log("hook_attempt_failed_seen", null);
            await Task.Delay(30_000);
            foreach (var url in replicaUrls)
                await StartReplicaAsync(url);
            await WaitForOpenerAsync();
            await AnswerAsync();
            break;
        }
    }
}
catch (Exception ex)
{
    Log("scenario_error", new { error = $"{ex.GetType().Name}: {ex.Message}" });
    Console.WriteLine($"SP2 scenario error: {ex.Message}");
}

await WaitForAgentAsync();
var agentExit = agent.HasExited ? agent.ExitCode : -1;
if (!agent.HasExited)
{
    agent.Kill(entireProcessTree: true);
    Log("agent_killed_at_deadline", null);
}

StepState? state = null;
try
{
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    state = await step.GetResultAsync<StepState>(rpcOptions: new RpcOptions { CancellationToken = deadline.Token });
}
catch (Exception)
{
    state = await step.QueryAsync(w => w.State());
}

foreach (var process in replicas.Values.Where(p => !p.HasExited))
    process.Kill(entireProcessTree: true);
stopWorker.Cancel();
try { await workerTask; } catch (OperationCanceledException) { }

// ---- Evidence ----

var transcript = ReadJsonLines(Path.Combine(runDir, "transcript.jsonl"));
var toolUses = new List<string>();
var toolErrors = new List<string>();
foreach (var line in transcript)
{
    if (!line.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content) ||
        content.ValueKind != JsonValueKind.Array)
        continue;
    foreach (var block in content.EnumerateArray())
    {
        var type = block.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type == "tool_use")
            toolUses.Add(block.GetProperty("name").GetString() ?? "");
        if (type == "tool_result" && block.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True)
            toolErrors.Add(block.GetProperty("content").ToString());
    }
}
var askCalls = toolUses.Count(n => n == "mcp__engine__ask_human");

var clientEvents = ReadJsonLines(clientLog);
double? EventTime(string name) => clientEvents.Where(e => e.GetProperty("event").GetString() == name)
    .Select(e => (double?)e.GetProperty("t").GetDouble()).FirstOrDefault();
var openedAt = EventTime("opened");
var answerReceivedAt = EventTime("answer_received");
var openerPid = clientEvents.Where(e => e.GetProperty("event").GetString() == "opened")
    .Select(e => e.GetProperty("pid").GetInt32()).FirstOrDefault();
var failuresWhileWaiting = clientEvents.Count(e =>
    e.GetProperty("event").GetString() == "connection_failed" &&
    e.GetProperty("t").GetDouble() > (openedAt ?? double.MaxValue) &&
    e.GetProperty("t").GetDouble() < (answerReceivedAt ?? double.MaxValue));
var harnessEvents = ReadJsonLines(harnessLog);
var openerKilledBeforeAnswer = harnessEvents.Any(e =>
    e.GetProperty("event").GetString() == "replica_killed" &&
    e.GetProperty("detail").GetProperty("pid").GetInt32() == openerPid &&
    e.GetProperty("t").GetDouble() < (answerReceivedAt ?? double.MaxValue));

var hookEvents = ReadJsonLines(hookLog);
var firstDecisionAt = hookEvents.Where(e => e.GetProperty("event").GetString() == "decision")
    .Select(e => (double?)e.GetProperty("t").GetDouble()).FirstOrDefault();
var hookFailuresBeforeDecision = hookEvents.Count(e => e.GetProperty("event").GetString() == "attempt_failed" &&
    e.GetProperty("t").GetDouble() < (firstDecisionAt ?? double.MaxValue));

string? ReadFile(string name)
{
    var path = Path.Combine(sandboxDir, name);
    return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
}
var answerText = ReadFile("answer.txt");
var startText = ReadFile("start.txt");

// ---- Checks ----

var checks = new Dictionary<string, bool>();
if (scenario is "S1" or "S2" or "L1")
{
    checks["E1 opener killed before the answer was delivered; client saw a connection failure while waiting"] =
        openerKilledBeforeAnswer && failuresWhileWaiting >= 1;
    checks["C1 answer.txt holds the nonce"] = answerText == nonce;
}
if (scenario == "S2")
    checks["E2 answer recorded while no replica was running"] = answeredWhileNoneRunning;
if (scenario == "S4")
    checks["E3 hook client failed at least once before a decision"] = hookFailuresBeforeDecision >= 1;
checks["C2 one question in the workflow and one ask_human call"] =
    state?.Questions.Count == 1 && askCalls == 1;
checks["C3 no tool error reached the agent"] = toolErrors.Count == 0;
checks["C4 report 'done' recorded and agent exited 0"] =
    state?.Reports.Any(r => r.Outcome == "done") == true && agentExit == 0;
if (scenario is "S3" or "S3b")
    checks["C5 agent told no answer came; late answer rejected"] =
        answerText == "NO ANSWER" && lateAnswerResult?.StartsWith("rejected") == true &&
        (scenario == "S3" || lateAnswerResult.Contains("timed out"));
if (scenario == "S4")
    checks["C6 first tool call ran; no denial"] = startText == "started" && toolErrors.Count == 0;

var summary = new
{
    stepId, scenario, variant, run = runNumber, nonce, answerText, startText, agentExit,
    askCalls, toolUses, toolErrors,
    questions = state?.Questions, openSignals = state?.OpenSignals, reports = state?.Reports,
    openerPid, openerKilledBeforeAnswer, failuresWhileWaiting,
    secondsFromOpenToAnswer = openedAt is null || answerReceivedAt is null
        ? (double?)null : Math.Round(answerReceivedAt.Value - openedAt.Value, 1),
    answeredWhileNoneRunning, hookFailuresBeforeDecision, lateAnswerResult,
    clientEvents = clientEvents.GroupBy(e => e.GetProperty("event").GetString()).ToDictionary(g => g.Key!, g => g.Count()),
    checks,
};
await File.WriteAllTextAsync(Path.Combine(runDir, "summary.json"),
    JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

Console.WriteLine($"SP2 {scenario}-{variant}-{runNumber}: answer={answerText ?? "<none>"} nonce={nonce} " +
                  $"askCalls={askCalls} questions={state?.Questions.Count} failuresWhileWaiting={failuresWhileWaiting} " +
                  $"hookFailures={hookFailuresBeforeDecision} exit={agentExit}");
foreach (var (name, ok) in checks)
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
foreach (var error in toolErrors.Take(5))
    Console.WriteLine($"    tool error: {error[..Math.Min(200, error.Length)]}");
return checks.Values.All(v => v) ? 0 : 1;

// ---- Helpers ----

Process StartAgent()
{
    var replicasEnv = string.Join(",", replicaUrls);
    var sandboxScripts = Path.Combine(baseDir, "sandbox");
    var mcpConfig = new
    {
        mcpServers = new Dictionary<string, object>
        {
            ["engine"] = new
            {
                command = "python3",
                args = new[] { Path.Combine(sandboxScripts, "engine_client.py") },
                env = new Dictionary<string, string>
                {
                    ["STEP_ID"] = stepId,
                    ["REPLICAS"] = replicasEnv,
                    ["CLIENT_LOG"] = clientLog,
                    ["CLIENT_MODE"] = variant == "n1" ? "noreconnect" : "reconnect",
                },
            },
        },
    };
    var hookCommand = $"STEP_ID={stepId} REPLICAS={replicasEnv} HOOK_LOG={hookLog} " +
                      $"HOOK_MODE={(variant == "n2" ? "failclosed" : "hold")} " +
                      $"python3 {Path.Combine(sandboxScripts, "hook_client.py")}";
    var settings = new
    {
        hooks = new
        {
            PreToolUse = new[]
            {
                new { matcher = "*", hooks = new[] { new { type = "command", command = hookCommand, timeout = 300 } } },
            },
        },
    };
    var mcpPath = Path.Combine(runDir, "mcp.json");
    var settingsPath = Path.Combine(runDir, "settings.json");
    File.WriteAllText(mcpPath, JsonSerializer.Serialize(mcpConfig));
    File.WriteAllText(settingsPath, JsonSerializer.Serialize(settings));

    const string prompt = """
        This is a test of an engine integration. Do exactly these steps, in order, and nothing else:
        1. Use the Write tool to create the file start.txt containing the word started.
        2. Call the ask_human tool once, with the question "What label should I write?" and the options ["red", "green", "blue"]. It may take a long time to return; wait for it. Never call it again.
        3. If the tool says the person answered, use the Write tool to write their answer, exactly as given and nothing else, to answer.txt. If it says no answer came, write exactly NO ANSWER to answer.txt.
        4. Call the report tool with outcome "done" and a one-line summary.
        Then stop.
        """;

    var info = new ProcessStartInfo("claude")
    {
        WorkingDirectory = sandboxDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    foreach (var a in new[]
             {
                 "-p", prompt, "--model", "haiku", "--output-format", "stream-json", "--verbose",
                 "--mcp-config", mcpPath, "--strict-mcp-config", "--settings", settingsPath,
                 "--setting-sources", "project",
                 "--allowedTools", "mcp__engine__ask_human", "mcp__engine__report", "Write",
             })
        info.ArgumentList.Add(a);
    info.Environment["MCP_TOOL_TIMEOUT"] = "7200000";
    info.Environment["MCP_TIMEOUT"] = "60000";

    var process = Process.Start(info)!;
    var transcriptPath = Path.Combine(runDir, "transcript.jsonl");
    var stderrPath = Path.Combine(runDir, "agent-stderr.log");
    _ = Task.Run(async () =>
    {
        await using var file = File.CreateText(transcriptPath);
        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
        {
            await file.WriteLineAsync(line);
            await file.FlushAsync();
        }
    });
    _ = Task.Run(async () => await File.WriteAllTextAsync(stderrPath, await process.StandardError.ReadToEndAsync()));
    Log("agent_started", new { pid = process.Id });
    return process;
}

async Task StartReplicaAsync(string url)
{
    var dll = Path.Combine(baseDir, "Sp2.Replica/bin/Debug/net10.0/Sp2.Replica.dll");
    var info = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var a in new[] { dll, "--urls", url, "--store", storeDir, "--temporal", Address })
        info.ArgumentList.Add(a);
    var process = Process.Start(info)!;
    var logPath = Path.Combine(runDir, $"replica-{new Uri(url).Port}.log");
    process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (replicas) File.AppendAllText(logPath, e.Data + "\n"); };
    process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (replicas) File.AppendAllText(logPath, "ERR " + e.Data + "\n"); };
    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    replicas[url] = process;

    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    await WaitUntilAsync(() =>
    {
        try { return http.GetAsync(url + "/health").Result.IsSuccessStatusCode; }
        catch { return false; }
    }, TimeSpan.FromSeconds(30), $"replica {url} healthy");
    Log("replica_started", new { url, pid = process.Id });
}

async Task KillReplicaAsync(string url)
{
    var process = replicas[url];
    var pid = process.Id;
    process.Kill(entireProcessTree: true);
    await process.WaitForExitAsync();
    Log("replica_killed", new { url, pid });
}

/// <summary>Waits for the client to log that a replica opened the question; returns that replica.</summary>
async Task<string> WaitForOpenerAsync()
{
    await WaitUntilAsync(() => ReadJsonLines(clientLog).Any(e => e.GetProperty("event").GetString() == "opened"),
        TimeSpan.FromMinutes(5), "question opened");
    var opened = ReadJsonLines(clientLog).First(e => e.GetProperty("event").GetString() == "opened");
    var url = opened.GetProperty("replica").GetString()!;
    Log("question_opened", new { url, pid = opened.GetProperty("pid").GetInt32() });
    return url;
}

async Task AnswerAsync()
{
    var question = (await step.QueryAsync(w => w.State())).Questions.Single();
    var reply = await step.ExecuteUpdateAsync(w => w.AnswerAsync(new AnswerIn(question.Id, nonce)));
    Log("answered", new { questionId = question.Id, nonce, reply });
}

async Task WaitForAgentAsync()
{
    var remaining = agentDeadline - DateTime.UtcNow;
    if (remaining <= TimeSpan.Zero)
        return;
    using var cts = new CancellationTokenSource(remaining);
    try { await agent.WaitForExitAsync(cts.Token); } catch (OperationCanceledException) { }
}

async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, string what)
{
    var until = DateTime.UtcNow + timeout;
    while (!condition())
    {
        if (DateTime.UtcNow > until)
            throw new TimeoutException($"timed out waiting for {what}");
        await Task.Delay(250);
    }
}

List<JsonElement> ReadJsonLines(string path)
{
    if (!File.Exists(path))
        return [];
    var lines = new List<JsonElement>();
    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using var reader = new StreamReader(stream);
    while (reader.ReadLine() is { } line)
    {
        try { lines.Add(JsonDocument.Parse(line).RootElement.Clone()); }
        catch (JsonException) { }
    }
    return lines;
}

void Log(string name, object? detail)
{
    var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    lock (harnessLog)
        File.AppendAllText(harnessLog, JsonSerializer.Serialize(new { t, @event = name, detail }) + "\n");
}

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
