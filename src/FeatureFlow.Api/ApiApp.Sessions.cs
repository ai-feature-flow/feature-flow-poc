using System.Text;
using System.Text.Json;
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
    /// Interactive sessions (docs/04 section 4.4): fetch, pre-submit checks, submit and abandon, as the authenticated person
    /// (F8). Reading a session needs <c>viewer</c> in its run's scope, as reading the run does; acting on one needs what
    /// answering its decision needs: the approval rule's roles in the run's scope and <c>from_chat</c>, read now (docs/05
    /// sections 3 and 8). A submission and an abandonment are answers and go through the one answer path
    /// (<c>SubmitAnswerAsync</c>, with <c>min_count</c>); a check is the run's <c>session_check</c> update.
    /// </summary>
    static void MapSessions(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/sessions", (HttpContext c, string id) => api.Guard(c, caller => api.SessionsAsync(c, caller, id)));
        app.MapGet("/sessions/{sid}", (HttpContext c, string sid) => api.Guard(c, caller => api.FetchSessionAsync(c, caller, sid)));
        app.MapGet("/sessions/{sid}/checks", (HttpContext c, string sid) => api.Guard(c, caller => api.SessionChecksAsync(c, caller, sid)));
        app.MapPost("/sessions/{sid}/checks", (HttpContext c, string sid) => api.Guard(c, caller => api.CheckSessionAsync(c, caller, sid)));
        app.MapPost("/sessions/{sid}/submit", (HttpContext c, string sid) => api.Guard(c, caller => api.SubmitSessionAsync(c, caller, sid)));
        app.MapPost("/sessions/{sid}/abandon", (HttpContext c, string sid) => api.Guard(c, caller => api.AbandonSessionAsync(c, caller, sid)));
    }

    sealed partial class Handlers
    {
        public async Task<IResult> SessionsAsync(HttpContext context, Caller caller, string id)
        {
            if (!await MayViewAsync(caller, id, context.RequestAborted))
                return Error(404, $"no run {id}");
            var sessions = (await Records.QueryAsync<SessionRecord>(Queries.Sessions, new Dictionary<string, string> { ["run_id"] = id }, context.RequestAborted)).Unwrap();
            return Results.Json(new { sessions }, Json);
        }

        /// <summary>The session's record and workspace document, with the drafts' and the context's content inline.</summary>
        public async Task<IResult> FetchSessionAsync(HttpContext context, Caller caller, string sid)
        {
            var ct = context.RequestAborted;
            if (await SessionAsync(sid, ct) is not { } session || !await MayViewAsync(caller, session.RunId, ct))
                return Error(404, $"no session {sid}");
            if (await DocumentAsync(sid, ct) is not { } document)
                return Error(404, $"session {sid} has no workspace yet: see the run's timeline");
            var drafts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in document.Files.Where(f => f.SeedRef is not null))
                drafts[file.Path] = await TextAsync(file.SeedRef!, ct);
            var consumed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (name, reference) in document.Context)
                consumed[name] = await TextAsync(reference, ct);
            return Results.Json(new { session, workspace = document, drafts, context = consumed }, Json);
        }

        /// <summary>A session's checks, each with its result's content and what the run's timeline says of it.</summary>
        public async Task<IResult> SessionChecksAsync(HttpContext context, Caller caller, string sid)
        {
            var ct = context.RequestAborted;
            if (await SessionAsync(sid, ct) is not { } session || !await MayViewAsync(caller, session.RunId, ct))
                return Error(404, $"no session {sid}");
            var records = (await Records.QueryAsync<SessionCheckRecord>(Queries.SessionChecks, new Dictionary<string, string> { ["session_id"] = sid }, ct)).Unwrap();
            var timeline = (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string> { ["run_id"] = session.RunId, ["after"] = "0" }, ct)).Unwrap()
                .Where(t => t.Kind == "session_check" && t.Detail?.StartsWith(sid + " ", StringComparison.Ordinal) == true).ToList();
            var checks = new List<object>();
            foreach (var check in records)
                checks.Add(new
                {
                    check,
                    detail = timeline.LastOrDefault(t => t.Detail?.StartsWith($"{sid} {check.CheckId} ", StringComparison.Ordinal) == true)?.Detail,
                    result = check.ResultRef is { } reference ? await TextAsync(reference, ct) : null,
                });
            return Results.Json(new { checks }, Json);
        }

        public async Task<IResult> CheckSessionAsync(HttpContext context, Caller caller, string sid)
        {
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
            var step = body?["step"]?.GetValue<string>();
            var revision = body?["revision"]?.GetValue<string>();
            if (step is null || !SessionNames.IsCommit(revision))
                return Error(400, "a check needs step (a step the session's checks_from names) and revision (a full commit id)");
            var (session, _, _, refused) = await ActableAsync(caller, sid, context.RequestAborted);
            if (refused is not null)
                return refused;
            var args = new SessionCheckArgs("check-" + Guid.NewGuid().ToString("N")[..10], sid, step, revision!, caller.Actor);
            try
            {
                var reply = await services.Client.GetWorkflowHandle(session!.RunId).ExecuteUpdateAsync<AnswerReply>(SessionNames.Check, [args]);
                return Results.Json(new { check_id = args.CheckId, result = reply.Result }, Json);
            }
            catch (Exception e) when (e is WorkflowUpdateFailedException or RpcException)
            {
                return Error(409, e is RpcException ? $"run {session!.RunId} has ended or cannot take updates: {e.Message}" : e.InnerException?.Message ?? e.Message);
            }
        }

        public async Task<IResult> SubmitSessionAsync(HttpContext context, Caller caller, string sid)
        {
            var body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted) as JsonObject;
            var revision = body?["revision"]?.GetValue<string>();
            if (!SessionNames.IsCommit(revision))
                return Error(400, "a submission needs revision: the full id of the commit to submit, pushed to the session's branch");
            var (session, run, wait, refused) = await ActableAsync(caller, sid, context.RequestAborted);
            if (refused is not null)
                return refused;
            var document = await DocumentAsync(sid, context.RequestAborted);
            if (document is null)
                return Error(409, $"session {sid} has no workspace: see the run's timeline");
            return await SubmitAnswerAsync(context, caller, run!, wait!, new AnswerArgs(session!.WaitId, caller.Actor, document.SubmitOutcome, null, Revision: revision));
        }

        public async Task<IResult> AbandonSessionAsync(HttpContext context, Caller caller, string sid)
        {
            var (session, run, wait, refused) = await ActableAsync(caller, sid, context.RequestAborted);
            if (refused is not null)
                return refused;
            if ((await DocumentAsync(sid, context.RequestAborted))?.AbandonOutcome is not { } outcome)
                return Error(409, $"session {sid}'s decision declares no outcome to abandon it with; answer it with ff run answer");
            return await SubmitAnswerAsync(context, caller, run!, wait!, new AnswerArgs(session!.WaitId, caller.Actor, outcome, null));
        }

        /// <summary>
        /// The session, its run and its decision's wait, if <paramref name="caller"/> may act on it now: the decision is open, and
        /// the caller may answer it as the approval rule says (roles in the run's scope, <c>from_chat</c>), as an answer would.
        /// </summary>
        async Task<(SessionRecord? Session, RunRecord? Run, WaitRecord? Wait, IResult? Refused)> ActableAsync(Caller caller, string sid, CancellationToken ct)
        {
            if (await SessionAsync(sid, ct) is not { } session || await RunAsync(session.RunId, ct) is not { } run)
                return (null, null, null, Error(404, $"no session {sid}"));
            var scope = ScopeOf(run.Repositories);
            if (!RolesIn(caller, scope).Contains(PeopleRoles.Viewer))
                return (null, null, null, Error(404, $"no session {sid}"));
            var wait = (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string> { ["run_id"] = session.RunId }, ct)).Unwrap()
                .FirstOrDefault(w => w.WaitId == session.WaitId);
            if (session.Status != SessionStatuses.Prepared || wait is not { Open: true })
                return (null, null, null, Error(409, $"session {sid} is {(session.Status == SessionStatuses.Prepared ? "no longer open" : session.Status)}"));
            var types = await DecisionTypesAsync(ct);
            var rule = Approval.For(wait.DecisionType, services.Organisation.Policy, TeamOf(scope), types, wait.Kind);
            if (Refuse(caller, scope, RolesToAnswer(services.Organisation.Policy, types, wait, TeamOf(scope)),
                    $"act on session {sid} ({wait.DecisionType}; approval: {rule.Source})") is { } refused)
                return (null, null, null, refused);
            if (caller.FromChat && !rule.Rule.FromChat)
                return (null, null, null, Error(403, $"{wait.DecisionType} may not be answered from chat ({rule.Source}); act on session {sid} in the CLI"));
            return (session, run, wait, null);
        }

        async Task<bool> MayViewAsync(Caller caller, string runId, CancellationToken ct) =>
            await RunAsync(runId, ct) is { } run && RolesIn(caller, ScopeOf(run.Repositories)).Contains(PeopleRoles.Viewer);

        async Task<SessionRecord?> SessionAsync(string sid, CancellationToken ct) =>
            (await Records.QueryAsync<SessionRecord>(Queries.Session, new Dictionary<string, string> { ["session_id"] = sid }, ct)).Unwrap().FirstOrDefault();

        async Task<SessionDocument?> DocumentAsync(string sid, CancellationToken ct)
        {
            var events = (await Records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = SessionNames.EventKey(sid) }, ct)).Unwrap();
            return events.LastOrDefault(e => e.Kind == SessionNames.WorkspaceEvent) is { } recorded
                ? JsonSerializer.Deserialize<SessionDocument>(await TextAsync(recorded.Detail, ct), Json)
                : null;
        }

        async Task<string> TextAsync(string reference, CancellationToken ct)
        {
            await using var stream = (await Artifacts.GetAsync(new ArtifactRef(reference), ct)).Unwrap();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return await reader.ReadToEndAsync(ct);
        }
    }
}
