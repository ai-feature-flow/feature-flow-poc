namespace FeatureFlow.Api.Contracts;

/// <summary>A step's form (docs/03), as the readable form names it: a <c>run.child</c> step is an action, and a fragment's steps are in the plan in its place.</summary>
public enum StepForm
{
    Agent,
    Decision,
    Action,
    Chain,
    Parallel,
    ForEach,
}

/// <summary>
/// <c>GET /runs/{id}/plan</c>: the run's current plan revision: its readable form (docs/09 section 4.4) and each step with
/// its status; and the groups its steps sit in (a fragment's use, a branch of a <c>for_each</c> or <c>parallel</c>).
/// </summary>
public sealed record PlanView(
    string RunId,
    int Revision,
    string Template,
    string Readable,
    IReadOnlyList<PlanStepView> Steps,
    IReadOnlyList<PlanGroupView>? Groups = null);

/// <summary>What a group of steps is: a fragment's use, or one branch of a <c>for_each</c> or <c>parallel</c> step.</summary>
public enum GroupKind
{
    Fragment,
    Branch,
}

/// <summary>How a step of a proposed plan (<c>?proposed=</c>) differs from the current one.</summary>
public enum PlanChange
{
    Added,
    Removed,
    Changed,
}

/// <summary>A group of steps: its id (a fragment's use id, or <c>&lt;container&gt;#&lt;index&gt;</c>), the group or step it sits in, its kind, its label and, for a branch, its item.</summary>
public sealed record PlanGroupView(string Id, string? Parent, GroupKind Kind, string Label, string? Item);

/// <summary>
/// A step of the plan: its id and container, its form, what it does, its outcomes, whether it is protected, its status
/// from the run, whether a breakpoint is set on it, and the outcome it was skipped with; the group or step it sits in,
/// how often it ran, its attempts used, allowed and granted, the child run it started, when its status last changed, on a
/// proposed plan how it differs, and its <c>with</c> and <c>limits</c> as written in the plan (block YAML; null where the
/// step gives none), which an amend's <c>set</c> replaces whole.
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
    string? SkippedWith,
    string? Parent = null,
    int Executions = 0,
    int? AttemptsUsed = null,
    long? AttemptsAllowed = null,
    long AttemptsGranted = 0,
    string? ChildRunId = null,
    DateTimeOffset? ChangedAt = null,
    PlanChange? Change = null,
    string? With = null,
    string? Limits = null);

/// <summary>An outcome of a step: its name, its class, where it routes, and the artifacts the step produces on it (by name; empty where none).</summary>
public sealed record PlanOutcomeView(string Name, string Class, string? Route, IReadOnlyList<string>? Produces = null);
