using Microsoft.AspNetCore.Builder;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>Inbound events: webhooks that wake waits, and chat answers through the gateway as the mapped person. M7, W3.</summary>
    static void MapInbound(WebApplication app, Handlers api)
    {
    }

    sealed partial class Handlers
    {
    }
}
