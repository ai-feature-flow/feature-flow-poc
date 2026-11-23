using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>A run's tree (A12): the tree of the run's root, so a link to a child shows the whole tree.</summary>
    static void MapTrees(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/tree", (HttpContext c, string id) => api.Guard(c, caller => api.TreeAsync(c, caller, id)))
            .Described<RunTreeView>("getRunTree", 200, 404);
    }

    sealed partial class Handlers
    {
        /// <summary>
        /// The tree of the run's root (A12), so a link to a child shows the whole tree: from the topmost ancestor the caller
        /// views, each run with its view, its plan (or why none), its open waits as the caller sees them, and its child runs the
        /// caller views, nested. Every run's plan revisions come from one read of the root's artifacts, and its libraries from
        /// the lock-keyed cache.
        /// </summary>
        public async Task<IResult> TreeAsync(HttpContext context, Caller caller, string id)
        {
            var ct = context.RequestAborted;
            if (await VisibleRunAsync(caller, id, ct) is not { } root)
                return Error(404, $"no run {id}");
            for (var depth = 0; root.ParentRunId is { } parent && depth < 32; depth++)
            {
                if (await VisibleRunAsync(caller, parent, ct) is not { } above)
                    break;
                root = above;
            }

            var revisions = await PlanRevisionsAsync(root.RunId, ct);
            return Results.Json(new RunTreeView(await NodeAsync(caller, root, revisions, ct)), Json);
        }

        async Task<RunTreeNode> NodeAsync(Caller caller, RunRecord run, IReadOnlyList<ArtifactRecord> revisions, CancellationToken ct)
        {
            var children = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string> { ["parent_run_id"] = run.RunId }, ct)).Unwrap()
                .Where(c => RolesIn(caller, ScopeOf(c.Repositories)).Contains(PeopleRoles.Viewer)).OrderBy(c => c.StartedAt ?? c.UpdatedAt).ThenBy(c => c.RunId, StringComparer.Ordinal)
                .ToList();
            var state = await RunStateAsync(run.RunId, ct, children);
            var (loaded, refusal) = await LoadPlanAsync(run, revision: null, extraPatch: null, ct, revisions);
            var waits = await WaitViewsAsync(caller, run, state.OpenWaits.Where(w => w.Kind != "").ToList(), answers: null, ct);
            var nodes = new List<RunTreeNode>();
            foreach (var child in children)
                nodes.Add(await NodeAsync(caller, child, revisions, ct));
            return new RunTreeNode(ViewOf(run, await PullRequestLinksAsync(run.RunId, ct)), loaded is null ? null : PlanViewOf(run.RunId, loaded, state),
                refusal?.Message, waits, nodes);
        }
    }
}
