using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Validation;

/// <summary>One line of the effective policy view: for a step and a topic, what the workflow asks, the organisation's cap, the result and the setting that decided it.</summary>
public sealed record PolicyRow(string StepId, string Topic, string Request, string Cap, string Result, string From)
{
    public override string ToString() => $"{StepId} {Topic}: {Result} (asks {Request}; cap {Cap}; from {From})";
}

/// <summary>A merge step's target as run start knows it: the repository, and its default branch when the merge goes into it.</summary>
public sealed record MergeTarget(string Repository, string? DefaultBranch);

/// <summary>
/// The effective policy view (docs/05 section 7, docs/09 section 2.3): for every merge step and decision step, the
/// request, the cap, the result and which setting decided it. Information, not a check: anything it would block is
/// already a run-start check. Run start gives each merge step's targets and its resolved <c>engine_can_merge</c>;
/// <c>ff show</c>, which has no run, describes the request as the plan states it.
/// </summary>
public static class EffectivePolicy
{
    public static IReadOnlyList<PolicyRow> For(Plan plan, PolicyConfig policy, Func<string, EffectiveApproval> approval,
        Func<Step, (bool? EngineCanMerge, IReadOnlyList<MergeTarget> Targets)>? merges = null)
    {
        var rows = new List<PolicyRow>();
        foreach (var step in plan.AllSteps)
        {
            if (step.Action == "scm.merge")
            {
                rows.Add(WhoMerges(step, policy, merges?.Invoke(step)));
                rows.Add(Floor(step, "checks before merging", "require_checks", "policy.merge.require_checks", policy.RequireChecks));
                rows.Add(Floor(step, "tests before merging", "require_tested", "policy.merge.require_tested", policy.RequireTested));
            }

            if (step is { Form: StepForm.Decision, Decision: { } decision })
            {
                var type = decision.Type ?? $"{plan.Document.Level ?? "run"}.decision";
                var (rule, source) = approval(type);
                var chat = rule.FromChat ? "chat or console" : "console or CLI only";
                rows.Add(new PolicyRow(step.Id, "who answers", type, "-", $"{string.Join(" or ", rule.Roles)}, {rule.MinCount} {(rule.MinCount == 1 ? "person" : "people")}, {chat}", source));
                var cap = $"auto {(rule.Auto.Value ? "allowed" : "not allowed")}{(rule.Auto.Locked ? ", locked" : "")}";
                var result = decision.Auto is null ? "a person answers"
                    : rule.Auto.Value ? $"resolves '{decision.Auto}' at once, actor policy (not a person's approval)"
                    : rule.Auto.Locked ? "refused at run start (check 22)" : "a person answers (auto not allowed)";
                rows.Add(new PolicyRow(step.Id, "auto-resolution", decision.Auto is null ? "none" : $"auto: {decision.Auto}", cap, result, source));
            }
        }

        return rows;
    }

    static PolicyRow WhoMerges(Step step, PolicyConfig policy, (bool? EngineCanMerge, IReadOnlyList<MergeTarget> Targets)? known)
    {
        var cap = policy.EngineMayMerge.Count == 0 ? "engine_may_merge: none" : $"engine_may_merge: {string.Join(", ", policy.EngineMayMerge)}";
        const string from = "F1: policy.merge.default_branch.engine_may_merge";
        if (known is not { } k)
        {
            var value = step.With?["engine_can_merge"];
            var request = "engine_can_merge = " + (value is null ? "true (the action's default)" : ReadableForm.Describe(value, step.Scope));
            return new PolicyRow(step.Id, "who merges", request, cap,
                "the engine, except into a default branch whose repository engine_may_merge does not list: there a person merges (action request)", from);
        }

        var asked = k.EngineCanMerge is null ? "engine_can_merge undecided at start" : $"engine_can_merge = {(k.EngineCanMerge.Value ? "true" : "false")}";
        var results = k.Targets.Select(t =>
            k.EngineCanMerge == false ? $"a person merges into {t.Repository} (engine_can_merge false)"
            : t.DefaultBranch is null ? $"the engine merges into {t.Repository} (not its default branch)"
            : RepositoryPatterns.Any(policy.EngineMayMerge, t.Repository) ? $"the engine merges into {t.Repository}'s default branch {t.DefaultBranch}"
            : $"a person merges into {t.Repository}'s default branch {t.DefaultBranch} (action request)");
        return new PolicyRow(step.Id, "who merges", asked, cap, string.Join("; ", results.DefaultIfEmpty("no repository")), from);
    }

    /// <summary>A merge evidence floor, <c>require_checks</c> or <c>require_tested</c>: what the step asks and what its locked or default cap makes of it.</summary>
    static PolicyRow Floor(Step step, string topic, string input, string key, Cap<bool>? floor)
    {
        var value = step.With?[input];
        var noun = input == "require_checks" ? "checks" : "tests";
        var request = value is null ? $"{input} not set (the action's default, true)" : $"{input} = " + ReadableForm.Describe(value, step.Scope);
        var cap = floor is { } c ? $"{input} {(c.Value ? "true" : "false")}{(c.Locked ? ", locked" : "")}" : "none";
        var literal = (value as YScalar)?.AsBool();
        var (result, from) = floor is { Value: true, Locked: true } && value is not null && literal != true
            ? ("refused at run start (check 22)", $"{key}, locked")
            : value is not null && literal is null ? ("as the expression gives at the step (true unless it gives false)", "the workflow's request")
            : (literal ?? true) ? ($"{noun} required", value is null ? "scm.merge's default" : "the workflow's request")
            : ($"{noun} not required", floor is { Value: true } ? "the workflow's request (the organisation's default is not locked)" : "the workflow's request");
        return new PolicyRow(step.Id, topic, request, cap, result, from);
    }

    /// <summary>The view as lines, for the readable form and the run start record.</summary>
    public static IReadOnlyList<string> Lines(IReadOnlyList<PolicyRow> rows) =>
        rows.Select(r => $"{r.StepId} {r.Topic}: {r.Result} | asks: {r.Request} | cap: {r.Cap} | from: {r.From}").ToList();
}
