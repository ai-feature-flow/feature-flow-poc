using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>The caller's own views: the waits that need it (<c>GET /attention</c>) and who it is (<c>GET /me</c>).</summary>
    static void MapAttention(WebApplication app, Handlers api)
    {
        app.MapGet("/attention", (Delegate)((HttpContext c) => api.Guard(c, caller => api.AttentionAsync(c, caller))))
            .Described<AttentionList>("getAttention", 200);
        app.MapGet("/me", (Delegate)((HttpContext c) => api.Guard(c, caller => api.MeAsync(caller))))
            .Described<MeResponse>("getMe", 200);
    }

    sealed partial class Handlers
    {
        /// <summary>
        /// Every open wait the caller may answer now, oldest first (<see cref="NeedingAsync"/>), each with its run's ancestors,
        /// root first (A13); and the caller's recent answers: the last 10 of the past 7 days on runs it views, newest first.
        /// </summary>
        public async Task<IResult> AttentionAsync(HttpContext context, Caller caller)
        {
            var ct = context.RequestAborted;
            var runs = new Dictionary<string, RunRecord?>(StringComparer.Ordinal);
            async Task<RunRecord?> RunAsync(string id)
            {
                if (!runs.TryGetValue(id, out var run))
                    runs[id] = run = (await Records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap().FirstOrDefault();
                return run;
            }

            var items = new List<AttentionItem>();
            foreach (var item in await NeedingAsync(caller, ct))
            {
                var path = new List<RunSummary>();
                for (var parent = item.Run.ParentRunId; parent is not null && path.Count < 32 && await RunAsync(parent) is { } above
                     && RolesIn(caller, ScopeOf(above.Repositories)).Contains(PeopleRoles.Viewer); parent = above.ParentRunId)
                    path.Insert(0, SummaryOf(above));
                items.Add(item with { Path = path });
            }

            return Results.Json(new AttentionList(items, await RecentAnswersAsync(caller, RunAsync, ct)), Json);
        }

        /// <summary>
        /// The caller's answers to waits (not aborts) of the past 7 days on runs it views, newest first, at most 10, each with its
        /// run, and the wait's step and title. An answer is recorded "sent" before delivery and again with the run's reply, so
        /// only its latest record is listed.
        /// </summary>
        async Task<IReadOnlyList<RecentAnswerView>> RecentAnswersAsync(Caller caller, Func<string, Task<RunRecord?>> runOf, CancellationToken ct)
        {
            var since = DateTimeOffset.UtcNow.AddDays(-7);
            var answers = (await Records.QueryAsync<AnswerRecord>(Queries.RecentAnswers, new Dictionary<string, string> { ["actor"] = caller.Actor, ["limit"] = "50" }, ct))
                .Unwrap().Where(a => a.WaitId != "abort" && a.At >= since).OrderByDescending(a => a.At).DistinctBy(a => (a.RunId, a.WaitId, a.Answer));
            var waits = new Dictionary<string, IReadOnlyList<WaitRecord>>(StringComparer.Ordinal);
            var recent = new List<RecentAnswerView>();
            foreach (var a in answers)
            {
                if (recent.Count == 10)
                    break;
                if (await runOf(a.RunId) is not { } run || !RolesIn(caller, ScopeOf(run.Repositories)).Contains(PeopleRoles.Viewer))
                    continue;
                if (!waits.TryGetValue(run.RunId, out var runWaits))
                    waits[run.RunId] = runWaits = (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string> { ["run_id"] = run.RunId }, ct)).Unwrap();
                var wait = runWaits.LastOrDefault(w => w.WaitId == a.WaitId);
                var title = wait is null ? null : (await WaitViewsAsync(caller, run, [wait], answers: null, ct))[0].Title;
                var shown = AnswerViewOf(a);
                recent.Add(new RecentAnswerView(SummaryOf(run), a.WaitId, wait?.StepId ?? "", title, shown.Choice, shown.Reason, a.Result, a.At));
            }

            return recent;
        }

        /// <summary>
        /// Every open wait the caller may answer now, oldest first: from the record store's open waits of runs still going
        /// (<see cref="Queries.OpenWaits"/>), less the engine's status waits (<c>stalled</c>, <c>stuck</c>), those of runs the
        /// caller does not view, those <see cref="CheckAnswer"/> refuses it, and a <c>min_count</c> decision the caller has
        /// already answered while it waits for others. Libraries are read through the cache, and only for a wait whose type
        /// has no organisation rule (LOG 395).
        /// </summary>
        async Task<IReadOnlyList<AttentionItem>> NeedingAsync(Caller caller, CancellationToken ct)
        {
            var open = (await Records.QueryAsync<OpenWaitRecord>(Queries.OpenWaits, new Dictionary<string, string>(), ct)).Unwrap()
                .Where(o => o.Wait.Kind is not ("" or "stalled" or "stuck") && RolesIn(caller, ScopeOf(o.Run.Repositories)).Contains(PeopleRoles.Viewer)).ToList();
            var views = new Dictionary<(string Run, string Wait), WaitView>();
            foreach (var run in open.GroupBy(o => o.Run.RunId, StringComparer.Ordinal))
            {
                var waits = run.Select(o => o.Wait).ToList();
                foreach (var (wait, view) in waits.Zip(await WaitViewsAsync(caller, run.First().Run, waits, answers: null, ct)))
                    views[(run.Key, wait.WaitId)] = view;
            }

            // A wait the caller may not answer, or a decision it has answered while it waits for others, does not need the caller.
            return open.Select(o => new AttentionItem(SummaryOf(o.Run), views[(o.Run.RunId, o.Wait.WaitId)]))
                .Where(i => i.Wait.CanAnswer && i.Wait.Progress?.Any(p => p.Actors.Contains(caller.Actor, StringComparer.Ordinal)) != true).ToList();
        }

        /// <summary>
        /// The authenticated caller: its actor and channel, its identity-provider groups (null for the local token), its
        /// organisation-wide roles, and for each team where it holds any, its roles in that team's scope (docs/05 section 3.3).
        /// </summary>
        public Task<IResult> MeAsync(Caller caller)
        {
            var teams = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            foreach (var name in services.Organisation.Teams.Keys)
                if (RolesIn(caller, new RoleScope(name, [])) is { Count: > 0 } roles)
                    teams[name] = roles;
            return Task.FromResult(Results.Json(new MeResponse(caller.Actor, caller.Channel, caller.Groups, RolesIn(caller, RoleScope.Organisation), teams), Json));
        }
    }
}
