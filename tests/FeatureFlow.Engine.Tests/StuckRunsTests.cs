using FeatureFlow.Providers.Abstractions;
using Google.Protobuf.WellKnownTypes;
using Temporalio.Api.Enums.V1;
using Temporalio.Api.Failure.V1;
using Temporalio.Api.Workflow.V1;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// Stuck-run detection's rule (docs/02 section 8, LOG 270): why a run that has made no progress is not moving, from the
/// work Temporal says is pending. The integration tests run it against real runs.
/// </summary>
public sealed class StuckRunsTests
{
    static readonly StuckRunTimings Timings = new(TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(1));
    static readonly DateTimeOffset Started = DateTimeOffset.UtcNow.AddMinutes(-40);

    static PendingActivityInfo Activity(string type, PendingActivityState state, string? failure = null, bool heartbeat = false)
    {
        var info = new PendingActivityInfo { ActivityId = "7", ActivityType = new() { Name = type }, State = state, Attempt = 12, LastStartedTime = Timestamp.FromDateTimeOffset(Started) };
        if (failure is not null)
            info.LastFailure = new Failure { Message = "tracker: unreachable: no route to host", ApplicationFailureInfo = new() { Type = failure } };
        if (heartbeat)
            info.LastHeartbeatTime = Timestamp.FromDateTimeOffset(Started.AddMinutes(5));
        return info;
    }

    static StuckFinding? Classify(TimeSpan quiet, bool personAwaited = false, PendingWorkflowTaskInfo? task = null, int children = 0, params PendingActivityInfo[] activities) =>
        StuckRuns.Classify(activities, task, children, personAwaited, quiet, Timings);

    static readonly TimeSpan Long = TimeSpan.FromMinutes(45);

    [Fact]
    public void An_activity_failing_with_infrastructure_errors_is_stalled_whether_between_attempts_or_in_one()
    {
        foreach (var state in new[] { PendingActivityState.Scheduled, PendingActivityState.Started })
        {
            var finding = Classify(TimeSpan.FromMinutes(16), activities: Activity("RunAction", state, "infrastructure"));
            Assert.Equal(NotificationEvents.Stalled, finding?.Kind);
            Assert.Contains("RunAction has failed 12 times", finding!.Reason);
            Assert.Contains("unreachable: no route to host", finding.Reason);
        }
    }

    [Fact]
    public void Stalled_does_not_wait_for_people_or_the_stuck_threshold()
    {
        Assert.Equal(NotificationEvents.Stalled, Classify(TimeSpan.FromMinutes(16), personAwaited: true, activities: Activity("RunAction", PendingActivityState.Scheduled, "infrastructure"))?.Kind);
    }

    [Fact]
    public void An_activity_that_failed_then_recovered_and_heartbeats_is_not_stalled()
    {
        Assert.Null(Classify(Long, activities: Activity("RunAgentStep", PendingActivityState.Started, "infrastructure", heartbeat: true)));
    }

    [Fact]
    public void Nothing_alive_and_nobody_awaited_past_the_stuck_threshold_is_stuck()
    {
        var unstarted = Classify(Long, activities: Activity("RunAgentStep", PendingActivityState.Scheduled));
        var timedOut = Classify(Long, activities: Activity("Record", PendingActivityState.Scheduled, "timeout-not-an-application-failure"));
        var nothing = Classify(Long);

        Assert.Equal(NotificationEvents.Stuck, unstarted?.Kind);
        Assert.Contains("RunAgentStep has waited 45 min for a worker", unstarted!.Reason);
        Assert.Equal(NotificationEvents.Stuck, timedOut?.Kind);
        Assert.Contains("nothing is in flight", nothing!.Reason);
    }

    [Fact]
    public void A_failing_workflow_task_is_stuck_even_with_an_activity_running()
    {
        var finding = Classify(Long, task: new PendingWorkflowTaskInfo { Attempt = 9 }, activities: Activity("RunAction", PendingActivityState.Started));

        Assert.Equal(NotificationEvents.Stuck, finding?.Kind);
        Assert.Contains("workflow task has failed 9 times", finding!.Reason);
    }

    [Fact]
    public void A_running_activity_a_condition_wait_a_child_or_a_person_keeps_a_quiet_run_from_being_stuck()
    {
        Assert.Null(Classify(Long, activities: Activity("RunAgentStep", PendingActivityState.Started)));
        Assert.Null(Classify(Long, activities: Activity("RunAction", PendingActivityState.Scheduled, "waiting")));
        Assert.Null(Classify(Long, children: 1));
        Assert.Null(Classify(Long, personAwaited: true));
    }

    [Fact]
    public void Below_its_threshold_a_quiet_run_is_neither()
    {
        Assert.Null(Classify(TimeSpan.FromMinutes(20), activities: Activity("RunAgentStep", PendingActivityState.Scheduled)));
        Assert.Null(Classify(TimeSpan.FromMinutes(20), task: new PendingWorkflowTaskInfo { Attempt = 3 }));
    }
}
