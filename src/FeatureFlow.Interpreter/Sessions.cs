using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

public sealed partial class RunCore
{
    /// <summary>
    /// A value a decision's <c>session</c> names (docs/03 section 2.3: its repository or folder), each <c>{ expr: }</c>
    /// evaluated now at the step. Throws <see cref="EvaluationException"/> when it cannot be evaluated.
    /// </summary>
    public object? ResolveSessionValue(string stepId, YNode node, string where)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        return Resolve(node, step, where, name => Findings?.Read(name) ?? State.Findings.GetValueOrDefault(name));
    }
}
