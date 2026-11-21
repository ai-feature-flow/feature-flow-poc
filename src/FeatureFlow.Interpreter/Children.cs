using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

/// <summary>
/// What a child run starts with (docs/03 section 9.2): its workflow id (its run id), template, parameters as JSON text, the
/// repositories it is given, its tracker issue, and this run's artifacts as they are now, as its parent's and its root's.
/// </summary>
public sealed record ChildSpec(
    string WorkflowId,
    string Template,
    IReadOnlyDictionary<string, string> Parameters,
    IReadOnlyList<string> Repositories,
    string? TrackerIssue,
    IReadOnlyDictionary<string, string> ParentArtifacts,
    IReadOnlyDictionary<string, string>? RootArtifacts);

/// <summary>
/// Child runs (docs/03 section 9; docs/02 section 3): the core decides when a <c>run.child</c> step starts its child
/// (<see cref="StartChild"/>) and when one is no longer needed (<see cref="CancelChild"/>), and holds the children that run
/// (<see cref="RunState.Children"/>), so they are carried with the rest of its state over a hand-over.
/// </summary>
public sealed partial class RunCore
{
    /// <summary>
    /// What the child of a <c>run.child</c> step's execution starts with, evaluated now. Throws <see cref="EvaluationException"/>
    /// when the step's inputs cannot be evaluated.
    /// </summary>
    public ChildSpec ChildSpec(string stepId, int execution)
    {
        var inputs = ResolveInputs(stepId);
        var template = inputs.GetValueOrDefault("template") as string ?? "";
        var given = inputs.GetValueOrDefault("parameters") as IReadOnlyDictionary<string, object?> ?? new Dictionary<string, object?>();
        // docs/03 section 9.2: the parent's resolved parameters for names the child declares, and its artifacts as they are now.
        var parameters = ChildParameters(template, given)
            .Where(kv => kv.Value is not null)
            .ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value));
        var artifacts = RunArtifacts(stepId);
        var repositories = (inputs.GetValueOrDefault("repositories") as IEnumerable<object?> ?? Run.Repositories)
            .Select(r => RepositoryNames.As(r?.ToString() ?? "", Run.Repositories)).ToList();
        return new ChildSpec($"{Run.Id}.{stepId}.{execution}", template, parameters, repositories, inputs.GetValueOrDefault("tracker_issue") as string,
            artifacts, Run.ParentId is null ? artifacts : Run.RootArtifacts);
    }

    /// <summary>The <c>run.child</c> step that started a running child, or null when no running child has that workflow id.</summary>
    public string? ChildStep(string workflowId) => State.Children.LastOrDefault(c => c.WorkflowId == workflowId)?.StepId;

    /// <summary>Every child run this run started, running or ended: one per execution of each <c>run.child</c> step (LOG 215).</summary>
    public IReadOnlyList<string> ChildrenStarted() =>
        State.Steps.Where(kv => StepById(kv.Key)?.Descriptor?.Handling == ActionHandling.ChildRun).OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => Enumerable.Range(1, kv.Value.Executions).Select(n => $"{Run.Id}.{kv.Key}.{n}")).ToList();

    ApplyResult StartedChild(ChildStarted c)
    {
        State.Children.Add(new ChildRun(c.StepId, c.Execution, c.WorkflowId));
        Trace("child_started", c.StepId, c.WorkflowId);
        return ApplyResult.Ok;
    }

    /// <summary>
    /// A child has ended: it is no longer running, and its step completes with the outcome of the child's status if it still
    /// runs the execution that started the child. An end the step's contract refuses stops the step for a person.
    /// </summary>
    ApplyResult EndedChild(ChildEnded e)
    {
        var at = State.Children.FindLastIndex(c => c.WorkflowId == e.WorkflowId);
        if (at < 0)
            return ApplyResult.Refused($"{e.WorkflowId} is not a running child of this run");
        var child = State.Children[at];
        State.Children.RemoveAt(at);
        if (!IsCurrent(child.StepId, child.Execution))
            return ApplyResult.Ok;
        if (Apply(new StepCompleted(child.StepId, ChildOutcome(e.Status), Artifacts: e.Artifacts)) is { Accepted: false } refused)
            Apply(new StepCannotAnswer(child.StepId, "the child's end could not be applied: " + string.Join("; ", refused.Errors)));
        return ApplyResult.Ok;
    }

    /// <summary>Whether a step's result still belongs to the run: the run goes on and the step runs the execution that started it.</summary>
    public bool IsCurrent(string stepId, int execution) =>
        State.Status == RunStatus.Running && State.InFlight.Contains(stepId) && State.Steps.GetValueOrDefault(stepId)?.Executions == execution;
}
