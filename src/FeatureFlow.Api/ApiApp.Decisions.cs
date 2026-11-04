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
        app.MapGet("/runs/{id}/findings", (HttpContext c, string id) => api.Guard(c, _ => api.FindingsAsync(c, id)));
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
            if (body["artifacts"] is { } given)
            {
                var (stored, problem) = await StoreAnswerArtifactsAsync(given, context.RequestAborted);
                if (problem is not null)
                    return Error(400, problem);
                args = args with { Artifacts = stored };
            }

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

        /// <summary>The most an answer may carry: a person's triage or another small document, not a repository's files.</summary>
        const int AnswerArtifacts = 16, AnswerArtifactBytes = 1 << 20;

        /// <summary>
        /// What a person produced in answering, such as a triage of findings (LOG 202, 248): each artifact by declared name,
        /// given as text or as a JSON value, stored now so the answer carries references. The decision's outcome must promise
        /// each name (the run refuses others); its content is checked by the step that reads it.
        /// </summary>
        async Task<(IReadOnlyDictionary<string, string>? Stored, string? Problem)> StoreAnswerArtifactsAsync(JsonNode given, CancellationToken ct)
        {
            if (given is not JsonObject artifacts || artifacts.Count > AnswerArtifacts)
                return (null, $"artifacts is a map of at most {AnswerArtifacts} artifact names to their content");
            var stored = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, value) in artifacts)
            {
                var text = value switch
                {
                    JsonValue v when v.TryGetValue<string>(out var s) => s,
                    JsonObject or JsonArray => value.ToJsonString(),
                    _ => null,
                };
                if (text is null || text.Trim().Length == 0)
                    return (null, $"artifact {name} must be given as text or a JSON map or list");
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                if (bytes.Length > AnswerArtifactBytes)
                    return (null, $"artifact {name} is {bytes.Length} bytes; an answer's artifacts are at most {AnswerArtifactBytes}");
                stored[name] = (await Artifacts.PutAsync(new MemoryStream(bytes), ct)).Unwrap().Id;
            }

            return (stored, null);
        }

        /// <summary>The run's findings ledger, as a person triaging it needs it: every finding of the run and its children, with its ledger id and state.</summary>
        public async Task<IResult> FindingsAsync(HttpContext context, string id)
        {
            var ledger = (await Records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = id }, context.RequestAborted)).Unwrap();
            return Results.Text(ArtifactShapes.WriteLibrary(FindingsLedger.View(ledger)), "application/json");
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
