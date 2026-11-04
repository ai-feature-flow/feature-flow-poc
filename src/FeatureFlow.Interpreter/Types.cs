using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

/// <summary>Run statuses (docs/01 section 1.1).</summary>
public enum RunStatus
{
    Running,
    Completed,
    Failed,
    Rejected,
    Partial,
    Aborted,
    Incomplete,
}

public static class RunStatuses
{
    public static string Name(this RunStatus status) => status.ToString().ToLowerInvariant();

    public static RunStatus? Parse(string text) => text switch
    {
        "running" => RunStatus.Running,
        "completed" => RunStatus.Completed,
        "failed" => RunStatus.Failed,
        "rejected" => RunStatus.Rejected,
        "partial" => RunStatus.Partial,
        "aborted" => RunStatus.Aborted,
        "incomplete" => RunStatus.Incomplete,
        _ => null,
    };
}

/// <summary>
/// What the core needs to know about the run, fixed at run start. <see cref="Parameters"/> are the request's
/// values; declared defaults fill the rest.
/// </summary>
public sealed record RunInfo(
    string Id,
    IReadOnlyList<string> Repositories,
    IReadOnlyDictionary<string, YNode> Parameters,
    string? ParentId = null,
    string? TrackerIssue = null,
    string? ParentBranch = null,
    IReadOnlyDictionary<string, string>? DefaultBranches = null);

/// <summary>
/// Limits and policy the core enforces, carried in the run's state because workflow code cannot read
/// configuration (docs/05 section 8). Defaults are the engine's.
/// </summary>
public sealed record RunOptions
{
    /// <summary>A step's attempt allowance when neither the step nor its document's <c>defaults.limits</c> sets one.</summary>
    public long DefaultAttempts { get; init; } = 3;

    /// <summary><c>limits.races</c> for <c>scm.merge</c> when unset (docs/04 section 7.3; LOG 61).</summary>
    public long DefaultRaces { get; init; } = 10;

    /// <summary>
    /// Whether the work branch follows <c>metadata.branch_pattern</c> (LOG 97). The shell sets it from a patch marker,
    /// so runs recorded before M4 keep <c>ff/&lt;level&gt;/&lt;run id&gt;</c>.
    /// </summary>
    public bool BranchFromPattern { get; init; }

    /// <summary>The run ceiling: attempts across every step of the run (F4).</summary>
    public long RunCeiling { get; init; } = 200;

    /// <summary>The organisation's per-step ceiling; a patch that sets an allowance above it relaxes F4 (check 30). Null: none.</summary>
    public long? StepCeiling { get; init; }

    /// <summary><c>planning.allowed_roles</c>: role names an agent's patch may use without approval (docs/05 section 6). Null: any.</summary>
    public IReadOnlyCollection<string>? AllowedRoles { get; init; }

    /// <summary>Whether organisation policy allows auto-resolution for a decision type (docs/04 section 4.2). Default: never.</summary>
    public Func<string, bool> AutoResolve { get; init; } = _ => false;

    /// <summary>
    /// The organisation's hard ceilings (F4; docs/05 section 5): an attempt grant that takes a step's allowance or
    /// the run ceiling above them needs an administrator (<c>grant_attempts_beyond</c>). Null: none.
    /// </summary>
    public long? HardStepCeiling { get; init; }

    public long? HardRunCeiling { get; init; }
}

/// <summary>Something the core asks the shell to do. The Temporal workflow (M3) or the re-drive runner carries it out.</summary>
public abstract record Command;

public sealed record StartStep(string StepId, int Execution) : Command;

public sealed record OpenDecision(string StepId, int Execution) : Command;

public sealed record RaiseIntervention(string WaitId, string StepId, string? Outcome, string Reason) : Command;

public sealed record RequestPatchApproval(string WaitId, PatchDocument Patch) : Command;

public sealed record CancelStep(string StepId) : Command;

/// <summary>A decision, intervention or patch approval that is no longer wanted: its join decided, or the run ended.</summary>
public sealed record CloseWait(string WaitId, string StepId) : Command;

public sealed record CleanUp(string StepId) : Command;

public sealed record EndRun(RunStatus Status) : Command;

/// <summary>A child run's agent step proposed a patch to the parent's plan (docs/03 section 9.4): the shell signals the parent.</summary>
public sealed record SendPatchToParent(string StepId, PatchDocument Patch) : Command;

/// <summary>Something that happened, given to the core to apply.</summary>
public abstract record RunInput;

/// <summary>
/// A step ended with an outcome. <see cref="Patch"/> is a patch the step proposed, or the patch
/// <c>plan.expand</c> built (<see cref="PlanExpansion"/>).
/// </summary>
public sealed record StepCompleted(
    string StepId,
    string Outcome,
    IReadOnlyDictionary<string, object?>? Outputs = null,
    IReadOnlyDictionary<string, string>? Artifacts = null,
    PatchDocument? Patch = null,
    bool PatchToParent = false) : RunInput;

public enum ErrorClass
{
    /// <summary>A provider or the platform failed: retried, never counted (docs/01 section 4).</summary>
    Infrastructure,

    /// <summary>The attempt failed, for example invalid output after correction turns: retried within the allowance.</summary>
    Work,
}

public sealed record StepFailed(string StepId, ErrorClass Class, string Reason) : RunInput;

/// <summary>
/// A running step's activity was stopped before it ended, for example a wait at a hand-over (LOG 139). The step returns
/// to pending at the same execution: no attempt is used, and when the core next advances it starts that execution again,
/// so whatever the execution recorded (when a wait began, the head it waits on) still applies.
/// </summary>
public sealed record StepInterrupted(string StepId, string Reason) : RunInput;

/// <summary>
/// A person answered a decision step. <see cref="Artifacts"/> are what an interactive session's submission produced, by
/// declared name (docs/04 section 4.4): when given, they must be exactly the artifacts the outcome promises, and are the
/// step's, as a completed step's are. Null: the answer produces nothing.
/// </summary>
public sealed record DecisionAnswered(string StepId, string Outcome, IReadOnlyDictionary<string, string>? Artifacts = null) : RunInput;

/// <summary>
/// A patch from outside a step: a person through the API, or the engine. <see cref="By"/> is set by the shell
/// from the authenticated caller (F8); the document's own <c>proposed_by</c> is ignored.
/// </summary>
public sealed record PatchProposed(PatchDocument Patch, Proposer By) : RunInput;

public sealed record PatchAnswered(string WaitId, bool Approve) : RunInput;

/// <summary>The recovery actions that answer an intervention (docs/01 section 7.2).</summary>
public enum RecoveryKind
{
    /// <summary>Run the stopped step again.</summary>
    Retry,

    /// <summary>Run a named step next, optionally with <c>clean_up</c>.</summary>
    GoTo,

    /// <summary>Run a named planning step next.</summary>
    Replan,

    /// <summary>Add attempts to the stopped step's allowance, then retry.</summary>
    GrantAttempts,

    /// <summary>End the run with a chosen status.</summary>
    End,
}

/// <summary>A recovery action with its arguments: the step for go to and re-plan, the attempts to grant, the status to end with.</summary>
/// <remarks><see cref="BeyondHardCeiling"/> is set by the shell when the authenticated actor may grant beyond the hard ceiling (an administrator).</remarks>
public sealed record Recovery(RecoveryKind Kind, string? StepId = null, bool CleanUp = false, long Attempts = 0, RunStatus? Status = null, bool BeyondHardCeiling = false);

/// <summary>A person answered an intervention with a recovery action. The shell names the actor; the core does not need it.</summary>
public sealed record InterventionAnswered(string WaitId, Recovery Action) : RunInput;

/// <summary>A person stopped the run (docs/05 section 5, <c>abort</c>): it ends <c>aborted</c>.</summary>
public sealed record RunAborted(string Reason) : RunInput;

/// <summary>
/// A running step cannot produce a trustworthy outcome, for example its inputs cannot be evaluated: it stops and
/// an intervention is raised instead of a guessed outcome (docs/04 section 1, "cannot answer").
/// </summary>
public sealed record StepCannotAnswer(string StepId, string Reason) : RunInput;

/// <summary>
/// A child run proposed a patch to this run's plan (docs/03 section 9.4). <see cref="ChildStepId"/> is the
/// <c>run.child</c> step that started the child; <see cref="ProposerStepId"/> is the agent step in the child.
/// </summary>
public sealed record ChildPatchProposed(string ChildStepId, PatchDocument Patch, string ProposerStepId) : RunInput;

/// <summary>
/// The counts of the <c>findings</c> namespace, from the ledger (docs/03 section 5.3), read at ledger
/// <see cref="Version"/>. Counts read at an older version than the run holds are stale (parallel recorders) and ignored.
/// </summary>
public sealed record FindingsChanged(IReadOnlyDictionary<string, long> Counts, long Version = 0) : RunInput;

public enum PatchStatus
{
    Applied,
    Pending,
    Rejected,
}

/// <summary>The result of a patch (docs/03 section 10.3), with the checks that blocked it.</summary>
public sealed record PatchResult(PatchStatus Status, IReadOnlyList<Diagnostic> Diagnostics)
{
    public IReadOnlyList<int> BlockingChecks => Diagnostics.Where(d => d.Blocks).Select(d => d.Check).Distinct().Order().ToList();
}

/// <summary>Whether an input was accepted. A refused input changes nothing.</summary>
public sealed record ApplyResult(bool Accepted, IReadOnlyList<string> Errors, PatchResult? Patch = null)
{
    public static readonly ApplyResult Ok = new(true, []);

    public static ApplyResult Refused(params string[] errors) => new(false, errors);
}

/// <summary>One entry of the run's timeline, for tests and the record.</summary>
public sealed record TraceEntry(string Kind, string StepId, string? Detail = null)
{
    public override string ToString() => Detail is null ? $"{Kind} {StepId}" : $"{Kind} {StepId}: {Detail}";
}

public enum WaitKind
{
    Decision,
    Intervention,
    PatchApproval,
}

/// <summary>A durable wait for a person: an open decision, an intervention or a patch approval.</summary>
public sealed record OpenWait(string Id, WaitKind Kind, string StepId, string? Outcome, string Reason, PatchDocument? Patch = null, bool FromChild = false);

/// <summary>Reads the <c>findings</c> namespace when a route is evaluated; each route evaluation reads each name once.</summary>
public interface IFindingsSource
{
    long Read(string name);
}
