namespace FeatureFlow.Spike.Contracts;

/// <summary>
/// The ten patch operations. docs/09 section 15.3.
/// </summary>
public static class PatchOps
{
    public const string InsertBefore = "insert_before";
    public const string InsertAfter = "insert_after";
    public const string ReplaceStep = "replace_step";
    public const string RemoveStep = "remove_step";
    public const string ReplaceInputs = "replace_inputs";
    public const string ReplaceLimits = "replace_limits";
    public const string ReplaceRole = "replace_role";
    public const string ChangeRoute = "change_route";
    public const string AddBranch = "add_branch";
    public const string RemoveBranch = "remove_branch";
    public const string AddChain = "add_chain";
}

public sealed class PatchChange
{
    public string Op { get; set; } = "";

    /// <summary>The step id the operation applies to.</summary>
    public string Target { get; set; } = "";

    /// <summary>The step to insert or replace with.</summary>
    public Step? Step { get; set; }

    public Dictionary<string, object?>? Inputs { get; set; }

    public StepLimits? Limits { get; set; }

    public string? Role { get; set; }

    /// <summary>For change_route: the outcome whose route changes.</summary>
    public string? Outcome { get; set; }

    /// <summary>For change_route: the new target.</summary>
    public RouteTarget? To { get; set; }

    /// <summary>For add_chain: where every non-completed outcome goes.</summary>
    public string? OnOther { get; set; }
}

public sealed class PlanPatch
{
    public string ApiVersion { get; set; } = "feature-flow/v1";

    public string Kind { get; set; } = "PlanPatch";

    /// <summary>Optimistic concurrency. docs/09 15.1.</summary>
    public int BaseRevision { get; set; }

    /// <summary>Where the run continues. docs/09 15.1.1.</summary>
    public string? ResumeAt { get; set; }

    public string? ProposedBy { get; set; }

    public string? Reason { get; set; }

    public List<PatchChange> Changes { get; set; } = new();
}

/// <summary>
/// The three results of applyPatch. docs/09 15.5 and docs/01 3.4: applied,
/// pending human decision, rejected. Not two.
/// </summary>
public enum PatchResultKind
{
    Applied,
    PendingHumanDecision,
    Rejected,
}

public sealed class PatchResult
{
    public PatchResultKind Kind { get; set; }

    public Plan? Plan { get; set; }

    public int NewRevision { get; set; }

    public string? ResumeAt { get; set; }

    /// <summary>Why it was rejected or held. Never silently dropped (R7).</summary>
    public List<string> Reasons { get; set; } = new();

    public static PatchResult Applied(Plan plan, int revision, string? resumeAt) =>
        new() { Kind = PatchResultKind.Applied, Plan = plan, NewRevision = revision, ResumeAt = resumeAt };

    public static PatchResult Held(string reason) =>
        new() { Kind = PatchResultKind.PendingHumanDecision, Reasons = { reason } };

    public static PatchResult Rejected(params string[] reasons) =>
        new() { Kind = PatchResultKind.Rejected, Reasons = reasons.ToList() };
}
