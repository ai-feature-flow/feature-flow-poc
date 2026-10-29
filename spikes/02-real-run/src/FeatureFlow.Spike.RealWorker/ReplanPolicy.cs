using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>Applies organisation re-plan policy before the interpreter mutates a plan.</summary>
public static class ReplanPolicy
{
    public static PatchResult Apply(
        Plan plan,
        PlanPatch patch,
        OrganisationConfiguration organisation)
    {
        if (patch.ProposedBy?.StartsWith("agent:", StringComparison.Ordinal) == true)
        {
            var disallowed = IntroducedRoles(patch)
                .Where(role => !organisation.ReplanAllowedRoles.Contains(role))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(role => role, StringComparer.Ordinal)
                .ToList();

            if (disallowed.Count > 0)
            {
                return PatchResult.Held(
                    "agent re-plan introduces role(s) outside " +
                    $"policy.replan.allowed_roles: {string.Join(", ", disallowed)}");
            }
        }

        return ApplyPatch.Apply(plan, patch);
    }

    private static IEnumerable<string> IntroducedRoles(PlanPatch patch)
    {
        foreach (var change in patch.Changes)
        {
            if (!string.IsNullOrWhiteSpace(change.Role))
                yield return change.Role;

            if (change.Inputs is not null &&
                change.Inputs.TryGetValue("role", out var inputRole) &&
                inputRole?.ToString() is { Length: > 0 } role)
            {
                yield return role;
            }

            if (change.Step?.Inputs.TryGetValue("role", out var stepRole) == true &&
                stepRole?.ToString() is { Length: > 0 } insertedRole)
            {
                yield return insertedRole;
            }
        }
    }
}
