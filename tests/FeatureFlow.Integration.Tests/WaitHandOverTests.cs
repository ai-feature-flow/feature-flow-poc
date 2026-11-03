using System.Globalization;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// LOG 139 on a Temporal dev server: a <c>ci.checks</c> waiting on a check that stays in progress runs beside a chain of
/// short actions that grows the history past the hand-over threshold. The run hands over while the wait is open, the
/// wait resumes in the next run as the same execution, and it passes there once the check completes, with the time it
/// began waiting kept from the first run.
/// </summary>
[Trait("Category", "Integration")]
public class WaitHandOverTests
{
    const string Repo = "acme/api";
    const string Script = """{ "wait_beside_work:*": { delay_from: wait_ms } }""";

    [Fact]
    public async Task A_run_hands_over_while_a_wait_is_open_and_the_wait_ends_in_the_next_run_with_its_start_kept()
    {
        var world = $"it-waits-{Guid.NewGuid():N}";
        var forge = InMemoryForge.Named(world);
        var repo = forge.CreateRepository(Repo, new Dictionary<string, string> { ["src/App.cs"] = "class App {}\n" });
        forge.Protect(repo, new BranchProtection("main", true, true, 1, [], false));
        var head = forge.Commit(repo, "refs/heads/ff/waits", new Dictionary<string, string?> { ["src/App.cs"] = "class App { }\n" });
        forge.ReportCheck(repo, head, new CheckRun("build", CheckStates.InProgress, null, "ci"));

        await using var h = await Harness.StartAsync("waits", Script, omitTypes: ["vcs", "code_host", "ci"],
            libraries: [Path.Combine(Harness.TestLibraries, "m6")],
            providers: string.Join("\n", new[] { "vcs", "code_host", "ci" }.Select(t => $"  {t}: {{ instances: {{ memory: {{ world: {world} }} }} }}")));
        var work = "[" + string.Join(", ", Enumerable.Repeat(1_000, 60)) + "]";
        var id = h.Start("m6:templates/wait_beside_work", "--param", $"work={work}", "--repo", Repo, "--continue-as-new-events", "100");
        var handle = h.Client.GetWorkflowHandle(id);

        // The run must hand over with the wait open. Drained instead, the wait would hold the hand-over until the check
        // completes, which it does not until the run has handed over.
        RunView view;
        var until = DateTime.UtcNow.AddSeconds(90);
        do
        {
            await Task.Delay(500, TestContext.Current.CancellationToken);
            view = await handle.QueryAsync<RunView>(RunWorkflowNames.View, []);
        }
        while (view.HandOvers.Count == 0 && view.Status is "starting" or "running" && DateTime.UtcNow < until);

        Assert.True(view.HandOvers.Count >= 1,
            $"no hand-over within 90 s while the wait was open (draining {view.Draining}, {view.HistoryEvents} events, running {string.Join(", ", view.InFlight)})");
        Assert.True(view.HandOvers[0].InterruptedWaits >= 1, $"the first hand-over interrupted no wait: {view.HandOvers[0]}");

        forge.ReportCheck(repo, head, new CheckRun("build", CheckStates.Completed, Conclusions.Success, "ci"));
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "180");
        var record = await h.ShowAsync(id);
        Assert.True(exit == 0 && end.Trim() == $"run {id} completed", end + error + record.ToJsonString());

        // One execution of the wait: started once, interrupted and resumed at each hand-over, never counted again.
        var timeline = record["timeline"]!.AsArray().Select(e => (Kind: e!["kind"]!.ToString(), Step: e["stepId"]!.ToString(), Detail: e["detail"]?.ToString())).ToList();
        Assert.Single(timeline, e => e is { Kind: "started", Step: "checks" });
        Assert.Contains(timeline, e => e is { Kind: "interrupted", Step: "checks" });
        Assert.All(timeline.Where(e => e is { Kind: "resumed", Step: "checks" }), e => Assert.Equal("execution 1", e.Detail));
        Assert.Equal(["passed"], timeline.Where(e => e is { Kind: "completed", Step: "checks" }).Select(e => e.Detail));
        var firstHandOver = timeline.FindIndex(e => e.Kind == "continued_as_new");
        Assert.True(timeline.FindIndex(e => e is { Kind: "completed", Step: "checks" }) > firstHandOver, "the wait ended before the first hand-over");

        // The time it began waiting was recorded once, in the first run, and kept: the resumed execution has the same key.
        var records = h.Host.Providers.Required<IRecordStoreProvider>("record_store");
        var events = (await records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = $"{id}/checks/1" },
            TestContext.Current.CancellationToken)).Value!;
        var since = Assert.Single(events, e => e.Kind == "since");
        var runs = await h.RunsOfAsync(id);
        Assert.True(runs.Count >= 2, $"{runs.Count} run(s)");
        Assert.True(DateTimeOffset.Parse(since.Detail, CultureInfo.InvariantCulture) < runs[1].StartTime,
            $"the wait's start {since.Detail} is not from the first run, which ended at {runs[1].StartTime:O}");
        Assert.Single(events, e => e.Kind == "checks_head");

        var final = await handle.QueryAsync<RunView>(RunWorkflowNames.View, []);
        Assert.All(final.HandOvers, o => Assert.Equal(0, o.InFlightActivities));
        Assert.Equal(0, final.ActivitiesStartedWhileDraining);
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
        await h.RecordHistoryAsync(id, "wait_hand_over");
        await h.RecordRunHistoryAsync(id, 1, "wait_hand_over_resumed");
    }
}
