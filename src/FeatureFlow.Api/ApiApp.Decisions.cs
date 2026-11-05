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
            if (body["artifacts"] is { } given)
            {
                // What the person produced, such as a triage of findings (LOG 248), stored and sent by reference on the one answer path.
                var (stored, problem) = await StoreAnswerArtifactsAsync(given, context.RequestAborted);
                if (problem is not null)
                    return Error(400, problem);
                args = args with { Artifacts = stored };
            }

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
            var types = await DecisionTypesAsync(ct);
            var rule = Approval.For(wait.DecisionType, services.Organisation.Policy, TeamOf(scope), types, wait.Kind);
            var intervention = wait.Kind == "intervention";
            var grant = intervention && args.Action == "grant_attempts";
            // A recovery needs recover, and a grant grant_attempts as well (coordinator decision C7).
            var needed = intervention ? RolesForKey(Overrides.Recover) : RolesToAnswer(services.Organisation.Policy, types, wait, TeamOf(scope));
            if (Refuse(caller, scope, needed, intervention ? $"recover at {wait.DecisionType} (recover)" : $"answer {wait.DecisionType} (approval: {rule.Source})") is { } refused)
                return refused;
            if (grant && Refuse(caller, scope, RolesForKey(Overrides.GrantAttempts), $"grant attempts at {wait.DecisionType} (grant_attempts)") is { } refusedGrant)
                return refusedGrant;
            if (caller.FromChat && !rule.Rule.FromChat)
                return Error(403, $"{wait.DecisionType} may not be answered from chat ({rule.Source}); answer it in the console or the CLI");

            args = args with
            {
                Actor = caller.Actor,
                BeyondHardCeiling = grant && VariantsHeld(caller, scope, Overrides.GrantAttempts).Contains(Overrides.GrantAttemptsBeyond),
            };
            var minCount = intervention ? 1 : Math.Max(1, rule.Rule.MinCount);
            // Beside OIDC, a credential with no identity-provider groups (the shared local token) is not a person who can be
            // counted among several (LOG 221).
            if (minCount > 1 && caller.Groups is null && services.Installation.ApiAuth.Mode == "oidc")
                return Error(403, $"{wait.DecisionType} needs {minCount} people ({rule.Source}); answer it as yourself through the identity provider, not with the local token");
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

        /// <summary>The organisation's libraries' decision types, read now: approval is checked at the moment of use (docs/05 section 8).</summary>
        async Task<List<DecisionTypeItem>> DecisionTypesAsync(CancellationToken ct)
        {
            var types = new List<DecisionTypeItem>();
            foreach (var source in services.Organisation.Libraries)
                if ((await LibrarySources.OpenAsync(source, services.Organisation.Location, ct)).Library is { } library)
                    types.AddRange(library.DecisionTypes);
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
