using System.Text.Json.Serialization;

namespace FeatureFlow.Interpreter.Wire;

// What the API and the Temporal workflow exchange. Plain serialisable records with no Temporal type, so both
// the API and the engine reference them without referencing each other (docs/11 section 2.3).

/// <summary>The names of the run workflow and of its updates, signals and queries.</summary>
public static class RunWorkflowNames
{
    public const string Workflow = "Run";

    /// <summary>Update: a person's answer to a gate, question, intervention, breakpoint or patch approval (docs/02 section 7).</summary>
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

    /// <summary>
    /// Signal, from the agent worker's model proxy: a step's model cannot be reached, and why (LOG 326). The run records it
    /// on its timeline; the agent worker sends it once per step and cause.
    /// </summary>
    public const string ModelUnreachable = "model_unreachable";

    /// <summary>
    /// Update: a person's run-time override (docs/05 section 5) other than an answer or an abort, with <see cref="OverrideArgs"/>.
    /// The API authorised the key and recorded the override (an <c>OverrideRecord</c>) first, as it does answers.
    /// </summary>
    public const string Override = "override";

    /// <summary>
    /// Signal, from the inbound gateway: an external event (a webhook) that may end a wait early, with <see cref="WakeSignal"/>.
    /// A hint: the wait reads again to confirm (events wake, reads confirm), so a wrong or repeated wake changes nothing.
    /// </summary>
    public const string Wake = "wake";

    /// <summary>Query, from the agent endpoint's <c>inspect_plan</c>: the plan as an agent step may see it, with <see cref="AgentPlanQuery"/>.</summary>
    public const string AgentPlan = "agent_plan";

    /// <summary>
    /// Query, from the agent endpoint's <c>propose_patch</c> and <c>report</c>: what the core would do with an agent step's patch
    /// now, with <see cref="PatchPreviewQuery"/>. Pure: it changes no state and records nothing.
    /// </summary>
    public const string PatchPreview = "patch_preview";

    /// <summary>Query, on a parent's workflow: what its core would do with a child's patch now, with <see cref="ChildPatchPreviewQuery"/>.</summary>
    public const string ChildPatchPreview = "child_patch_preview";
}

/// <summary>
/// A run-time override (docs/05 section 5), from the API, which authenticated <see cref="Actor"/> (F8) and authorised
/// <see cref="Key"/>, a base key of <c>Model.Overrides</c>. <see cref="Variants"/> are the variant keys the caller also holds
/// (<c>skip_protected</c>, <c>grant_attempts_beyond</c>): the workflow refuses an override on protected content, or beyond
/// the hard ceiling, without the matching one, since only it knows what is protected. <see cref="Change"/> holds the
/// key's arguments by name (for example <c>outcome</c> for <c>skip</c>, <c>patch</c> for <c>amend</c>, <c>role</c> for
/// <c>change_role</c>, <c>attempts</c> for <c>grant_attempts</c>, <c>set</c> for <c>breakpoint</c>, <c>item</c> and <c>with</c>
/// for <c>override_library</c>). Idempotent by <see cref="OverrideId"/>, which the API assigns.
/// </summary>
public sealed record OverrideArgs(
    string OverrideId,
    string Key,
    string Actor,
    string? Reason,
    string? StepId,
    IReadOnlyDictionary<string, string> Change,
    IReadOnlyList<string>? Variants = null);

/// <summary>
/// An external event that may end a wait early: its source (<c>code_host</c>, <c>ci</c>, <c>tracker</c>), the event's name,
/// and the step to wake, or null for every wait of the run.
/// </summary>
public sealed record WakeSignal(string Source, string Event, string? StepId = null);

/// <summary>A question an agent asked: the run opens a question wait for it, answered with the <c>answer</c> update.</summary>
public sealed record QuestionSignal(string QuestionId, string StepId, string StepKey, string Text, IReadOnlyList<string> Options, long TimeoutSeconds);

/// <summary>A step's model could not be reached through the model proxy, with the cause the proxy saw.</summary>
public sealed record ModelUnreachableSignal(string StepId, string Cause);

/// <summary>An approval rule's decision type pattern and whether it allows auto-resolution (docs/05 section 3.2).</summary>
public sealed record AutoRule(string Match, bool Auto);

/// <summary>
/// The limits and policy a run enforces, fixed when it starts (docs/05 section 8) and carried in its state,
/// because workflow code cannot read configuration. <see cref="RequireReview"/> is the organisation's
/// <c>planning.require_review</c> for the run's template (M7). <see cref="RoleMapping"/> is its <c>role_mapping</c>: each
/// library agent role's runtime instance and model (LOG 267). <see cref="RequireChecks"/> and <see cref="RequireTested"/>
/// are its <c>merge.require_checks</c> and <c>merge.require_tested</c> caps, which a patch is checked against (F1).
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
    long? WorkItemCeiling = null,
    bool? RequireReview = null,
    IReadOnlyDictionary<string, Model.AgentRoleMapping>? RoleMapping = null,
    Model.Cap<bool>? RequireChecks = null,
    Model.Cap<bool>? RequireTested = null,
    TimeSpan? ReminderInterval = null)
{
    /// <summary>
    /// The runtime instance and model an agent of <paramref name="role"/> runs with: its own entry, else <c>default</c>; null
    /// with neither, when the installation's default runtime runs it with that runtime's own model.
    /// </summary>
    public Model.AgentRoleMapping? AgentFor(string? role) =>
        RoleMapping is null ? null
        : role is not null && RoleMapping.TryGetValue(role, out var own) ? own
        : RoleMapping.GetValueOrDefault("default");

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
        RequireReview = RequireReview ?? false,
        RequireChecks = RequireChecks,
        RequireTested = RequireTested,
        ReminderInterval = ReminderInterval,
    };

    static bool Matches(string pattern, string type) =>
        new Model.DecisionTypeItem(pattern, null, new Model.SourceLocation("", 0, 0)).Matches(type);
}

/// <summary>
/// A patch a child sent, held until the parent's core is ready to apply it. <see cref="ReviewRequired"/> is the proposing step's
/// <c>planning.review: required</c> (false in histories from before it was carried).
/// </summary>
public sealed record ParentPatchSignal(string ChildWorkflowId, string ProposerStepId, string Patch, bool ReviewRequired = false);

/// <summary>
/// A hand-over: the run's state when it continued-as-new, after draining (SP1's record): how many waits it interrupted to
/// hand over (LOG 139), and what arrived or started while it drained (LOG 365).
/// </summary>
public sealed record HandOver(int Run, long HistoryEvents, int InFlightActivities, int RunningChildren, int OpenWaits, int PendingChildEnds, int InterruptedWaits,
    int ActivitiesStartedWhileDraining, int ChildEndsWhileDraining, int AnswersWhileDraining);

/// <summary>
/// The shell's carried state: the core's, which holds every wait open and closed and every child running, and what the
/// Temporal workflow itself must remember: inbound signals not yet given to the core, record numbering and the hand-overs.
/// </summary>
public sealed record ShellState(
    CarriedCore Core,
    IReadOnlyList<string> PendingChildEnds,
    IReadOnlyList<ParentPatchSignal> PendingParentPatches,
    long TimelineSequence,
    IReadOnlyList<HandOver> HandOvers,
    bool CancelledByParent);

/// <summary>
/// The run workflow's input: on the first run, what the API or the parent gives; after a hand-over, the same with
/// <see cref="Carried"/> set. A child gets its parent's and its root's artifacts as they were when it started, by name
/// (docs/03 section 7.2: consumed with <c>from: parent</c> or <c>from: root</c>). <see cref="TrackerIssue"/> is the run's
/// issue (<c>run.tracker_issue</c>); a child's comes from its <c>run.child</c> step, never from its parent. <see cref="WorkItems"/>
/// is the run tree's budget (F11), set by the root and passed down unchanged. <see cref="ParentContext"/> is a child's parent's
/// rendered run context when it started (docs/04 section 3.5).
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
    IReadOnlyDictionary<string, string>? ParentArtifacts = null,
    IReadOnlyDictionary<string, string>? RootArtifacts = null,
    string? TrackerIssue = null,
    WorkItemBudget? WorkItems = null,
    string? ParentContext = null);

/// <summary>
/// A run tree's work-item budget (F11; docs/05 section 3.1): its root run and the most descendant runs the tree may create,
/// the root template's <c>budget.max_work_items</c> within the organisation's ceiling.
/// </summary>
public sealed record WorkItemBudget(string RootRunId, long Limit);

/// <summary>
/// A person's answer, from the API, which authenticated the actor and recorded the answer first (docs/04
/// section 4.1). For a gate, <see cref="Outcome"/>; for a question, the answer's text as <see cref="Outcome"/>; for a patch
/// approval, <see cref="Action"/> <c>approve</c> or <c>reject</c>; for an intervention or breakpoint, a recovery
/// <see cref="Action"/> (<see cref="Recoveries"/>). The core routes it (<see cref="RunCore.Route"/>). An answer may carry what the person produced:
/// <see cref="Artifacts"/>, by declared name, already stored and validated by the API (a session's submitted files, a
/// person's <c>triage</c>), and <see cref="Revision"/>, the exact commit a session submitted.
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
    bool BeyondHardCeiling = false,
    IReadOnlyDictionary<string, string>? Artifacts = null,
    string? Revision = null,
    string? Reason = null);

/// <summary>A person stops the run; <see cref="ClosePullRequests"/> asks that the pull requests it opened be closed too (docs/05 section 5).</summary>
public sealed record AbortArgs(string Actor, string Reason, bool ClosePullRequests = false);

public sealed record AnswerReply(string Result);

/// <summary>A wait as the workflow holds it.</summary>
public sealed record WaitView(string WaitId, string Kind, string StepId, string Reason);

/// <summary>The query's answer, for tests: the run's status, whether it is draining, its children and its hand-overs.</summary>
public sealed record RunView(
    string Status,
    bool Ready,
    bool Draining,
    int Run,
    long HistoryEvents,
    IReadOnlyList<WaitView> Waits,
    IReadOnlyList<string> InFlight,
    IReadOnlyList<ChildRun> Children,
    IReadOnlyList<HandOver> HandOvers);

/// <summary>
/// The <c>agent_plan</c> query: the plan as the agent step <see cref="StepKey"/> (its agent step record's key, from the step's
/// token) may see it. <see cref="Target"/> is <c>self</c> or <c>parent</c>; for <c>parent</c> this run answers only with
/// <see cref="AgentPlanView.ForwardTo"/>, and the endpoint queries the parent.
/// </summary>
public sealed record AgentPlanQuery(
    [property: JsonPropertyName("step_key")] string StepKey,
    [property: JsonPropertyName("target")] string Target);

/// <summary>
/// The answer to <see cref="AgentPlanQuery"/> (design note s1): <see cref="Status"/> <c>ok</c>, <c>forward</c> (ask
/// <see cref="ForwardTo"/>, the parent's workflow id) or <c>unavailable</c> with <see cref="Message"/> saying why; and with
/// <c>ok</c>, the run, the agent's own step and authority, the readable form (<c>ReadableForm.Render</c>), where the run is
/// (<see cref="Positions"/>: step ids entered, running, waiting or active parallels) and each step.
/// </summary>
public sealed record AgentPlanView(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("forward_to")] string? ForwardTo,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("level")] string Level,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("you")] AgentPlanSelf? You,
    [property: JsonPropertyName("authority")] AgentPlanAuthority? Authority,
    [property: JsonPropertyName("readable")] string Readable,
    [property: JsonPropertyName("positions")] IReadOnlyList<string> Positions,
    [property: JsonPropertyName("steps")] IReadOnlyList<AgentPlanStep> Steps)
{
    public const string Ok = "ok";
    public const string Forward = "forward";
    public const string Unavailable = "unavailable";
}

/// <summary>The proposing step: its id and execution. Null in a parent's view, where the agent's step is not a step.</summary>
public sealed record AgentPlanSelf(
    [property: JsonPropertyName("step_id")] string StepId,
    [property: JsonPropertyName("execution")] int Execution);

/// <summary>
/// The agent's authority over the plan it is shown: its step's <c>planning.targets</c> and <c>review</c>, the organisation's
/// <c>planning.require_review</c> for the run's template, and <c>planning.allowed_roles</c> when set (null: any role).
/// </summary>
public sealed record AgentPlanAuthority(
    [property: JsonPropertyName("targets")] IReadOnlyList<string> Targets,
    [property: JsonPropertyName("review")] string Review,
    [property: JsonPropertyName("require_review")] bool RequireReview,
    [property: JsonPropertyName("allowed_roles")] IReadOnlyList<string>? AllowedRoles);

/// <summary>
/// A step as an agent sees it: the API's <c>PlanStepView</c> fields (its container, form, role or action as <see cref="What"/>,
/// outcomes and routes, protected, status; a wait shows as <c>waiting(&lt;kind&gt;)</c>) and <see cref="Source"/>, the step as
/// written (YAML), which <c>replace</c> and <c>set</c> need; null where the core does not keep it.
/// </summary>
public sealed record AgentPlanStep(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("container")] string? Container,
    [property: JsonPropertyName("form")] string Form,
    [property: JsonPropertyName("what")] string What,
    [property: JsonPropertyName("outcomes")] IReadOnlyList<AgentPlanOutcome> Outcomes,
    [property: JsonPropertyName("protected")] bool Protected,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("source")] string? Source);

/// <summary>An outcome of a step: its name, its class, and where it routes.</summary>
public sealed record AgentPlanOutcome(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("class")] string Class,
    [property: JsonPropertyName("route")] string? Route);

/// <summary>
/// The <c>patch_preview</c> query: what the core would do now with <see cref="Patch"/> (the patch document as YAML text) from the
/// agent step <see cref="StepKey"/>. With <see cref="ToParent"/> the run checks only that the step may propose to its parent
/// and answers with <see cref="PatchPreview.ForwardTo"/>; the endpoint then asks the parent (<see cref="ChildPatchPreviewQuery"/>).
/// </summary>
public sealed record PatchPreviewQuery(
    [property: JsonPropertyName("step_key")] string StepKey,
    [property: JsonPropertyName("patch")] string Patch,
    [property: JsonPropertyName("to_parent")] bool ToParent);

/// <summary>
/// The <c>child_patch_preview</c> query, on the parent: what its core would do with <see cref="Patch"/> (YAML) from the child
/// workflow <see cref="ChildWorkflowId"/>'s step <see cref="ProposerStepId"/>, whose <c>planning.review</c> is
/// <c>required</c> when <see cref="ReviewRequired"/>.
/// </summary>
public sealed record ChildPatchPreviewQuery(
    [property: JsonPropertyName("child_workflow_id")] string ChildWorkflowId,
    [property: JsonPropertyName("proposer_step_id")] string ProposerStepId,
    [property: JsonPropertyName("patch")] string Patch,
    [property: JsonPropertyName("review_required")] bool ReviewRequired);

/// <summary>
/// The answer to a preview: <see cref="Status"/> <c>applies</c>, <c>needs_approval</c> (<see cref="Reasons"/>: why a person must
/// decide), <c>refused</c> (<see cref="Messages"/>: the checks' messages), <c>unavailable</c> (<see cref="Messages"/>: why), or
/// <c>forward</c> (ask the parent, <see cref="ForwardTo"/>). <see cref="Revision"/> is the plan revision it was checked against;
/// <see cref="ReviewRequired"/> the proposing step's <c>planning.review: required</c>, for the endpoint to carry to the parent.
/// </summary>
public sealed record PatchPreview(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reasons")] IReadOnlyList<string> Reasons,
    [property: JsonPropertyName("messages")] IReadOnlyList<string> Messages,
    [property: JsonPropertyName("revision")] int Revision,
    [property: JsonPropertyName("forward_to")] string? ForwardTo,
    [property: JsonPropertyName("review_required")] bool ReviewRequired)
{
    public const string Applies = "applies";
    public const string NeedsApproval = "needs_approval";
    public const string Refused = "refused";
    public const string Unavailable = "unavailable";
    public const string Forward = "forward";
}
