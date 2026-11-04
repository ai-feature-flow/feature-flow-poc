using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

/// <summary>
/// Answers to interventions (docs/01 section 7.2), aborting a run, a step that cannot answer, and the values a
/// shell needs to carry out a step: its evaluated inputs, and a child run's outcome.
/// </summary>
public sealed partial class RunCore
{
    /// <summary>Statuses a person may end a run with. <c>completed</c> is not one: only an end route completes a run (F9).</summary>
    public static readonly IReadOnlyList<RunStatus> EndableStatuses = [RunStatus.Failed, RunStatus.Rejected, RunStatus.Partial, RunStatus.Aborted];

    /// <summary>
    /// Whether an answer from outside the run would be accepted now, without applying it: the update validator's
    /// check. Returns null when it would be accepted, else the reason.
    /// </summary>
    public string? Check(RunInput input)
    {
        if (State.Status != RunStatus.Running)
            return $"the run has ended ({State.Status.Name()})";
        switch (input)
        {
            case DecisionAnswered a:
                if (State.Waits.FirstOrDefault(w => w.Kind == WaitKind.Decision && w.StepId == a.StepId) is null || StepById(a.StepId) is not { } step)
                    return $"no decision is open at {a.StepId}";
                return step.DeclaredOutcomes.Any(o => o.Name == a.Outcome)
                    ? null
                    : $"{a.StepId}: '{a.Outcome}' is not a declared outcome ({string.Join(", ", step.DeclaredOutcomes.Select(o => o.Name))})";
            case InterventionAnswered i:
                return RecoveryError(i, out _);
            case PatchAnswered p:
                return State.Waits.Any(w => w.Id == p.WaitId && w.Kind == WaitKind.PatchApproval) ? null : $"no patch approval is open as {p.WaitId}";
            case RunAborted:
                return null;
            case BreakpointSet or StepSkipped or AttemptsGranted or RoleChanged or LibraryOverridden or LibraryOverrideProposed:
                return SteeringError(input);
            default:
                return $"{input.GetType().Name} is not an answer";
        }
    }

    string? RecoveryError(InterventionAnswered i, out OpenWait? wait)
    {
        wait = State.Waits.FirstOrDefault(w => w.Id == i.WaitId && w.Kind is WaitKind.Intervention or WaitKind.Breakpoint);
        if (wait is null)
            return $"no intervention or breakpoint is open as {i.WaitId}";
        var action = i.Action;
        switch (action.Kind)
        {
            case RecoveryKind.Continue when wait.Kind != WaitKind.Breakpoint:
                return $"{i.WaitId} is an intervention; continue answers a breakpoint";
            case RecoveryKind.Retry or RecoveryKind.Continue:
                return StepById(wait.StepId) is null ? $"{wait.StepId} is not a step of plan revision {State.Revision}; go to another step" : null;
            case RecoveryKind.GrantAttempts:
                if (StepById(wait.StepId) is null)
                    return $"{wait.StepId} is not a step of plan revision {State.Revision}";
                // A grant at a step that uses no attempts (a decision, a join) raises the run ceiling alone.
                return GrantError(StepById(wait.StepId)!.ConsumesAttempts ? wait.StepId : null, action.Attempts, action.BeyondHardCeiling);
            case RecoveryKind.GoTo:
                if (action.StepId is null || FindIn(action.StepId) is not { } target)
                    return $"go to: '{action.StepId}' is not a step of plan revision {State.Revision}";
                return action.CleanUp && StepById(target)?.Form == StepForm.Decision
                    ? $"go to: {target} is a decision, which touches no repository; clean_up resets to where a step started, so go to the step after it"
                    : null;
            case RecoveryKind.Replan:
                if (action.StepId is null || FindIn(action.StepId) is not { } id)
                    return $"re-plan: '{action.StepId}' is not a step of plan revision {State.Revision}";
                return StepById(id)?.Agent?.Planning is null ? $"re-plan: {id} is not an agent step with planning authority" : null;
            case RecoveryKind.End:
                return action.Status is { } status && EndableStatuses.Contains(status)
                    ? null
                    : $"end: the status must be one of {string.Join(", ", EndableStatuses.Select(s => s.Name()))}";
            default:
                return $"unknown recovery action {action.Kind}";
        }
    }

    ApplyResult Recover(InterventionAnswered i)
    {
        if (RecoveryError(i, out var wait) is { } error)
            return ApplyResult.Refused(error);
        State.Waits.Remove(wait!);
        var action = i.Action;
        Trace("recovered", wait!.StepId, action.Kind switch
        {
            RecoveryKind.GoTo or RecoveryKind.Replan => $"{action.Kind.ToString().ToLowerInvariant()} {action.StepId}{(action.CleanUp ? " (clean_up)" : "")}",
            RecoveryKind.GrantAttempts => $"grant {action.Attempts} attempt(s)",
            RecoveryKind.End => $"end {action.Status!.Value.Name()}",
            RecoveryKind.Continue => "continue",
            _ => "retry",
        });
        // At a breakpoint the paused step starts as it was entered, past its breakpoint this once.
        var resume = wait.Kind == WaitKind.Breakpoint ? new Entry(wait.StepId, wait.CleanUp, PastBreakpoint: true) : new Entry(wait.StepId, false);
        switch (action.Kind)
        {
            case RecoveryKind.Retry or RecoveryKind.Continue:
                State.Pending.Add(resume);
                break;
            case RecoveryKind.GrantAttempts:
                Grant(StepById(wait.StepId)!.ConsumesAttempts ? wait.StepId : null, action.Attempts);
                State.Pending.Add(resume);
                break;
            case RecoveryKind.GoTo or RecoveryKind.Replan:
                State.Pending.Add(new Entry(FindIn(action.StepId!)!, action.CleanUp));
                break;
            case RecoveryKind.End:
                End(action.Status!.Value, wait.StepId, "ended by a person at an intervention");
                break;
        }

        return ApplyResult.Ok;
    }

    ApplyResult Abort(RunAborted a)
    {
        End(RunStatus.Aborted, "", "aborted: " + a.Reason);
        return ApplyResult.Ok;
    }

    ApplyResult CannotAnswer(StepCannotAnswer s)
    {
        if (StepById(s.StepId) is null || !State.InFlight.Contains(s.StepId))
            return ApplyResult.Refused($"{s.StepId} is not running");
        State.InFlight.Remove(s.StepId);
        Intervene(s.StepId, null, s.Reason);
        return ApplyResult.Ok;
    }

    /// <summary>
    /// Every output a step publishes, at its type's zero value: its declared outputs, the engine's outputs of an
    /// agent step (docs/04 section 3.6; <c>coverage_known</c> true) and an action's published outputs. What a
    /// fake or recording provider reports when nothing else is given, as the re-drive runner does (LOG 75).
    /// </summary>
    public static Dictionary<string, object?> ZeroOutputs(Step step)
    {
        var names = step.Outputs.Select(o => o.Name).ToList();
        if (step.Form == StepForm.Agent)
            names.AddRange(Model.Actions.AgentEngineOutputs);
        else if (step.Action is { } a && Model.Actions.All.GetValueOrDefault(a)?.Outputs is { } fixedOutputs)
            names.AddRange(fixedOutputs);
        var outputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var name in names.Distinct())
            outputs[name] = name == "coverage_known" ? true : OutputType(step, name) switch
            {
                "number" => 0L,
                "boolean" => false,
                "string" => "",
                "array" => new List<object?>(),
                _ => null,
            };
        return outputs;
    }

    /// <summary>A <c>run.child</c> step's outcome from the child's run status (docs/03 section 9.3).</summary>
    public static string ChildOutcome(RunStatus status) => status switch
    {
        RunStatus.Completed => "completed",
        RunStatus.Partial => "partial",
        RunStatus.Rejected => "rejected",
        _ => "failed",
    };

    /// <summary>
    /// The parameters a child run starts with (docs/03 section 6.2): this run's resolved parameters for the names the
    /// child's template declares, then <paramref name="given"/>, the <c>run.child</c> step's <c>with.parameters</c>, which
    /// take precedence. A parameter of this run that has no value is not passed, so the child's own default applies.
    /// </summary>
    public Dictionary<string, object?> ChildParameters(string template, IReadOnlyDictionary<string, object?> given)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var declared = LibraryReference.TryParse(template) is { } reference && libraries?.Resolve(reference)?.Yaml(reference.Type, reference.Name) is YMap document
                       && document["parameters"] is YMap parameters
            ? parameters.Keys.ToList()
            : [];
        long ReadFindings(string name) => Findings?.Read(name) ?? State.Findings.GetValueOrDefault(name);
        foreach (var name in declared.Where(n => !given.ContainsKey(n) && Plan.Document.Parameter(n) is not null))
        {
            try
            {
                if (evaluator.Parameter(Plan.Root, name, ReadFindings) is { } value)
                    result[name] = value;
            }
            catch (EvaluationException)
            {
                // Unbound here: the child's own default, or its run start check (18), decides.
            }
        }

        foreach (var (name, value) in given)
            result[name] = value;
        return result;
    }

    /// <summary>
    /// A step's <c>with</c> inputs as plain values, each <c>{ expr: }</c> evaluated now. Throws
    /// <see cref="EvaluationException"/> when one cannot be evaluated; the shell then reports
    /// <see cref="StepCannotAnswer"/>.
    /// </summary>
    public Dictionary<string, object?> ResolveInputs(string stepId)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (step.With is null)
            return result;
        var cache = new Dictionary<string, long>(StringComparer.Ordinal);
        long ReadFindings(string name)
        {
            if (!cache.TryGetValue(name, out var value))
                cache[name] = value = Findings?.Read(name) ?? State.Findings.GetValueOrDefault(name);
            return value;
        }

        foreach (var (key, node) in step.With.Entries)
            result[key.Text] = Resolve(node, step, $"with.{key.Text}", ReadFindings);
        return result;
    }

    object? Resolve(YNode node, Step step, string where, Func<string, long> findings)
    {
        if (node.IsSingleKeyMap("expr", out var text) && text is YScalar s)
            return evaluator.Evaluate(new Expression(s.Text, ExpressionUse.Value, step.Scope, s.Location, where, step.Id), findings);
        return node switch
        {
            YMap m => m.Entries.ToDictionary(e => e.Key.Text, e => Resolve(e.Value, step, $"{where}.{e.Key.Text}", findings)),
            YSeq q => q.Items.Select((x, k) => Resolve(x, step, $"{where}[{k}]", findings)).ToList(),
            YUnbound u => throw new EvaluationException($"{where}: parameter '{u.Parameter}' is unbound"),
            _ => node.ToPlain(),
        };
    }

    bool renderingBranch;

    /// <summary>
    /// The run's work branch (docs/03 section 1, LOG 44): the template's <c>metadata.branch_pattern</c> rendered with
    /// <c>params</c> and <c>run</c>, else <c>ff/&lt;level&gt;/&lt;run id&gt;</c>. The same in every repository of the run.
    /// </summary>
    public string WorkBranch()
    {
        var level = Plan.Document.Level ?? "run";
        var fallback = $"ff/{level}/{Run.Id}";
        if (!Options.BranchFromPattern || Plan.Document.BranchPattern is not { } pattern || renderingBranch)
            return fallback;
        renderingBranch = true;
        try
        {
            return Render(pattern, Plan.Root, null, Plan.Document.BranchPatternLocation, "metadata.branch_pattern", null);
        }
        catch (EvaluationException)
        {
            return fallback;
        }
        finally
        {
            renderingBranch = false;
        }
    }

    /// <summary>The repository a step names in its <c>repository</c> field, evaluated; null when it names none.</summary>
    public string? ResolveRepository(string stepId)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        return step.Repository is { } node ? Resolve(node, step, "repository", name => Findings?.Read(name) ?? State.Findings.GetValueOrDefault(name)) as string : null;
    }

    /// <summary>
    /// A prompt that an action input names by reference, such as <c>scm.pull_request</c>'s <c>body</c>, rendered like a
    /// task (docs/03 section 6.3). Null when <paramref name="value"/> is not a prompt reference the run's libraries hold.
    /// </summary>
    public string? RenderPrompt(string stepId, string value)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        if (LibraryReference.TryParse(value) is not { Type: "prompts" } reference || libraries?.Resolve(reference) is not { } library
            || library.Text("prompts", reference.Name) is not { } text)
            return null;
        return Render(text, step.Scope, step.Id, null, $"{reference} text", ResolveInputs(stepId));
    }

    /// <summary>
    /// An agent step's task, rendered (docs/03 section 6.3): each <c>{{ expr }}</c> evaluated with the step's
    /// <c>with</c> values and <c>params</c>, <c>run</c> and <c>steps</c>. Null when the step names no task. A string
    /// renders as itself; other values as JSON.
    /// </summary>
    public (string? Text, string? Library) RenderTask(string stepId)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        if (step.Agent?.Task is not { Reference: { } reference, Unbound: false } || libraries?.Resolve(reference) is not { } library
            || library.Text("tasks", reference.Name) is not { } text)
            return (null, null);
        var inputs = ResolveInputs(stepId);
        return (Render(text, step.Scope, step.Id, null, $"{reference} text", inputs), library.Name);
    }

    string Render(string text, Scope scope, string? stepId, SourceLocation? location, string where, IReadOnlyDictionary<string, object?>? with)
    {
        var output = new System.Text.StringBuilder();
        var at = 0;
        while (true)
        {
            var start = text.IndexOf("{{", at, StringComparison.Ordinal);
            var end = start < 0 ? -1 : text.IndexOf("}}", start + 2, StringComparison.Ordinal);
            if (start < 0 || end < 0)
            {
                output.Append(text, at, text.Length - at);
                return output.ToString();
            }

            output.Append(text, at, start - at);
            var expression = new Expression(text[(start + 2)..end].Trim(), ExpressionUse.Text, scope, location ?? new SourceLocation(where, 0, 0), where, stepId,
                with?.Keys.ToList());
            var value = evaluator.Evaluate(expression, name => Findings?.Read(name) ?? State.Findings.GetValueOrDefault(name), with);
            output.Append(value as string ?? (value is null ? "" : YamlWriter.WritePlain(value)));
            at = end + 2;
        }
    }
}
