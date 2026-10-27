using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// What a completed step contributes to workflow state. docs/09 8.3: expressions
/// see outcomes, artifact references, published outputs and recorded choices.
/// Nothing else. This is the whole per-step footprint in carried state.
/// </summary>
public sealed class StepOutcomeRecord
{
    public string Outcome { get; set; } = "";

    /// <summary>Published outputs, capped at 16 entries and 256 bytes (docs/09 8.3.2).</summary>
    public Dictionary<string, string> Outputs { get; set; } = new(StringComparer.Ordinal);

    /// <summary>A recorded choice from a gate (docs/09 8.3).</summary>
    public string? Choice { get; set; }
}

/// <summary>
/// Workflow state carried across continue-as-new.
///
/// docs/01 3.5 and DP5: workflow state is compact and holds references only.
/// Review 12 A1: the continue-as-new input is ONE payload, so its ceiling is the
/// 2 MB payload limit, not the 50 MB history limit. Carried state must therefore
/// be bounded by plan size, not by steps executed.
///
/// What is carried: the plan, cursors, and per step only what an expression can
/// read. Attempt budgets and progress counters are carried only for steps that
/// can still run. Everything else goes to the record store.
/// </summary>
public sealed class InterpreterState
{
    public Plan Plan { get; set; } = new();

    /// <summary>One cursor per in-flight branch. docs/01 3.3.1.</summary>
    public List<string> Cursors { get; set; } = new();

    /// <summary>Per step, only what expressions can read. Bounded by plan size.</summary>
    public Dictionary<string, StepOutcomeRecord> StepOutcomes { get; set; } =
        new(StringComparer.Ordinal);

    /// <summary>Attempts consumed, for steps that can still run.</summary>
    public Dictionary<string, int> AttemptBudgets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Consecutive resumes with no progress, for steps that can still run.</summary>
    public Dictionary<string, int> ResumesWithoutProgress { get; set; } =
        new(StringComparer.Ordinal);

    public int ContinueAsNewCount { get; set; }

    public int StepsExecuted { get; set; }

    public string? TerminalStatus { get; set; }

    /// <summary>Spike-only: the last infrastructure failure, for diagnosis.</summary>
    public string? LastInfrastructureError { get; set; }

    /// <summary>
    /// Review 12 A4 / D24: the run is waiting on infrastructure and has been for
    /// long enough to alert. It never fails on its own; a human may abort it.
    /// </summary>
    public bool StalledOnInfrastructure { get; set; }

    public DateTimeOffset? StalledSince { get; set; }

    public string? StalledStepId { get; set; }

    /// <summary>
    /// Spike-only: size of the state carried into this run, in bytes. Review 12
    /// A1 asks for the continue-as-new input size to be reported per
    /// continue-as-new, so it can be shown to be bounded by plan size.
    /// </summary>
    public long CarriedStateBytes { get; set; }

    public InterpreterState Clone()
    {
        var copy = (InterpreterState)MemberwiseClone();
        copy.Plan = Plan.Clone();
        copy.Cursors = new List<string>(Cursors);
        copy.StepOutcomes = new Dictionary<string, StepOutcomeRecord>(StringComparer.Ordinal);
        foreach (var (id, record) in StepOutcomes)
        {
            copy.StepOutcomes[id] = new StepOutcomeRecord
            {
                Outcome = record.Outcome,
                Choice = record.Choice,
                Outputs = new Dictionary<string, string>(record.Outputs, StringComparer.Ordinal),
            };
        }
        copy.AttemptBudgets = new Dictionary<string, int>(AttemptBudgets, StringComparer.Ordinal);
        copy.ResumesWithoutProgress =
            new Dictionary<string, int>(ResumesWithoutProgress, StringComparer.Ordinal);
        return copy;
    }

    /// <summary>
    /// Drop per-step bookkeeping for steps that have finished. A completed step
    /// can never run again, so its attempt budget and progress counter are dead
    /// weight in every subsequent run.
    /// </summary>
    public void PruneCompleted()
    {
        foreach (var id in StepOutcomes.Keys)
        {
            AttemptBudgets.Remove(id);
            ResumesWithoutProgress.Remove(id);
        }
    }
}

/// <summary>
/// The full per-step record. This is what goes to the record store, not to
/// carried state. docs/01 3.6: an agent.run step records, per attempt, the
/// commit it resumed from and the session reference it used.
/// </summary>
public sealed class StepRunDetail
{
    public string StepId { get; set; } = "";

    public string Outcome { get; set; } = "";

    public int Attempts { get; set; }

    public int Resumes { get; set; }

    /// <summary>True if this step ever restarted when it should have resumed. Measurement 6.</summary>
    public bool RestartedWhenShouldHaveResumed { get; set; }

    /// <summary>Infrastructure failures seen, which must not consume attempts.</summary>
    public int InfrastructureFailures { get; set; }

    /// <summary>The sandbox command handle, key and session seen on the last attempt.</summary>
    public string? CommandHandle { get; set; }

    public string? SandboxKey { get; set; }

    public string? SessionRef { get; set; }

    public List<AttemptProgress> Attempts_ { get; set; } = new();

    public Dictionary<string, string> Outputs { get; set; } = new(StringComparer.Ordinal);
}

public sealed class PatchRecord
{
    public int BaseRevision { get; set; }

    public int? NewRevision { get; set; }

    public string Result { get; set; } = "";

    public string? Reason { get; set; }

    public string? ProposedBy { get; set; }

    public DateTimeOffset RecordedAt { get; set; }
}

public sealed class HistorySample
{
    public int ContinueAsNewIndex { get; set; }

    public long HistoryLengthBytes { get; set; }

    public int HistoryEventCount { get; set; }

    public int StepsExecuted { get; set; }

    /// <summary>Review 12 A1: the continue-as-new input size, per continue-as-new.</summary>
    public long CarriedStateBytes { get; set; }
}
