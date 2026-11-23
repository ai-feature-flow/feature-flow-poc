using FeatureFlow.Api.Contracts;
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
        /// <summary>The tree of the run's root: not built yet.</summary>
        public async Task<IResult> TreeAsync(HttpContext context, Caller caller, string id)
        {
            if (await VisibleRunAsync(caller, id, context.RequestAborted) is null)
                return Error(404, $"no run {id}");
            return Error(501, "not built yet (B1 A12)");
        }
    }
}
