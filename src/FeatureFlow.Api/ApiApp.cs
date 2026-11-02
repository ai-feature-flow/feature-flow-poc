using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FeatureFlow.Api;

/// <summary>What the API service uses: providers, configuration and the Temporal client. It keeps no state of its own (docs/02 section 1).</summary>
public sealed record ApiServices(ProviderSet Providers, InstallationConfig Installation, OrganisationConfig Organisation, ITemporalClient Client);

/// <summary>The authenticated caller: the actor and roles come from the credential, never from the request body (F8).</summary>
public sealed record Caller(string Actor, IReadOnlyList<string> Roles);

/// <summary>
/// The API service (docs/02 sections 1 and 7), as far as M3 needs it: start, query, answer and abort, behind a
/// local token (the solo profile). Queries read the record store, never Temporal (docs/02 section 5). An answer
/// is authorised, recorded with the authenticated actor, then delivered as a Temporal Update (docs/04 section 4.1).
/// </summary>
public static class ApiApp
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static WebApplication Build(ApiServices services, params string[] urls)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(urls.Length == 0 ? ["http://127.0.0.1:8480"] : urls);
        builder.Logging.ClearProviders();
        var app = builder.Build();
        var api = new Handlers(services);

        app.MapPost("/runs", (Delegate)((HttpContext c) => api.Guard(c, caller => api.StartAsync(c, caller))));
        app.MapGet("/runs", (Delegate)((HttpContext c) => api.Guard(c, _ => api.ListAsync(c))));
        app.MapGet("/runs/{id}", (HttpContext c, string id) => api.Guard(c, _ => api.ShowAsync(c, id)));
        app.MapPost("/runs/{id}/answers", (HttpContext c, string id) => api.Guard(c, caller => api.AnswerAsync(c, caller, id)));
        app.MapPost("/runs/{id}/abort", (HttpContext c, string id) => api.Guard(c, caller => api.AbortAsync(c, caller, id)));
        return app;
    }

    sealed class Handlers(ApiServices services)
    {
        IRecordStoreProvider Records => services.Providers.Required<IRecordStoreProvider>("record_store");

        IArtifactStoreProvider Artifacts => services.Providers.Required<IArtifactStoreProvider>("artifact_store");

        /// <summary>Authenticates the request by its bearer token, compared in constant time with the configured secret.</summary>
        public async Task<IResult> Guard(HttpContext context, Func<Caller, Task<IResult>> handle)
        {
            var auth = services.Installation.ApiAuth;
            var header = context.Request.Headers.Authorization.ToString();
            var given = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
            var secrets = services.Providers.Required<ISecretsProvider>("secrets");
            var expected = auth.TokenRef is null ? null : (await secrets.ResolveAsync(auth.TokenRef, context.RequestAborted)).Value;
            if (expected is null || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected)))
                return Results.Json(new { error = "unauthenticated" }, Json, statusCode: 401);
            try
            {
                return await handle(new Caller(auth.Actor, auth.Roles));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                return Error(500, $"{e.GetType().Name}: {e.Message}");
            }
        }

        public async Task<IResult> StartAsync(HttpContext context, Caller caller)
        {
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
            var template = body?["template"]?.GetValue<string>();
            if (template is null || LibraryReference.TryParse(template) is null)
                return Error(400, "template must be a template reference, such as std:templates/ticket_delivery");
            var repositories = body!["repositories"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
            var parameters = (body["parameters"] as JsonObject ?? []).ToDictionary(kv => kv.Key, kv => kv.Value?.ToJsonString() ?? "null");
            var dryRun = body["dry_run"]?.GetValue<bool>() ?? false;
            var handOver = body["continue_as_new_events"]?.GetValue<long>() ?? services.Installation.ContinueAsNewEvents;

            var lockRef = await WriteLockAsync(context.RequestAborted);
            var runId = "run-" + Guid.NewGuid().ToString("N")[..12];
            var now = DateTimeOffset.UtcNow;
            await Records.AppendAsync(new RecordBatch(
                [new RunRecord(runId, null, template, "starting", caller.Actor, repositories, dryRun, now, null)],
                Locks: [new LockRecord(runId, lockRef, now)]), context.RequestAborted);
            var start = new RunStart(runId, template, repositories, parameters, lockRef, dryRun, caller.Actor, HandOverEvents: handOver);
            await services.Client.StartWorkflowAsync(RunWorkflowNames.Workflow, [start],
                new WorkflowOptions(runId, services.Installation.TaskQueue));
            return Results.Json(new { run_id = runId }, Json, statusCode: 201);
        }

        /// <summary>
        /// The run's lock (docs/08 sections 3 and 5.1): every content file of the organisation's libraries stored
        /// by content hash, and the lock that lists them stored as an artifact itself.
        /// </summary>
        async Task<string> WriteLockAsync(CancellationToken ct)
        {
            var locked = new List<LockedLibrary>();
            foreach (var source in services.Organisation.Libraries)
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(services.Organisation.Location))!, source.Path));
                var d = new DiagnosticBag();
                var library = Library.Library.Open(path, d) ?? throw new InvalidOperationException($"library {source.Name} at {path} does not open: {string.Join("; ", d)}");
                foreach (var (_, text) in library.Files.All)
                    (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), ct)).Unwrap();
                locked.Add(LibraryLock.Pin(library));
            }

            var lockText = JsonSerializer.Serialize(new LibraryLock(locked), Json);
            return (await Artifacts.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), ct)).Unwrap().Id;
        }

        public async Task<IResult> ListAsync(HttpContext context)
        {
            var runs = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string>(), context.RequestAborted)).Unwrap();
            return Results.Json(new { runs }, Json);
        }

        public async Task<IResult> ShowAsync(HttpContext context, string id)
        {
            var p = new Dictionary<string, string> { ["run_id"] = id };
            var run = (await Records.QueryAsync<RunRecord>(Queries.Run, p, context.RequestAborted)).Unwrap().FirstOrDefault();
            if (run is null)
                return Error(404, $"no run {id}");
            var waits = (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string>(p) { ["open"] = "true" }, context.RequestAborted)).Unwrap();
            var after = context.Request.Query["after"].FirstOrDefault() ?? "0";
            var timeline = (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string>(p) { ["after"] = after }, context.RequestAborted)).Unwrap();
            var children = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string> { ["parent_run_id"] = id }, context.RequestAborted)).Unwrap();
            return Results.Json(new { run, waits, timeline, children }, Json);
        }

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
                body["clean_up"]?.GetValue<bool>() ?? false, body["attempts"]?.GetValue<long>() ?? 0, body["status"]?.GetValue<string>());
            var answerText = JsonSerializer.Serialize(args, Json);
            await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, waitId, caller.Actor, answerText, "sent", DateTimeOffset.UtcNow)]), context.RequestAborted);
            try
            {
                var reply = await services.Client.GetWorkflowHandle(id).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Answer, [args]);
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, waitId, caller.Actor, answerText, reply.Result, DateTimeOffset.UtcNow)]), context.RequestAborted);
                return Results.Json(new { result = reply.Result }, Json);
            }
            catch (WorkflowUpdateFailedException e)
            {
                var message = e.InnerException?.Message ?? e.Message;
                await Records.AppendAsync(new RecordBatch(Answers: [new AnswerRecord(id, waitId, caller.Actor, answerText, "refused: " + message, DateTimeOffset.UtcNow)]), context.RequestAborted);
                return Error(409, message);
            }
        }

        /// <summary>
        /// Who may answer (docs/05 sections 3.2 and 5): a decision by the first approval rule matching its type;
        /// an intervention and a patch approval, a steerer, unless a rule matches their type.
        /// </summary>
        IReadOnlyList<string> RequiredRoles(WaitRecord wait)
        {
            if (services.Organisation.Policy.RuleFor(wait.DecisionType) is { Roles.Count: > 0 } rule)
                return rule.Roles;
            return wait.Kind == "decision" ? ["approver"] : ["steerer"];
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
            catch (WorkflowUpdateFailedException e)
            {
                return Error(409, e.InnerException?.Message ?? e.Message);
            }
        }

        static IResult Error(int status, string message) => Results.Json(new { error = message }, Json, statusCode: status);
    }
}
