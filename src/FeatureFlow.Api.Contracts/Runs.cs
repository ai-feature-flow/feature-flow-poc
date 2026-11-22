namespace FeatureFlow.Api.Contracts;

/// <summary>
/// <c>POST /runs</c>: the template to start, its repositories, and its parameters by name, each value JSON text (<c>"\"ACME-1\""</c>,
/// <c>"3"</c>, <c>"true"</c>). <see cref="ContinueAsNewEvents"/> is an administrator's override of the hand-over threshold.
/// </summary>
public sealed record StartRunRequest(
    string Template,
    IReadOnlyList<string>? Repositories = null,
    IReadOnlyDictionary<string, string>? Parameters = null,
    bool DryRun = false,
    string? ParentBranch = null,
    string? TrackerIssue = null,
    long? ContinueAsNewEvents = null);

public sealed record StartRunResponse(string RunId);

/// <summary><c>GET /runs</c>: the runs the caller views, most recently updated first.</summary>
public sealed record RunList(IReadOnlyList<RunSummary> Runs);

/// <summary>A run in a list: what a row shows.</summary>
public sealed record RunSummary(
    string RunId,
    string? ParentRunId,
    string Template,
    string Status,
    string Requester,
    IReadOnlyList<string> Repositories,
    bool DryRun,
    DateTimeOffset UpdatedAt);

/// <summary>A run as its page shows it; <see cref="Detail"/> says why it ended or what it waits on, when there is something to say.</summary>
public sealed record RunView(
    string RunId,
    string? ParentRunId,
    string Template,
    string Status,
    string Requester,
    IReadOnlyList<string> Repositories,
    bool DryRun,
    DateTimeOffset UpdatedAt,
    string? Detail);

/// <summary>
/// <c>GET /runs/{id}</c>: the run, its waits, its timeline (after <c>?after=</c>), its children, and its history of answers
/// and overrides, with the override keys the caller may use on it.
/// </summary>
public sealed record RunDetail(
    RunView Run,
    IReadOnlyList<WaitView> Waits,
    IReadOnlyList<TimelineEntry> Timeline,
    IReadOnlyList<RunSummary> Children,
    IReadOnlyList<AnswerView> Answers,
    IReadOnlyList<OverrideView> Overrides,
    IReadOnlyList<string> OverridesAllowed);

/// <summary>One entry of a run's timeline, numbered in the order the run recorded it.</summary>
public sealed record TimelineEntry(long Sequence, string Kind, string StepId, string? Detail, DateTimeOffset At);

/// <summary>What a wait is: a person's decision of some kind, or a status the engine shows (<c>stalled</c>, <c>stuck</c>) that no one answers.</summary>
public enum WaitKind
{
    Gate,
    Question,
    Intervention,
    PatchApproval,
    Breakpoint,
    Stalled,
    Stuck,
}

/// <summary>
/// A wait: its kind, step and decision type, the text to show (<see cref="Prompt"/>) and the choices that answer it, with
/// those a person must give a reason for (<see cref="ReasonRequired"/>, the choices that route the step as a failure; any
/// other reason is optional); what its step consumed, by reference; whether the caller may answer it now; how many people
/// must answer it the same way
/// (<see cref="MinCount"/>: 1 where the approval rule sets none, and for a closed wait or one whose rule cannot be read
/// now); for a decision several people must answer, who has answered each way; and once closed, the answer that closed it.
/// </summary>
public sealed record WaitView(
    string WaitId,
    WaitKind Kind,
    string StepId,
    string DecisionType,
    string Prompt,
    IReadOnlyList<string> Choices,
    IReadOnlyList<string> ReasonRequired,
    bool Open,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ConsumedArtifactView> Consumed,
    bool CanAnswer,
    int MinCount,
    IReadOnlyList<AnswerProgress>? Progress,
    ClosedAnswer? Answer);

/// <summary>An artifact a gate's step consumed: its key as staged (<c>name</c>, <c>parent/name</c>, <c>child/&lt;step&gt;/name</c>), its name and reference.</summary>
public sealed record ConsumedArtifactView(string Key, string Name, string? Ref);

/// <summary>People who have answered one way while a decision waits for more (<c>min_count</c>): the choice, who, and how many are needed.</summary>
public sealed record AnswerProgress(string Choice, IReadOnlyList<string> Actors, int Needed);

/// <summary>The answer that closed a wait: who, which choice, their reason, and when.</summary>
public sealed record ClosedAnswer(string Actor, string Choice, string? Reason, DateTimeOffset At);

/// <summary>An answer as recorded: the wait (<c>abort</c> for an abort), who, the choice, their reason, and the result (<c>sent</c>, the run's reply, <c>pending: ...</c> or <c>refused: ...</c>).</summary>
public sealed record AnswerView(string WaitId, string Actor, string Choice, string? Reason, string Result, DateTimeOffset At);

/// <summary>A run-time override as recorded: its key, who and why (null when no reason was given), the step and the change by argument name, and the result.</summary>
public sealed record OverrideView(
    string OverrideId,
    string Key,
    string Actor,
    string? Reason,
    string? StepId,
    IReadOnlyDictionary<string, string> Change,
    string Result,
    DateTimeOffset At);
