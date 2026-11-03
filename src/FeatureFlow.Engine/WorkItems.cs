using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Engine;

/// <summary>The work-item budget (F11; docs/05 section 3.1): how a run tree's budget is fixed and what a patch would add to it.</summary>
public static class WorkItems
{
    /// <summary>
    /// A root run's budget: its template's <c>budget.max_work_items</c>, within the organisation's ceiling, or the ceiling
    /// when the template sets none. Null when neither does: the tree is unlimited. A template asking for more than the
    /// ceiling does not start (check 22), so the lower of the two is the request itself.
    /// </summary>
    public static WorkItemBudget? Root(string runId, Plan plan, RunPolicy? policy) =>
        (plan.Document.MaxWorkItems, policy?.WorkItemCeiling) switch
        {
            ({ } asked, { } ceiling) => new WorkItemBudget(runId, Math.Min(asked, ceiling)),
            ({ } asked, null) => new WorkItemBudget(runId, asked),
            (null, { } ceiling) => new WorkItemBudget(runId, ceiling),
            _ => null,
        };

    /// <summary>How many child runs a patch adds: its <c>run.child</c> steps.</summary>
    public static int ChildSteps(PatchDocument patch) =>
        patch.Changes.Count(op => op.Step?["action"]?.ScalarText() == "run.child");
}
