using FeatureFlow.Providers.Abstractions;
using Microsoft.Extensions.Logging;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.Workflow.V1;
using Temporalio.Api.WorkflowService.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FeatureFlow.Engine;

/// <summary>
/// The stuck-run check's fixed timings (LOG 270): how long a run may make no progress before it is <c>stalled</c> (a provider
/// unreachable) or <c>stuck</c> (nothing in flight), and how often runs are checked. Not settings; tests shorten them.
/// </summary>
public sealed record StuckRunTimings(TimeSpan StalledAfter, TimeSpan StuckAfter, TimeSpan Interval)
{
    public static readonly StuckRunTimings Default = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));
}

/// <summary>What the check found about a run: <c>stalled</c> or <c>stuck</c>, and why.</summary>
public sealed record StuckFinding(string Kind, string Reason);

/// <summary>
/// Stuck-run detection (docs/02 section 8, docs/01 section 1.2): a periodic check, outside every run, that never changes
/// one. The record store says which runs have made no progress (no timeline entry) for a while; Temporal's description of
/// each says why. A run whose activity keeps failing with an infrastructure error is <c>stalled</c>: a provider is
/// unreachable, and the run waits rather than failing (P5). A run with nothing in flight, nobody awaited and a workflow
/// task failing or an activity no worker takes is <c>stuck</c>. Each shows as an open wait of that kind on the run, its
/// notification sent when it opens; the wait closes, sending <c>resumed</c>, only when the run moves again (a new timeline
/// entry) or ends. At most one wait opens per stretch without progress, so a check that sees an activity between attempts
/// neither closes it nor posts again. Each orchestration replica runs its own check: with several, each may post its own
/// message for the same stretch (LOG 307).
/// </summary>
/// <para><paramref name="find"/> replaces the look-up in Temporal (tests of the record-store side).</para>
public sealed class StuckRuns(EngineServices services, ITemporalClient? client, ILogger logger, StuckRunTimings? timings = null,
    Func<string, bool, TimeSpan, CancellationToken, Task<StuckFinding?>>? find = null)
{
    readonly StuckRunTimings t = timings ?? StuckRunTimings.Default;

    IRecordStoreProvider Records => services.RecordStore;

    /// <summary>Checks every <see cref="StuckRunTimings.Interval"/> until cancelled; a failed check is logged and the next one runs.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await Task.Delay(t.Interval, cancellationToken);
                await CheckAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                logger.LogWarning(e, "stuck runs: the check failed; it runs again in {Interval}", t.Interval);
            }
        }
    }

    /// <summary>One pass over the runs the record store lists: the active ones, and those ended in the last day (to close their waits).</summary>
    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var runs = (await Records.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string>(), cancellationToken)).Unwrap();
        foreach (var run in runs.Where(r => Active(r) || now - r.UpdatedAt < TimeSpan.FromDays(1)))
        {
            // One run's fault (a record it cannot read, a description Temporal refuses) must not hide the runs after it.
            try
            {
                await CheckRunAsync(run, now, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "stuck runs: {RunId} could not be checked; the pass goes on", run.RunId);
            }
        }
    }

    static bool Active(RunRecord run) => run.Status is "starting" or "running";

    static bool Ours(WaitRecord wait) => wait.Kind is NotificationEvents.Stalled or NotificationEvents.Stuck;

    async Task CheckRunAsync(RunRecord run, DateTimeOffset now, CancellationToken ct)
    {
        var p = new Dictionary<string, string> { ["run_id"] = run.RunId };
        var waits = (await Records.QueryAsync<WaitRecord>(Queries.Waits, p, ct)).Unwrap();
        var opened = waits.Where(w => w.Open && Ours(w)).ToList();
        if (!Active(run) && opened.Count == 0)
            return;
        var timeline = (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string>(p) { ["after"] = "0" }, ct)).Unwrap();
        var last = timeline.LastOrDefault();
        var progress = last is null || last.At < run.UpdatedAt ? run.UpdatedAt : last.At;
        var finding = Active(run) && now - progress >= t.StalledAfter
            ? await (find ?? FindAsync)(run.RunId, waits.Any(w => w.Open && !Ours(w)), now - progress, ct)
            : null;

        // A wait belongs to a stretch without progress, named by the run's last timeline entry. It closes only when the run
        // has moved (a new entry) or ended, never because one check did not see the cause: a heartbeating retry between
        // failures must not send a false "resumed" (LOG 307). At most one wait per stretch, and none reopens.
        var stretch = (last?.Sequence ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var batch = new List<WaitRecord>();
        foreach (var wait in opened.Where(w => !Active(run) || StretchOf(w.WaitId) != stretch))
            batch.Add(wait with { Open = false, UpdatedAt = now });
        if (finding is not null && waits.All(w => !(Ours(w) && StretchOf(w.WaitId) == stretch)))
            batch.Add(new WaitRecord(run.RunId, $"{finding.Kind}-{stretch}", finding.Kind, "", finding.Kind, finding.Reason, [], true, now));
        if (batch.Count == 0)
            return;
        var records = new RecordBatch(Waits: batch);
        (await Records.AppendAsync(records, ct)).Unwrap();
        foreach (var wait in batch)
            logger.LogWarning("stuck runs: {RunId} {Change} {Kind}: {Reason}", run.RunId, wait.Open ? "is" : "is no longer", wait.Kind, wait.Reason);
        await new RunNotifier(services, logger).NotifyAsync(records, ct);
    }

    static string StretchOf(string waitId) => waitId[(waitId.LastIndexOf('-') + 1)..];

    async Task<StuckFinding?> FindAsync(string runId, bool personAwaited, TimeSpan quiet, CancellationToken ct)
    {
        DescribeWorkflowExecutionResponse description;
        try
        {
            var temporal = client ?? throw new InvalidOperationException("stuck-run detection needs a Temporal client");
            description = await temporal.WorkflowService.DescribeWorkflowExecutionAsync(
                new DescribeWorkflowExecutionRequest { Namespace = temporal.Options.Namespace, Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = runId } },
                new() { CancellationToken = ct });
        }
        catch (RpcException e) when (e.Code == RpcException.StatusCode.NotFound)
        {
            return null;
        }

        if (description.WorkflowExecutionInfo?.Status != WorkflowExecutionStatus.Running)
            return null;
        return Classify(description.PendingActivities, description.PendingWorkflowTask, description.PendingChildren.Count, personAwaited, quiet, t);
    }

    /// <summary>
    /// Why a run that has made no progress for <paramref name="quiet"/> is not moving, from its pending work. Stalled: an
    /// activity whose last attempt failed with an infrastructure error and that is not now running and heartbeating (one
    /// recovered and working, such as an agent step, heartbeats). Stuck, after <see cref="StuckRunTimings.StuckAfter"/> and
    /// with nobody awaited: a workflow task failing, or nothing alive (an activity running, one waiting on a condition
    /// between reads, or a child run).
    /// </summary>
    public static StuckFinding? Classify(IReadOnlyCollection<PendingActivityInfo> activities, PendingWorkflowTaskInfo? task, int children, bool personAwaited,
        TimeSpan quiet, StuckRunTimings timings)
    {
        static string? FailureType(PendingActivityInfo a) => a.LastFailure?.ApplicationFailureInfo?.Type;
        static bool Heartbeating(PendingActivityInfo a) =>
            a.State == PendingActivityState.Started && a.LastHeartbeatTime is { } beat && a.LastStartedTime is { } started && beat.ToDateTimeOffset() > started.ToDateTimeOffset();

        if (activities.FirstOrDefault(a => FailureType(a) == "infrastructure" && !Heartbeating(a)) is { } failing)
            return new StuckFinding(NotificationEvents.Stalled,
                $"{failing.ActivityType?.Name} has failed {failing.Attempt} times with an infrastructure error and no progress for {Minutes(quiet)}: {failing.LastFailure.Message}. " +
                "The run waits and goes on when the provider answers");
        if (quiet < timings.StuckAfter || personAwaited)
            return null;
        if (task is { Attempt: > 1 })
            return new StuckFinding(NotificationEvents.Stuck, $"its workflow task has failed {task.Attempt} times and nothing has moved for {Minutes(quiet)}");
        if (children > 0 || activities.Any(a => a.State == PendingActivityState.Started || FailureType(a) == "waiting"))
            return null;
        return new StuckFinding(NotificationEvents.Stuck, activities.FirstOrDefault() is { } waiting
            ? $"{waiting.ActivityType?.Name} has waited {Minutes(quiet)} for a worker to take it (is the worker for its queue running?)"
            : $"nothing is in flight, nobody is awaited and nothing has moved for {Minutes(quiet)}");
    }

    static string Minutes(TimeSpan span) => $"{Math.Floor(span.TotalMinutes):0} min";
}
