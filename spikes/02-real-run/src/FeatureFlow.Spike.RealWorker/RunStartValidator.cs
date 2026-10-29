using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.RealWorker;

public sealed record RunStartValidationProblem(int Check, string Message);

public sealed class RunStartValidationResult
{
    public List<RunStartValidationProblem> Problems { get; } = new();

    public bool Accepted => Problems.Count == 0;

    public void WriteTo(TextWriter writer)
    {
        foreach (var check in new[] { 20, 21, 22 })
        {
            var problems = Problems.Where(problem => problem.Check == check).ToList();
            if (problems.Count == 0)
            {
                writer.WriteLine($"RUN START check {check}: PASS");
                continue;
            }

            foreach (var problem in problems)
                writer.WriteLine($"RUN START check {check}: FAIL - {problem.Message}");
        }

        writer.WriteLine(Accepted
            ? "RUN START validation: ACCEPTED"
            : $"RUN START validation: REJECTED ({Problems.Count} problem(s))");
    }
}

/// <summary>Checks 20-22 from docs/12, run before the worker connects to Temporal.</summary>
public static class RunStartValidator
{
    public static RunStartValidationResult Validate(
        Plan plan,
        OrganisationConfiguration organisation)
    {
        var result = new RunStartValidationResult();
        ValidateCapabilities(plan, organisation, result);
        ValidateDecisionTypes(plan, organisation, result);
        ValidateAttempts(plan, organisation, result);
        return result;
    }

    private static void ValidateCapabilities(
        Plan plan,
        OrganisationConfiguration organisation,
        RunStartValidationResult result)
    {
        var required = plan.Steps
            .SelectMany(step => step.Requires?.Capabilities ?? Enumerable.Empty<string>())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var step in plan.Steps)
        {
            if (step.Kind == "agent.run")
            {
                required.Add("sandbox");
                required.Add("agent_runtime");
            }
            if (step.Kind.StartsWith("scm.", StringComparison.Ordinal))
                required.Add("code_host");
            if (step.Kind.StartsWith("ci.", StringComparison.Ordinal))
                required.Add("ci");
        }

        foreach (var capability in required.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!organisation.SupportsCapability(capability))
            {
                result.Problems.Add(new RunStartValidationProblem(
                    20, $"required capability '{capability}' has no configured provider"));
            }
        }
    }

    private static void ValidateDecisionTypes(
        Plan plan,
        OrganisationConfiguration organisation,
        RunStartValidationResult result)
    {
        foreach (var step in plan.Steps.Where(step => step.Gate is not null))
        {
            var decisionType = step.Gate!.DecisionType;
            if (!organisation.Approval.ContainsKey(decisionType))
            {
                result.Problems.Add(new RunStartValidationProblem(
                    21,
                    $"step '{step.Id}' uses unknown gate.decision_type '{decisionType}'; " +
                    "policy.approval has no matching key"));
            }
        }
    }

    private static void ValidateAttempts(
        Plan plan,
        OrganisationConfiguration organisation,
        RunStartValidationResult result)
    {
        var declaredTotal = 0;
        foreach (var step in plan.Steps.Where(step => step.Limits is not null))
        {
            var attempts = step.Limits!.Attempts;
            declaredTotal += attempts;

            if (organisation.AttemptCeilings.TryGetValue(step.Kind, out var ceiling) &&
                attempts > ceiling)
            {
                result.Problems.Add(new RunStartValidationProblem(
                    22,
                    $"step '{step.Id}' declares {attempts} attempts for '{step.Kind}', " +
                    $"above policy.attempts.ceiling {ceiling}"));
            }
        }

        if (organisation.PerRunAttemptCeiling is { } perRun && declaredTotal > perRun)
        {
            result.Problems.Add(new RunStartValidationProblem(
                22,
                $"plan declares {declaredTotal} attempts, above " +
                $"policy.attempts.ceiling.perRun {perRun}"));
        }
    }
}
