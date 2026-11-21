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
        /// Every open wait the caller may answer now, oldest first: from the record store's open waits of runs still going
        /// (<see cref="Queries.OpenWaits"/>), less the engine's status waits (<c>stalled</c>, <c>stuck</c>), those of runs the
        /// caller does not view, those <see cref="CheckAnswer"/> refuses it, and a <c>min_count</c> decision the caller has
        /// already answered while it waits for others. Libraries are read through the cache, and only for a wait whose type
        /// has no organisation rule (LOG A3-0h).
        /// </summary>
        public async Task<IResult> AttentionAsync(HttpContext context, Caller caller)
        {
            var ct = context.RequestAborted;
            var open = (await Records.QueryAsync<OpenWaitRecord>(Queries.OpenWaits, new Dictionary<string, string>(), ct)).Unwrap();
            var answers = new Dictionary<string, IReadOnlyList<AnswerRecord>>(StringComparer.Ordinal);
            var items = new List<AttentionItem>();
            foreach (var (wait, run) in open)
            {
                if (wait.Kind is "" or "stalled" or "stuck" || !RolesIn(caller, ScopeOf(run.Repositories)).Contains(PeopleRoles.Viewer))
                    continue;
                var (canAnswer, minCount) = await MayAnswerAsync(caller, run, wait, ct);
                if (!canAnswer)
                    continue;
                IReadOnlyList<AnswerProgress>? progress = null;
                if (minCount > 1)
                {
                    if (!answers.TryGetValue(run.RunId, out var recorded))
                        answers[run.RunId] = recorded = (await Records.QueryAsync<AnswerRecord>(Queries.Answers, new Dictionary<string, string> { ["run_id"] = run.RunId }, ct)).Unwrap();
                    progress = ProgressOf(wait, recorded, minCount);
                    // The caller has answered and the decision waits for others: it no longer needs the caller.
                    if (progress.Any(p => p.Actors.Contains(caller.Actor, StringComparer.Ordinal)))
                        continue;
                }

                items.Add(new AttentionItem(SummaryOf(run), WaitViewOf(wait, canAnswer: true, minCount, progress, answer: null)));
            }

            return Results.Json(new AttentionList(items), Json);
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
