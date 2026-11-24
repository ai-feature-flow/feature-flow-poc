using FeatureFlow.Api.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>The policy in force for a run: who answers its decisions, who may use each override, and its attempt limits.</summary>
    static void MapPolicy(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/policy", (HttpContext c, string id) => api.Guard(c, caller => api.RunPolicyAsync(c, caller, id)))
            .Described<RunPolicyView>("getRunPolicy", 200, 404);
    }

    sealed partial class Handlers
    {
        /// <summary>Not built yet (build-console-b2 package B builds it).</summary>
        public Task<IResult> RunPolicyAsync(HttpContext context, Caller caller, string id) => Task.FromResult(NotBuilt());
    }
}
