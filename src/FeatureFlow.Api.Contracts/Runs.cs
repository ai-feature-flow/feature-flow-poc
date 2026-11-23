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

/// <summary>
/// A run in a list: what a row shows. Its plan's level, the parent's step that started it, its tracker issue and title
/// (the issue's title, else null), when it started, its pull requests; and for a root of an active tree, the steps
/// current across the tree, how many waits there need the caller and how many steps are running. Runs recorded before
/// these fields existed have them null or zero.
/// </summary>
public sealed record RunSummary(
    string RunId,
    string? ParentRunId,
    string Template,
    string Status,
    string Requester,
    IReadOnlyList<string> Repositories,
    bool DryRun,
    DateTimeOffset UpdatedAt,
    string? Level = null,
    string? ParentStepId = null,
    TrackerIssueView? TrackerIssue = null,
    string? Title = null,
    DateTimeOffset? StartedAt = null,
    IReadOnlyList<PullRequestLink>? PullRequests = null,
    IReadOnlyList<CurrentStep>? CurrentSteps = null,
    int NeedsYou = 0,
    int Running = 0);

/// <summary>A run as its page shows it; <see cref="Detail"/> says why it ended or what it waits on, when there is something to say. The trailing fields are as on <see cref="RunSummary"/>.</summary>
public sealed record RunView(
    string RunId,
    string? ParentRunId,
    string Template,
    string Status,
    string Requester,
    IReadOnlyList<string> Repositories,
    bool DryRun,
    DateTimeOffset UpdatedAt,
    string? Detail,
    string? Level = null,
    string? ParentStepId = null,
    TrackerIssueView? TrackerIssue = null,
    string? Title = null,
    DateTimeOffset? StartedAt = null,
    IReadOnlyList<PullRequestLink>? PullRequests = null);

/// <summary>A run's tracker issue: its key (<c>ACME-12</c>, <c>owner/repo#200</c>), and its title and link when they could be read.</summary>
public sealed record TrackerIssueView(string Key, string? Title, string? Url);

/// <summary>A pull request a run opened, as a link: its repository and number, its URL, and its state (<c>open</c>, <c>merged</c>, <c>closed</c>).</summary>
public sealed record PullRequestLink(string Repository, int Number, string? Url, string State);

/// <summary>A step current in a run's tree: its run, its id, and its status as <see cref="PlanStepView.Status"/> names it.</summary>
public sealed record CurrentStep(string RunId, string StepId, string Status);

/// <summary>
/// A pull request as last read by the run: where, its title and state, whether it can merge, its branches and head commit,
/// its size and files (the first 300), its checks and reviews, and the step that read it and when.
/// </summary>
public sealed record PullRequestView(
    string Repository,
    int Number,
    string? Url,
    string Title,
    string State,
    bool Draft,
    bool? Mergeable,
    string? MergeableState,
    string Head,
    string HeadRevision,
    string Base,
    int? Additions,
    int? Deletions,
    int? ChangedFiles,
    IReadOnlyList<PullRequestFileView> Files,
    IReadOnlyList<CheckView> Checks,
    IReadOnlyList<string> ApprovedBy,
    IReadOnlyList<string> ChangesRequestedBy,
    string StepId,
    DateTimeOffset ReadAt);

/// <summary>A file a pull request changes: its path, its status (<c>added</c>, <c>modified</c>, <c>removed</c>, <c>renamed</c>) and its line counts.</summary>
public sealed record PullRequestFileView(string Path, string Status, int Additions, int Deletions);

/// <summary>A check on a pull request's head: its name, state, conclusion once complete, and link.</summary>
public sealed record CheckView(string Name, string State, string? Conclusion, string? Url);

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
    IReadOnlyList<string> OverridesAllowed,
    IReadOnlyList<PullRequestView>? PullRequests = null,
    IReadOnlyDictionary<string, string>? Inputs = null,
    IReadOnlyList<CurrentStep>? CurrentSteps = null);

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
/// those a person must give a reason for (<see cref="ReasonRequired"/>, the choices that route the step as a failure or end the run; any
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
    ClosedAnswer? Answer,
    string? Title = null,
    string? Summary = null,
    WaitCauseView? Cause = null,
    PatchView? Patch = null,
    PullRequestView? PullRequest = null,
    string? SessionId = null);

/// <summary>
/// An artifact a gate's step consumed: its key as staged (<c>name</c>, <c>parent/name</c>, <c>child/&lt;step&gt;/name</c>), its
/// name and reference; on an open gate, its declared type and the document's <c>changes</c> list where it has one.
/// </summary>
public sealed record ConsumedArtifactView(string Key, string Name, string? Ref, string? Type = null, IReadOnlyList<string>? Changes = null);

/// <summary>
/// Why an intervention stopped a step: the step, the outcome it ended with and the core's reason, what the step itself last
/// said, its attempts used and allowed, what sent it back each time, the attempts people granted it, and whether the last
/// two times it was sent back said the same thing.
/// </summary>
public sealed record WaitCauseView(
    string StepId,
    string? Outcome,
    string Reason,
    string? Said,
    int? AttemptsUsed,
    long? AttemptsAllowed,
    IReadOnlyList<SentBackView> SentBack,
    IReadOnlyList<GrantView> Grants,
    bool LikelyToRepeat);

/// <summary>A time a step was sent back: the execution, the step and outcome that sent it back where known, and what was said.</summary>
public sealed record SentBackView(int Execution, string? ByStep, string? Outcome, string Text);

/// <summary>Attempts a person granted a step: how many, who, why, and when.</summary>
public sealed record GrantView(long Attempts, string Actor, string? Reason, DateTimeOffset At);

/// <summary>
/// A plan patch awaiting approval: who proposed it (its type and step), why, what needs approval, where the run continues,
/// the plan revision it applies to, whether a child run proposed it, and the patch as a document.
/// </summary>
public sealed record PatchView(
    string ProposerType,
    string? ProposerStepId,
    string? Reason,
    IReadOnlyList<string> NeedsApproval,
    string? ContinuesAt,
    long BaseRevision,
    bool FromChild,
    string Document);

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
