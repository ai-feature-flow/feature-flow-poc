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

/// <summary>
/// A parent run's work branch in each repository it gives a child (LOG 196): the child's parent branch. Created from the
/// parent's own parent branch, else the repository's default branch, where it does not exist yet.
/// </summary>
public sealed record WorkBranchesCall(IReadOnlyList<string> Repositories, string WorkBranch, string? ParentBranch, bool DryRun);

/// <summary>What <see cref="RunActivities.EnsureWorkBranchesAsync"/> did, for the timeline, or why it could not.</summary>
public sealed record WorkBranchesResult(IReadOnlyList<string> Events, string? Error);

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
    /// Creates the run's work branch in each repository that lacks it, as the first step to touch a repository does
    /// (docs/04 section 2), so a child started with it as its parent branch finds it (LOG 196). An infrastructure error is
    /// retried; a missing parent branch or a configuration fault comes back as the reason the step stops for a person.
    /// </summary>
    [Activity]
    public async Task<WorkBranchesResult> EnsureWorkBranchesAsync(WorkBranchesCall call)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        if (services.Providers.ForRun("vcs", call.DryRun) is not IVcsProvider vcs || services.Providers.ForRun("code_host", call.DryRun) is not ICodeHostProvider host)
            return new WorkBranchesResult([], null);
        var events = new List<string>();
        string? Fails<T>(ProviderResult<T> result, string what)
        {
            if (result.Ok)
                return null;
            var error = result.Error!;
            if (error.Class == ProviderErrorClass.Infrastructure && !error.IsConfigurationFault)
                throw new ApplicationFailureException($"{what}: {error.Reason}: {error.Message}", errorType: "infrastructure");
            return $"could not {what}: {(error.IsConfigurationFault ? error.DescribeFault() : $"{error.Reason}: {error.Message}")}";
        }

        foreach (var repository in call.Repositories)
        {
            var repo = new RepoRef(repository);
            var work = await vcs.ResolveAsync(repo, "refs/heads/" + call.WorkBranch, ct);
            if (Fails(work, $"read {call.WorkBranch} in {repository}") is { } readFailed)
                return new WorkBranchesResult(events, readFailed);
            if (work.Value is not null)
                continue;
            var parent = call.ParentBranch;
            if (parent is null)
            {
                var named = await host.GetDefaultBranchAsync(repo, ct);
                if (Fails(named, $"read {repository}'s default branch") is { } defaultFailed)
                    return new WorkBranchesResult(events, defaultFailed);
                parent = named.Value!;
            }

            var start = await vcs.ResolveAsync(repo, "refs/heads/" + parent, ct);
            if (Fails(start, $"read {parent} in {repository}") is { } parentFailed)
                return new WorkBranchesResult(events, parentFailed);
            if (start.Value is null)
                return new WorkBranchesResult(events, $"{repository} has no branch {parent} to start {call.WorkBranch} from");
            var pushed = await vcs.PushAsync(repo, "refs/heads/" + call.WorkBranch, start.Value, null, null, ct);
            if (pushed is { Ok: false, Error.Reason: "base_moved" })
                continue;                                      // made meanwhile, by a step of this run or a retry
            if (Fails(pushed, $"create {call.WorkBranch} in {repository}") is { } pushFailed)
                return new WorkBranchesResult(events, pushFailed);
            events.Add($"created branch {call.WorkBranch} in {repository} at {start.Value[..Math.Min(10, start.Value.Length)]} from {parent}, for a child run");
        }

        return new WorkBranchesResult(events, null);
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
