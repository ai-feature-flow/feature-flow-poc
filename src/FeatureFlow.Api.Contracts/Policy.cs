namespace FeatureFlow.Api.Contracts;

/// <summary>
/// <c>GET /runs/{id}/policy</c>: the policy in force for a run. Who answers each decision type of its plan, who may use each
/// override and whether the caller may, and its attempt limits.
/// </summary>
public sealed record RunPolicyView(string RunId, IReadOnlyList<DecisionPolicyView> Decisions, IReadOnlyList<OverridePolicyView> Overrides, AttemptPolicyView Attempts);

/// <summary>
/// A decision type of the run's plan: its steps, the roles that may answer and how many people must, whether organisation
/// policy allows it to be answered automatically, and where the rule comes from (<c>organisation</c>, <c>library</c> or <c>default</c>).
/// </summary>
public sealed record DecisionPolicyView(
    string DecisionType,
    IReadOnlyList<string> StepIds,
    IReadOnlyList<string> Roles,
    int MinCount,
    bool Automatic,
    string Source);

/// <summary>An override key: the roles that may use it, and whether the caller holds one of them.</summary>
public sealed record OverridePolicyView(string Key, IReadOnlyList<string> Roles, bool Yours);

/// <summary>The run's attempt limits: a step's default attempts, the run's ceiling, and the engine's hard ceilings per step and per run; null where the API cannot see one.</summary>
public sealed record AttemptPolicyView(long? DefaultAttempts, long? RunCeiling, long? HardStepCeiling, long? HardRunCeiling);
