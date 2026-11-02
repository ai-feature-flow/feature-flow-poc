using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Client;

namespace FeatureFlow.Engine.Agent;

/// <summary>Tells a run that an agent asked a question. The Temporal implementation signals the run's workflow id.</summary>
public interface IRunSignals
{
    Task QuestionOpenedAsync(string workflowId, QuestionSignal question, CancellationToken cancellationToken);
}

public sealed class TemporalRunSignals(ITemporalClient client) : IRunSignals
{
    public Task QuestionOpenedAsync(string workflowId, QuestionSignal question, CancellationToken cancellationToken) =>
        client.GetWorkflowHandle(workflowId).SignalAsync(RunWorkflowNames.QuestionOpened, [question], new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
}

/// <summary>What the agent endpoint uses: tokens, the record store, the run's signal, and the model proxy.</summary>
public sealed record AgentEndpointServices(StepTokens Tokens, IRecordStoreProvider Records, IRunSignals Signals, ModelProxy? Proxy, TimeSpan? LongPoll = null);

/// <summary>
/// The agent worker's endpoint (docs/02 section 4.2, RW17; LOG 68): the hook endpoint, the engine tools the
/// sandbox client forwards, and the model proxy. Every call carries the step's token; a replica keeps nothing in
/// memory, so any replica serves any step from the token and the record store.
/// </summary>
public static class AgentEndpoint
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WebApplication Build(AgentEndpointServices services, string url)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        builder.Services.AddSingleton(services);
        var app = builder.Build();
        Map(app, services);
        return app;
    }

    public static void Map(WebApplication app, AgentEndpointServices s)
    {
        app.MapPost("/agent/v1/hooks/{hook}", async (HttpContext http, string hook) =>
            await Authorised(http, s, async claims =>
            {
                var input = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                return Results.Json(await HookAsync(s, claims, hook, input.RootElement, http.RequestAborted), Json);
            }));

        app.MapPost("/agent/v1/report", (Func<HttpContext, Task<IResult>>)(async http =>
            await Authorised(http, s, async claims =>
            {
                var input = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                return Results.Json(await ReportAsync(s, claims, input.RootElement, http.RequestAborted), Json);
            })));

        app.MapPost("/agent/v1/questions", (Func<HttpContext, Task<IResult>>)(async http =>
            await Authorised(http, s, async claims =>
            {
                var input = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                return Results.Json(await OpenQuestionAsync(s, claims, input.RootElement, http.RequestAborted), Json);
            })));

        app.MapGet("/agent/v1/questions/{id}", async (HttpContext http, string id, int? wait) =>
            await Authorised(http, s, async claims =>
                Results.Json(await PollQuestionAsync(s, claims, id, TimeSpan.FromSeconds(Math.Clamp(wait ?? 25, 0, 55)), http.RequestAborted), Json)));

        app.Map("/model/{**path}", async (HttpContext http, string? path) =>
        {
            var token = Bearer(http) ?? http.Request.Headers["x-api-key"].FirstOrDefault();
            if (s.Proxy is null || s.Tokens.Check(token) is not { } claims || !await ActiveAsync(s, claims))
            {
                http.Response.StatusCode = 401;
                await http.Response.WriteAsync("""{"type":"error","error":{"type":"authentication_error","message":"unknown step token"}}""");
                return;
            }

            var (status, input, output) = await s.Proxy.ForwardAsync(http, path ?? "", http.RequestAborted);
            await AppendAsync(s, new RecordBatch(AgentEvents: [new AgentEventRecord(claims.StepKey, "model", $"{http.Request.Method} /{path} {status} in={input} out={output}", DateTimeOffset.UtcNow)]));
        });
    }

    static string? Bearer(HttpContext http) =>
        http.Request.Headers.Authorization.FirstOrDefault() is { } value && value.StartsWith("Bearer ", StringComparison.Ordinal) ? value["Bearer ".Length..].Trim() : null;

    static async Task<IResult> Authorised(HttpContext http, AgentEndpointServices s, Func<StepClaims, Task<IResult>> handle) =>
        s.Tokens.Check(Bearer(http)) is { } claims && await ActiveAsync(s, claims)
            ? await handle(claims)
            : Results.Json(new { error = "unknown or revoked step token" }, Json, statusCode: 401);

    /// <summary>A token works only while its step attempt is recorded and not ended: the activity revokes it at the end.</summary>
    public static async Task<bool> ActiveAsync(AgentEndpointServices s, StepClaims claims) =>
        (await s.Records.QueryAsync<AgentStepRecord>(Queries.AgentStep, new Dictionary<string, string> { ["step_key"] = claims.StepKey }, CancellationToken.None))
        .Unwrap().FirstOrDefault() is { Revoked: false };

    static async Task AppendAsync(AgentEndpointServices s, RecordBatch batch) => (await s.Records.AppendAsync(batch, CancellationToken.None)).Unwrap();

    public static async Task<HookDecision> HookAsync(AgentEndpointServices s, StepClaims claims, string hook, JsonElement input, CancellationToken cancellationToken)
    {
        var tool = input.TryGetProperty("tool_name", out var t) ? t.GetString() ?? "" : "";
        var toolInput = input.TryGetProperty("tool_input", out var ti) ? ti : default;
        var decision = hook == "pre_tool_use" ? HookPolicy.PreToolUse(claims, tool, toolInput) : HookDecision.Allow;
        var detail = hook switch
        {
            "pre_tool_use" => $"{tool} {decision.Decision}" + (decision.Reason is null ? "" : $": {decision.Reason}"),
            "post_tool_use" => HookPolicy.FileOpened(tool, toolInput) is { } file ? $"{tool} {file}" : tool,
            _ => "",
        };
        await AppendAsync(s, new RecordBatch(AgentEvents: [new AgentEventRecord(claims.StepKey, "hook_" + hook, detail, DateTimeOffset.UtcNow)]));
        return decision;
    }

    public sealed record ReportReply(bool Accepted, bool Stop, string Message);

    /// <summary>
    /// The <c>report</c> tool (docs/04 section 3.3): checks the report and records it. A rejection returns the
    /// errors for a correction turn; after <c>correction_turns</c> rejections the next one fails the attempt and
    /// tells the agent to stop (SP3).
    /// </summary>
    public static async Task<ReportReply> ReportAsync(AgentEndpointServices s, StepClaims claims, JsonElement input, CancellationToken cancellationToken)
    {
        var step = (await s.Records.QueryAsync<AgentStepRecord>(Queries.AgentStep, new Dictionary<string, string> { ["step_key"] = claims.StepKey }, cancellationToken))
            .Unwrap().FirstOrDefault() ?? throw new InvalidOperationException($"no agent step {claims.StepKey} is recorded");
        var reports = (await s.Records.QueryAsync<ReportRecord>(Queries.Reports, new Dictionary<string, string> { ["step_key"] = claims.StepKey }, cancellationToken)).Unwrap();
        var rejected = reports.Count(r => !r.Accepted);
        if (rejected > step.CorrectionTurns)
            return new ReportReply(false, true, "The attempt has failed: stop now. Do not call report again.");

        var missing = input.TryGetProperty("missing", out var m) && m.ValueKind == JsonValueKind.Array
            ? m.EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.Ordinal)
            : [];
        var report = input.TryGetProperty("report", out var r) ? r : input;
        var (checkedReport, errors) = ReportCheck.Check(report, step.Contract, missing);
        var record = new ReportRecord(claims.StepKey, reports.Count + 1, checkedReport is not null, checkedReport, errors, DateTimeOffset.UtcNow);
        await AppendAsync(s, new RecordBatch(Reports: [record]));
        if (checkedReport is not null)
            return new ReportReply(true, true, "Report accepted. Your step is finished: stop now.");
        var list = string.Join("\n", errors.Select(e => "- " + e));
        return rejected + 1 > step.CorrectionTurns
            ? new ReportReply(false, true, $"Report rejected:\n{list}\nThe attempt has failed: stop now. Do not call report again.")
            : new ReportReply(false, false, $"Report rejected:\n{list}\nFix what it names and call report again (correction turn {rejected + 1} of {step.CorrectionTurns}).");
    }

    public sealed record QuestionReply(string Status, string? Answer, string? Message);

    /// <summary>
    /// <c>ask_human</c> (docs/04 section 3.4): records the question by the id the sandbox client gave it and signals
    /// the run, both idempotently, so a retry against another replica resumes the same question (LOG 59).
    /// </summary>
    public static async Task<QuestionReply> OpenQuestionAsync(AgentEndpointServices s, StepClaims claims, JsonElement input, CancellationToken cancellationToken)
    {
        var id = input.GetProperty("questionId").GetString()!;
        var existing = await QuestionAsync(s, id, cancellationToken);
        if (existing is not null)
            return existing.StepKey == claims.StepKey
                ? new QuestionReply(existing.Status, existing.Answer, null)
                : new QuestionReply("unknown", null, "no such question for this step");
        var step = (await s.Records.QueryAsync<AgentStepRecord>(Queries.AgentStep, new Dictionary<string, string> { ["step_key"] = claims.StepKey }, cancellationToken))
            .Unwrap().FirstOrDefault() ?? throw new InvalidOperationException($"no agent step {claims.StepKey} is recorded");
        if (step.QuestionsMax <= 0)
            return new QuestionReply("unavailable", null, "This step does not take questions. Use your judgement, and say in your summary what you assumed.");
        var asked = (await s.Records.QueryAsync<QuestionRecord>(Queries.Questions, new Dictionary<string, string> { ["step_key"] = claims.StepKey }, cancellationToken)).Unwrap();
        if (asked.Count >= step.QuestionsMax)
            return new QuestionReply("limit", null, $"You have asked the {step.QuestionsMax} questions this step allows. Use your judgement, and say in your summary what you assumed.");

        var text = input.TryGetProperty("question", out var q) ? q.GetString() ?? "" : "";
        if (input.TryGetProperty("context", out var c) && c.GetString() is { Length: > 0 } context)
            text += "\n\n" + context;
        var options = input.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
            ? o.EnumerateArray().Select(x => x.GetString() ?? "").Take(4).ToList()
            : [];
        await AppendAsync(s, new RecordBatch(Questions: [new QuestionRecord(claims.StepKey, claims.RunId, id, text, options, QuestionRecord.Open, null, DateTimeOffset.UtcNow)]));
        await s.Signals.QuestionOpenedAsync(claims.WorkflowId, new QuestionSignal(id, claims.StepId, claims.StepKey, text, options, step.QuestionsTimeoutSeconds), cancellationToken);
        return new QuestionReply(QuestionRecord.Open, null, null);
    }

    /// <summary>Long-polls the record store for the question's answer or timeout.</summary>
    public static async Task<QuestionReply> PollQuestionAsync(AgentEndpointServices s, StepClaims claims, string id, TimeSpan wait, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            var question = await QuestionAsync(s, id, cancellationToken);
            if (question is null || question.StepKey != claims.StepKey)
                return new QuestionReply("unknown", null, "no such question for this step");
            if (question.Status != QuestionRecord.Open || DateTimeOffset.UtcNow >= deadline)
                return new QuestionReply(question.Status, question.Answer, null);
            await Task.Delay(s.LongPoll ?? TimeSpan.FromMilliseconds(250), cancellationToken);
        }
    }

    static async Task<QuestionRecord?> QuestionAsync(AgentEndpointServices s, string id, CancellationToken cancellationToken) =>
        (await s.Records.QueryAsync<QuestionRecord>(Queries.Question, new Dictionary<string, string> { ["question_id"] = id }, cancellationToken)).Unwrap().FirstOrDefault();
}
