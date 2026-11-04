using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>
    /// Run-time overrides other than answers and aborts (docs/05 section 5), delivered as the workflow's override update:
    /// <c>POST /runs/{id}/overrides</c> with <c>key</c>, <c>step_id</c>, <c>reason</c> and <c>change</c> (the key's arguments
    /// by name, as <see cref="OverrideArgs"/> lists them). The API authorises the key in the run's scope, passes the variants
    /// the caller also holds, and records the override with the authenticated actor before and after delivery (LOG 203).
    /// </summary>
    static void MapOverrides(WebApplication app, Handlers api)
    {
        app.MapPost("/runs/{id}/overrides", (HttpContext c, string id) => api.Guard(c, caller => api.OverrideAsync(c, caller, id)));
    }

    /// <summary>The keys that have their own route: answers (and recovery at an intervention) and aborts.</summary>
    static readonly IReadOnlyDictionary<string, string> OtherRoutes = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [Overrides.Answer] = "answer a decision with POST /runs/{id}/answers",
        [Overrides.AnswerQuestion] = "answer an agent's question with POST /runs/{id}/answers",
        [Overrides.Recover] = "recover at an intervention with POST /runs/{id}/answers",
        [Overrides.Abort] = "abort a run with POST /runs/{id}/abort",
    };

    sealed partial class Handlers
    {
        public async Task<IResult> OverrideAsync(HttpContext context, Caller caller, string id)
        {
            var ct = context.RequestAborted;
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: ct) as JsonObject;
            var key = body?["key"]?.GetValue<string>();
            if (key is null || !Overrides.IsKey(key) || Overrides.Variants.Values.Any(v => v.Contains(key)))
                return Error(400, $"key must be one of {string.Join(", ", Overrides.DefaultRoles.Keys.Where(k => !OtherRoutes.ContainsKey(k) && !Overrides.Variants.Values.Any(v => v.Contains(k))))}" +
                                  " (a protected or beyond-ceiling variant is not asked for: the run applies it where the caller holds it)");
            if (OtherRoutes.TryGetValue(key, out var route))
                return Error(400, $"{key} is not made here: {route}");
            var reason = body!["reason"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(reason))
                return Error(400, "reason is required: every override is recorded with its reason (docs/05 section 5)");
            var change = new Dictionary<string, string>(StringComparer.Ordinal);
            if (body["change"] is JsonObject given)
                foreach (var (name, value) in given)
                    change[name] = value is JsonValue v && v.TryGetValue<string>(out var text) ? text : value?.ToJsonString() ?? "null";
            else if (body["change"] is not null)
                return Error(400, "change must be an object of the override's arguments by name");

            var run = await RunAsync(id, ct);
            var scope = ScopeOf(run?.Repositories ?? []);
            if (Refuse(caller, scope, RolesForKey(key), $"use the {key} override") is { } refused)
                return refused;
            if (run is null)
                return Error(404, $"no run {id}");

            // F8: the actor is the authenticated caller; an actor in the body is ignored.
            var args = new OverrideArgs("override-" + Guid.NewGuid().ToString("N")[..12], key, caller.Actor, reason, body["step_id"]?.GetValue<string>(), change,
                VariantsHeld(caller, scope, key));
            OverrideRecord Record(string result) => new(id, args.OverrideId, key, caller.Actor, reason, args.StepId, change, result, DateTimeOffset.UtcNow);
            await Records.AppendAsync(new RecordBatch(Overrides: [Record("sent")]), ct);
            try
            {
                var reply = await services.Client.GetWorkflowHandle(id).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Override, [args]);
                await Records.AppendAsync(new RecordBatch(Overrides: [Record(reply.Result)]), ct);
                return Results.Json(new { override_id = args.OverrideId, result = reply.Result }, Json);
            }
            catch (Exception e) when (e is WorkflowUpdateFailedException or RpcException)
            {
                var message = e is RpcException ? $"run {id} has ended or cannot take updates: {e.Message}" : e.InnerException?.Message ?? e.Message;
                await Records.AppendAsync(new RecordBatch(Overrides: [Record("refused: " + message)]), ct);
                return Error(409, message);
            }
        }
    }
}
