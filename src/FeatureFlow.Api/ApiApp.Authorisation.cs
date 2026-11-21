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

        /// <summary>
        /// Whether the caller may answer the wait now, and if not the refusal; the one rule for the answer path and for
        /// <c>can_answer</c> on reads (docs/05 section 3.2). A gate, question, patch approval or breakpoint needs the approval
        /// rule's roles in the run's scope; an intervention the <c>recover</c> override's, and <c>grant_attempts</c>'s as well
        /// for a grant (<paramref name="action"/>; reads pass none). Then <c>from_chat</c>, and beside OIDC, a credential with
        /// no identity-provider groups (the local token) is not a person who can be counted among several for <c>min_count</c>
        /// (LOG 221). <c>stalled</c> and <c>stuck</c> are the engine's status, which no one answers.
        /// </summary>
        AnswerCheck CheckAnswer(Caller caller, RunRecord run, WaitRecord wait, IReadOnlyList<DecisionTypeItem> types, string? action)
        {
            var scope = ScopeOf(run.Repositories);
            var team = TeamOf(scope);
            var approval = Approval.For(wait.DecisionType, services.Organisation.Policy, team, types, wait.Kind);
            var intervention = wait.Kind == "intervention";
            var minCount = intervention ? 1 : (int)Math.Max(1, approval.Rule.MinCount);
            return new AnswerCheck(Refusal(), approval, minCount);

            IResult? Refusal()
            {
                if (wait.Kind is "stalled" or "stuck")
                    return Error(409, $"{wait.WaitId} is the run's status ({wait.Kind}), not a decision: no one answers it");
                // A recovery needs recover, and a grant grant_attempts as well (coordinator decision C7).
                var needed = intervention ? RolesForKey(Overrides.Recover) : RolesToAnswer(services.Organisation.Policy, types, wait, team);
                if (Refuse(caller, scope, needed, intervention ? $"recover at {wait.DecisionType} (recover)" : $"answer {wait.DecisionType} (approval: {approval.Source})") is { } refused)
                    return refused;
                if (intervention && action == Overrides.GrantAttempts
                    && Refuse(caller, scope, RolesForKey(Overrides.GrantAttempts), $"grant attempts at {wait.DecisionType} (grant_attempts)") is { } refusedGrant)
                    return refusedGrant;
                if (caller.FromChat && !approval.Rule.FromChat)
                    return Error(403, $"{wait.DecisionType} may not be answered from chat ({approval.Source}); answer it in the console or the CLI");
                if (minCount > 1 && caller.Groups is null && services.Installation.ApiAuth.Mode == "oidc")
                    return Error(403, $"{wait.DecisionType} needs {minCount} people ({approval.Source}); answer it as yourself through the identity provider, not with the local token");
                return null;
            }
        }

        readonly LibraryCache libraries = new(TimeProvider.System);

        /// <summary>
        /// The decision types <see cref="CheckAnswer"/> needs for <paramref name="wait"/> on a read route: none where the
        /// organisation's policy decides (an intervention, or a type with an organisation rule), else the libraries' from
        /// <see cref="LibraryCache"/> (LOG A3-0h). Null when a library cannot be read, so the read shows the wait as not
        /// answerable rather than judging it by the engine's looser default (LOG 289).
        /// </summary>
        async Task<IReadOnlyList<DecisionTypeItem>?> CachedTypesAsync(WaitRecord wait, CancellationToken ct)
        {
            if (wait.Kind == "intervention" || services.Organisation.Policy.RuleFor(wait.DecisionType) is not null)
                return [];
            var read = await libraries.ReadAsync(services.Organisation, ct);
            return read.All(l => l.Library is not null) ? read.SelectMany(l => l.Library!.DecisionTypes).ToList() : null;
        }

        /// <summary>
        /// A read's view of <see cref="CheckAnswer"/>: whether the caller may answer the wait now (it is open, its run is still
        /// going, and the rule allows the caller), and the rule's <c>min_count</c> whoever asks (1 where it is not known).
        /// </summary>
        async Task<(bool CanAnswer, int MinCount)> MayAnswerAsync(Caller caller, RunRecord run, WaitRecord wait, CancellationToken ct)
        {
            if (!wait.Open || wait.Kind is "stalled" or "stuck" || await CachedTypesAsync(wait, ct) is not { } types)
                return (false, 1);
            var check = CheckAnswer(caller, run, wait, types, action: null);
            return (check.Refusal is null && run.Status is "starting" or "running", check.MinCount);
        }
    }

    /// <summary>What <see cref="Handlers.CheckAnswer"/> found: the refusal, or null where the caller may answer; the approval rule that applies and its <c>min_count</c>.</summary>
    sealed record AnswerCheck(IResult? Refusal, EffectiveApproval Approval, int MinCount);
}
