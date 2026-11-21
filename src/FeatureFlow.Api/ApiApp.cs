using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
}

/// <summary>
/// The API service (docs/02 sections 1 and 7): start, query, answer and abort, behind authentication. Queries read the
/// record store, never Temporal (docs/02 section 5). An answer is authorised, recorded with the authenticated actor, then
/// delivered as a Temporal Update (docs/04 section 4.1). Handlers are split by area, each file mapping its own routes:
/// runs, decisions, overrides, sessions, inbound events and the console; authentication is in its own file.
/// </summary>
public static partial class ApiApp
{
    /// <summary>The API's JSON: snake_case, as the OpenAPI document describes it (LOG A3-0a).</summary>
    static readonly JsonSerializerOptions Json = ApiJson.Options;

    /// <summary>
    /// The JSON of what the API stores or reads beside the engine: the library lock, a session's workspace document, an
    /// answer as its record holds it. The engine's format, not the API's.
    /// </summary>
    static readonly JsonSerializerOptions Stored = new(JsonSerializerDefaults.Web);

    /// <summary>Where the OpenAPI document is served, unauthenticated (docs/02 section 7).</summary>
    public const string OpenApiPath = "/openapi.json";

    public static WebApplication Build(ApiServices services, params string[] urls)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(urls.Length == 0 ? ["http://127.0.0.1:8480"] : urls);
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
                return Task.CompletedTask;
            });
        });
        var app = builder.Build();
        var api = new Handlers(services, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FeatureFlow.Api"));

        app.MapOpenApi(OpenApiPath);
        MapRuns(app, api);
        MapDecisions(app, api);
        MapOverrides(app, api);
        MapSessions(app, api);
        MapAttention(app, api);
        MapInbound(app, api);
        MapConsole(app, api);
        return app;
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
    /// no content type, so routing does not refuse a request (415) before the guard has authenticated it (LOG A3-0t); the
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
        /// The request's body as <typeparamref name="T"/>, read after authentication (LOG A3-0t), or a 400 naming what is
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
