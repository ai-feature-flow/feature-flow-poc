using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Temporalio.Exceptions;

namespace FeatureFlow.Api;

/// <summary>
/// The inbound chat gateway's authenticator (LOG 204): a chat client's signed request, verified by the provider that
/// posted the message (<see cref="IChatInbound"/>), is the person the provider maps the chat user to, with channel
/// <c>chat</c>, so approval rules' <c>from_chat</c> applies. An unmapped user is no one: the gateway never answers as
/// itself. It accepts only the gateway's own route, so a signed chat request cannot reach another endpoint.
/// </summary>
public sealed class ChatGatewayAuthenticator : IAuthenticator
{
    /// <summary>Where the authenticator keeps the provider's reading of the request, for the gateway's handler.</summary>
    public const string AnswerItem = "ff.chat_answer";

    public async Task<Caller?> AuthenticateAsync(HttpContext context, ApiServices services)
    {
        if (context.Items[ApiApp.InboundRequestItem] is not InboundRequest request || context.GetRouteValue("source") is not string source
            || !context.Request.Path.StartsWithSegments("/inbound/chat", StringComparison.Ordinal)
            || ApiApp.Find<IChatInbound>(services, s => s.Source == source) is not { } inbound)
            return null;
        var read = await inbound.ReadAnswerAsync(request, context.RequestAborted);
        context.Items[AnswerItem] = read;
        return read.Value?.Person is { } person ? new Caller(person.Actor, person.Roles, person.Groups, CallerChannels.Chat) : null;
    }
}

public static partial class ApiApp
{
    /// <summary>Where an inbound route keeps the request as it arrived, for the provider to verify.</summary>
    public const string InboundRequestItem = "ff.inbound_request";

    /// <summary>The first provider instance of any type that is a <typeparamref name="T"/> and matches.</summary>
    public static T? Find<T>(ApiServices services, Func<T, bool> match) where T : class =>
        services.Providers.Types.SelectMany(services.Providers.Instances).Select(i => i.Provider).OfType<T>().FirstOrDefault(match);

    /// <summary>Inbound events: webhooks that wake waits, and chat answers through the gateway as the mapped person. M7, W3.</summary>
    static void MapInbound(WebApplication app, Handlers api)
    {
        app.MapPost("/inbound/events/{source}", (HttpContext c, string source) => api.InboundEventAsync(c, source));
        app.MapPost("/inbound/chat/{source}", (HttpContext c, string source) => api.InboundChatAsync(c, source));
    }

    sealed partial class Handlers
    {
        /// <summary>
        /// Deliveries already handled, bounded. Held in memory: a wake is only a hint, so a delivery repeated after a
        /// restart costs one read in each waiting run and changes no outcome (LOG 228).
        /// </summary>
        readonly DeliveryLog deliveries = new(10_000);

        static async Task<InboundRequest> ReadInboundAsync(HttpContext context)
        {
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            var headers = context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
            var request = new InboundRequest(headers, body, DateTimeOffset.UtcNow);
            context.Items[InboundRequestItem] = request;
            return request;
        }

        /// <summary>
        /// A code host's or CI's webhook (docs/02 sections 2.3 and 7): verified by its provider, then a <c>wake</c> to every
        /// run still going that names the event's repository (LOG 205). The wake is a hint; each wait reads again, so the
        /// event decides nothing. Idempotent on the delivery id, which is remembered only once every wake is sent.
        /// </summary>
        public async Task<IResult> InboundEventAsync(HttpContext context, string source)
        {
            if (Find<IInboundEventSource>(services, s => s.Source == source) is not { } events)
                return Error(404, $"no provider receives {source} events");
            var request = await ReadInboundAsync(context);
            var read = await events.ReadEventAsync(request, context.RequestAborted);
            if (!read.Ok)
                return read.Error!.Reason switch
                {
                    "unauthenticated" => Error(401, read.Error.Message),
                    "validation_failed" => Error(400, read.Error.Message),
                    _ => Error(503, $"{source} events cannot be read now: {read.Error.Reason}"),
                };
            var e = read.Value!;
            var delivery = $"{source}/{e.DeliveryId}";
            if (deliveries.Contains(delivery))
                return Results.Json(new { result = "duplicate", delivery = e.DeliveryId }, Json);
            var woken = new List<string>();
            if (e.Repository is { } repository)
            {
                var runs = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string>(), context.RequestAborted)).Unwrap()
                    .Where(r => r.Status is "starting" or "running" && r.Repositories.Contains(repository, StringComparer.OrdinalIgnoreCase));
                foreach (var run in runs)
                {
                    try
                    {
                        await services.Client.GetWorkflowHandle(run.RunId).SignalAsync(RunWorkflowNames.Wake, [new WakeSignal(source, e.Event)]);
                        woken.Add(run.RunId);
                    }
                    catch (RpcException x) when (x.Code == RpcException.StatusCode.NotFound)
                    {
                        // The run ended after its record was read: nothing waits there.
                        logger.LogInformation("wake {Run} for {Delivery}: {Message}", run.RunId, delivery, x.Message);
                    }
                }
            }

            deliveries.Add(delivery);
            return Results.Json(new { result = e.Repository is null ? "ignored" : "woken", delivery = e.DeliveryId, woken }, Json, statusCode: 202);
        }

        /// <summary>
        /// A person's answer from a chat client (docs/02 section 7, docs/06 section 3.10): authenticated by
        /// <see cref="ChatGatewayAuthenticator"/> as the mapped person, then made the ordinary API decision call as that
        /// person, so approval rules, <c>from_chat</c> and <c>min_count</c> apply unchanged. Refused before anything is
        /// recorded when the request does not verify or the chat user is no one.
        /// </summary>
        public async Task<IResult> InboundChatAsync(HttpContext context, string source)
        {
            if (Find<IChatInbound>(services, s => s.Source == source) is null)
                return Error(404, $"no provider receives {source} answers");
            await ReadInboundAsync(context);
            var caller = await new ChatGatewayAuthenticator().AuthenticateAsync(context, services);
            var read = context.Items[ChatGatewayAuthenticator.AnswerItem] as ProviderResult<ChatAnswer>;
            if (caller is null)
                return read?.Value is { } unmapped
                    ? Error(403, $"the {source} user {unmapped.ChatUser} is not mapped to a person, so it may not answer")
                    : read?.Error?.Reason == "validation_failed"
                        ? Error(400, read.Error.Message)
                        : Error(401, read?.Error?.Message ?? "unauthenticated");
            try
            {
                return await AnswerFromChatAsync(context, caller, read!.Value!);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "{Method} {Path} failed", context.Request.Method, context.Request.Path);
                return Error(500, "internal error; the API's log has the detail");
            }
        }

        /// <summary>
        /// The chat answer as the API's own answer request: a recovery action for an intervention, an outcome for anything
        /// else (the answer handler maps a patch approval's approve or reject), for the wait the button named.
        /// Transport-neutral: a socket-mode receiver would make the same call with the answers it receives.
        /// </summary>
        public async Task<IResult> AnswerFromChatAsync(HttpContext context, Caller caller, ChatAnswer answer)
        {
            var wait = (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string> { ["run_id"] = answer.RunId }, context.RequestAborted)).Unwrap()
                .FirstOrDefault(w => w.WaitId == answer.WaitId);
            var body = new Dictionary<string, string> { ["wait_id"] = answer.WaitId, [wait?.Kind == "intervention" ? "action" : "outcome"] = answer.Choice };
            context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body)));
            context.Request.ContentType = "application/json";
            return await AnswerAsync(context, caller, answer.RunId);
        }
    }

    /// <summary>A bounded set of the deliveries handled most recently.</summary>
    sealed class DeliveryLog(int capacity)
    {
        readonly Lock gate = new();
        readonly HashSet<string> seen = new(StringComparer.Ordinal);
        readonly Queue<string> order = new();

        public bool Contains(string id)
        {
            lock (gate)
                return seen.Contains(id);
        }

        public void Add(string id)
        {
            lock (gate)
            {
                if (!seen.Add(id))
                    return;
                order.Enqueue(id);
                if (order.Count > capacity)
                    seen.Remove(order.Dequeue());
            }
        }
    }
}
