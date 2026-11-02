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

/// <summary>A sandbox, created by the sandbox provider (M4). M3 runs agent steps with no sandbox: <see cref="None"/>.</summary>
public sealed record SandboxHandle(string Id, string Provider)
{
    public static readonly SandboxHandle None = new("none", "none");
}

/// <summary>An agent step's task (docs/06 section 3.2), as far as M3 renders it: the role, task text and inputs (JSON text).</summary>
public sealed record AgentTask(StepIdentity Step, string Role, string? TaskText, IReadOnlyDictionary<string, string> Inputs, StepContract Contract);

public sealed record AgentLimits(TimeSpan? Timeout, int CorrectionTurns);

public sealed record AgentSession(string Id, string Runtime);

public sealed record AgentProgress(bool Finished, int Turns);

/// <summary>
/// The agent runtime (docs/06 section 3.2). M3 calls it without a sandbox; M4 adds the sandbox, hooks and the
/// engine MCP server. <see cref="GetResultAsync"/> is the source of truth for the agent's report.
/// </summary>
public interface IAgentRuntimeProvider : IProvider
{
    Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken);

    Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken);

    Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken);

    Task<ProviderResult<StepReport>> GetResultAsync(AgentSession session, CancellationToken cancellationToken);

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
