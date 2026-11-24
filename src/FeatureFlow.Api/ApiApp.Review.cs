using FeatureFlow.Api.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>A run's review report (A14): its review passes and angles, findings, external review and trace.</summary>
    static void MapReview(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/review", (HttpContext c, string id) => api.Guard(c, caller => api.ReviewReportAsync(c, caller, id)))
            .Described<ReviewReport>("getReviewReport", 200, 404);
    }

    sealed partial class Handlers
    {
        /// <summary>Not built yet (build-console-b2 package B builds it).</summary>
        public Task<IResult> ReviewReportAsync(HttpContext context, Caller caller, string id) => Task.FromResult(NotBuilt());
    }
}
