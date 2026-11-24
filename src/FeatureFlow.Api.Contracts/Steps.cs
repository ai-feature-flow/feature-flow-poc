namespace FeatureFlow.Api.Contracts;

/// <summary>
/// <c>GET /runs/{id}/steps/{step}</c> (A3): one step of a run as its page shows it. Its executions, oldest first, each with its
/// artifacts, agent sessions, reports and questions; its waits as <c>GET /runs/{id}</c> gives them, open first; its findings, in
/// ledger order, and <see cref="FindingSteps"/>, the step and the steps that record its findings (a <c>step_changed</c> for any
/// of them concerns the page); its events, oldest first; the patch its agent has staged, only while the latest execution
/// runs; and the instructions people gave for its next attempt that are still pending.
/// </summary>
public sealed record StepDetail(
    string RunId,
    string StepId,
    IReadOnlyList<StepExecutionView> Executions,
    IReadOnlyList<WaitView> Waits,
    IReadOnlyList<FindingView> Findings,
    IReadOnlyList<string> FindingSteps,
    IReadOnlyList<StepEventView> Events,
    StagedPatchView? StagedPatch,
    IReadOnlyList<InstructionView> Instructions);

/// <summary>
/// One execution of a step: its number, when it started and ended, the outcome it ended with and the step the run entered
/// next; the artifacts it produced, prompts and transcripts included; its agent sessions (none for a step that is not an
/// agent); the report tool calls of its agent and the questions it asked.
/// </summary>
public sealed record StepExecutionView(
    int Number,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? Outcome,
    string? Then,
    IReadOnlyList<ArtifactView> Artifacts,
    IReadOnlyList<AgentSessionView> Sessions,
    IReadOnlyList<StepReportView> Reports,
    IReadOnlyList<StepQuestionView> Questions);

/// <summary>An agent session of an execution: its number, and its prompt and transcript where they are stored.</summary>
public sealed record AgentSessionView(int Session, string? PromptRef, string? TranscriptRef);

/// <summary>A report tool call of an execution's agent: its order, whether the engine accepted it, the outcome and summary it gave, and why it was refused.</summary>
public sealed record StepReportView(int Sequence, bool Accepted, string? Outcome, string? Summary, IReadOnlyList<string> Errors, DateTimeOffset At);

/// <summary>A question an execution's agent asked: its id, text and options, its status (<c>open</c>, <c>answered</c>, <c>timed_out</c>) and the answer.</summary>
public sealed record StepQuestionView(string QuestionId, string Text, IReadOnlyList<string> Options, string Status, string? Answer, DateTimeOffset At);

/// <summary>Where a step's event comes from.</summary>
public enum StepEventSource
{
    Timeline,
    Agent,
    Override,
    Answer,
    Finding,
}

/// <summary>
/// One event of a step, merged from its sources: when, the source and its kind (a timeline kind; an agent event kind such as
/// <c>hook_*</c>, <c>model</c> or <c>patch_*</c>; an override key; an answer's choice; a finding event kind), the execution it
/// belongs to where known, its detail as text, who (for an override, an answer or a finding event) and the finding concerned.
/// </summary>
public sealed record StepEventView(
    DateTimeOffset At,
    StepEventSource Source,
    string Kind,
    int? Execution,
    string Text,
    string? Actor,
    string? FindingId = null);

/// <summary>
/// The plan patch a running step's agent has staged (A8): the execution, its target (<c>self</c>, this run's plan, or
/// <c>parent</c>), the plan revision it was checked against, the patch document's text, and when it was staged.
/// </summary>
public sealed record StagedPatchView(int Execution, string Target, int Revision, string Patch, DateTimeOffset At);

/// <summary>A person's instructions for the step's next attempt (the <c>instruct</c> override): the override, who gave them, the text and when.</summary>
public sealed record InstructionView(string OverrideId, string Actor, string Text, DateTimeOffset At);
