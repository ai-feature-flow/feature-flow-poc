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
            default:
                return $"{input.GetType().Name} is not an answer";
        }
    }

    string? RecoveryError(InterventionAnswered i, out OpenWait? wait)
    {
        wait = State.Waits.FirstOrDefault(w => w.Id == i.WaitId && w.Kind == WaitKind.Intervention);
        if (wait is null)
            return $"no intervention is open as {i.WaitId}";
        var action = i.Action;
        switch (action.Kind)
        {
            case RecoveryKind.Retry:
                return StepById(wait.StepId) is null ? $"{wait.StepId} is not a step of plan revision {State.Revision}; go to another step" : null;
            case RecoveryKind.GrantAttempts:
                if (action.Attempts <= 0)
                    return "grant attempts needs a positive number of attempts";
                return StepById(wait.StepId) is null ? $"{wait.StepId} is not a step of plan revision {State.Revision}" : null;
            case RecoveryKind.GoTo:
                return action.StepId is null || FindIn(action.StepId) is null ? $"go to: '{action.StepId}' is not a step of plan revision {State.Revision}" : null;
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
            _ => "retry",
        });
        switch (action.Kind)
        {
            case RecoveryKind.Retry:
                State.Pending.Add(new Entry(wait.StepId, false));
                break;
            case RecoveryKind.GrantAttempts:
                Record(wait.StepId).AttemptsGranted += action.Attempts;
                State.RunCeilingGranted += action.Attempts;
                State.Pending.Add(new Entry(wait.StepId, false));
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
}
