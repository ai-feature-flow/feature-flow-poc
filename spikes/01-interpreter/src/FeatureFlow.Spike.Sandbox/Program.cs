using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Fake sandbox + fake agent runtime + fake hook endpoint for Spike 1.
// Runs as its own OS process so it can outlive a worker crash (docs/01 3.6, mechanism 3).

var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
var port = 5199;
string? urls = null;

for (var i = 0; i < cliArgs.Length; i++)
{
    var a = cliArgs[i];
    if (a == "--port" && i + 1 < cliArgs.Length) { int.TryParse(cliArgs[i + 1], out port); i++; }
    else if (a.StartsWith("--port=", StringComparison.Ordinal)) { int.TryParse(a["--port=".Length..], out port); }
    else if (a == "--urls" && i + 1 < cliArgs.Length) { urls = cliArgs[i + 1]; i++; }
    else if (a.StartsWith("--urls=", StringComparison.Ordinal)) { urls = a["--urls=".Length..]; }
}

urls ??= $"http://127.0.0.1:{port}";

var builder = WebApplication.CreateBuilder(Array.Empty<string>());
builder.WebHost.UseUrls(urls);
builder.Logging.ClearProviders();

var app = builder.Build();

var store = new SandboxStore();

// One line per request, prefixed "SANDBOX ".
app.Use(async (ctx, next) =>
{
    var sw = System.Diagnostics.Stopwatch.StartNew();
    Exception? failure = null;
    try { await next(); }
    catch (Exception ex) { failure = ex; throw; }
    finally
    {
        sw.Stop();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
        var note = failure is null ? "" : $" error={failure.GetType().Name}";
        Console.WriteLine($"SANDBOX {stamp} {ctx.Request.Method} {ctx.Request.Path}{ctx.Request.QueryString} -> {ctx.Response.StatusCode} {sw.ElapsedMilliseconds}ms{note}");
    }
});

app.MapGet("/health", () => Results.Ok(new OkResponse(true)));

app.MapGet("/hook/{stepId}", (string stepId) =>
    store.IsHookDown(stepId)
        ? Results.Json(new UnreachableResponse("unreachable"), statusCode: StatusCodes.Status503ServiceUnavailable)
        : Results.Ok(new OkResponse(true)));

app.MapPost("/commands", async (HttpContext ctx) =>
{
    var req = await ReadJson<StartCommandRequest>(ctx);
    if (req is null) return Results.BadRequest(new UnreachableResponse("invalid_body"));
    if (string.IsNullOrWhiteSpace(req.StepId)) return Results.BadRequest(new UnreachableResponse("stepId_required"));

    var cmd = store.Start(req);
    return Results.Ok(cmd.ToStartResponse());
});

app.MapGet("/commands/{handle}", (string handle) =>
{
    var cmd = store.Find(handle);
    if (cmd is null) return Results.NotFound(new UnreachableResponse("unknown_handle"));
    if (cmd.IsConnectionBroken(DateTimeOffset.UtcNow))
        return Results.Json(new UnreachableResponse("unreachable"), statusCode: StatusCodes.Status503ServiceUnavailable);
    return Results.Ok(cmd.ToStatusResponse());
});

app.MapPost("/commands/{handle}/break-connection", (string handle, double seconds) =>
{
    var cmd = store.Find(handle);
    if (cmd is null) return Results.NotFound(new UnreachableResponse("unknown_handle"));
    if (seconds <= 0) seconds = 20;
    var until = cmd.BreakConnection(seconds);
    return Results.Ok(new BreakResponse(cmd.Handle, seconds, until));
});

app.MapPost("/commands/{handle}/stall", (string handle) =>
{
    var cmd = store.Find(handle);
    if (cmd is null) return Results.NotFound(new UnreachableResponse("unknown_handle"));
    cmd.Stall();
    return Results.Ok(cmd.ToStatusResponse());
});

app.MapPost("/commands/{handle}/fail-hook", (string handle) =>
{
    var cmd = store.Find(handle);
    if (cmd is null) return Results.NotFound(new UnreachableResponse("unknown_handle"));
    store.MarkHookDown(cmd.StepId);
    return Results.Ok(new FailHookResponse(cmd.Handle, cmd.StepId));
});

app.MapPost("/admin/reset", () =>
{
    store.Reset();
    return Results.Ok(new OkResponse(true));
});

app.MapGet("/admin/state", () => Results.Ok(store.DumpState()));

Console.WriteLine($"SANDBOX listening on {urls} (port {port})");
app.Run();

static async Task<T?> ReadJson<T>(HttpContext ctx) where T : class
{
    try
    {
        return await ctx.Request.ReadFromJsonAsync<T>(new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
    }
    catch (JsonException) { return null; }
}

// ---------- state ----------

sealed class SandboxStore
{
    private readonly ConcurrentDictionary<string, Command> _byHandle = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Command> _byIdempotencyKey = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _hookDown = new(StringComparer.Ordinal);
    private readonly object _startGate = new();
    private int _nextId;

    public Command Start(StartCommandRequest req)
    {
        lock (_startGate)
        {
            if (!string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                // Re-attachment by idempotency key: never start a second command for the same key.
                if (_byIdempotencyKey.TryGetValue(req.IdempotencyKey!, out var existing))
                {
                    if (!string.IsNullOrWhiteSpace(req.SessionRef))
                        existing.RecordResume(req.SessionRef!);   // mechanism 2: resume the session
                    return existing;
                }
            }

            var created = Create(req);
            _byHandle[created.Handle] = created;
            if (!string.IsNullOrWhiteSpace(req.IdempotencyKey)) _byIdempotencyKey[req.IdempotencyKey!] = created;
            return created;
        }
    }

    private Command Create(StartCommandRequest req)
    {
        var id = Interlocked.Increment(ref _nextId);
        var handle = $"cmd-{id}";
        var sessionRef = string.IsNullOrWhiteSpace(req.SessionRef) ? $"sess-{id}" : req.SessionRef!;
        var sandboxKey = string.IsNullOrWhiteSpace(req.IdempotencyKey) ? $"sbx-{req.StepId}-{handle}" : req.IdempotencyKey!;
        return new Command(
            handle,
            req.StepId,
            req.IdempotencyKey,
            sessionRef,
            sandboxKey,
            req.DurationSeconds <= 0 ? 30 : req.DurationSeconds,
            req.ProduceCommits ?? true,
            req.CommitEverySeconds <= 0 ? 5 : req.CommitEverySeconds,
            DateTimeOffset.UtcNow);
    }

    public Command? Find(string handle) => _byHandle.TryGetValue(handle, out var c) ? c : null;

    public bool IsHookDown(string stepId) => _hookDown.ContainsKey(stepId);

    public void MarkHookDown(string stepId) => _hookDown[stepId] = 1;

    public void Reset()
    {
        _byHandle.Clear();
        _byIdempotencyKey.Clear();
        _hookDown.Clear();
    }

    public object DumpState() => new
    {
        commands = _byHandle.Values.OrderBy(c => c.Handle, StringComparer.Ordinal).Select(c => c.ToStatusResponse()).ToArray(),
        hookDownStepIds = _hookDown.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray()
    };
}

sealed class Command
{
    private readonly object _gate = new();
    private DateTimeOffset? _stalledAt;
    private DateTimeOffset? _brokenUntil;
    private int _resumeCount;

    public Command(
        string handle, string stepId, string? idempotencyKey, string sessionRef, string sandboxKey,
        double durationSeconds, bool produceCommits, double commitEverySeconds, DateTimeOffset startedAt)
    {
        Handle = handle;
        StepId = stepId;
        IdempotencyKey = idempotencyKey;
        SessionRef = sessionRef;
        SandboxKey = sandboxKey;
        DurationSeconds = durationSeconds;
        ProduceCommits = produceCommits;
        CommitEverySeconds = commitEverySeconds;
        StartedAt = startedAt;
    }

    public string Handle { get; }
    public string StepId { get; }
    public string? IdempotencyKey { get; }
    public string SessionRef { get; }
    public string SandboxKey { get; }
    public double DurationSeconds { get; }
    public bool ProduceCommits { get; }
    public double CommitEverySeconds { get; }
    public DateTimeOffset StartedAt { get; }

    public void Stall()
    {
        lock (_gate) { _stalledAt ??= DateTimeOffset.UtcNow; }
    }

    public DateTimeOffset BreakConnection(double seconds)
    {
        var until = DateTimeOffset.UtcNow.AddSeconds(seconds);
        lock (_gate) { _brokenUntil = until; }
        return until;
    }

    public bool IsConnectionBroken(DateTimeOffset now)
    {
        lock (_gate) { return _brokenUntil.HasValue && now < _brokenUntil.Value; }
    }

    public void RecordResume(string sessionRef)
    {
        lock (_gate)
        {
            if (string.Equals(sessionRef, SessionRef, StringComparison.Ordinal)) _resumeCount++;
        }
    }

    // Everything is computed from the start timestamp on each read: no background timer.
    private Snapshot TakeSnapshot()
    {
        var now = DateTimeOffset.UtcNow;
        lock (_gate)
        {
            var elapsed = Math.Max(0, (now - StartedAt).TotalSeconds);
            var status = elapsed >= DurationSeconds ? "completed" : "running";

            // Progress clock freezes at the stall point; the wall clock does not.
            var progress = Math.Max(0, _stalledAt.HasValue ? (_stalledAt.Value - StartedAt).TotalSeconds : elapsed);

            var turns = (int)Math.Floor(progress);
            string? lastCommit = null;
            if (ProduceCommits)
            {
                var commitIndex = (int)Math.Floor(progress / CommitEverySeconds);
                if (commitIndex > 0) lastCommit = CommitId(StepId, commitIndex);
            }

            var broken = _brokenUntil.HasValue && now < _brokenUntil.Value;
            return new Snapshot(Math.Round(elapsed, 2), status, turns, lastCommit, broken, _resumeCount);
        }
    }

    public CommandStartResponse ToStartResponse()
    {
        var s = TakeSnapshot();
        return new CommandStartResponse(Handle, s.Status, SessionRef, s.LastCommit, s.Turns, s.Elapsed, SandboxKey);
    }

    public CommandStatusResponse ToStatusResponse()
    {
        var s = TakeSnapshot();
        return new CommandStatusResponse(Handle, s.Status, SessionRef, s.LastCommit, s.Turns, s.Elapsed, SandboxKey, s.Broken, s.Resumes);
    }

    // Deterministic: short hex of a hash of stepId + commit index. No randomness, no GUIDs.
    private static string CommitId(string stepId, int commitIndex)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{stepId}:{commitIndex}"));
        return Convert.ToHexString(bytes)[..7].ToLowerInvariant();
    }

    private readonly record struct Snapshot(
        double Elapsed, string Status, int Turns, string? LastCommit, bool Broken, int Resumes);
}

// ---------- contracts ----------

sealed record StartCommandRequest(
    string StepId,
    string? IdempotencyKey,
    string? SessionRef,
    double DurationSeconds,
    bool? ProduceCommits,
    double CommitEverySeconds);

sealed record CommandStartResponse(
    string Handle,
    string Status,
    string SessionRef,
    string? LastCommit,
    int SessionTurns,
    double ElapsedSeconds,
    string SandboxKey);

sealed record CommandStatusResponse(
    string Handle,
    string Status,
    string SessionRef,
    string? LastCommit,
    int SessionTurns,
    double ElapsedSeconds,
    string SandboxKey,
    bool ConnectionBroken,
    int ResumeCount);

sealed record OkResponse(bool Ok);
sealed record UnreachableResponse(string Reason);
sealed record BreakResponse(string Handle, double BrokenForSeconds, DateTimeOffset BrokenUntil);
sealed record FailHookResponse(string Handle, string StepId);
