using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    sealed partial class Handlers
    {
        /// <summary>A run's scope for people roles (docs/05 section 3.3): its repositories and the team that owns one of them.</summary>
        RoleScope ScopeOf(IReadOnlyList<string> repositories) => new(Approval.TeamFor(services.Organisation, repositories)?.Name, repositories);

        /// <summary>The caller's people roles in a scope, read from the current configuration on every request (docs/05 section 8).</summary>
        IReadOnlyList<string> RolesIn(Caller caller, RoleScope scope) => PeopleRoles.Expand(services.PeopleRoles.RolesFor(caller, scope));

        TeamConfig? TeamOf(RoleScope scope) => scope.Team is { } team ? services.Organisation.Teams.GetValueOrDefault(team) : null;

        async Task<RunRecord?> RunAsync(string id, CancellationToken ct) =>
            (await Records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap().FirstOrDefault();

        /// <summary>Null when the caller holds one of <paramref name="needed"/> in the scope; else the refusal, naming what was refused and why.</summary>
        IResult? Refuse(Caller caller, RoleScope scope, IReadOnlyList<string> needed, string what)
        {
            if (caller.Actor == PeopleRoles.PolicyActor)
                return Error(403, $"'{PeopleRoles.PolicyActor}' is the actor of automatic answers and may not act as a person (F8)");
            return needed.Intersect(RolesIn(caller, scope)).Any()
                ? null
                : Error(403, $"{caller.Actor} may not {what}: it needs one of the roles {string.Join(", ", needed)}");
        }

        /// <summary>The roles that may use a run-time override key, read now: <c>policy.overrides</c>, else the catalogue's default.</summary>
        IReadOnlyList<string> RolesForKey(string key) => Overrides.RolesFor(key, services.Organisation.Policy.Overrides);

        /// <summary>The variants of <paramref name="key"/> (<c>skip_protected</c>, <c>grant_attempts_beyond</c>) the caller also holds in the scope.</summary>
        IReadOnlyList<string> VariantsHeld(Caller caller, RoleScope scope, string key)
        {
            var roles = RolesIn(caller, scope);
            return Overrides.Variants.GetValueOrDefault(key, []).Where(v => RolesForKey(v).Intersect(roles).Any()).ToList();
        }
    }
}
