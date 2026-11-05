using FeatureFlow.Engine;
using FeatureFlow.Hosting;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;
using Temporalio.Api.Workflow.V1;
using Temporalio.Api.WorkflowService.V1;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// Stuck-run detection against real runs (docs/02 section 8, docs/01 section 1.2): a provider unreachable past the threshold
/// shows <c>stalled</c> and an activity no worker takes shows <c>stuck</c>, each as an open wait notified once, closed with
/// <c>resumed</c> when the run moves again; neither changes the run, which completes. The check runs here with its
/// thresholds at zero (the stuck one too, in the second test), called directly rather than on its interval.
/// </summary>
[Trait("Category", "Integration")]
public class StuckRunTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static string Routed(params string[] events) =>
        "notifications: { routing: { " + string.Join(", ", events.Select(e => e + ": { channel: \"#ff\" }")) + " }, events: [" + string.Join(", ", events) + "] }";

    static Task<Harness> StartAsync(string name, string script = "{}", bool agentWorker = true) =>
        Harness.StartAsync(name, script, autoApprove: true, omitTypes: ["notifications"], agentWorker: agentWorker,
            providers: $"  notifications: {{ instances: {{ memory: {{ world: {name}-{Guid.NewGuid():N} }} }} }}",
            organisation: Routed(NotificationEvents.Stalled, NotificationEvents.Stuck, NotificationEvents.Resumed));

    static StuckRuns Check(Harness h, TimeSpan stuckAfter) =>
        new(new EngineServices(h.Host.Providers, h.Host.Configuration.Installation, h.Host.Configuration.Organisation), h.Client, NullLogger.Instance,
            new StuckRunTimings(TimeSpan.Zero, stuckAfter, TimeSpan.FromMinutes(1)));

    static async Task<IReadOnlyList<PendingActivityInfo>> PendingAsync(Harness h, string id) =>
        (await h.Client.WorkflowService.DescribeWorkflowExecutionAsync(new DescribeWorkflowExecutionRequest
        {
            Namespace = "default",
            Execution = new Temporalio.Api.Common.V1.WorkflowExecution { WorkflowId = id },
        })).PendingActivities;

    static async Task UntilAsync(Func<Task<bool>> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"waited 90 s for {what}");
            await Task.Delay(200, Ct);
        }
    }

    static async Task<List<string>> OpenWaitsAsync(Harness h, string id) =>
        (await h.ShowAsync(id))["waits"]!.AsArray().Select(w => w!["kind"]!.ToString()).ToList();

    /// <summary>The run's stalled, stuck and resumed messages about stuck-run detection's waits, in order.</summary>
    static async Task<List<string>> EventsAsync(Harness h, string id, string kind) =>
        (await h.Host.Providers.Required<IRecordStoreProvider>("record_store").QueryAsync<NotificationRecord>(Queries.Notifications,
            new Dictionary<string, string> { ["run_id"] = id }, Ct)).Unwrap().Where(n => n.Key.Contains($"|{kind}-", StringComparison.Ordinal)).Select(n => n.Event).ToList();

    [Fact]
    public async Task A_provider_unreachable_shows_stalled_once_and_resumed_when_it_answers_and_the_run_completes()
    {
        // The tests action is unreachable on its first four calls, then answers: Temporal retries it (1, 2, 4, 8 s apart).
        await using var h = await StartAsync("stalled", """{ "baseline.tests": { error: infrastructure, times: 4 } }""");
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-12");
        var check = Check(h, TimeSpan.FromHours(1));
        await UntilAsync(async () => (await PendingAsync(h, id)).Any(a => a.Attempt >= 2 && a.LastFailure?.ApplicationFailureInfo?.Type == "infrastructure"),
            "the tests action to fail twice");

        await check.CheckAsync(Ct);
        await check.CheckAsync(Ct);

        var view = await h.ShowAsync(id);
        var stalled = Assert.Single(view["waits"]!.AsArray(), w => w!["kind"]!.ToString() == NotificationEvents.Stalled)!;
        Assert.Contains("RunAction has failed", stalled["reason"]!.ToString());
        Assert.Contains("unreachable", stalled["reason"]!.ToString());
        Assert.Equal("running", view["run"]!["status"]!.ToString());
        Assert.Equal([NotificationEvents.Stalled], await EventsAsync(h, id, NotificationEvents.Stalled));

        await UntilAsync(async () => Harness.StartedSteps(await h.ShowAsync(id)).Contains("plan.plan"), "the run to move past the tests action");
        await check.CheckAsync(Ct);

        Assert.DoesNotContain(NotificationEvents.Stalled, await OpenWaitsAsync(h, id));
        Assert.Equal([NotificationEvents.Stalled, NotificationEvents.Resumed], await EventsAsync(h, id, NotificationEvents.Stalled));
        var (exit, end, _) = h.Cli("wait", id, "--timeout", "120");
        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), (await h.ShowAsync(id)).ToJsonString());
    }

    [Fact]
    public async Task An_activity_no_worker_takes_shows_stuck_and_resumed_once_a_worker_takes_it()
    {
        await using var h = await StartAsync("stuck", agentWorker: false);
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-13");
        var check = Check(h, TimeSpan.Zero);
        // Only the agent step left pending: the run's own record write, an activity in flight, would count as alive (a CI race).
        await UntilAsync(async () => (await PendingAsync(h, id)).Select(a => a.ActivityType.Name).SequenceEqual(["RunAgentStep"]),
            "the planner's agent step to be the only pending activity");

        await check.CheckAsync(Ct);

        var stuck = Assert.Single((await h.ShowAsync(id))["waits"]!.AsArray(), w => w!["kind"]!.ToString() == NotificationEvents.Stuck)!;
        Assert.Contains("RunAgentStep has waited", stuck["reason"]!.ToString());
        Assert.Equal([NotificationEvents.Stuck], await EventsAsync(h, id, NotificationEvents.Stuck));

        // An agent worker comes up: it takes the step, the run moves on, and the next check (at the usual threshold) closes the wait.
        await using var worker = await FeatureFlowHost.StartAsync(h.Host.Configuration, LocalProviders.Factories, false, true, null);
        await UntilAsync(async () => Harness.StartedSteps(await h.ShowAsync(id)).Contains("approve_spec"), "the run to move past the agent step");
        await Check(h, StuckRunTimings.Default.StuckAfter).CheckAsync(Ct);

        Assert.DoesNotContain(NotificationEvents.Stuck, await OpenWaitsAsync(h, id));
        Assert.Equal([NotificationEvents.Stuck, NotificationEvents.Resumed], await EventsAsync(h, id, NotificationEvents.Stuck));
        var (exit, end, _) = h.Cli("wait", id, "--timeout", "120");
        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), (await h.ShowAsync(id)).ToJsonString());
    }
}
