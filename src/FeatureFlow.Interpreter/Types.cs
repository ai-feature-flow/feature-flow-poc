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
/// values; declared defaults fill the rest. A child holds its parent's and its root's artifacts as they were when it
/// started, by name (docs/03 section 7.2: consumed <c>from: parent</c> or <c>from: root</c>), and its parent's rendered run
/// context (<see cref="ParentContext"/>; docs/04 section 3.5).
/// </summary>
public sealed record RunInfo(
    string Id,
    IReadOnlyList<string> Repositories,
    IReadOnlyDictionary<string, YNode> Parameters,
    string? ParentId = null,
    string? TrackerIssue = null,
    string? ParentBranch = null,
    IReadOnlyDictionary<string, string>? DefaultBranches = null,
    IReadOnlyDictionary<string, string>? ParentArtifacts = null,
    IReadOnlyDictionary<string, string>? RootArtifacts = null,
    string? ParentContext = null);

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

    /// <summary>
    /// The organisation's hard ceilings (F4; docs/05 section 5): an attempt grant that takes a step's allowance or
    /// the run ceiling above them needs an administrator (<c>grant_attempts_beyond</c>). Null: none.
    /// </summary>
    public long? HardStepCeiling { get; init; }

    public long? HardRunCeiling { get; init; }

    /// <summary>
    /// The organisation's <c>planning.require_review</c> for this run's template (docs/05 section 6): every agent patch
    /// becomes a patch approval, as a step's <c>planning.review: required</c> makes it.
    /// </summary>
    public bool RequireReview { get; init; }

    /// <summary>The organisation's <c>merge.require_checks</c> and <c>merge.require_tested</c> caps (docs/05 section 3.1), which a patch must not loosen (F1). Null: none.</summary>
    public Model.Cap<bool>? RequireChecks { get; init; }

    public Model.Cap<bool>? RequireTested { get; init; }

    /// <summary>The organisation's <c>notifications.reminder_interval</c> (docs/07 section 3): how often an open wait is reminded. Null: never (LOG 401).</summary>
    public TimeSpan? ReminderInterval { get; init; }

    /// <summary>The caps one step is checked against (check 22), as at run start.</summary>
    public Validation.StepCaps StepCaps => new(StepCeiling, RequireChecks, RequireTested);
}

/// <summary>Something the core asks the shell to do. The Temporal workflow (M3) or the re-drive runner carries it out.</summary>
public abstract record Command;

/// <summary>Which activity carries a step out: the agent worker's, an engine action's handler, or reading the list <c>plan.expand</c> expands.</summary>
public enum StepActivity
{
    Agent,
    Engine,
    Expand,
}

public sealed record StartStep(string StepId, int Execution) : Command;

/// <summary>
/// A <c>run.child</c> step starts its child run (docs/03 section 9): the shell reserves the work item, makes the work branches
/// and starts the child workflow from <see cref="RunCore.ChildSpec"/>, then gives the core <see cref="ChildStarted"/>.
/// </summary>
public sealed record StartChild(string StepId, int Execution) : Command;

/// <summary>The run no longer needs a child it started (a decided join, a cancelled step, or the run's end): the shell tells it.</summary>
public sealed record CancelChild(string StepId, string WorkflowId) : Command;

/// <summary>A decision step's gate opened as <see cref="WaitId"/>; <see cref="Session"/> when the step declares one, which the shell opens.</summary>
public sealed record OpenGate(string StepId, int Execution, string WaitId, bool Session) : Command;

/// <summary>
/// A wait needs a timer: at <see cref="At"/> the shell gives the core <see cref="WaitTimerFired"/>. Re-armed after a hand-over
/// from <see cref="RunCore.Timers"/> (docs/02 section 3: timers are re-created from state).
/// </summary>
public sealed record ArmTimer(string WaitId, DateTimeOffset At) : Command;

/// <summary>
/// An open wait's next reminder (docs/04 section 4): after <see cref="After"/> the shell gives the core <see cref="ReminderDue"/>
/// with <see cref="Number"/>. Relative, since the core keeps no clock; re-armed after a hand-over from <see cref="RunCore.Timers"/>,
/// up to one interval late (LOG 402).
/// </summary>
public sealed record ArmReminder(string WaitId, int Number, TimeSpan After) : Command;

public sealed record RaiseIntervention(string WaitId, string StepId, string? Outcome, string Reason) : Command;

public sealed record RequestPatchApproval(string WaitId, PatchDocument Patch) : Command;

public sealed record CancelStep(string StepId) : Command;

/// <summary>A wait that is no longer wanted: its join decided, its step ended, or the run ended.</summary>
public sealed record CloseWait(string WaitId, string StepId) : Command;

public sealed record CleanUp(string StepId) : Command;

public sealed record EndRun(RunStatus Status) : Command;

/// <summary>A child run's agent step proposed a patch to the parent's plan (docs/03 section 9.4): the shell signals the parent.</summary>
public sealed record SendPatchToParent(string StepId, PatchDocument Patch) : Command;

/// <summary>
/// A person approved an agent's library override (docs/05 section 6): the shell stores the content, writes the run's new
/// lock and gives the core <see cref="LibraryOverridden"/>, with <see cref="Actor"/> as who made it.
/// </summary>
public sealed record OverrideLibrary(string WaitId, string Item, string Content, string Actor, string Reason) : Command;

/// <summary>Something that happened, given to the core to apply.</summary>
public abstract record RunInput;

/// <summary>
/// A step ended with an outcome. <see cref="Patch"/> is a patch the step proposed, or the patch
/// <c>plan.expand</c> built (<see cref="PlanExpansion"/>). <see cref="Summary"/> is an agent's report summary, kept in the run
/// context (docs/04 section 3.5).
/// </summary>
public sealed record StepCompleted(
    string StepId,
    string Outcome,
    IReadOnlyDictionary<string, object?>? Outputs = null,
    IReadOnlyDictionary<string, string>? Artifacts = null,
    PatchDocument? Patch = null,
    bool PatchToParent = false,
    string? Summary = null) : RunInput;

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
/// A running engine action needs a person's decision before it asks them to act (docs/04 section 4.5): <c>scm.merge</c>,
/// when a person must merge, raises <c>&lt;level&gt;.merge_action</c>. The step keeps watching the code host while the
/// decision is open (<see cref="ActionDecisions"/>).
/// </summary>
public sealed record ActionDecisionRequested(string StepId, string Reason) : RunInput;

/// <summary>
/// A patch from outside a step: a person through the API, or the engine. <see cref="By"/> is set by the shell
/// from the authenticated caller (F8); the document's own <c>proposed_by</c> is ignored. A person's patch (<c>amend</c>,
/// docs/05 section 5) is not bound by F3, but touches protected content only when <see cref="MayTouchProtected"/>
/// (the caller holds <c>amend_protected</c>), and never changes a role (that is <see cref="RoleChanged"/>).
/// </summary>
public sealed record PatchProposed(PatchDocument Patch, Proposer By, bool MayTouchProtected = true) : RunInput;

/// <summary>A person's answer to a patch approval; <see cref="Actor"/> is who answered, for an approved library override's lock entry.</summary>
public sealed record PatchAnswered(string WaitId, bool Approve, string Actor = "") : RunInput;

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

    /// <summary>Breakpoints only: start the step that was paused before.</summary>
    Continue,
}

/// <summary>A recovery action with its arguments: the step for go to and re-plan, the attempts to grant, the status to end with.</summary>
/// <remarks><see cref="BeyondHardCeiling"/> is set by the shell when the authenticated actor may grant beyond the hard ceiling (an administrator).</remarks>
public sealed record Recovery(RecoveryKind Kind, string? StepId = null, bool CleanUp = false, long Attempts = 0, RunStatus? Status = null, bool BeyondHardCeiling = false);

/// <summary>A person answered an intervention with a recovery action. The shell names the actor; the core does not need it.</summary>
public sealed record InterventionAnswered(string WaitId, Recovery Action) : RunInput;

/// <summary>
/// The recovery actions by the name a person answers with (docs/01 section 7.2): one table for the answer, the wait's
/// choices, the CLI and the re-drive runner.
/// </summary>
public static class Recoveries
{
    public static readonly IReadOnlyList<(string Name, RecoveryKind Kind)> All =
    [
        ("retry", RecoveryKind.Retry), ("go_to", RecoveryKind.GoTo), ("replan", RecoveryKind.Replan),
        ("grant_attempts", RecoveryKind.GrantAttempts), ("end", RecoveryKind.End), ("continue", RecoveryKind.Continue),
    ];

    public static RecoveryKind? Parse(string? name) => All.FirstOrDefault(r => r.Name == name) is { Name: not null } r ? r.Kind : null;

    public static string Name(RecoveryKind kind) => All.First(r => r.Kind == kind).Name;

    /// <summary>The recoveries that answer a wait of <paramref name="kind"/>: at a breakpoint <c>continue</c> first, then the rest; at an intervention all but <c>continue</c>.</summary>
    public static IReadOnlyList<string> For(WaitKind kind) => kind switch
    {
        WaitKind.Breakpoint => ["continue", .. All.Where(r => r.Kind != RecoveryKind.Continue).Select(r => r.Name)],
        WaitKind.Intervention => All.Where(r => r.Kind != RecoveryKind.Continue).Select(r => r.Name).ToList(),
        _ => [],
    };
}

/// <summary>
/// An agent asked a question (docs/04 section 3.4) while its step runs, as the execution <see cref="StepKey"/> names. The
/// core holds it as a <see cref="WaitKind.Question"/> wait until it is answered, times out at <see cref="At"/> plus
/// <see cref="Timeout"/>, or its step stops running. Idempotent by <see cref="QuestionId"/>, which the sandbox client
/// assigned (LOG 59).
/// </summary>
public sealed record QuestionOpened(string QuestionId, string StepId, string StepKey, string Text, IReadOnlyList<string> Options, TimeSpan Timeout, DateTimeOffset At)
    : RunInput;

/// <summary>A person answered an agent's question with text. Questions never route the plan: the agent reads the answer.</summary>
public sealed record QuestionAnswered(string WaitId, string Answer) : RunInput;

/// <summary>A wait's timer, armed by <see cref="ArmTimer"/>, fired at <see cref="At"/>: a question past its deadline times out.</summary>
public sealed record WaitTimerFired(string WaitId, DateTimeOffset At) : RunInput;

/// <summary>
/// A wait's reminder <see cref="Number"/>, armed by <see cref="ArmReminder"/>, is due: if the wait is still open and this is its
/// next reminder, the core traces <c>reminded</c> and arms the one after; a repeated, late or closed wait's does nothing.
/// </summary>
public sealed record ReminderDue(string WaitId, int Number) : RunInput;

/// <summary>The shell opened an interactive session on gate <see cref="WaitId"/> (docs/04 section 4.4): answers to it follow the session's rules.</summary>
public sealed record GateSessionOpened(string WaitId) : RunInput;

/// <summary>A person stopped the run (docs/05 section 5, <c>abort</c>): it ends <c>aborted</c>.</summary>
public sealed record RunAborted(string Reason) : RunInput;

/// <summary>
/// A person sets or clears a breakpoint (docs/04 section 4.5): the run pauses before <see cref="StepId"/> and opens a
/// breakpoint decision, each time it is about to start it, until the breakpoint is cleared. Set only on a step that is
/// not running or waiting.
/// </summary>
public sealed record BreakpointSet(string StepId, bool Set, string Actor) : RunInput;

/// <summary>
/// A person skips a step (docs/05 section 5, <c>skip</c>): the next time the run would start it, it ends with
/// <see cref="Outcome"/>, a declared outcome, without running and without using an attempt; its record shows it was
/// skipped. A step at a decision, a breakpoint or an intervention is skipped at once. A protected step needs
/// <see cref="MayTouchProtected"/> (<c>skip_protected</c>).
/// </summary>
public sealed record StepSkipped(string StepId, string Outcome, string Actor, string Reason, bool MayTouchProtected) : RunInput;

/// <summary>
/// A person grants attempts (docs/05 section 5, <c>grant_attempts</c>): to <see cref="StepId"/>'s allowance and the
/// run ceiling, or the run ceiling alone when it is null. The same grant as the recovery action, without the retry;
/// beyond the hard ceiling only with <see cref="BeyondHardCeiling"/> (<c>grant_attempts_beyond</c>, F4).
/// </summary>
public sealed record AttemptsGranted(string? StepId, long Attempts, bool BeyondHardCeiling, string Actor) : RunInput;

/// <summary>
/// A person changes an agent step's role (docs/05 section 5, <c>change_role</c>): applied as a person's patch that
/// replaces the step with the role changed, the one way a role changes (docs/03 section 10.2).
/// </summary>
public sealed record RoleChanged(string StepId, string Role, string Actor, string Reason) : RunInput;

/// <summary>
/// The content future steps resolve was replaced (docs/08 section 5.2): <see cref="Libraries"/> is the run's library set
/// with <see cref="Entry"/> applied. Steps in flight keep what they resolved; the plan does not change.
/// </summary>
public sealed record LibraryOverridden(LibraryOverrideEntry Entry, Library.LibrarySet Libraries) : RunInput;

/// <summary>
/// An agent proposed a library override (docs/05 section 6): it never applies at once, but opens a patch approval for
/// a person; approved, the shell carries it out with the approver as the actor.
/// </summary>
public sealed record LibraryOverrideProposed(string StepId, string Item, string? Content) : RunInput;

/// <summary>The shell started the child run of a <c>run.child</c> step's execution as <see cref="WorkflowId"/> (or adopted it).</summary>
public sealed record ChildStarted(string StepId, int Execution, string WorkflowId) : RunInput;

/// <summary>
/// A child run has ended, as a read of it confirmed, with its status and the artifacts it ended with: its <c>run.child</c> step
/// completes with the matching outcome, if it still runs that execution.
/// </summary>
public sealed record ChildEnded(string WorkflowId, RunStatus Status, IReadOnlyDictionary<string, string>? Artifacts) : RunInput;

/// <summary>
/// A lock entry of a mid-run library override (docs/08 section 5.2): the item, its file, the old and new content hashes,
/// who and why, the steps in flight that keep the old content, and the protected steps that resolve it.
/// </summary>
public sealed record LibraryOverrideEntry(
    string Item,
    string Path,
    string OldHash,
    string NewHash,
    string Actor,
    string Reason,
    IReadOnlyList<string> InFlight,
    IReadOnlyList<string> ProtectedSteps);

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

/// <summary>
/// The kinds of human wait, each answered through <see cref="RunCore.Route"/> (docs/04 section 4): a decision step's gate (and
/// the gate an engine action raises, LOG 331), an agent's question, an intervention, a patch approval, a breakpoint.
/// </summary>
public enum WaitKind
{
    Gate,
    Question,
    Intervention,
    PatchApproval,
    Breakpoint,
}

public static class WaitKinds
{
    /// <summary>A wait kind as the record store writes it (<c>WaitRecord.Kind</c>) and wait ids begin: <c>gate</c>, <c>question</c>, <c>patch_approval</c>, ...</summary>
    public static string Name(this WaitKind kind) => kind == WaitKind.PatchApproval ? "patch_approval" : kind.ToString().ToLowerInvariant();
}

/// <summary>
/// A durable wait for a person. A breakpoint keeps its entry's <see cref="CleanUp"/>, for the step it paused; a patch approval
/// of an agent's library override holds <see cref="LibraryItem"/> and <see cref="LibraryContent"/> instead of a patch; a
/// question holds the agent's text as its <see cref="Reason"/>, its <see cref="Options"/>, the execution that asked it
/// (<see cref="StepKey"/>) and when it times out (<see cref="Deadline"/>); a gate with an open interactive session is
/// <see cref="Session"/>. <see cref="Reminders"/> counts the reminders sent while it is open.
/// </summary>
public sealed record OpenWait(string Id, WaitKind Kind, string StepId, string? Outcome, string Reason, PatchDocument? Patch = null, bool FromChild = false)
{
    public bool CleanUp { get; init; }
    public string? LibraryItem { get; init; }
    public string? LibraryContent { get; init; }
    public string? StepKey { get; init; }
    public IReadOnlyList<string>? Options { get; init; }
    public DateTimeOffset? Deadline { get; init; }
    public bool Session { get; init; }
    public int Reminders { get; init; }
}

/// <summary>How a wait closed (<see cref="ClosedWait.How"/>).</summary>
public static class WaitEnds
{
    /// <summary>A person answered it through Feature Flow.</summary>
    public const string Answered = "answered";

    /// <summary>A question's deadline passed: the agent carries on without an answer (LOG 18).</summary>
    public const string TimedOut = "timed_out";

    /// <summary>Its step stopped running: a question, or an action's gate the step ended without an outcome.</summary>
    public const string StepEnded = "step_ended";

    /// <summary>An action's gate the step ended with an outcome first: a person acted on the code host (LOG 341).</summary>
    public const string AnsweredOnCodeHost = "answered_on_code_host";

    /// <summary>No longer wanted: its join decided, its step was skipped, or the run ended.</summary>
    public const string Withdrawn = "withdrawn";
}

/// <summary>
/// A wait that has closed, and how (<see cref="WaitEnds"/>). An answered one keeps the answer's <see cref="Key"/> (a decision's
/// outcome, a question's text, a recovery with its arguments) and <see cref="Actor"/>, so a repeat of the same answer is a
/// duplicate and a different one a conflict (LOG 58), and the person's <see cref="Reason"/>, which the key leaves out.
/// </summary>
public sealed record ClosedWait(string Id, WaitKind Kind, string StepId, string How, string? Key = null, string? Actor = null, string? Reason = null);

/// <summary>A wait opened (<see cref="Open"/>) or closed since the shell last recorded; drained by the shell like the trace.</summary>
public sealed record WaitChange(OpenWait Wait, bool Open);

/// <summary>
/// What the record store shows of a wait: its record kind, decision type, the choices that answer it, the text to show, and
/// for a gate what its step consumed as it opened (LOG 394).
/// </summary>
public sealed record WaitDescription(string Kind, string Type, IReadOnlyList<string> Choices, string Prompt, IReadOnlyList<ConsumedArtifact>? Consumed = null);

/// <summary>A wait to record (<see cref="RunCore.TakeWaitChanges"/>): opened, with its description, or closed when <see cref="Opened"/> is null.</summary>
public sealed record WaitShown(string WaitId, string StepId, WaitDescription? Opened);

/// <summary>A question that closed answered (with the answer's text) or timed out: the record its agent reads (docs/04 section 3.4).</summary>
public sealed record QuestionClosed(string WaitId, string StepKey, string Text, IReadOnlyList<string> Options, bool Answered, string? Answer);

/// <summary>A run-time override the run applied, by the id the API gave it, so a repeat is answered without a second effect.</summary>
public sealed record AppliedOverride(string Id, string Key, string Result);

/// <summary>A child run a <c>run.child</c> step's execution started, by its workflow id (the child's run id).</summary>
public sealed record ChildRun(string StepId, int Execution, string WorkflowId);

/// <summary>
/// An artifact a step produced, for the record store's artifact index (docs/02 section 5): its step, execution and name, its
/// declared type, the engine action that produced it (<see cref="Producer"/>; null for an agent step or a decision), and whether
/// its type is evidence (F7).
/// </summary>
public sealed record ProducedArtifact(string StepId, int Execution, string Name, string? Type, string Reference, string? Producer, bool Evidence);

/// <summary>Reads the <c>findings</c> namespace when a route is evaluated; each route evaluation reads each name once.</summary>
public interface IFindingsSource
{
    long Read(string name);
}
