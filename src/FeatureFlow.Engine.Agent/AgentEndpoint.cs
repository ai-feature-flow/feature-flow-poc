using System.Text.Json;
using System.Text.Json.Serialization;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Temporalio.Client;

namespace FeatureFlow.Engine.Agent;

/// <summary>
/// Tells a run that an agent asked a question, or that its model cannot be reached, and asks it about its plan. The Temporal
/// implementation signals and queries the run's workflow id.
/// </summary>
public interface IRunSignals
{
    Task QuestionOpenedAsync(string workflowId, QuestionSignal question, CancellationToken cancellationToken);

    Task ModelUnreachableAsync(string workflowId, ModelUnreachableSignal signal, CancellationToken cancellationToken);

    /// <summary>The <c>agent_plan</c> query.</summary>
    Task<AgentPlanView> PlanAsync(string workflowId, AgentPlanQuery query, CancellationToken cancellationToken);

    /// <summary>The <c>patch_preview</c> query.</summary>
    Task<PatchPreview> PreviewAsync(string workflowId, PatchPreviewQuery query, CancellationToken cancellationToken);

    /// <summary>The <c>child_patch_preview</c> query, on a parent.</summary>
    Task<PatchPreview> ChildPreviewAsync(string workflowId, ChildPatchPreviewQuery query, CancellationToken cancellationToken);
}

public sealed class TemporalRunSignals(ITemporalClient client) : IRunSignals
{
    public Task QuestionOpenedAsync(string workflowId, QuestionSignal question, CancellationToken cancellationToken) =>
        client.GetWorkflowHandle(workflowId).SignalAsync(RunWorkflowNames.QuestionOpened, [question], new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });

    public Task ModelUnreachableAsync(string workflowId, ModelUnreachableSignal signal, CancellationToken cancellationToken) =>
        client.GetWorkflowHandle(workflowId).SignalAsync(RunWorkflowNames.ModelUnreachable, [signal], new WorkflowSignalOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });

    public Task<AgentPlanView> PlanAsync(string workflowId, AgentPlanQuery query, CancellationToken cancellationToken) =>
        client.GetWorkflowHandle(workflowId).QueryAsync<AgentPlanView>(RunWorkflowNames.AgentPlan, [query], new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });

    public Task<PatchPreview> PreviewAsync(string workflowId, PatchPreviewQuery query, CancellationToken cancellationToken) =>
        client.GetWorkflowHandle(workflowId).QueryAsync<PatchPreview>(RunWorkflowNames.PatchPreview, [query], new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });

    public Task<PatchPreview> ChildPreviewAsync(string workflowId, ChildPatchPreviewQuery query, CancellationToken cancellationToken) =>
        client.GetWorkflowHandle(workflowId).QueryAsync<PatchPreview>(RunWorkflowNames.ChildPatchPreview, [query], new WorkflowQueryOptions { Rpc = new RpcOptions { CancellationToken = cancellationToken } });
}

/// <summary>
/// What the agent endpoint uses: tokens, the record store, the run's signal, the model proxy of each agent runtime
/// instance that configures one (LOG 446), and each instance's reading of its hooks (LOG 444), by instance name.
/// </summary>
public sealed record AgentEndpointServices(StepTokens Tokens, IRecordStoreProvider Records, IRunSignals Signals,
    IReadOnlyDictionary<string, ModelProxy> Proxies, Func<string, IAgentHooks?> Hooks, TimeSpan? LongPoll = null)
{
    /// <summary>The model proxy of the step's runtime instance, or null when it configures none.</summary>
    public ModelProxy? ProxyFor(StepClaims claims) => claims.Runtime is { } name ? Proxies.GetValueOrDefault(name) : null;
}

/// <summary>
/// The agent worker's endpoint (docs/02 section 4.2, RW17; LOG 68): the hook endpoint, the engine tools the
/// sandbox client forwards, and the model proxy. Every call carries the step's token; a replica keeps nothing in
/// memory, so any replica serves any step from the token and the record store.
/// </summary>
public static partial class AgentEndpoint
{
    /// <summary>The endpoint's JSON: the web defaults (camelCase), which the sandbox client reads.</summary>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

        app.MapGet("/agent/v1/plan", async (HttpContext http, string? target) =>
            await Authorised(http, s, async claims => Results.Json(await InspectPlanAsync(s, claims, target ?? Self, http.RequestAborted), Json)));

        app.MapPost("/agent/v1/patches", (Func<HttpContext, Task<IResult>>)(async http =>
            await Authorised(http, s, async claims =>
            {
                var input = await JsonDocument.ParseAsync(http.Request.Body, cancellationToken: http.RequestAborted);
                return Results.Json(await ProposePatchAsync(s, claims, input.RootElement, http.RequestAborted), Json);
            })));

        app.Map("/model/{**path}", async (HttpContext http, string? path) =>
        {
            if (s.Tokens.Check(ModelToken(http)) is not { } claims || !await ActiveAsync(s, claims))
            {
                http.Response.StatusCode = 401;
                await http.Response.WriteAsync("""{"type":"error","error":{"type":"authentication_error","message":"unknown step token"}}""");
                return;
            }

            // The step's runtime instance's own proxy (LOG 446); an instance with none is not served.
            if (s.ProxyFor(claims) is not { } proxy)
            {
                http.Response.StatusCode = 404;
                await http.Response.WriteAsync("""{"type":"error","error":{"type":"not_found_error","message":"the step's agent runtime has no model proxy"}}""");
                return;
            }

            var (status, input, output, model, unreachable) = await proxy.ForwardAsync(http, path ?? "", http.RequestAborted);
            await AppendAsync(s, new RecordBatch(AgentEvents: [new AgentEventRecord(claims.StepKey, "model",
                $"{http.Request.Method} /{path} {status} in={input} out={output} model={model ?? "none"}", DateTimeOffset.UtcNow)]));
            if (unreachable is not null)
                await ModelUnreachableAsync(s, claims, unreachable, CancellationToken.None);
        });
    }

    /// <summary>
    /// The model could not be reached for this step (LOG 326): recorded as an agent event and signalled to the run, which
    /// shows it on its timeline, once per step and cause. A runtime retries many times, so later failures with the same
    /// cause add nothing. The check reads the record store, so any replica serves it.
    /// </summary>
    public static async Task ModelUnreachableAsync(AgentEndpointServices s, StepClaims claims, string cause, CancellationToken cancellationToken)
    {
        var events = (await s.Records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = claims.StepKey }, cancellationToken)).Unwrap();
        if (events.Any(e => e.Kind == ModelUnreachableEvent && e.Detail == cause))
            return;
        await AppendAsync(s, new RecordBatch(AgentEvents: [new AgentEventRecord(claims.StepKey, ModelUnreachableEvent, cause, DateTimeOffset.UtcNow)]));
        await s.Signals.ModelUnreachableAsync(claims.WorkflowId, new ModelUnreachableSignal(claims.StepId, cause), cancellationToken);
    }

    public const string ModelUnreachableEvent = "model_unreachable";

    static string? Bearer(HttpContext http) =>
        http.Request.Headers.Authorization.FirstOrDefault() is { } value && value.StartsWith("Bearer ", StringComparison.Ordinal) ? value["Bearer ".Length..].Trim() : null;

    /// <summary>
    /// The step's token on a model request: a bearer token or <c>x-api-key</c>. Copilot CLI accepts only a GitHub-prefixed
    /// token, so its runtime sends <c>ghs_</c> and the step's token (LOG 496); one such prefix is dropped here, on the model
    /// route only.
    /// </summary>
    static string? ModelToken(HttpContext http)
    {
        var token = Bearer(http) ?? http.Request.Headers["x-api-key"].FirstOrDefault();
        return token?.StartsWith(EngineEndpoints.GitHubTokenPrefix, StringComparison.Ordinal) == true ? token[EngineEndpoints.GitHubTokenPrefix.Length..] : token;
    }

    static async Task<IResult> Authorised(HttpContext http, AgentEndpointServices s, Func<StepClaims, Task<IResult>> handle)
    {
        if (s.Tokens.Check(Bearer(http)) is not { } claims || !await ActiveAsync(s, claims))
            return Results.Json(new { error = "unknown or revoked step token" }, Json, statusCode: 401);
        try
        {
            return await handle(claims);
        }
        catch (EngineFaultException e)
        {
            // LOG 60: the client retries, then ends the session resumably; the agent is not told.
            return Results.Json(new { error = e.Message }, Json, statusCode: 503);
        }
    }

    /// <summary>A token works only while its step attempt is recorded and not ended: the activity revokes it at the end.</summary>
    public static async Task<bool> ActiveAsync(AgentEndpointServices s, StepClaims claims) =>
        (await s.Records.QueryAsync<AgentStepRecord>(Queries.AgentStep, new Dictionary<string, string> { ["step_key"] = claims.StepKey }, CancellationToken.None))
        .Unwrap().FirstOrDefault() is { Revoked: false };

    static async Task AppendAsync(AgentEndpointServices s, RecordBatch batch) => (await s.Records.AppendAsync(batch, CancellationToken.None)).Unwrap();

    public static async Task<HookDecision> HookAsync(AgentEndpointServices s, StepClaims claims, string hook, JsonElement input, CancellationToken cancellationToken)
    {
        // The step's runtime reads its CLI's hook input and words its refusal (LOG 444); the policy sees tool classes only.
        var hooks = claims.Runtime is { } runtime ? s.Hooks(runtime) : null;
        var call = hooks?.Classify(input) ?? new ToolCall(input.TryGetProperty("tool_name", out var t) ? t.GetString() ?? "" : "", null);
        var decision = hook != "pre_tool_use" ? HookDecision.Allow
            : hooks is null ? HookDecision.Deny($"the step's agent runtime '{claims.Runtime}' does not read hooks")
            : HookPolicy.PreToolUse(claims, call);
        if (decision.Decision == "deny" && hooks is not null)
            decision = decision with { Reply = hooks.Deny(decision.Reason!) };
        var detail = hook switch
        {
            "pre_tool_use" => $"{call.Tool} {decision.Decision}" + (decision.Reason is null ? "" : $": {decision.Reason}"),
            "post_tool_use" => HookPolicy.FileOpened(call) is { } file ? $"{call.Tool} {file}" : call.Tool,
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
        if (checkedReport is { } passed)
            (checkedReport, errors) = await WithStagedPatchAsync(s, claims, passed, cancellationToken);
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
    /// The body of <c>POST /agent/v1/patches</c> (<c>propose_patch</c>): the plan <see cref="Target"/> (<c>self</c> or
    /// <c>parent</c>), the revision the agent read (<see cref="BaseRevision"/>), where the run continues once it applies
    /// (<see cref="ResumeAt"/>, required for <c>self</c>), why, and the operations of docs/03 s10.2 as JSON; or
    /// <see cref="Withdraw"/> to clear the staged patch.
    /// </summary>
    public sealed record PatchRequest(
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("base_revision")] int BaseRevision,
        [property: JsonPropertyName("resume_at")] string? ResumeAt,
        [property: JsonPropertyName("reason")] string Reason,
        [property: JsonPropertyName("changes")] IReadOnlyList<JsonElement> Changes,
        [property: JsonPropertyName("withdraw")] bool Withdraw = false);

    /// <summary>
    /// The answer to <see cref="PatchRequest"/>: <see cref="Status"/> <c>staged</c> (sent with the next accepted report, its
    /// <see cref="Effect"/> <c>applies</c> or <c>needs_approval</c> with <see cref="Reasons"/>), <c>refused</c> (<see cref="Messages"/>:
    /// the checks' messages), <c>withdrawn</c>, <c>limit</c> (too many proposals or too large) or <c>unavailable</c>;
    /// <see cref="Revision"/> is the plan revision checked against, and <see cref="Message"/> the text for the agent.
    /// </summary>
    public sealed record PatchReply(
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("effect")] string? Effect,
        [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
        [property: JsonPropertyName("messages")] IReadOnlyList<string> Messages,
        [property: JsonPropertyName("revision")] int Revision,
        [property: JsonPropertyName("message")] string Message)
    {
        public const string Staged = "staged";
        public const string Refused = "refused";
        public const string Withdrawn = "withdrawn";
        public const string Limit = "limit";
        public const string Unavailable = "unavailable";
    }

    /// <summary>The agent event recording when a question opened; its detail is the question's id.</summary>
    public const string QuestionOpenedEvent = "question_opened";

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
        // The opening time is kept apart from the question, whose record the run replaces when it closes: the step's
        // deadline is extended by the time its questions were open (LOG 106).
        var now = DateTimeOffset.UtcNow;
        await AppendAsync(s, new RecordBatch(Questions: [new QuestionRecord(claims.StepKey, claims.RunId, id, text, options, QuestionRecord.Open, null, now)],
            AgentEvents: [new AgentEventRecord(claims.StepKey, QuestionOpenedEvent, id, now)]));
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
