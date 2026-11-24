using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Temporalio.Client;

namespace FeatureFlow.Api;

/// <summary>What the API service uses: providers, configuration and the Temporal client. It keeps no state of its own (docs/02 section 1).</summary>
public sealed record ApiServices(ProviderSet Providers, InstallationConfig Installation, OrganisationConfig Organisation, ITemporalClient Client)
{
    /// <summary>How requests are authenticated, tried in order; the first that knows the credential decides. Empty: the local token.</summary>
    public IReadOnlyList<IAuthenticator> Authenticators { get; init; } = [];

    /// <summary>A caller's people roles in a run's scope (docs/05 section 3.3). Default: the roles the credential carries.</summary>
    public IPeopleRoles PeopleRoles { get; init; } = new CredentialRoles();

    /// <summary>How often the change feed reads the record store's changes for <c>GET /events</c> (docs/02 section 7).</summary>
    public TimeSpan EventPoll { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How often <c>GET /events</c> sends a heartbeat (<c>: ping</c>), so idle connections stay open through proxies.</summary>
    public TimeSpan EventHeartbeat { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>The clock the change feed's poll and heartbeat run on.</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}

/// <summary>
/// The API service (docs/02 sections 1 and 7): start, query, answer and abort, behind authentication. Queries read the
/// record store, never Temporal (docs/02 section 5). An answer is authorised, recorded with the authenticated actor, then
/// delivered as a Temporal Update (docs/04 section 4.1). Handlers are split by area, each file mapping its own routes:
/// runs, decisions, overrides, sessions, inbound events, the console and change events; authentication is in its own file.
/// </summary>
public static partial class ApiApp
{
    /// <summary>The API's JSON: snake_case, as the OpenAPI document describes it (LOG 388).</summary>
    static readonly JsonSerializerOptions Json = ApiJson.Options;

    /// <summary>
    /// The JSON of what the API stores or reads beside the engine: the library lock, a session's workspace document, an
    /// answer as its record holds it. The engine's format, not the API's.
    /// </summary>
    static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web);

    /// <summary>Where the OpenAPI document is served, unauthenticated (docs/02 section 7).</summary>
    public const string OpenApiPath = "/openapi.json";

    /// <summary>The OpenAPI security scheme every described route takes: <c>Authorization: Bearer &lt;token&gt;</c>.</summary>
    const string BearerScheme = "bearer";

    public static WebApplication Build(ApiServices services, params string[] urls)
    {
        urls = urls.Length == 0 ? ["http://127.0.0.1:8480"] : urls;
        RefuseUnlessLoopback(services.Installation.ApiAuth, urls);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(urls);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o => ApiJson.Configure(o.SerializerOptions));
        builder.Services.AddOpenApi(o =>
        {
            o.OpenApiVersion = Microsoft.OpenApi.OpenApiSpecVersion.OpenApi3_1;
            o.AddDocumentTransformer((document, _, _) =>
            {
                document.Info = new() { Title = "Feature Flow API", Version = "1", Description = "The engine's API (docs/02 section 7). Generated; see OpenApiTests." };
                document.Servers = [];
                // Every route the document describes takes the bearer token (docs/02 section 7); the document itself, which it
                // does not describe, is served without one.
                document.Components ??= new();
                document.Components.SecuritySchemes = new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>
                {
                    [BearerScheme] = new Microsoft.OpenApi.OpenApiSecurityScheme
                    {
                        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
                        Scheme = "bearer",
                        Description = "The installation's API token, or an identity provider's token where the API uses OIDC",
                    },
                };
                document.Security = [new() { [new Microsoft.OpenApi.OpenApiSecuritySchemeReference(BearerScheme, document)] = [] }];
                return Task.CompletedTask;
            });
        });
        AddEvents(builder.Services, services);
        var app = builder.Build();
        var api = new Handlers(services, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FeatureFlow.Api"));

        app.MapOpenApi(OpenApiPath);
        MapRuns(app, api);
        MapArtifacts(app, api);
        MapActivity(app, api);
        MapSteps(app, api);
        MapReview(app, api);
        MapPolicy(app, api);
        MapTrees(app, api);
        MapDecisions(app, api);
        MapOverrides(app, api);
        MapSessions(app, api);
        MapAttention(app, api);
        MapInbound(app, api);
        MapConsole(app, api);
        MapEvents(app, api);
        MapTemplates(app, api);
        MapRepositories(app, api);
        return app;
    }

    /// <summary>
    /// Starts the API and returns the addresses it listens on. Under <c>api.auth.mode: none</c> every bound address must be
    /// loopback, not only the configured ones: Kestrel's own configuration (<c>Kestrel__Endpoints__…</c>,
    /// <c>ASPNETCORE_URLS</c>) can bind others. If one is not, the API is stopped and this throws (LOG 457).
    /// </summary>
    public static async Task<IReadOnlyList<string>> StartCheckedAsync(WebApplication app, ApiAuthConfig auth)
    {
        await app.StartAsync();
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.ToList();
        try
        {
            RefuseUnlessLoopback(auth, bound);
        }
        catch (StartupRefusal)
        {
            await app.StopAsync();
            throw;
        }

        return bound;
    }

    /// <summary>Under <c>api.auth.mode: none</c>, refuses an API address that is not loopback (<see cref="LoopbackOnly"/>; LOG 457).</summary>
    public static void RefuseUnlessLoopback(ApiAuthConfig auth, IEnumerable<string> urls)
    {
        if (auth.Mode == "none" && LoopbackOnly(urls) is { } refused)
            throw new StartupRefusal($"api.auth.mode is none, so the API must listen only on loopback (localhost, 127.0.0.1 or [::1]), " +
                                     $"but it would listen on '{refused}'. Set FF_API_LISTEN to a loopback address, or use api.auth.mode local_token or oidc.");
    }

    /// <summary>
    /// The first address that is not loopback, or null: loopback is a host of <c>localhost</c> (any case), an IPv4 address
    /// in 127.0.0.0/8 or <c>::1</c>. Everything else is refused, including every-interface forms (<c>0.0.0.0</c>,
    /// <c>[::]</c>, <c>*</c>, <c>+</c>), IPv4-mapped forms such as <c>::ffff:127.0.0.1</c>, Unix sockets and other names.
    /// </summary>
    public static string? LoopbackOnly(IEnumerable<string> urls) => urls.FirstOrDefault(url => !IsLoopback(url));

    static bool IsLoopback(string url)
    {
        string host;
        try
        {
            var address = BindingAddress.Parse(url);
            if (address.IsUnixPipe)
                return false;
            host = address.Host;
        }
        catch (FormatException)
        {
            return false;
        }

        return IsLoopbackHost(host);
    }

    /// <summary>A host name or address, without its port, that is loopback: <c>localhost</c> (any case), 127.0.0.0/8 or <c>::1</c> (bracketed or not).</summary>
    internal static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        if (!System.Net.IPAddress.TryParse(host.StartsWith('[') && host.EndsWith(']') ? host[1..^1] : host, out var ip))
            return false;
        return ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? ip.GetAddressBytes()[0] == 127
            : ip.Equals(System.Net.IPAddress.IPv6Loopback);
    }

    /// <summary>
    /// A route's description for the OpenAPI document: its operation id (the generated client's function name), its success
    /// response, and its refusals, each an <see cref="ErrorResponse"/>. Every guarded route may answer 401.
    /// </summary>
    static RouteHandlerBuilder Described<TResponse>(this RouteHandlerBuilder route, string operationId, int status, params int[] refusals)
    {
        route.WithName(operationId).Produces<TResponse>(status);
        foreach (var refusal in refusals.Prepend(StatusCodes.Status401Unauthorized).Distinct())
            route.Produces<ErrorResponse>(refusal);
        return route;
    }

    /// <summary>
    /// A route's request body, read by <see cref="Handlers.BodyAsync{T}"/> inside the guard, declared for the document. It names
    /// no content type, so routing does not refuse a request (415) before the guard has authenticated it (LOG 407); the
    /// document then describes the body as JSON.
    /// </summary>
    static RouteHandlerBuilder Taking<TRequest>(this RouteHandlerBuilder route) where TRequest : notnull =>
        route.WithMetadata(new JsonBody(typeof(TRequest)));

    sealed record JsonBody(Type RequestType) : Microsoft.AspNetCore.Http.Metadata.IAcceptsMetadata
    {
        public IReadOnlyList<string> ContentTypes => [];

        public bool IsOptional => false;
    }

    /// <summary>
    /// The approval rule's roles for a wait (docs/05 sections 3.2 to 4), as <see cref="Approval.For"/> finds the rule: the
    /// team's, the organisation's, the declaring library's default, else the engine's. Where a steerer may, an
    /// administrator may too (docs/05 section 3.3). An intervention is answered as the <c>recover</c> override instead (LOG 218).
    /// </summary>
    public static IReadOnlyList<string> RolesToAnswer(PolicyConfig policy, IReadOnlyList<DecisionTypeItem> types, WaitRecord wait, TeamConfig? team = null)
    {
        var roles = Approval.For(wait.DecisionType, policy, team, types, wait.Kind).Rule.Roles;
        return roles.Contains("steerer") && !roles.Contains("admin") ? [.. roles, "admin"] : roles;
    }

    sealed partial class Handlers(ApiServices services, ILogger logger)
    {
        IRecordStoreProvider Records => services.Providers.Required<IRecordStoreProvider>("record_store");

        IArtifactStoreProvider Artifacts => services.Providers.Required<IArtifactStoreProvider>("artifact_store");

        static IResult Error(int status, string message) => Results.Json(new ErrorResponse(message), Json, statusCode: status);

        /// <summary>
        /// The request's body as <typeparamref name="T"/>, read after authentication (LOG 407), or a 400 naming what is
        /// wrong: not JSON, a required property missing or null, a value of the wrong type, or a property the record does not
        /// name (an <c>actor</c> included: the actor is the authenticated caller, F8).
        /// </summary>
        public static async Task<(T? Body, IResult? Refused)> BodyAsync<T>(HttpContext context) where T : class
        {
            try
            {
                return await JsonSerializer.DeserializeAsync<T>(context.Request.Body, ApiJson.Requests, context.RequestAborted) is { } body
                    ? (body, null)
                    : (null, Error(400, "the body must be a JSON object"));
            }
            catch (JsonException e)
            {
                return (null, Error(400, $"the body is not a valid request: {e.Message}"));
            }
        }
    }
}
