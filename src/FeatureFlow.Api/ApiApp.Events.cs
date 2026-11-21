using System.Globalization;
using System.Text;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>The process's one <see cref="ChangeFeed"/>, a service so tests can poll it.</summary>
    static void AddEvents(IServiceCollection services, ApiServices api) =>
        services.AddSingleton(provider => new ChangeFeed(api, provider.GetRequiredService<ILogger<ChangeFeed>>()));

    /// <summary>
    /// <c>GET /events</c> (docs/02 section 7, G8): change notifications as server-sent events, for any authenticated caller,
    /// each frame only for a run the caller views (LOG 399). The feed starts with the host and stops before it.
    /// </summary>
    static void MapEvents(WebApplication app, Handlers api)
    {
        var feed = app.Services.GetRequiredService<ChangeFeed>();
        app.Lifetime.ApplicationStarted.Register(feed.Start);
        app.Lifetime.ApplicationStopping.Register(feed.Stop);
        app.MapGet("/events", (Delegate)((HttpContext c) => api.Guard(c, caller => api.EventsAsync(c, caller))))
            .WithName("getEvents").Produces<ChangeEvent>(200, "text/event-stream").Produces<ErrorResponse>(401);
    }

    sealed partial class Handlers
    {
        /// <summary>How long a client waits before reconnecting, sent first (<c>retry:</c>, milliseconds).</summary>
        const int ReconnectMilliseconds = 3000;

        /// <summary>
        /// The stream: <c>retry</c>; a <c>reset</c> frame when <c>Last-Event-ID</c> is older than the feed holds; the changes
        /// after it otherwise; then each change as it is read, and <c>: ping</c> at the heartbeat. Proxies are asked not to
        /// buffer or cache it (<c>Cache-Control: no-store</c>, <c>X-Accel-Buffering: no</c>).
        /// </summary>
        public async Task<IResult> EventsAsync(HttpContext context, Caller caller)
        {
            var feed = context.RequestServices.GetRequiredService<ChangeFeed>();
            var ct = context.RequestAborted;
            long? lastEventId = context.Request.Headers["Last-Event-ID"].ToString() is { Length: > 0 } header
                ? long.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : -1
                : null;
            using var subscription = await feed.SubscribeAsync(lastEventId, ct);

            var response = context.Response;
            response.ContentType = "text/event-stream";
            response.Headers.CacheControl = "no-store";
            response.Headers["X-Accel-Buffering"] = "no";
            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            async Task Write(string text)
            {
                await response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
                await response.Body.FlushAsync(ct);
            }

            async Task Frame(ChangeRecord change)
            {
                if (KindOfChange(change.Kind) is not { } kind || await feed.RepositoriesAsync(change.RunId, ct) is not { } repositories
                    || !RolesIn(caller, ScopeOf(repositories)).Contains(PeopleRoles.Viewer))
                    return;
                var data = JsonSerializer.Serialize(new ChangeEvent(change.Sequence, change.RunId, kind, change.WaitId, change.At), Json);
                await Write($"id: {change.Sequence}\nevent: {change.Kind}\ndata: {data}\n\n");
            }

            try
            {
                await Write($"retry: {ReconnectMilliseconds}\n\n");
                if (subscription.ResetTo is { } latest)
                    await Write($"id: {latest}\nevent: reset\ndata: {{}}\n\n");
                foreach (var change in subscription.Replay)
                    await Frame(change);
                await foreach (var frame in subscription.Frames.ReadAllAsync(ct))
                    if (frame is null)
                        await Write(": ping\n\n");
                    else
                        await Frame(frame);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The client went away.
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The response has started, so the stream ends here; the client reconnects.
                logger.LogWarning(e, "GET /events for {Actor} ended", caller.Actor);
            }

            return Results.Empty;
        }

        static ChangeKind? KindOfChange(string kind) => kind switch
        {
            ChangeKinds.RunChanged => ChangeKind.RunChanged,
            ChangeKinds.WaitOpened => ChangeKind.WaitOpened,
            ChangeKinds.WaitClosed => ChangeKind.WaitClosed,
            _ => null,
        };
    }
}
