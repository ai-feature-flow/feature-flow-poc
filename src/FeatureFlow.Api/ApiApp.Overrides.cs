using Microsoft.AspNetCore.Builder;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>Run-time overrides other than answers and aborts (docs/05 section 5), delivered as the workflow's override update. M7, W1 and W2.</summary>
    static void MapOverrides(WebApplication app, Handlers api)
    {
    }

    sealed partial class Handlers
    {
    }
}
