using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Activities;
using Temporalio.Exceptions;

namespace FeatureFlow.Engine;

/// <summary>A declarative tracker update to make (docs/03 section 2.5): the run's issue and the library status to move it to.</summary>
public sealed record TrackerCall(string Issue, string Status, string StepId, bool DryRun);

/// <summary>
/// A request against a run tree's work-item budget (F11): to create <see cref="Adding"/> descendant runs. With
/// <see cref="Child"/>, the one run about to be started, recorded as the reservation if the budget allows it.
/// </summary>
public sealed record WorkItemRequest(WorkItemBudget Budget, int Adding, RunRecord? Child = null);

/// <summary>The engine's activities for delivery: declarative tracker updates and the work-item budget.</summary>
public sealed partial class RunActivities
{
    /// <summary>
    /// Moves the run's tracker issue to a library status. A failed update never fails a step (docs/03 section 2.5): an
    /// infrastructure error is retried; a configuration fault, a status the tracker does not map or an issue it does not
    /// have comes back as a note for the run's timeline.
    /// </summary>
    [Activity]
    public async Task<string> UpdateTrackerAsync(TrackerCall call)
    {
        if (services.Providers.ForRun("tracker", call.DryRun) is not ITrackerProvider tracker)
            return $"no tracker is configured: {call.Issue} stays as it is (wanted {call.Status})";
        var result = await tracker.TransitionAsync(new IssueRef(call.Issue), call.Status, ActivityExecutionContext.Current.CancellationToken);
        if (result.Ok)
            return $"moved {call.Issue} to {call.Status}";
        var error = result.Error!;
        if (error.Class == ProviderErrorClass.Infrastructure && !error.IsConfigurationFault)
            throw new ApplicationFailureException($"tracker: {error.Reason}: {error.Message}", errorType: "infrastructure");
        return $"could not move {call.Issue} to {call.Status}: {(error.IsConfigurationFault ? error.DescribeFault() : $"{error.Reason}: {error.Message}")}";
    }

    /// <summary>
    /// The work-item budget (F11): counts the root's descendant runs in the record store (every run whose parent chain
    /// reaches the root, the child being asked for excepted, so a retry does not count it twice) and refuses when adding
    /// would pass the limit. A child the budget allows is recorded at once, so the next request counts it. Returns why
    /// it is refused, or null.
    /// </summary>
    [Activity]
    public async Task<string?> ReserveWorkItemsAsync(WorkItemRequest request)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var descendants = new HashSet<string>(StringComparer.Ordinal);
        var frontier = new Queue<string>([request.Budget.RootRunId]);
        while (frontier.TryDequeue(out var parent))
        {
            var children = await services.RecordStore.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string> { ["parent_run_id"] = parent }, ct);
            if (!children.Ok)
                throw new ApplicationFailureException($"the record store cannot list runs: {children.Error!.Message}", errorType: "infrastructure");
            foreach (var child in children.Value!.Where(c => descendants.Add(c.RunId)))
                frontier.Enqueue(child.RunId);
        }

        if (request.Child is { } reserved)
            descendants.Remove(reserved.RunId);
        if (descendants.Count + request.Adding > request.Budget.Limit)
            return $"F11: the run tree of {request.Budget.RootRunId} may create {request.Budget.Limit} descendant run(s) (budget.max_work_items within the "
                   + $"organisation's ceiling); it has {descendants.Count}, and this would add {request.Adding}";
        if (request.Child is { } record)
        {
            var written = await services.RecordStore.AppendAsync(new RecordBatch([record]), ct);
            if (!written.Ok)
                throw new ApplicationFailureException($"the record store cannot record the child run: {written.Error!.Message}", errorType: "infrastructure");
        }

        return null;
    }
}
