using System.Text.Json;
using Sp2;
using Temporalio.Client;

// One agent worker replica: the engine endpoint and the hook endpoint
// (docs/02 section 4.2). Stateless: it signals the run and reads the record
// store, and keeps nothing in memory, so any replica can serve any request.
//
// Usage: Sp2.Replica --urls http://localhost:7751 --store <dir> [--temporal localhost:7733]

var store = Arg("--store") ?? throw new ArgumentException("--store is required");
var temporal = await TemporalClient.ConnectAsync(new(Arg("--temporal") ?? "localhost:7733"));
var pid = Environment.ProcessId;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
var app = builder.Build();

void Log(string message) => Console.WriteLine($"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()} pid={pid} {message}");

app.MapGet("/health", () => Results.Ok(new { pid }));

app.MapPost("/questions", async (OpenRequest request) =>
{
    // Opening is idempotent in the workflow, so a client retrying after a lost
    // response does not open a second question.
    await temporal.GetWorkflowHandle(request.StepId).SignalAsync(Names.OpenQuestion,
        [new QuestionOpen(request.QuestionId, request.Text, request.Options ?? [])]);
    Log($"open {request.StepId}/{request.QuestionId}");
    return Results.Ok(new { status = "open", pid });
});

app.MapGet("/questions/{stepId}/{questionId}", async (string stepId, string questionId, int? wait, CancellationToken ct) =>
{
    var path = RecordStore.PathFor(store, stepId, questionId);
    var until = DateTime.UtcNow.AddSeconds(wait ?? 25);
    Log($"wait {stepId}/{questionId}");
    while (true)
    {
        if (File.Exists(path))
        {
            var record = JsonSerializer.Deserialize<QuestionRecord>(await File.ReadAllTextAsync(path, ct))!;
            if (record.Status != "open" || DateTime.UtcNow >= until)
                return Results.Ok(new { status = record.Status, answer = record.Answer, pid });
        }
        else if (DateTime.UtcNow >= until)
        {
            return Results.Ok(new { status = "open", answer = (string?)null, pid });
        }
        await Task.Delay(200, ct);
    }
});

app.MapPost("/report", async (ReportRequest request) =>
{
    await temporal.GetWorkflowHandle(request.StepId).SignalAsync(Names.Report,
        [new ReportIn(request.Outcome, request.Summary ?? "")]);
    Log($"report {request.StepId} {request.Outcome}");
    return Results.Ok(new { pid });
});

app.MapPost("/hooks/pre_tool_use", (HookRequest request) =>
{
    var command = request.ToolInput.ValueKind == JsonValueKind.Object &&
                  request.ToolInput.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "";
    var deny = command.Contains("git push");
    Log($"hook {request.StepId} {request.ToolName} {(deny ? "deny" : "allow")}");
    return Results.Ok(new { decision = deny ? "deny" : "allow", reason = deny ? "agents never push" : "", pid });
});

Log("listening");
await app.RunAsync();

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

record OpenRequest(string StepId, string QuestionId, string Text, List<string>? Options);

record ReportRequest(string StepId, string Outcome, string? Summary);

record HookRequest(string StepId, string ToolName, JsonElement ToolInput);
