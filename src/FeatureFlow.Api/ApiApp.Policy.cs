using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>The policy in force for a run: who answers its decisions, who may use each override, and its attempt limits.</summary>
    static void MapPolicy(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/policy", (HttpContext c, string id) => api.Guard(c, caller => api.RunPolicyAsync(c, caller, id)))
            .Described<RunPolicyView>("getRunPolicy", 200, 404);
    }

    sealed partial class Handlers
    {
        /// <summary>
        /// The policy in force for a run, read now, as answers and overrides are checked (docs/05 section 8), for a viewer of the
        /// run. Each decision type its plan can open (a decision step's type, else <c>&lt;level&gt;.decision</c>; an action's gate
        /// type, such as <c>ticket.merge_action</c>) with its steps and the rule <see cref="Approval.For"/> finds in the run's
        /// scope: the roles that may answer (an admin wherever a steerer may), how many must, whether it may be answered
        /// automatically, and whether the rule is the organisation's, a library's or the engine's default. The library rules
        /// are the organisation's libraries as now read, else, where one cannot be read, those the run locked. Each override
        /// key of the catalogue but <c>answer</c> and <c>answer_question</c>, its roles and whether the caller holds one; and the attempt limits configuration sets, null
        /// where it leaves the engine's default.
        /// </summary>
        public async Task<IResult> RunPolicyAsync(HttpContext context, Caller caller, string id)
        {
            var ct = context.RequestAborted;
            if (await VisibleRunAsync(caller, id, ct) is not { } run)
                return Error(404, $"no run {id}");
            var scope = ScopeOf(run.Repositories);
            var team = TeamOf(scope);
            var loaded = (await LoadPlanAsync(run, null, null, ct)).Plan;
            var read = await libraries.ReadAsync(services.Organisation, ct);
            IReadOnlyList<DecisionTypeItem> declared = read.All(l => l.Library is not null)
                ? [.. read.SelectMany(l => l.Library!.DecisionTypes)]
                : [.. loaded?.Libraries.DecisionTypes ?? []];
            var policy = services.Organisation.Policy;

            var decisions = new List<DecisionPolicyView>();
            if (loaded?.Plan is { } plan)
            {
                var level = plan.Document.Level ?? "run";
                var types = plan.AllSteps
                    .Select(s => (Step: s.Id, Type: s.Form == Model.Plans.StepForm.Decision ? s.Decision?.Type ?? $"{level}.decision" : s.Descriptor?.GateType(level)))
                    .Where(x => x.Type is not null).GroupBy(x => x.Type!, StringComparer.Ordinal);
                foreach (var type in types)
                {
                    var approval = Approval.For(type.Key, policy, team, declared);
                    var roles = approval.Rule.Roles;
                    decisions.Add(new DecisionPolicyView(type.Key, type.Select(x => x.Step).ToList(),
                        roles.Contains(PeopleRoles.Steerer) && !roles.Contains(PeopleRoles.Admin) ? [.. roles, PeopleRoles.Admin] : roles,
                        (int)Math.Max(1, approval.Rule.MinCount), approval.Rule.Auto.Value, SourceOf(approval)));
                }
            }

            var held = caller.Actor == PeopleRoles.PolicyActor ? [] : RolesIn(caller, scope);
            // Answers have no roles of their own: the decisions' rules above say who answers (Overrides.cs).
            var overrides = Overrides.DefaultRoles.Keys.Where(k => k is not (Overrides.Answer or Overrides.AnswerQuestion)).Select(k => (Key: k, Roles: RolesForKey(k)))
                .Select(o => new OverridePolicyView(o.Key, o.Roles, o.Roles.Intersect(held).Any())).ToList();
            var attempts = new AttemptPolicyView(services.Installation.DefaultAttempts, policy.AttemptsPerRun, policy.HardPerStep, policy.HardPerRun);
            return Results.Json(new RunPolicyView(id, decisions, overrides, attempts), Json);
        }
    }

    /// <summary>Where an approval rule comes from, as <see cref="Approval.For"/> gives it: <c>organisation</c>, <c>library</c> or <c>default</c>.</summary>
    static string SourceOf(EffectiveApproval approval) => approval.Kind switch
    {
        ApprovalSource.Organisation => "organisation",
        ApprovalSource.Library => "library",
        _ => "default",
    };
}
