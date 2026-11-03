namespace FeatureFlow.Interpreter;

/// <summary>One execution of a step: its outcome, outputs and artifact references (docs/03 section 4.2).</summary>
public sealed class Execution
{
    public required int Number { get; init; }
    public string? Outcome { get; set; }
    public Dictionary<string, object?> Outputs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Artifacts { get; } = new(StringComparer.Ordinal);
}

/// <summary>A step's counters and its current and previous executions.</summary>
public sealed class StepRecord
{
    /// <summary>Executions started: <c>steps.&lt;id&gt;.executions</c>.</summary>
    public int Executions { get; set; }

    /// <summary>Attempts used against the step's allowance (docs/01 section 4).</summary>
    public int AttemptsUsed { get; set; }

    /// <summary>For <c>scm.merge</c>: executions that ended <c>base_moved</c>, against <c>limits.races</c> (LOG 61).</summary>
    public int RacesUsed { get; set; }

    /// <summary>Attempts a person granted at an intervention, added to the step's allowance and race allowance.</summary>
    public long AttemptsGranted { get; set; }

    public Execution? Current { get; set; }
    public Execution? Previous { get; set; }
}

public enum BranchStatus
{
    Waiting,
    Running,
    Succeeded,
    Failed,
    Cancelled,
}

/// <summary>A <c>parallel</c> or chain that has been entered: the status of each branch, in branch order.</summary>
public sealed class ContainerRecord
{
    public required List<int> BranchIndexes { get; init; }
    public required List<BranchStatus> Branches { get; init; }
    public bool Active { get; set; } = true;
}

/// <summary>A step a route has entered and the core has not yet started (the run's position, docs/01 section 1.2).</summary>
public sealed record Entry(string StepId, bool CleanUp);

/// <summary>
/// The run's carried state (docs/02 section 2.1). Every collection the core iterates is a list or an ordinal
/// sorted set, so the core's behaviour depends only on the plan and this state; dictionaries are only looked up.
/// </summary>
public sealed class RunState
{
    public RunStatus Status { get; set; } = RunStatus.Running;

    /// <summary>Plan revision: 1 at run start, one more for each applied patch.</summary>
    public int Revision => 1 + Patches.Count;

    public List<Model.Plans.PatchDocument> Patches { get; } = [];

    /// <summary>Every step id that has been in any revision, for check 3.</summary>
    public SortedSet<string> IdHistory { get; } = new(StringComparer.Ordinal);

    public Dictionary<string, StepRecord> Steps { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, ContainerRecord> Containers { get; } = new(StringComparer.Ordinal);
    public List<Entry> Pending { get; } = [];
    public List<string> InFlight { get; } = [];
    public List<OpenWait> Waits { get; } = [];

    /// <summary>Attempts used by every step of the run, against the run ceiling (F4).</summary>
    public long RunAttempts { get; set; }

    /// <summary>Attempts granted at interventions, added to the run ceiling as well as to the step's allowance.</summary>
    public long RunCeilingGranted { get; set; }

    public Dictionary<string, long> Findings { get; } = new(StringComparer.Ordinal);

    /// <summary>The ledger version <see cref="Findings"/> was read at; older counts are not applied over newer ones (review F3).</summary>
    public long FindingsVersion { get; set; }
    public List<TraceEntry> Trace { get; } = [];
    public int NextWaitNumber { get; set; } = 1;
}
