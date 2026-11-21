using FeatureFlow.Model;
using FeatureFlow.Model.Plans;

namespace FeatureFlow.Interpreter;

/// <summary>An outcome a step may report, with its class name (<c>success</c>, <c>failure</c>, <c>blocked</c>, <c>~</c>).</summary>
public sealed record OutcomeFact(string Name, string Class);

/// <summary>An artifact a step promises: for one outcome, or for every success when <see cref="Outcome"/> is null; with its declared type.</summary>
public sealed record ArtifactPromise(string? Outcome, string Name, string? Type);

/// <summary>
/// What a step must report (docs/03 section 7, docs/04 section 3.3): its outcomes, the outputs it publishes with their types
/// and zero values, the artifacts it promises, and the outputs it reports itself.
/// </summary>
public sealed record StepContractFacts(
    IReadOnlyList<OutcomeFact> Outcomes,
    IReadOnlyDictionary<string, string> OutputTypes,
    IReadOnlyList<ArtifactPromise> Artifacts,
    IReadOnlyDictionary<string, object?> ZeroOutputs,
    IReadOnlyList<string> Reported);

/// <summary>
/// An agent step's settings from the plan (docs/04 section 3): the role's write access and tool classes, the rendered task and
/// its library, skills, limits, push and uncommitted-work settings, the run's level, the artifacts it consumes by key (the
/// findings ledger by its view name), the role's name, which the run's <c>role_mapping</c> maps to a runtime, and the run
/// context rendered for it (docs/04 section 3.5), null with <c>context: none</c> or when the run has none.
/// </summary>
public sealed record AgentStepFacts(
    string WriteAccess,
    IReadOnlyList<string> Tools,
    string? TaskText,
    string? TaskLibrary,
    IReadOnlyList<string> Skills,
    int CorrectionTurns,
    int QuestionsMax,
    TimeSpan? QuestionsTimeout,
    bool Push,
    string OnUncommitted,
    string Level,
    IReadOnlyDictionary<string, string> Consumed,
    string? Role,
    string? RunContext = null);

/// <summary>
/// What a step's activity is given, from the plan and the state now: which activity, what it runs (a role or an action), its
/// inputs evaluated (prompt references rendered), its contract, its timeout, and an agent step's settings.
/// </summary>
public sealed record StepSpec(StepActivity Activity, string What, IReadOnlyDictionary<string, object?> Inputs, StepContractFacts Contract, TimeSpan? Timeout,
    AgentStepFacts? Agent);

/// <summary>Steps' specifications for the shell's activities (docs/04 sections 2 and 3). Workflow code holds the library (LOG 83), so tasks render here.</summary>
public sealed partial class RunCore
{
    /// <summary>
    /// What a step's activity is given, or why it cannot start: its inputs cannot be evaluated, it consumes an artifact that
    /// does not exist (check 32, when <paramref name="atStart"/>), or a prompt or task it names cannot be rendered. A session's
    /// pre-submit check is given the same, without check 32, so it stands for the step (docs/09 section 4.2).
    /// </summary>
    public (StepSpec? Spec, string? Error) StepSpec(string stepId, bool atStart = true)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        Dictionary<string, object?> inputs;
        try
        {
            inputs = ResolveInputs(stepId);
        }
        catch (EvaluationException e)
        {
            return (null, "its inputs cannot be evaluated: " + e.Message);
        }

        if (atStart && MissingConsumed(stepId) is { } missing)
            return (null, missing);

        AgentStepFacts? agent = null;
        if (step.Form == StepForm.Action)
        {
            try
            {
                foreach (var name in step.Descriptor?.ReferenceInputs ?? [])
                    if (inputs.GetValueOrDefault(name) is string reference && RenderPrompt(stepId, reference) is { } rendered)
                        inputs[name] = rendered;
            }
            catch (EvaluationException e)
            {
                return (null, "a prompt it names cannot be rendered: " + e.Message);
            }
        }
        else
        {
            try
            {
                agent = AgentFacts(step);
            }
            catch (EvaluationException e)
            {
                return (null, "its task cannot be rendered: " + e.Message);
            }
        }

        var activity = ActivityOf(step);
        return (new StepSpec(activity, step.Form == StepForm.Agent ? step.Agent!.Role?.Text ?? "" : step.Action!, inputs, Contract(step),
            Durations.Parse(step.Limits?.Timeout), agent), null);
    }

    static StepContractFacts Contract(Step step)
    {
        var zero = ZeroOutputs(step);
        return new StepContractFacts(
            step.Outcomes().Where(o => o.Name != Actions.AttemptsExhausted).Select(o => new OutcomeFact(o.Name, o.Class.Name())).ToList(),
            zero.Keys.ToDictionary(n => n, n => OutputType(step, n) ?? "dyn"),
            step.Produces.Select(p => new ArtifactPromise(p.Outcome, p.Name, p.Type?.Reference?.ToString())).ToList(),
            zero,
            step.Outputs.Select(o => o.Name).ToList());
    }

    AgentStepFacts AgentFacts(Step step)
    {
        var agent = step.Agent!;
        var role = agent.Role?.Reference is { } roleRef ? libraries?.Resolve(roleRef)?.Role(roleRef.Name) : null;
        var (taskText, taskLibrary) = RenderTask(step.Id);
        var limits = step.Limits;
        var consumed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in Consumed(step.Id))
            if (c.LedgerView)
                // The ledger changes outside the run's artifacts: the agent worker stages its view when the step starts (LOG 63).
                consumed[c.Key] = Validation.PlanValidator.FindingsLedger;
            else if (c.Reference is { } reference)
                consumed.TryAdd(c.Key, reference);
        return new AgentStepFacts(
            agent.WriteAccess ?? role?.WriteAccess ?? "none",
            role?.Tools ?? ["read", "search"],
            taskText,
            taskLibrary,
            agent.Skills.Select(s => s.Reference?.ToString()).OfType<string>().ToList(),
            (int)(limits?.CorrectionTurns ?? 2),
            (int)(limits?.QuestionsMax ?? (limits?.QuestionsTimeout is not null ? 3 : 0)),
            Durations.Parse(limits?.QuestionsTimeout),
            agent.Push,
            agent.OnUncommitted,
            Plan.Document.Level ?? "run",
            consumed,
            agent.Role?.Reference?.Name,
            agent.Context == "none" ? null : RunContext());
    }

    /// <summary>Whether a running step's action waits on the outside world between reads, so a hand-over or a wake interrupts it (LOG 139).</summary>
    public bool WaitsBetweenReads(string stepId) => StepById(stepId)?.Descriptor?.WaitsBetweenReads == true;

    /// <summary>Whether an outcome of a step is a success.</summary>
    public bool IsSuccess(string stepId, string outcome) => StepById(stepId)?.Outcomes().FirstOrDefault(o => o.Name == outcome)?.Class == OutcomeClass.Success;

    /// <summary>Executions a step has started: <c>steps.&lt;id&gt;.executions</c>.</summary>
    public int Executions(string stepId) => State.Steps.GetValueOrDefault(stepId)?.Executions ?? 0;

    /// <summary>The steps that have run at least once, by id: where a <c>clean_up</c> reset looks for the step's starting point.</summary>
    public IReadOnlyList<string> StepsRun() => State.Steps.Where(kv => kv.Value.Executions > 0).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList();
}
