namespace FeatureFlow.Spike.Contracts;

/// <summary>
/// The outcome vocabulary. docs/09 section 4.1, from docs/06 section 1.1 plus
/// any_failed and not_approved.
/// </summary>
public static class Outcomes
{
    public const string Completed = "completed";
    public const string Blocked = "blocked";
    public const string Failed = "failed";
    public const string AttemptsExhausted = "attempts_exhausted";
    public const string ReplanRequested = "replan_requested";
    public const string ReplanParent = "replan_parent";
    public const string Partial = "partial";
    public const string Approved = "approved";
    public const string Rejected = "rejected";
    public const string NotApproved = "not_approved";
    public const string FindingsOpen = "findings_open";
    public const string Ready = "ready";
    public const string AnyFailed = "any_failed";

    /// <summary>docs/09 section 4.2. Every kind must route every outcome it can return.</summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> ByKind =
        new Dictionary<string, IReadOnlySet<string>>
        {
            ["agent.run"] = Set(Completed, Blocked, Failed, AttemptsExhausted, ReplanRequested),
            ["human.gate"] = Set(Approved, Rejected),
            ["human.session"] = Set(Completed, Rejected),
            ["policy.auto_approve"] = Set(Approved, NotApproved),
            ["run.child"] = Set(Completed, Failed, ReplanParent, AttemptsExhausted),
            ["parallel"] = Set(Completed, AnyFailed),
            ["plan.replan"] = Set(ReplanRequested, Failed, AttemptsExhausted),
            ["plan.validate"] = Set(Completed, Failed),
            ["scm.create_branch"] = Set(Completed, Failed, AttemptsExhausted),
            ["scm.merge_base_forward"] = Set(Completed, Blocked, AttemptsExhausted),
            ["scm.open_pr"] = Set(Completed, Failed, AttemptsExhausted),
            ["scm.update_pr"] = Set(Completed, Failed, AttemptsExhausted),
            ["scm.merge"] = Set(Completed, Blocked, Rejected, AttemptsExhausted),
            ["scm.revert"] = Set(Completed, Failed, AttemptsExhausted),
            ["ci.trigger"] = Set(Completed, Failed, AttemptsExhausted),
            ["ci.wait_checks"] = Set(Completed, Failed, Blocked, AttemptsExhausted),
            ["ci.read_results"] = Set(Completed, Partial, AttemptsExhausted),
            ["test.run_tiers"] = Set(Completed, Failed, AttemptsExhausted),
            ["test.run_composition"] = Set(Completed, Failed, AttemptsExhausted),
            ["analysis.run"] = Set(Completed, Failed, AttemptsExhausted),
            ["review.run"] = Set(Completed, FindingsOpen, Failed, AttemptsExhausted),
            ["findings.triage"] = Set(Completed, Blocked, AttemptsExhausted),
            ["findings.post_declines"] = Set(Completed, Failed, AttemptsExhausted),
            ["findings.evaluate_readiness"] = Set(Ready, FindingsOpen),
            ["fix.verify"] = Set(Completed, Failed, AttemptsExhausted),
            ["wait.for_state"] = Set(Completed, Blocked, AttemptsExhausted),
            ["artifact.bundle"] = Set(Completed, Failed, AttemptsExhausted),
            ["artifact.validate"] = Set(Completed, Failed),
            ["artifact.import"] = Set(Completed, Failed, AttemptsExhausted),
            ["artifact.publish"] = Set(Completed, Failed, AttemptsExhausted),
            ["delivery.deploy_order"] = Set(Completed),
            ["delivery.deploy"] = Set(Completed, Failed, Blocked, AttemptsExhausted),
            ["delivery.gate"] = Set(Approved, Rejected),
            ["delivery.report"] = Set(Completed, AttemptsExhausted),
            ["tracker.create"] = Set(Completed, Failed, AttemptsExhausted),
            ["tracker.update"] = Set(Completed, Failed, AttemptsExhausted),
            ["tracker.transition"] = Set(Completed, Blocked, AttemptsExhausted),
            ["tracker.link"] = Set(Completed, Failed, AttemptsExhausted),
            ["tracker.comment"] = Set(Completed, Failed, AttemptsExhausted),
            ["notify.send"] = Set(Completed, Failed, AttemptsExhausted),
        };

    /// <summary>
    /// Local, pure steps. docs/09 section 4.2: they do not consume attempts, so
    /// they do not return attempts_exhausted.
    /// </summary>
    public static readonly IReadOnlySet<string> LocalKinds = new HashSet<string>
    {
        "plan.validate", "policy.auto_approve", "findings.evaluate_readiness",
        "delivery.deploy_order", "artifact.validate",
    };

    private static IReadOnlySet<string> Set(params string[] outcomes) =>
        new HashSet<string>(outcomes, StringComparer.Ordinal);
}
