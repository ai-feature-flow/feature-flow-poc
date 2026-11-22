using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace FeatureFlow.Engine;

/// <summary>
/// Run-time overrides (docs/05 section 5): the <c>override</c> update (<c>RunWorkflowNames.Override</c>, <c>OverrideArgs</c>),
/// mid-run library overrides (docs/08 section 5.2) and closing an aborted run's pull requests. M7, W1. The API authenticated
/// the actor (F8), authorised the base key and recorded the override; the run's core, which alone knows what is protected,
/// routes it (<see cref="RunCore.RouteOverride"/>, <see cref="RunCore.Override"/>): it refuses protected content or a grant
/// beyond the hard ceiling without the variant the caller holds, and anything that relaxes a floor (LOG 203). The shell
/// stores a library override's content and closes an aborted run's pull requests, which are activities.
/// </summary>
public partial class RunWorkflow
{
    /// <summary>Query: the pull requests a run opened, and the child runs it started (LOG 215), for an aborted ancestor to close.</summary>
    public const string PullRequestsQuery = "pull_requests";

    /// <summary>Signal, from a run to its running children: a library override, which applies to the run tree from that run down (docs/08 section 5.2).</summary>
    public const string ParentLibraryOverride = "parent_library_override";

    /// <summary>An abort asked for the run's open pull requests to be closed (docs/05 section 5).</summary>
    bool closePullRequests;

    /// <summary>The run's current lock: its start lock with the run's library overrides applied. Carried by the next run's <c>LockRef</c>.</summary>
    string? currentLock;

    string LockRef => currentLock ?? input.LockRef;

    /// <summary>Lock records written since the last record: the run's lock after each library override (the last write is the run's lock).</summary>
    readonly List<LockRecord> lockRecords = [];

    [WorkflowUpdateValidator(nameof(OverrideAsync))]
    public void ValidateOverride(OverrideArgs o)
    {
        if (core?.RouteOverride(o) is Refused refused)
            throw new ApplicationFailureException(refused.Error);
    }

    [WorkflowUpdate(RunWorkflowNames.Override)]
    public async Task<AnswerReply> OverrideAsync(OverrideArgs o)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        if (draining)
            answersWhileDraining++;
        var stored = core!.RouteOverride(o) is StoresLibrary library
            ? await OverrideLibraryAsync(library.Item, library.Content, null, o.Actor, o.Reason)
            : null;
        var outcome = core.Override(o, stored);
        if (outcome.Error is { } error)
            throw new ApplicationFailureException(error);
        closePullRequests |= outcome.ClosePullRequests;
        dirty = true;
        return new AnswerReply(outcome.Result);
    }

    /// <summary>
    /// Replaces a library item for this run's future steps and its children's (docs/08 section 5.2): the content is stored,
    /// the run's lock rewritten with the item's new hash, and the core resolves from the new libraries; steps in flight keep
    /// what they resolved. Recorded as a lock entry: actor, reason, old and new hash, and the steps that keep the old.
    /// </summary>
    async Task<string> OverrideLibraryAsync(string item, string? content, string? contentHash, string actor, string? reason)
    {
        if (core!.LibraryFile(item) is not { } file)
            throw new ApplicationFailureException($"{item} is not in the run's libraries");
        BeginActivity();
        LibraryOverrideResult overridden;
        try
        {
            overridden = await Workflow.ExecuteActivityAsync((RunActivities a) => a.OverrideLibraryAsync(new LibraryOverrideCall(LockRef, file.Library, file.Path, content, contentHash)),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2), RetryPolicy = new() { MaximumAttempts = 5 } });
        }
        catch (ActivityFailureException e)
        {
            throw new ApplicationFailureException($"the library override could not be stored: {e.InnerException?.Message ?? e.Message}");
        }
        finally
        {
            EndActivity();
        }

        var next = RunActivities.Build(overridden.Libraries, new DiagnosticBag());
        var entry = new LibraryOverrideEntry(item, $"{file.Library}/{file.Path}", overridden.OldHash, overridden.NewHash, actor, reason,
            core.InFlight.ToList(), core.ProtectedResolving(item));
        var applied = core.Apply(new LibraryOverridden(entry, next));
        if (!applied.Accepted)
            throw new ApplicationFailureException(string.Join("; ", applied.Errors));
        currentLock = overridden.LockRef;
        lockRecords.Add(new LockRecord(input.RunId, currentLock, Workflow.UtcNow));
        foreach (var child in core.Children.ToList())
            await SignalQuietlyAsync(child.WorkflowId, ParentLibraryOverride, [new InheritedLibraryOverride(item, overridden.NewHash, actor, reason)]);
        dirty = true;
        return overridden.OldHash == overridden.NewHash ? "applied: the content was already in use" : $"applied: {overridden.OldHash} -> {overridden.NewHash}";
    }

    /// <summary>A parent's library override reaching a running child, which applies it to its own lock (the run tree from that run down).</summary>
    [WorkflowSignal(ParentLibraryOverride)]
    public async Task ParentLibraryOverrideAsync(InheritedLibraryOverride inherited)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        if (core!.Status != RunStatus.Running || core.LibraryItemError(inherited.Item) is not null)
            return;
        try
        {
            await OverrideLibraryAsync(inherited.Item, null, inherited.ContentHash, inherited.Actor, inherited.Reason is null ? "from the parent run" : $"from the parent run: {inherited.Reason}");
        }
        catch (ApplicationFailureException e)
        {
            core.Note("library_override_refused", "", $"{inherited.Item} from the parent run: {e.Message}");
            dirty = true;
        }
    }

    /// <summary>
    /// An agent's library override (docs/05 section 6) becomes a patch approval for a person, whatever it touches; one the core
    /// refuses (no planning authority, no content, or content that cannot be overridden) is recorded.
    /// </summary>
    void ProposeLibraryOverride(string stepId, IReadOnlyDictionary<string, string> proposal)
    {
        var item = proposal.GetValueOrDefault("item") ?? "";
        var result = core!.Apply(new LibraryOverrideProposed(stepId, item, proposal.GetValueOrDefault("with")));
        if (!result.Accepted)
            core.Note("library_override_rejected", stepId, $"{item}: {string.Join("; ", result.Errors)}");
    }

    [WorkflowQuery(PullRequestsQuery)]
    public RunPullRequests PullRequests() => new(core?.PullRequestsOpened() ?? [], core?.ChildrenStarted() ?? []);

    /// <summary>
    /// Closes an aborted run's open pull requests when the person asked (docs/05 section 5; m6-results follow-up 3). Each is
    /// noted on the timeline, closed or left; a code host that refuses does not undo the abort.
    /// </summary>
    async Task CloseAbortedPullRequestsAsync()
    {
        if (!closePullRequests || core!.Status != RunStatus.Aborted)
            return;
        var opened = core.PullRequestsOpened();
        var started = core.ChildrenStarted();
        if (opened.Count == 0 && started.Count == 0)
        {
            core.Note("pull_request_closed", "", "the run opened no pull requests");
            return;
        }

        IReadOnlyList<string> events;
        try
        {
            // The children's, ended or running, are read from them by the activity: the run tree's pull requests (R1 F6).
            events = await Workflow.ExecuteActivityAsync((RunActivities a) => a.ClosePullRequestsAsync(new ClosePullRequestsCall(input.RunId, opened, "", input.DryRun, started)),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2), ScheduleToCloseTimeout = TimeSpan.FromMinutes(10), RetryPolicy = new() { MaximumInterval = TimeSpan.FromSeconds(30) } });
        }
        catch (ActivityFailureException e)
        {
            events = [$"could not close {string.Join(", ", opened)}: {e.InnerException?.Message ?? e.Message}"];
        }

        foreach (var e in events)
            core.Note("pull_request_closed", "", e);
    }
}

/// <summary>What <see cref="RunWorkflow.PullRequestsQuery"/> answers: the run's pull requests and the child runs it started.</summary>
public sealed record RunPullRequests(IReadOnlyList<string> PullRequests, IReadOnlyList<string> Children);

/// <summary>A parent's library override for a running child: the item, the stored content's hash, and who and why.</summary>
public sealed record InheritedLibraryOverride(string Item, string ContentHash, string Actor, string? Reason);
