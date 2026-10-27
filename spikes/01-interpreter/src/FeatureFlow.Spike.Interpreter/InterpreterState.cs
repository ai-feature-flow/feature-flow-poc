using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// Workflow state. docs/01 3.1: planRevision, cursors, stepRuns, findingsSummary,
/// attemptBudgets, pendingDecision.
/// </summary>
public sealed class InterpreterState
{
    public Plan Plan { get; set; } = new();

    /// <summary>One cursor per in-flight branch. docs/01 3.3.1.</summary>
    public List<string> Cursors { get; set; } = new();

    public Dictionary<string, StepRun> StepRuns { get; set; } = new(StringComparer.Ordinal);

    public Dictionary<string, int> AttemptBudgets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Consecutive resumes with no progress, per step. docs/01 3.7.</summary>
    public Dictionary<string, int> ResumesWithoutProgress { get; set; } = new(StringComparer.Ordinal);

    public List<PatchRecord> PatchRecords { get; set; } = new();

    public int ContinueAsNewCount { get; set; }

    /// <summary>History event count observed at each continue-as-new. Measurement 1.</summary>
    public List<HistorySample> HistorySamples { get; set; } = new();

    public int StepsExecuted { get; set; }

    public string? TerminalStatus { get; set; }

    /// <summary>Spike-only: the last infrastructure failure, for diagnosis.</summary>
    public string? LastInfrastructureError { get; set; }

    public InterpreterState Clone()
    {
        var copy = (InterpreterState)MemberwiseClone();
        copy.Plan = Plan.Clone();
        copy.Cursors = new List<string>(Cursors);
        copy.StepRuns = new Dictionary<string, StepRun>(StepRuns, StringComparer.Ordinal);
        copy.AttemptBudgets = new Dictionary<string, int>(AttemptBudgets, StringComparer.Ordinal);
        copy.ResumesWithoutProgress =
            new Dictionary<string, int>(ResumesWithoutProgress, StringComparer.Ordinal);
        copy.PatchRecords = new List<PatchRecord>(PatchRecords);
        copy.HistorySamples = new List<HistorySample>(HistorySamples);
        return copy;
    }
}

public sealed class StepRun
{
    public string StepId { get; set; } = "";

    public string Outcome { get; set; } = "";

    public int Attempts { get; set; }

    public int Resumes { get; set; }

    /// <summary>True if this step ever restarted when it should have resumed. Measurement 6.</summary>
    public bool RestartedWhenShouldHaveResumed { get; set; }

    /// <summary>Spike-only: infrastructure failures seen, which must not consume attempts.</summary>
    public int InfrastructureFailures { get; set; }

    /// <summary>
    /// Spike-only: the sandbox command handle, key and session seen on the last
    /// attempt. The resume evidence from docs/01 3.6, read by the harness to
    /// prove re-attachment rather than restart.
    /// </summary>
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
}
