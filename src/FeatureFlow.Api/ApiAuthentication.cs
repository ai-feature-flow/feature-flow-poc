using FeatureFlow.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace FeatureFlow.Api;

/// <summary>The authenticators and people roles an installation configures (docs/07 section 2, <c>api.auth</c>).</summary>
public static class ApiAuthentication
{
    /// <summary>OIDC first, then the local token where it has a secret; the local token alone otherwise.</summary>
    public static IReadOnlyList<IAuthenticator> For(ApiAuthConfig auth, HttpClient? http = null) => auth.Mode == "oidc"
        ? auth.TokenRef is null ? [new OidcAuthenticator(auth, http)] : [new OidcAuthenticator(auth, http), new LocalTokenAuthenticator()]
        : [new LocalTokenAuthenticator()];
}

/// <summary>
/// OIDC bearer tokens (docs/02 section 7): the issuer's metadata and keys are discovered from
/// <c>{issuer}/.well-known/openid-configuration</c> and cached; a token must be signed by one of its keys, issued by the
/// configured issuer for the configured audience, and unexpired. The actor is the <c>actor_claim</c> claim and the groups
/// the <c>role_claim</c> claim; the organisation-wide roles come from the groups (docs/05 section 3.3). A token that is
/// not a JWT is left to the next authenticator; an invalid one is refused, never passed on as someone else. A token signed by
/// a key the cache lacks is refused and asks for a refresh, so later requests see a rotated key (LOG 220).
/// </summary>
public sealed class OidcAuthenticator : IAuthenticator
{
    readonly ApiAuthConfig settings;
    readonly ConfigurationManager<OpenIdConnectConfiguration> metadata;
    readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false };

    public OidcAuthenticator(ApiAuthConfig settings, HttpClient? http = null)
    {
        this.settings = settings;
        var issuer = settings.Issuer ?? throw new ArgumentException("OIDC needs api.auth.issuer");
        var address = issuer.TrimEnd('/') + "/.well-known/openid-configuration";
        var retriever = new HttpDocumentRetriever(http ?? new HttpClient()) { RequireHttps = new Uri(address).Scheme == Uri.UriSchemeHttps };
        metadata = new ConfigurationManager<OpenIdConnectConfiguration>(address, new OpenIdConnectConfigurationRetriever(), retriever);
    }

    public async Task<Caller?> AuthenticateAsync(HttpContext context, ApiServices services)
    {
        var header = context.Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : "";
        if (!handler.CanReadToken(token))
            return null;
        var result = await ValidateAsync(token, context.RequestAborted);
        // A key the cached metadata lacks may be a rotated one: the next request reads the issuer's keys again (LOG 220).
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
            metadata.RequestRefresh();
        if (!result.IsValid)
            return null;

        var claims = result.ClaimsIdentity.Claims.ToList();
        var actor = claims.FirstOrDefault(c => c.Type == settings.ActorClaim)?.Value;
        if (string.IsNullOrWhiteSpace(actor) || actor == PeopleRoles.PolicyActor)
            return null;

        var groups = claims.Where(c => c.Type == settings.GroupsClaim).Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
        return new Caller(actor, PeopleRoles.FromGroups(groups, services.Organisation, null), groups);
    }

    async Task<TokenValidationResult> ValidateAsync(string token, CancellationToken ct)
    {
        OpenIdConnectConfiguration discovered;
        try
        {
            discovered = await metadata.GetConfigurationAsync(ct);
        }
        catch (InvalidOperationException e)
        {
            return new TokenValidationResult { IsValid = false, Exception = e };
        }

        return await handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            // The configured issuer, not the metadata's: a metadata document cannot name another.
            ValidIssuer = settings.Issuer,
            ValidAudience = settings.Audience,
            IssuerSigningKeys = discovered.SigningKeys,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1),
        });
    }
}

/// <summary>
/// People roles in a run's scope (docs/05 section 3.3): from the caller's identity-provider groups, through the
/// organisation's <c>roles</c> and the run's team's; a credential with no groups (the local token) keeps its own roles.
/// Either way, held roles imply the roles below them (<see cref="PeopleRoles.Expand"/>).
/// </summary>
public sealed class OrganisationRoles(OrganisationConfig organisation) : IPeopleRoles
{
    public IReadOnlyList<string> RolesFor(Caller caller, RoleScope scope) => caller.Groups is null
        ? PeopleRoles.Expand(caller.Roles)
        : PeopleRoles.FromGroups(caller.Groups, organisation, scope.Team is { } team ? organisation.Teams.GetValueOrDefault(team) : null);
}
