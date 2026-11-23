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
        /// has no organisation rule (LOG 395).
        /// </summary>
        public async Task<IResult> AttentionAsync(HttpContext context, Caller caller)
        {
            var ct = context.RequestAborted;
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
            var items = open.Select(o => new AttentionItem(SummaryOf(o.Run), views[(o.Run.RunId, o.Wait.WaitId)]))
                .Where(i => i.Wait.CanAnswer && i.Wait.Progress?.Any(p => p.Actors.Contains(caller.Actor, StringComparer.Ordinal)) != true).ToList();
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
