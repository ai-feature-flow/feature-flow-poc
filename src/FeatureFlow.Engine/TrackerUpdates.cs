using FeatureFlow.Interpreter;
using FeatureFlow.Model.Plans;

namespace FeatureFlow.Engine;

/// <summary>One declarative tracker update that is due: the step (or fragment use) that declared it, and the library status to move to.</summary>
public sealed record TrackerUpdate(string StepId, string Status, string Why);

/// <summary>
/// Declarative <c>tracker</c> updates (docs/03 section 2.5), worked out from what the run just did, as its timeline records
/// it: a step's <c>on_start</c> when it starts, and <c>on.&lt;outcome&gt;</c> when it ends with that outcome. A fragment use's
/// <c>on_start</c> applies when the fragment's first step starts, and its <c>on.&lt;exit&gt;</c> when a step inside it ends
/// with an outcome routed straight to that exit. Pure, so the workflow derives the same updates on replay.
/// </summary>
public static class TrackerUpdates
{
    public static bool Declared(Plan plan) => plan.AllSteps.Any(s => s.Tracker is not null) || plan.Instances.Any(i => i.Tracker is not null);

    public static IReadOnlyList<TrackerUpdate> Due(Plan plan, IEnumerable<TraceEntry> trace)
    {
        var due = new List<TrackerUpdate>();
        var steps = plan.AllSteps.ToDictionary(s => s.Id, StringComparer.Ordinal);
        foreach (var entry in trace)
        {
            if (!steps.TryGetValue(entry.StepId, out var step))
                continue;
            if (entry.Kind == "started")
            {
                if (step.Tracker?.OnStart is { } status)
                    due.Add(new TrackerUpdate(step.Id, status, $"{step.Id} started"));
                foreach (var instance in plan.Instances.Where(i => i.FirstStepId == step.Id && i.Tracker?.OnStart is not null))
                    due.Add(new TrackerUpdate(instance.Id, instance.Tracker!.OnStart!, $"{instance.Id} started"));
            }
            else if (entry.Kind == "completed" && entry.Detail is { } outcome)
            {
                if (step.Tracker?.On.GetValueOrDefault(outcome) is { } status)
                    due.Add(new TrackerUpdate(step.Id, status, $"{step.Id} ended {outcome}"));
                foreach (var route in step.Routes.Where(r => r.Outcome == outcome && r.Target is ExitFragment))
                {
                    var exit = ((ExitFragment)route.Target).Exit;
                    if (plan.Instances.FirstOrDefault(i => i.Id == route.Scope.DocumentScope.UseId) is { Tracker: { } tracker } instance
                        && tracker.On.GetValueOrDefault(exit) is { } exitStatus)
                        due.Add(new TrackerUpdate(instance.Id, exitStatus, $"{instance.Id} left by {exit}"));
                }
            }
        }

        return due;
    }
}
