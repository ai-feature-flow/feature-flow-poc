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

    /// <summary>The result an answer is recorded with while it waits for more people to answer the same way (<c>min_count</c>).</summary>
    const string Pending = "pending: ";

    sealed partial class Handlers
    {
        public async Task<IResult> AnswerAsync(HttpContext context, Caller caller, string id)
        {
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
            var waitId = body?["wait_id"]?.GetValue<string>();
            if (waitId is null)
                return Error(400, "wait_id is required");
            var run = await RunAsync(id, context.RequestAborted);
            var wait = run is null ? null : (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string> { ["run_id"] = id }, context.RequestAborted)).Unwrap()
                .FirstOrDefault(w => w.WaitId == waitId);
            if (run is null || wait is null || wait.Kind == "")
                return Error(404, $"no wait {waitId} in run {id}");

            var outcome = body!["outcome"]?.GetValue<string>();
            var action = body["action"]?.GetValue<string>();
            if (wait.Kind == "patchapproval" && action is null && outcome is "approve" or "reject")
                (action, outcome) = (outcome, null);
            // F8: the actor is the authenticated caller; an actor or roles in the body are ignored.
            var args = new AnswerArgs(waitId, caller.Actor, outcome, action, body["step_id"]?.GetValue<string>(),
                body["clean_up"]?.GetValue<bool>() ?? false, body["attempts"]?.GetValue<long>() ?? 0, body["status"]?.GetValue<string>());
            return await SubmitAnswerAsync(context, caller, run, wait, args);
        }

        /// <summary>
        /// Every way a person answers a wait goes through here (docs/04 section 4.1), so approval applies to all of them:
        /// the approval rule's roles in the run's scope, or for an intervention the <c>recover</c> override's (and
        /// <c>grant_attempts</c>'s for a grant); <c>from_chat</c>; and <c>min_count</c>, distinct people answering the same
        /// way before the answer is delivered, each recorded as they answer. The answer is recorded with the authenticated
        /// actor, then delivered as the <c>answer</c> update.
        /// </summary>
        public async Task<IResult> SubmitAnswerAsync(HttpContext context, Caller caller, RunRecord run, WaitRecord wait, AnswerArgs args)
        {
            var ct = context.RequestAborted;
            var scope = ScopeOf(run.Repositories);
            var rule = Approval.For(wait.DecisionType, services.Organisation.Policy, TeamOf(scope), DecisionTypes(), wait.Kind);
            var intervention = wait.Kind == "intervention";
            var key = intervention ? args.Action == "grant_attempts" ? Overrides.GrantAttempts : Overrides.Recover : null;
            var needed = key is not null ? RolesForKey(key) : RolesToAnswer(services.Organisation.Policy, DecisionTypes(), wait, TeamOf(scope));
            if (Refuse(caller, scope, needed, intervention ? $"recover at {wait.DecisionType} ({key})" : $"answer {wait.DecisionType} (approval: {rule.Source})") is { } refused)
                return refused;
            if (caller.FromChat && !rule.Rule.FromChat)
                return Error(403, $"{wait.DecisionType} may not be answered from chat ({rule.Source}); answer it in the console or the CLI");

            args = args with
            {
                Actor = caller.Actor,
                BeyondHardCeiling = key == Overrides.GrantAttempts && VariantsHeld(caller, scope, Overrides.GrantAttempts).Contains(Overrides.GrantAttemptsBeyond),
            };
            var minCount = intervention ? 1 : Math.Max(1, rule.Rule.MinCount);
            if (minCount > 1)
            {
                var earlier = (await Records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = run.RunId }, ct)).Unwrap()
                    .Where(a => a.WaitId == wait.WaitId && a.Result.StartsWith(Pending, StringComparison.Ordinal) && SameWay(a.Answer, args))
                    .Select(a => a.Actor);
                var people = earlier.Append(caller.Actor).Distinct(StringComparer.Ordinal).ToList();
                if (people.Count < minCount)
                {
                    var progress = $"{people.Count} of {minCount} people have answered this way ({rule.Source})";
                    await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(run.RunId, wait.WaitId, caller.Actor, JsonSerializer.Serialize(args, Json),
                        Pending + progress, DateTimeOffset.UtcNow)]), ct);
                    return Results.Json(new { result = "recorded: " + progress }, Json, statusCode: 202);
                }

                // Every person who answered is the actor of the delivered answer.
                args = args with { Actor = string.Join(", ", people) };
            }

            return await DeliverAnswerAsync(run.RunId, wait.WaitId, caller.Actor, args, ct);
        }

        /// <summary>Whether a recorded answer is the same answer as <paramref name="args"/>, whoever gave it.</summary>
        static bool SameWay(string recorded, AnswerArgs args)
        {
            try
            {
                return JsonSerializer.Deserialize<AnswerArgs>(recorded, Json) is { } other
                    && JsonSerializer.Serialize(other with { Actor = "" }, Json) == JsonSerializer.Serialize(args with { Actor = "" }, Json);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        async Task<IResult> DeliverAnswerAsync(string runId, string waitId, string recordedActor, AnswerArgs args, CancellationToken ct)
        {
            var answerText = JsonSerializer.Serialize(args, Json);
            await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(runId, waitId, recordedActor, answerText, "sent", DateTimeOffset.UtcNow)]), ct);
            try
            {
                var reply = await services.Client.GetWorkflowHandle(runId).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Answer, [args]);
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(runId, waitId, recordedActor, answerText, reply.Result, DateTimeOffset.UtcNow)]), ct);
                return Results.Json(new { result = reply.Result }, Json);
            }
            catch (Exception e) when (e is WorkflowUpdateFailedException or RpcException)
            {
                var message = e is RpcException ? $"run {runId} has ended or cannot take updates: {e.Message}" : e.InnerException?.Message ?? e.Message;
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(runId, waitId, recordedActor, answerText, "refused: " + message, DateTimeOffset.UtcNow)]), ct);
                return Error(409, message);
            }
        }

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
            var run = await RunAsync(id, context.RequestAborted);
            var scope = ScopeOf(run?.Repositories ?? []);
            if (Refuse(caller, scope, RolesForKey(Overrides.Abort), "abort a run") is { } refused)
                return refused;
            if (run is null)
                return Error(404, $"no run {id}");
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
            var args = new AbortArgs(caller.Actor, body?["reason"]?.GetValue<string>() ?? "no reason given", body?["close_pull_requests"]?.GetValue<bool>() ?? false);
            var text = args.ClosePullRequests ? args.Reason + " (close pull requests)" : args.Reason;
            await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, "abort", caller.Actor, text, "sent", DateTimeOffset.UtcNow)]), context.RequestAborted);
            try
            {
                var reply = await services.Client.GetWorkflowHandle(id).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Abort, [args]);
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, "abort", caller.Actor, text, reply.Result, DateTimeOffset.UtcNow)]), context.RequestAborted);
                return Results.Json(new { result = reply.Result }, Json);
            }
            catch (Exception e) when (e is WorkflowUpdateFailedException or RpcException)
            {
                var message = e is RpcException ? $"run {id} has ended or cannot take updates: {e.Message}" : e.InnerException?.Message ?? e.Message;
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, "abort", caller.Actor, text, "refused: " + message, DateTimeOffset.UtcNow)]), context.RequestAborted);
                return Error(409, message);
            }
        }
    }
}
