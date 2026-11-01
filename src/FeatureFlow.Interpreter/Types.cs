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
    string? TrackerIssue = null);

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

    /// <summary>The run ceiling: attempts across every step of the run (F4).</summary>
    public long RunCeiling { get; init; } = 200;

    /// <summary>The organisation's per-step ceiling; a patch that sets an allowance above it relaxes F4 (check 30). Null: none.</summary>
    public long? StepCeiling { get; init; }

    /// <summary><c>planning.allowed_roles</c>: role names an agent's patch may use without approval (docs/05 section 6). Null: any.</summary>
    public IReadOnlyCollection<string>? AllowedRoles { get; init; }

    /// <summary>Whether organisation policy allows auto-resolution for a decision type (docs/04 section 4.2). Default: never.</summary>
    public Func<string, bool> AutoResolve { get; init; } = _ => false;
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
    PatchDocument? Patch = null) : RunInput;

public enum ErrorClass
{
    /// <summary>A provider or the platform failed: retried, never counted (docs/01 section 4).</summary>
    Infrastructure,

    /// <summary>The attempt failed, for example invalid output after correction turns: retried within the allowance.</summary>
    Work,
}

public sealed record StepFailed(string StepId, ErrorClass Class, string Reason) : RunInput;

public sealed record DecisionAnswered(string StepId, string Outcome) : RunInput;

/// <summary>
/// A patch from outside a step: a person through the API, or the engine. <see cref="By"/> is set by the shell
/// from the authenticated caller (F8); the document's own <c>proposed_by</c> is ignored.
/// </summary>
public sealed record PatchProposed(PatchDocument Patch, Proposer By) : RunInput;

public sealed record PatchAnswered(string WaitId, bool Approve) : RunInput;

/// <summary>The counts of the <c>findings</c> namespace, from the ledger (docs/03 section 5.3).</summary>
public sealed record FindingsChanged(IReadOnlyDictionary<string, long> Counts) : RunInput;

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
public sealed record OpenWait(string Id, WaitKind Kind, string StepId, string? Outcome, string Reason, PatchDocument? Patch = null);

/// <summary>Reads the <c>findings</c> namespace when a route is evaluated; each route evaluation reads each name once.</summary>
public interface IFindingsSource
{
    long Read(string name);
}
