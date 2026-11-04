using System.Security.Cryptography;
using System.Text;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace FeatureFlow.Api;

/// <summary>How a caller reached the API: directly, or through the inbound chat gateway (approval rules' <c>from_chat</c>, docs/05 section 3.2).</summary>
public static class CallerChannels
{
    public const string Api = "api";
    public const string Chat = "chat";
}

/// <summary>
/// The authenticated caller: the actor, its organisation-wide people roles and its identity-provider groups come from the
/// credential, never from the request body (F8). A chat answer arrives as the person the gateway mapped the chat user
/// to, never as the gateway itself, with <see cref="Channel"/> <c>chat</c>. <see cref="Groups"/> is null for a credential
/// that carries roles rather than groups (the local token).
/// </summary>
public sealed record Caller(string Actor, IReadOnlyList<string> Roles, IReadOnlyList<string>? Groups = null, string Channel = CallerChannels.Api)
{
    public bool FromChat => Channel == CallerChannels.Chat;
}

/// <summary>
/// Turns a request into a <see cref="Caller"/>: the local token, OIDC, or the inbound chat gateway's signed request. Null
/// means this authenticator does not accept the request's credential; the API answers 401 when none does.
/// </summary>
public interface IAuthenticator
{
    Task<Caller?> AuthenticateAsync(HttpContext context, ApiServices services);
}

/// <summary>The solo profile's local token (docs/02 section 7): a bearer token compared in constant time with the configured secret; the actor and roles are the installation's <c>api.auth</c>.</summary>
public sealed class LocalTokenAuthenticator : IAuthenticator
{
    public async Task<Caller?> AuthenticateAsync(HttpContext context, ApiServices services)
    {
        var auth = services.Installation.ApiAuth;
        var header = context.Request.Headers.Authorization.ToString();
        var given = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..] : "";
        var secrets = services.Providers.Required<ISecretsProvider>("secrets");
        var expected = auth.TokenRef is null ? null : (await secrets.ResolveAsync(auth.TokenRef, context.RequestAborted)).Value;
        return expected is not null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(expected))
            ? new Caller(auth.Actor, auth.Roles)
            : null;
    }
}

/// <summary>Where a caller's roles are asked for: a run's team and repositories (docs/05 section 3.3: roles may be scoped by team or repository).</summary>
public sealed record RoleScope(string? Team, IReadOnlyList<string> Repositories)
{
    public static readonly RoleScope Organisation = new(null, []);
}

/// <summary>A caller's people roles in a scope: the organisation's <c>roles</c> and its teams' roles, from the caller's groups.</summary>
public interface IPeopleRoles
{
    IReadOnlyList<string> RolesFor(Caller caller, RoleScope scope);
}

/// <summary>The roles the credential itself carries, in every scope: the local token's.</summary>
public sealed class CredentialRoles : IPeopleRoles
{
    public IReadOnlyList<string> RolesFor(Caller caller, RoleScope scope) => caller.Roles;
}

public static partial class ApiApp
{
    sealed partial class Handlers
    {
        static readonly IReadOnlyList<IAuthenticator> LocalToken = [new LocalTokenAuthenticator()];

        /// <summary>Authenticates the request with the first authenticator that accepts its credential, then handles it as that caller.</summary>
        public async Task<IResult> Guard(HttpContext context, Func<Caller, Task<IResult>> handle)
        {
            Caller? caller = null;
            foreach (var authenticator in services.Authenticators.Count > 0 ? services.Authenticators : LocalToken)
                if ((caller = await authenticator.AuthenticateAsync(context, services)) is not null)
                    break;
            if (caller is null)
                return Results.Json(new { error = "unauthenticated" }, Json, statusCode: 401);
            try
            {
                return await handle(caller);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                // The detail goes to the log, never to the caller.
                logger.LogError(e, "{Method} {Path} failed", context.Request.Method, context.Request.Path);
                return Error(500, "internal error; the API's log has the detail");
            }
        }
    }
}
