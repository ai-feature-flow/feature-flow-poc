namespace FeatureFlow.Providers.Abstractions;

/// <summary>An outcome a step may report, with its class: <c>success</c>, <c>failure</c>, <c>blocked</c> or <c>~</c>.</summary>
public sealed record OutcomeSpec(string Name, string Class);

/// <summary>An artifact a step promises: for one outcome, or for every success outcome when <see cref="Outcome"/> is null.</summary>
public sealed record ArtifactSpec(string? Outcome, string Name);

/// <summary>
/// What a step must report (docs/03 section 7, docs/04 section 3.3): its outcomes, the outputs it publishes with
/// their types, and the artifacts it promises. <see cref="ZeroOutputs"/> holds every published output at its
/// type's zero value, as JSON text, for providers that report no values of their own.
/// </summary>
public sealed record StepContract(
    IReadOnlyList<OutcomeSpec> Outcomes,
    IReadOnlyDictionary<string, string> OutputTypes,
    IReadOnlyList<ArtifactSpec> Artifacts,
    IReadOnlyDictionary<string, string> ZeroOutputs)
{
    /// <summary>The outputs the step itself reports, as against those the engine publishes for it (docs/04 section 3.6).</summary>
    public IReadOnlyList<string> Reported { get; init; } = [];

    public string? FirstSuccess => Outcomes.FirstOrDefault(o => o.Class == "success")?.Name;

    /// <summary>The artifacts promised for an outcome.</summary>
    public IEnumerable<string> ArtifactsFor(string outcome) =>
        Artifacts.Where(a => a.Outcome == outcome || (a.Outcome is null && Outcomes.Any(o => o.Name == outcome && o.Class == "success")))
            .Select(a => a.Name).Distinct();
}

/// <summary>What one step reported. Output values are JSON text; artifacts are references into the artifact store.</summary>
public sealed record StepReport(
    string Outcome,
    IReadOnlyDictionary<string, string> Outputs,
    IReadOnlyDictionary<string, string> Artifacts,
    string? Summary = null,
    string? Patch = null,
    bool PatchToParent = false);

/// <summary>Who the step is: enough for a provider to find its work and for a fake to find its script.</summary>
public sealed record StepIdentity(string RunId, string Template, string StepId, int Execution, string IdempotencyKey);

/// <summary>
/// An agent step's settings, as the workflow resolved them from the plan (docs/04 section 3): the role's write
/// access and tool classes, the rendered task, the limits, the run's branches, and the artifacts it consumes.
/// </summary>
public sealed record AgentStepSpec(
    string WriteAccess,
    IReadOnlyList<string> Tools,
    string? TaskText,
    string? TaskLibrary,
    IReadOnlyList<string> Skills,
    int CorrectionTurns,
    int QuestionsMax,
    TimeSpan QuestionsTimeout,
    bool Push,
    string OnUncommitted,
    string Level,
    IReadOnlyDictionary<string, string> Branches,
    IReadOnlyDictionary<string, string> ParentBranches,
    IReadOnlyDictionary<string, string> Consumed,
    string WorkflowId,
    string LockRef);

/// <summary>One step for an activity to carry out. <see cref="Agent"/> is set for an agent step from M4.</summary>
public sealed record StepCall(StepIdentity Step, string Form, string What, IReadOnlyDictionary<string, string> Inputs, StepContract Contract, bool DryRun,
    TimeSpan? Timeout, AgentStepSpec? Agent = null);

/// <summary>How a step's activity ended: with a report, a work failure, or unable to answer (docs/04 section 1).</summary>
public sealed record StepOutcome(string Kind, StepReport? Report, string? Reason, IReadOnlyList<string>? Events = null)
{
    public const string Reported = "reported";
    public const string WorkFailed = "work_failed";
    public const string CannotAnswer = "cannot_answer";
}

/// <summary>The engine's endpoints as the agent in a sandbox reaches them, with the step's token (docs/02 section 4.2).</summary>
public sealed record EngineEndpoints(IReadOnlyList<string> AgentUrls, string ModelBaseUrl, string StepToken);

/// <summary>Where an agent works in its sandbox: the checkout, its output folder, the inputs manifest and its home (DR17, DR19).</summary>
public sealed record AgentPaths(string Checkout, string Output, string InputsManifest, string Home, string Client)
{
    /// <summary>The layout every sandbox uses: the checkout, the output folder, staged inputs, the runtime's home and the sandbox client.</summary>
    public static readonly AgentPaths Default = new("/ff/work", "/ff/out", "/ff/inputs/manifest.json", "/ff/home", "/ff/bin/ff-sandbox");
}

/// <summary>
/// An agent step's task (docs/06 section 3.2): the role, the rendered task with the reporting preamble, the tool
/// classes, the engine's endpoints and the step's token, the paths in the sandbox, and the model. M3's scripted
/// runtime reads only the identity, role, inputs and contract.
/// </summary>
public sealed record AgentTask(StepIdentity Step, string Role, string? TaskText, IReadOnlyDictionary<string, string> Inputs, StepContract Contract)
{
    public IReadOnlyList<string> Tools { get; init; } = [];
    public string WriteAccess { get; init; } = "none";
    public EngineEndpoints? Engine { get; init; }
    public AgentPaths? Paths { get; init; }

    /// <summary>Whether the engine's MCP tools are offered. False only for a runtime asked to report through the file (SP3 T3).</summary>
    public bool EngineTools { get; init; } = true;

    public bool Questions { get; init; }

    /// <summary>Which session of the step this is: 1 for the first; each continuation (a correction turn, a resume) adds one.</summary>
    public int Session { get; init; } = 1;
}

/// <summary>
/// An agent step's limits: its timeout, correction turns, and the longest a question may wait, which the
/// runtime's tool timeout must exceed (DR16).
/// </summary>
public sealed record AgentLimits(TimeSpan? Timeout, int CorrectionTurns, TimeSpan? QuestionsTimeout = null);

public sealed record AgentSession(string Id, string Runtime);

public sealed record AgentProgress(bool Finished, int Turns);

/// <summary>Why a session ended, as the runtime saw it.</summary>
public static class SessionEnd
{
    /// <summary>The runtime finished its turn normally.</summary>
    public const string Completed = "completed";

    /// <summary>The runtime could not reach its model: an infrastructure failure, resumable (SP5 N1).</summary>
    public const string ModelUnreachable = "model_unreachable";

    /// <summary>The sandbox client ended the session because the engine stayed unreachable (LOG 60).</summary>
    public const string EngineUnreachable = "engine_unreachable";

    /// <summary>The runtime exited with an error of its own.</summary>
    public const string Failed = "failed";
}

/// <summary>
/// What the runtime read when a session ended: how it ended, its usage, the files the agent opened where the
/// runtime streams its tool calls, and the transcript. <see cref="ReportFile"/> is the JSON the agent wrote to
/// <c>report.json</c> in its output folder (the file fallback, docs/04 section 3.3), unchecked; the engine checks
/// it. <see cref="Report"/> is a fake runtime's own report.
/// </summary>
public sealed record AgentResult(
    StepReport? Report,
    string End,
    string? Detail = null,
    long InputTokens = 0,
    long OutputTokens = 0,
    IReadOnlyList<string>? FilesOpened = null,
    string? Transcript = null,
    string? ReportFile = null);

/// <summary>
/// The agent runtime (docs/06 section 3.2). From M4 it runs in a sandbox, with hooks and the engine MCP server
/// configured from the task. A report through the engine's <c>report</c> tool is recorded by the engine; the
/// result read here carries the file fallback and how the session ended.
/// </summary>
public interface IAgentRuntimeProvider : IProvider
{
    Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken);

    /// <summary>Re-attaches to a session still running in the sandbox, by the step's key; <c>not_found</c> when none runs.</summary>
    Task<ProviderResult<AgentSession>> AttachAsync(SandboxHandle sandbox, AgentTask task, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult<AgentSession>.Failure("not_found", "this runtime cannot re-attach"));

    /// <summary>
    /// Continues a session that ended, from its recorded reference, with a further message: the errors of a
    /// rejected report (a correction turn), or word that the previous attempt was interrupted.
    /// </summary>
    Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken);

    Task<ProviderResult<AgentSession>> ContinueAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, string message, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult<AgentSession>.Failure("capability_missing", "this runtime cannot continue a session"));

    Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken);

    Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken);

    Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken);
}

/// <summary>One engine action to run, with its evaluated inputs (JSON text) and its contract.</summary>
public sealed record ActionRequest(StepIdentity Step, string Action, IReadOnlyDictionary<string, string> Inputs, StepContract Contract);

/// <summary>
/// The stand-in for the engine actions later milestones build (LOG 81). Every action except <c>run.child</c>
/// and <c>plan.expand</c> is run through it in M3. A real action calls the providers of docs/06 instead; each
/// milestone that builds one takes it off the stand-in. Never configured in a production installation.
/// </summary>
public interface IActionStandIn : IProvider
{
    Task<ProviderResult<StepReport>> RunAsync(ActionRequest request, CancellationToken cancellationToken);
}
