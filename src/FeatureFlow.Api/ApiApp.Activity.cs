using FeatureFlow.Api.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>An agent step's live activity: its sessions as turns, from the activity recorded while they run or their stored transcripts.</summary>
    static void MapActivity(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/steps/{step}/activity",
                (HttpContext c, string id, string step, int? execution) => api.Guard(c, caller => api.StepActivityAsync(c, caller, id, step, execution)))
            .Described<StepActivity>("getStepActivity", 200, 404);
    }

    sealed partial class Handlers
    {
        /// <summary>A step's activity for one execution, the latest unless <paramref name="execution"/> names one. Not built yet.</summary>
        public Task<IResult> StepActivityAsync(HttpContext context, Caller caller, string id, string step, int? execution) =>
            Task.FromResult(Error(StatusCodes.Status501NotImplemented, "step activity is not built yet"));
    }
}
