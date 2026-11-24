using FeatureFlow.Api.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>A step's detail (A3): its executions, waits, findings, events, staged patch and pending instructions, in one read.</summary>
    static void MapSteps(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/steps/{step}", (HttpContext c, string id, string step) => api.Guard(c, caller => api.StepDetailAsync(c, caller, id, step)))
            .Described<StepDetail>("getStepDetail", 200, 404);
    }

    sealed partial class Handlers
    {
        /// <summary>Not built yet (build-console-b2 package B builds it).</summary>
        public Task<IResult> StepDetailAsync(HttpContext context, Caller caller, string id, string step) => Task.FromResult(NotBuilt());

        /// <summary>A route whose contract is fixed and whose handler is not built yet: 501.</summary>
        static IResult NotBuilt() => Error(StatusCodes.Status501NotImplemented, "not built yet");
    }
}
