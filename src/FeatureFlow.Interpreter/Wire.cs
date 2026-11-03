namespace FeatureFlow.Interpreter.Wire;

// What the API and the Temporal workflow exchange. Plain serialisable records with no Temporal type, so both
// the API and the engine reference them without referencing each other (docs/11 section 2.3).

/// <summary>The names of the run workflow and of its updates, signals and query.</summary>
public static class RunWorkflowNames
{
    public const string Workflow = "Run";

    /// <summary>Update: a person's answer to a decision, intervention or patch approval (docs/02 section 7).</summary>
    public const string Answer = "answer";

    /// <summary>Update: a person stops the run.</summary>
    public const string Abort = "abort";

    /// <summary>Signal, from a child to its parent's workflow id: the child has ended. A hint; the parent reads the child to confirm.</summary>
    public const string ChildEnded = "child_ended";

    /// <summary>Signal, from a child to its parent's workflow id: a patch to the parent's plan (docs/03 section 9.4).</summary>
    public const string ParentPatch = "parent_patch";

    /// <summary>Signal, from a parent to a child it no longer needs (a decided join, or the parent's end).</summary>
    public const string ParentCancelled = "parent_cancelled";

    /// <summary>Query: the run's state as this workflow holds it, for tests. The API reads the record store.</summary>
    public const string View = "view";

    /// <summary>
    /// Signal, from the agent worker's engine tools: an agent asked a question (<c>ask_human</c>, docs/04 section
    /// 3.4). Idempotent by the question id, which the sandbox client assigned (LOG 59).
    /// </summary>
    public const string QuestionOpened = "question_opened";
}

/// <summary>A question an agent asked: the run opens a question decision for it, answered with the <c>answer</c> update.</summary>
public sealed record QuestionSignal(string QuestionId, string StepId, string StepKey, string Text, IReadOnlyList<string> Options, long TimeoutSeconds);

/// <summary>A question the run holds open, carried across continue-as-new; <see cref="Deadline"/> is when it times out.</summary>
public sealed record OpenQuestion(QuestionSignal Question, DateTimeOffset Deadline);

/// <summary>An approval rule's decision type pattern and whether it allows auto-resolution (docs/05 section 3.2).</summary>
public sealed record AutoRule(string Match, bool Auto);

/// <summary>
/// The limits and policy a run enforces, fixed when it starts (docs/05 section 8) and carried in its state,
/// because workflow code cannot read configuration.
/// </summary>
public sealed record RunPolicy(
    long DefaultAttempts,
    long DefaultRaces,
    long RunCeiling,
    long? StepCeiling,
    IReadOnlyList<string>? AllowedRoles,
    IReadOnlyList<AutoRule> AutoRules,
    long? HardStepCeiling = null,
    long? HardRunCeiling = null,
    IReadOnlyList<string>? EngineMayMerge = null,
    bool? BuiltActions = null,
    long? WorkItemCeiling = null)
{
    public RunOptions ToOptions() => new()
    {
        DefaultAttempts = DefaultAttempts,
        DefaultRaces = DefaultRaces,
        RunCeiling = RunCeiling,
        StepCeiling = StepCeiling,
        AllowedRoles = AllowedRoles,
        AutoResolve = type => AutoRules.FirstOrDefault(r => Matches(r.Match, type))?.Auto ?? false,
        HardStepCeiling = HardStepCeiling,
        HardRunCeiling = HardRunCeiling,
    };

    static bool Matches(string pattern, string type) =>
        new Model.DecisionTypeItem(pattern, null, new Model.SourceLocation("", 0, 0)).Matches(type);
}

/// <summary>A child run a <c>run.child</c> step started: its workflow id, by step id.</summary>
public sealed record ChildRun(string StepId, int Execution, string WorkflowId);

/// <summary>A patch a child sent, held until the parent's core is ready to apply it.</summary>
public sealed record ParentPatchSignal(string ChildWorkflowId, string ProposerStepId, string Patch);

/// <summary>A hand-over: the run's state when it continued-as-new, after draining (SP1's record).</summary>
public sealed record HandOver(int Run, long HistoryEvents, int InFlightActivities, int RunningChildren, int OpenWaits, int PendingChildEnds);

/// <summary>An answer already applied, by wait id, so a repeat of the same answer succeeds without a second effect.</summary>
public sealed record AppliedAnswer(string WaitId, string Key);

/// <summary>The shell's carried state: the core's, and what the Temporal workflow itself must remember.</summary>
public sealed record ShellState(
    CarriedCore Core,
    IReadOnlyList<ChildRun> Children,
    IReadOnlyList<string> PendingChildEnds,
    IReadOnlyList<ParentPatchSignal> PendingParentPatches,
    IReadOnlyList<AppliedAnswer> Answered,
    IReadOnlyList<string> RecordedOpenWaits,
    long TimelineSequence,
    IReadOnlyList<HandOver> HandOvers,
    bool CancelledByParent,
    int ActivitiesStartedWhileDraining,
    int ChildEndsWhileDraining,
    int AnswersWhileDraining,
    IReadOnlyList<OpenQuestion>? Questions = null,
    IReadOnlyList<string>? ClosedQuestions = null);

/// <summary>
/// The run workflow's input: on the first run, what the API or the parent gives; after a hand-over, the same with
/// <see cref="Carried"/> set. <see cref="TrackerIssue"/> is the run's issue (<c>run.tracker_issue</c>); a child's comes from
/// its <c>run.child</c> step, never from its parent. <see cref="WorkItems"/> is the run tree's budget (F11), set by the root
/// and passed down unchanged.
/// </summary>
public sealed record RunStart(
    string RunId,
    string Template,
    IReadOnlyList<string> Repositories,
    IReadOnlyDictionary<string, string> Parameters,
    string LockRef,
    bool DryRun,
    string Requester,
    string? ParentWorkflowId = null,
    string? ParentStepId = null,
    long HandOverEvents = 5120,
    RunPolicy? Policy = null,
    ShellState? Carried = null,
    string? ParentBranch = null,
    string? TrackerIssue = null,
    WorkItemBudget? WorkItems = null);

/// <summary>
/// A run tree's work-item budget (F11; docs/05 section 3.1): its root run and the most descendant runs the tree may create,
/// the root template's <c>budget.max_work_items</c> within the organisation's ceiling.
/// </summary>
public sealed record WorkItemBudget(string RootRunId, long Limit);

/// <summary>
/// A person's answer, from the API, which authenticated the actor and recorded the answer first (docs/04
/// section 4.1). For a decision, <see cref="Outcome"/>; for a patch approval, <see cref="Action"/>
/// <c>approve</c> or <c>reject</c>; for an intervention, a recovery <see cref="Action"/>: <c>retry</c>,
/// <c>go_to</c>, <c>replan</c>, <c>grant_attempts</c> or <c>end</c>.
/// </summary>
public sealed record AnswerArgs(
    string WaitId,
    string Actor,
    string? Outcome = null,
    string? Action = null,
    string? StepId = null,
    bool CleanUp = false,
    long Attempts = 0,
    string? Status = null,
    bool BeyondHardCeiling = false);

public sealed record AbortArgs(string Actor, string Reason);

public sealed record AnswerReply(string Result);

/// <summary>A wait as the workflow holds it.</summary>
public sealed record WaitView(string WaitId, string Kind, string StepId, string Reason);

/// <summary>The query's answer, for tests: the run's status, whether it is draining, and its hand-overs.</summary>
public sealed record RunView(
    string Status,
    bool Ready,
    bool Draining,
    int Run,
    long HistoryEvents,
    IReadOnlyList<WaitView> Waits,
    IReadOnlyList<string> InFlight,
    IReadOnlyList<ChildRun> Children,
    IReadOnlyList<HandOver> HandOvers,
    int ActivitiesStartedWhileDraining,
    int ChildEndsWhileDraining,
    int AnswersWhileDraining);
