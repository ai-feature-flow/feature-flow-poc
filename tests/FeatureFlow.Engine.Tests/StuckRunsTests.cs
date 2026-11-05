using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;
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

/// <summary>
/// The check's record-store side (LOG 307): which waits it opens and closes, and what it sends, with Temporal's answer
/// scripted. A wait closes only when the run has moved or ended; one run's fault does not stop the pass.
/// </summary>
public sealed class StuckRunsRecordTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-stuck-" + Guid.NewGuid().ToString("N"));
    readonly SqliteRecordStore records;
    readonly InMemoryNotifications chat = new();
    readonly Dictionary<string, StuckFinding?> findings = [];
    readonly DateTimeOffset start = DateTimeOffset.UtcNow.AddHours(-1);

    public StuckRunsRecordTests()
    {
        Directory.CreateDirectory(folder);
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);

    StuckRuns Check()
    {
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = records },
                ["notifications"] = new Dictionary<string, IProvider> { ["chat"] = chat },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["notifications"] = "chat" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "someone", []), 3, 10, Path.Combine(folder, "installation.yaml"));
        string[] events = [NotificationEvents.Stalled, NotificationEvents.Stuck, NotificationEvents.Resumed];
        var organisation = new OrganisationConfig("t", [], new PolicyConfig([], null, null, null, null), Path.Combine(folder, "organisation.yaml"))
        {
            Notifications = new NotificationsConfig(events.ToDictionary(e => e, _ => new NotificationRoute("#ff")), events),
        };
        return new StuckRuns(new EngineServices(providers, installation, organisation), null, NullLogger.Instance,
            new StuckRunTimings(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMinutes(1)),
            (run, _, _, _) => findings.TryGetValue(run, out var f) ? Task.FromResult(f) : throw new InvalidOperationException($"Temporal refused {run}"));
    }

    async Task RecordAsync(RecordBatch batch) => (await records.AppendAsync(batch, Ct)).Unwrap();

    Task StartAsync(string run) => RecordAsync(new RecordBatch(
        [new RunRecord(run, null, "std:templates/ticket_delivery", "running", "pat", [], false, start, null)],
        Timeline: [new TimelineRecord(run, 1, "started", "baseline.tests", null, start)]));

    Task MoveAsync(string run, long sequence) => RecordAsync(new RecordBatch(Timeline: [new TimelineRecord(run, sequence, "started", "plan", null, DateTimeOffset.UtcNow)]));

    async Task<List<string>> OpenAsync(string run) =>
        (await records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string> { ["run_id"] = run, ["open"] = "true" }, Ct)).Unwrap()
        .Select(w => w.WaitId).ToList();

    List<string> Posted() => chat.Messages("#ff").SelectMany(m => chat.Messages("#ff", m.Ref).Prepend(m)).Select(m => m.Text.Split(']')[0] + "]").ToList();

    static StuckFinding Stalled => new(NotificationEvents.Stalled, "the tracker is unreachable");

    [Fact]
    public async Task A_check_that_does_not_see_the_cause_leaves_the_wait_open_and_sends_nothing()
    {
        await StartAsync("r1");
        findings["r1"] = Stalled;
        await Check().CheckAsync(Ct);

        // A heartbeating retry between failures: Classify finds nothing, but the run has not moved.
        findings["r1"] = null;
        await Check().CheckAsync(Ct);
        findings["r1"] = Stalled;
        await Check().CheckAsync(Ct);

        Assert.Equal(["stalled-1"], await OpenAsync("r1"));
        Assert.Equal(["[stalled]"], Posted());
    }

    [Fact]
    public async Task Once_the_run_moves_its_wait_closes_and_a_new_stretch_opens_its_own()
    {
        await StartAsync("r1");
        findings["r1"] = Stalled;
        await Check().CheckAsync(Ct);

        await MoveAsync("r1", 2);
        await Check().CheckAsync(Ct);

        Assert.Equal(["stalled-2"], await OpenAsync("r1"));
        Assert.Equal(["[stalled]", "[resumed]", "[stalled]"], Posted());

        await MoveAsync("r1", 3);
        findings["r1"] = null;
        await Check().CheckAsync(Ct);

        Assert.Empty(await OpenAsync("r1"));
        Assert.Equal(["[stalled]", "[resumed]", "[stalled]", "[resumed]"], Posted());
    }

    [Fact]
    public async Task A_run_that_ended_has_its_wait_closed()
    {
        await StartAsync("r1");
        findings["r1"] = new StuckFinding(NotificationEvents.Stuck, "nothing is in flight");
        await Check().CheckAsync(Ct);
        await RecordAsync(new RecordBatch([new RunRecord("r1", null, "std:templates/ticket_delivery", "aborted", "pat", [], false, DateTimeOffset.UtcNow, null)]));

        await Check().CheckAsync(Ct);

        Assert.Empty(await OpenAsync("r1"));
        Assert.Equal(["[stuck]", "[resumed]"], Posted());
    }

    [Fact]
    public async Task A_run_that_cannot_be_checked_does_not_stop_the_pass()
    {
        // "r-bad" is listed first (most recently updated) and has no scripted answer: the finder throws for it.
        await StartAsync("r1");
        await RecordAsync(new RecordBatch([new RunRecord("r-bad", null, "std:templates/ticket_delivery", "running", "pat", [], false, start.AddMinutes(1), null)]));
        findings["r1"] = Stalled;

        await Check().CheckAsync(Ct);

        Assert.Equal(["stalled-1"], await OpenAsync("r1"));
    }
}
