using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>Decisions: answers to waits, and aborting a run.</summary>
    static void MapDecisions(WebApplication app, Handlers api)
    {
        app.MapPost("/runs/{id}/answers", (HttpContext c, string id) => api.Guard(c, caller => api.AnswerAsync(c, caller, id)));
        app.MapPost("/runs/{id}/abort", (HttpContext c, string id) => api.Guard(c, caller => api.AbortAsync(c, caller, id)));
    }

    sealed partial class Handlers
    {
        public async Task<IResult> AnswerAsync(HttpContext context, Caller caller, string id)
        {
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
            var waitId = body?["wait_id"]?.GetValue<string>();
            if (waitId is null)
                return Error(400, "wait_id is required");
            var wait = (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string> { ["run_id"] = id }, context.RequestAborted)).Unwrap()
                .FirstOrDefault(w => w.WaitId == waitId);
            if (wait is null || wait.Kind == "")
                return Error(404, $"no wait {waitId} in run {id}");

            var required = RequiredRoles(wait);
            if (!required.Intersect(caller.Roles).Any())
                return Error(403, $"{caller.Actor} may not answer {wait.DecisionType}: it needs one of the roles {string.Join(", ", required)}");

            var outcome = body!["outcome"]?.GetValue<string>();
            var action = body["action"]?.GetValue<string>();
            if (wait.Kind == "patchapproval" && action is null && outcome is "approve" or "reject")
                (action, outcome) = (outcome, null);
            var args = new AnswerArgs(waitId, caller.Actor, outcome, action, body["step_id"]?.GetValue<string>(),
                body["clean_up"]?.GetValue<bool>() ?? false, body["attempts"]?.GetValue<long>() ?? 0, body["status"]?.GetValue<string>(),
                BeyondHardCeiling: caller.Roles.Contains("admin"));
            var answerText = JsonSerializer.Serialize(args, Json);
            await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, waitId, caller.Actor, answerText, "sent", DateTimeOffset.UtcNow)]), context.RequestAborted);
            try
            {
                var reply = await services.Client.GetWorkflowHandle(id).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Answer, [args]);
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, waitId, caller.Actor, answerText, reply.Result, DateTimeOffset.UtcNow)]), context.RequestAborted);
                return Results.Json(new { result = reply.Result }, Json);
            }
            catch (Exception e) when (e is WorkflowUpdateFailedException or RpcException)
            {
                var message = e is RpcException ? $"run {id} has ended or cannot take updates: {e.Message}" : e.InnerException?.Message ?? e.Message;
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, waitId, caller.Actor, answerText, "refused: " + message, DateTimeOffset.UtcNow)]), context.RequestAborted);
                return Error(409, message);
            }
        }

        IReadOnlyList<string> RequiredRoles(WaitRecord wait) => RolesToAnswer(services.Organisation.Policy, DecisionTypes(), wait);

        /// <summary>The organisation's libraries' decision types, read now: approval is checked at the moment of use (docs/05 section 8).</summary>
        List<DecisionTypeItem> DecisionTypes()
        {
            var types = new List<DecisionTypeItem>();
            foreach (var source in services.Organisation.Libraries)
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(services.Organisation.Location))!, source.Path));
                if (Library.Library.Open(path, new DiagnosticBag()) is { } library)
                    types.AddRange(library.DecisionTypes);
            }

            return types;
        }

        public async Task<IResult> AbortAsync(HttpContext context, Caller caller, string id)
        {
            if (!caller.Roles.Contains("steerer") && !caller.Roles.Contains("admin"))
                return Error(403, $"{caller.Actor} may not abort a run: it needs the steerer role");
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
            var args = new AbortArgs(caller.Actor, body?["reason"]?.GetValue<string>() ?? "no reason given");
            await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, "abort", caller.Actor, args.Reason, "sent", DateTimeOffset.UtcNow)]), context.RequestAborted);
            try
            {
                var reply = await services.Client.GetWorkflowHandle(id).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Abort, [args]);
                return Results.Json(new { result = reply.Result }, Json);
            }
            catch (Exception e) when (e is WorkflowUpdateFailedException or RpcException)
            {
                return Error(409, e is RpcException ? $"run {id} has ended or cannot take updates: {e.Message}" : e.InnerException?.Message ?? e.Message);
            }
        }
    }
}
