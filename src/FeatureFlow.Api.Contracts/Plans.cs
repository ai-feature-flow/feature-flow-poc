namespace FeatureFlow.Api.Contracts;

/// <summary>A step's form (docs/03).</summary>
public enum StepForm
{
    Agent,
    Action,
    Decision,
    Child,
    Use,
}

/// <summary>
/// <c>GET /runs/{id}/plan</c>: the run's current plan revision: its readable form (docs/09 section 4.4) and each step with
/// its status.
/// </summary>
public sealed record PlanView(string RunId, int Revision, string Template, string Readable, IReadOnlyList<PlanStepView> Steps);

/// <summary>
/// A step of the plan: its id and container, its form, what it does, its outcomes, whether it is protected, its status
/// from the run, whether a breakpoint is set on it, and the outcome it was skipped with.
/// </summary>
public sealed record PlanStepView(
    string Id,
    string? Container,
    StepForm Form,
    string What,
    IReadOnlyList<PlanOutcomeView> Outcomes,
    bool Protected,
    string Status,
    bool Breakpoint,
    string? SkippedWith);

/// <summary>An outcome of a step: its name, its class, and where it routes.</summary>
public sealed record PlanOutcomeView(string Name, string Class, string? Route);
