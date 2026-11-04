using System.Text.Json;
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
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static WebApplication Build(ApiServices services, params string[] urls)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(urls.Length == 0 ? ["http://127.0.0.1:8480"] : urls);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        var app = builder.Build();
        var api = new Handlers(services, app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("FeatureFlow.Api"));

        MapRuns(app, api);
        MapDecisions(app, api);
        MapOverrides(app, api);
        MapSessions(app, api);
        MapInbound(app, api);
        MapConsole(app, api);
        return app;
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

        static IResult Error(int status, string message) => Results.Json(new { error = message }, Json, statusCode: status);
    }
}
