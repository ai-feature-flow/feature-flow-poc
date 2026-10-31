namespace Sp1;

/// <summary>The variant under test: the main rule, a negative control, or the exploratory run.</summary>
public static class Modes
{
    public const string Main = "main";

    /// <summary>N1: children signal the parent's first run id, not its workflow id.</summary>
    public const string SignalRunId = "n1";

    /// <summary>N2: children use the default parent-close policy (terminate).</summary>
    public const string Terminate = "n2";

    /// <summary>N3: answers are not carried across a hand-over.</summary>
    public const string DropAnswers = "n3";

    /// <summary>X1: the validator does not reject answers while draining.</summary>
    public const string NoStale = "x1";

    public static readonly string[] All = [Main, SignalRunId, Terminate, DropAnswers, NoStale];
}

public sealed class ChildPlan
{
    public string Id { get; set; } = "";
    public int Seconds { get; set; }
    public string Outcome { get; set; } = "";
    public int Wave { get; set; }
    public bool Started { get; set; }
    public bool Ended { get; set; }
    public string? RecordedOutcome { get; set; }
    public int EndRecords { get; set; }
    public int EndedInRun { get; set; } = -1;
}

public sealed class Decision
{
    public string Id { get; set; } = "";
    public string? Answer { get; set; }
}

public sealed record AnswerRecord(string DecisionId, string Answer, int Run);

/// <summary>A snapshot taken at each hand-over, after draining.</summary>
public sealed record HandOver(int Run, long HistoryEvents, int InFlight, int RunningChildren,
    int OpenDecisions, int PendingConfirms);

/// <summary>Everything the parent carries across continue-as-new.</summary>
public sealed class ParentState
{
    public string Mode { get; set; } = Modes.Main;
    public int Seed { get; set; }
    public int HistoryThreshold { get; set; } = 150;
    public string? FirstRunId { get; set; }
    public List<int> WorkMillis { get; set; } = [];
    public int NextWork { get; set; }
    public List<ChildPlan> Children { get; set; } = [];
    public List<Decision> Decisions { get; set; } = [];
    public List<AnswerRecord> AnswerLog { get; set; } = [];
    public List<string> PendingConfirms { get; set; } = [];
    public List<HandOver> HandOvers { get; set; } = [];
    public int ActivitiesStarted { get; set; }
    public int ActivitiesCompleted { get; set; }
    public int StartsWhileDraining { get; set; }
    public int SignalsReceived { get; set; }
    public int SignalsWhileDraining { get; set; }
    public int DuplicateAnswersAcked { get; set; }
}

/// <summary>What the query returns: the carried state plus this run's transient flags.</summary>
public sealed record ParentView(ParentState State, bool Draining, int InFlight, string RunId);

public sealed record Answer(string DecisionId, string Outcome);

public sealed record ChildInput(string Id, int Seconds, string Outcome, string ParentId, string? ParentRunId);

public sealed record ChildRead(string Status, string? Result);
