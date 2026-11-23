using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Engine.Actions;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.StdValidators;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M5 on the in-memory world: std's <c>ticket_delivery</c> runs on a Temporal dev server with the <c>memory</c> VCS,
/// code host, CI and sandbox, the scripted runtime acting in the sandbox, and the
/// engine's own actions, to a merge into its parent branch. And F1 at run start: a standalone ticket asking for an
/// engine merge into the default branch is refused unless the organisation allows it for that repository.
/// </summary>
[Trait("Category", "Integration")]
public class ForgeTicketTests
{
    const string Repo = Harness.RepoName;

    /// <summary>
    /// An engine on the harness's memory world, its scripted runtime given <paramref name="script"/> (the harness's defaults
    /// script the planner, implementer and reviewers otherwise).
    /// </summary>
    internal static async Task<(Harness H, InMemoryForge Forge, RepoRef Repo)> StartAsync(string name, string policy = "", bool autoApprove = true, string? script = null,
        string[]? noEdits = null, string[]? libraries = null)
    {
        var h = await Harness.StartAsync(name, script: script ?? "{}", autoApprove: autoApprove, libraries: libraries, policy: policy, noEdits: noEdits);
        return (h, h.Forge, h.Repo);
    }

    [Fact]
    public async Task A_std_ticket_runs_on_the_memory_providers_to_an_engine_merge_into_its_parent_branch()
    {
        var (h, forge, repo) = await StartAsync("forge-ticket", policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }");
        await using var _ = h;
        var before = forge.Resolve(repo, "main")!;

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-1", "--param", "engine_can_merge=true", "--repo", Repo);
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "240");

        var view = await h.ShowAsync(id);
        Assert.True(exit == 0, end + error + view.ToJsonString());
        Assert.Equal($"run {id} completed", end.Trim());

        // The implementer's commit reached main through the engine's own merge: one squash commit on main's old head.
        var main = forge.Resolve(repo, "main")!;
        var merged = forge.GetCommit(repo, main);
        Assert.Equal([before], merged.Parents);
        Assert.StartsWith("implement execution 1", forge.ReadFile(repo, main, $"work/{id}/implement.txt"), StringComparison.Ordinal);
        Assert.Contains($"Feature-Flow-Merge: {id}/deliver.merge", merged.Message, StringComparison.Ordinal);
        Assert.Equal(forge.GetCommit(repo, forge.Resolve(repo, "ff/ticket/T-1")!).Files, merged.Files);

        // The pull request was opened into main and, after the squash, closed with a link to the commit.
        var pr = new PullRequestRef(repo, 1);
        var state = (await new InMemoryCodeHost(forge).GetPullRequestAsync(pr, TestContext.Current.CancellationToken)).Unwrap();
        Assert.Equal(("main", "ff/ticket/T-1", PullRequestStates.Closed), (state.Base, state.Head, state.State));
        Assert.Contains(main, forge.Comments(pr).Single(), StringComparison.Ordinal);

        // E1: with no title from std, the pull request is titled after the ticket. A5: the run recorded it as its merge step last read it.
        Assert.Equal("Ticket T-1 (T-1)", state.Title);
        var records = h.Host.Providers.Required<IRecordStoreProvider>("record_store");
        var record = Assert.Single((await records.QueryAsync<PullRequestRecord>(Queries.PullRequests, new Dictionary<string, string> { ["run_id"] = id },
            TestContext.Current.CancellationToken)).Unwrap());
        Assert.Equal((Repo, 1, "deliver.merge", PullRequestStates.Closed), (record.Repository, record.Number, record.StepId, record.State.State));
        Assert.Contains(record.Files!, f => f.Path == $"work/{id}/implement.txt");
        Assert.Equal(record.State.HeadRevision, record.Checks?.Revision);

        // The path: engine actions made these steps' outcomes.
        string[] expected = ["baseline.tests", "baseline.analysis", "plan.plan", "approve_spec", "implement", "verify.tests", "verify.analysis",
            "deliver.open_pr", "deliver.checks", "deliver.catch_up", "deliver.merge"];
        var path = Harness.StartedSteps(view);
        Assert.Equal(expected, path.Where(expected.Contains).Distinct().ToArray());
        var trace = Trace(view);
        Assert.Contains(trace, d => d.Contains("tier unit at", StringComparison.Ordinal) && d.Contains("3 passed, 0 failed", StringComparison.Ordinal));
        Assert.Contains(trace, d => d.Contains("pull request acme/api#1 from ff/ticket/T-1 into main", StringComparison.Ordinal));
        Assert.Contains(trace, d => d.StartsWith("merged ", StringComparison.Ordinal) && d.Contains("into main", StringComparison.Ordinal));
        // M6: the plan and the claims were checked by the standard validators.
        Assert.Contains(trace, d => d.StartsWith("requirement_support (std): valid", StringComparison.Ordinal));
        Assert.Contains(trace, d => d.StartsWith("verification_satisfaction (std): valid", StringComparison.Ordinal));
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    /// <summary>
    /// The review's probe (PR 13, F1): the planner marks its only criterion as owned by the ticket itself, with no
    /// verification method, and the implementer claims no test. The std validators refuse the plan every time, so the
    /// ticket never reaches a merge: a run's criteria are met only by engine test evidence or by a person.
    /// </summary>
    [Fact]
    public async Task A_planner_that_marks_its_criteria_as_owned_to_skip_testing_does_not_get_a_merge()
    {
        const string SelfOwned = """
            {
              "plan.plan": { artifacts: {
                requirements: { schema: 1, scope: { kind: ticket, id: T-9 }, requirements: [
                  { id: R-1, title: Widgets are added, behaviour: An added widget is listed., supports_reason: The ticket's own behaviour } ] },
                delivery_plan: { schema: 1, scope: { kind: ticket, id: T-9 }, acceptance_criteria: [
                  { id: AC-1, title: Added widgets are listed, behaviour: Adding a widget then listing shows it., supports: [{ artifact: requirements, requirement: R-1 }],
                    owner: { kind: ticket, id: T-9 } } ] } } },
              implement: { artifacts: { implementation_claims: { schema: 1, claims: [] } } }
            }
            """;
        var (h, forge, repo) = await StartAsync("forge-self-owned", policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }", script: SelfOwned);
        await using var _ = h;
        var main = forge.Resolve(repo, "main")!;

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-9", "--param", "engine_can_merge=true", "--repo", Repo);
        var wait = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(3));

        var view = await h.ShowAsync(id);
        Assert.True(wait is not null, view.ToJsonString());
        Assert.Equal(main, forge.Resolve(repo, "main"));
        Assert.DoesNotContain("deliver.merge", Harness.StartedSteps(view));
        var trace = Trace(view);
        Assert.Contains(trace, d => d.StartsWith("verification_method_support (std): invalid", StringComparison.Ordinal) && d.Contains("no_method", StringComparison.Ordinal));
        h.Cli("abort", id, "--reason", "test done");
    }

    [Fact]
    public async Task A_ticket_started_with_a_parent_branch_lands_there_by_an_engine_merge_and_main_is_untouched()
    {
        // How a ticket inside an epic lands: into the epic's branch, which F1 does not govern (LOG 137).
        var (h, forge, repo) = await StartAsync("forge-parent");
        await using var _ = h;
        var main = forge.Resolve(repo, "main")!;
        var epic = forge.Commit(repo, "refs/heads/ff/epic/E-1", new Dictionary<string, string?> { ["epic.txt"] = "the epic's work\n" });

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-4", "--param", "engine_can_merge=true", "--repo", Repo,
            "--parent-branch", "ff/epic/E-1");
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "240");

        var view = await h.ShowAsync(id);
        Assert.True(exit == 0, end + error + view.ToJsonString());
        Assert.Equal($"run {id} completed", end.Trim());
        var landed = forge.Resolve(repo, "ff/epic/E-1")!;
        Assert.Equal([epic], forge.GetCommit(repo, landed).Parents);
        Assert.StartsWith("implement execution 1", forge.ReadFile(repo, landed, $"work/{id}/implement.txt"), StringComparison.Ordinal);
        Assert.Equal("the epic's work\n", forge.ReadFile(repo, landed, "epic.txt"));
        Assert.Equal(main, forge.Resolve(repo, "main"));
        var state = (await new InMemoryCodeHost(forge).GetPullRequestAsync(new PullRequestRef(repo, 1), TestContext.Current.CancellationToken)).Unwrap();
        Assert.Equal(("ff/epic/E-1", "ff/ticket/T-4"), (state.Base, state.Head));
    }

    /// <summary>
    /// LOG 193, found by the live epic: a sibling ticket merges into the epic's branch after this ticket branched from it. The
    /// reviewers' baseline is where the ticket parts from the epic's branch, not its new head, so the change they must read
    /// is the ticket's own file, not the sibling's (which is not in the ticket's checkout), and the ticket lands beside it.
    /// </summary>
    [Fact]
    public async Task A_sibling_merging_into_the_parent_branch_is_not_part_of_the_change_the_reviewers_read()
    {
        var (h, forge, repo) = await StartAsync("forge-sibling");
        await using var _ = h;
        forge.Commit(repo, "refs/heads/ff/epic/E-2", new Dictionary<string, string?> { ["epic.txt"] = "the epic's work\n" });

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-5", "--param", "engine_can_merge=true", "--repo", Repo,
            "--parent-branch", "ff/epic/E-2");
        var deadline = DateTime.UtcNow.AddSeconds(120);
        while (!Harness.StartedSteps(await h.ShowAsync(id)).Contains("plan.plan") && DateTime.UtcNow < deadline)
            await Task.Delay(200, TestContext.Current.CancellationToken);
        forge.Commit(repo, "refs/heads/ff/epic/E-2", new Dictionary<string, string?> { ["sibling.txt"] = "a sibling ticket's work\n" });
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "240");

        var view = await h.ShowAsync(id);
        Assert.True(exit == 0, end + error + view.ToJsonString());
        Assert.Equal($"run {id} completed", end.Trim());
        Assert.Contains(view["timeline"]!.AsArray(), e => e!["step_id"]!.ToString() == "verify.review.review_0.assess"
                                                          && e["detail"]?.ToString().Contains("changed_files_unread=0", StringComparison.Ordinal) == true);
        var landed = forge.Resolve(repo, "ff/epic/E-2")!;
        Assert.Equal("a sibling ticket's work\n", forge.ReadFile(repo, landed, "sibling.txt"));
        Assert.StartsWith("implement execution 1", forge.ReadFile(repo, landed, $"work/{id}/implement.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task F1_through_the_engine_a_standalone_ticket_asking_for_an_engine_merge_into_the_default_branch_does_not_start()
    {
        var (h, forge, repo) = await StartAsync("forge-f1");
        await using var _ = h;

        var refused = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-2", "--param", "engine_can_merge=true", "--repo", Repo);
        var (exit, end, _) = h.Cli("wait", refused, "--timeout", "60");

        Assert.Equal((1, $"run {refused} failed"), (exit, end.Trim()));
        var detail = (await h.ShowAsync(refused))["run"]!["detail"]!.ToString();
        Assert.Contains("check 23", detail, StringComparison.Ordinal);
        Assert.Contains("deliver.merge asks for an engine merge into acme/api's default branch main", detail, StringComparison.Ordinal);
        Assert.Null(forge.Resolve(repo, "ff/ticket/T-2"));          // nothing ran

        // Control: the same ticket with std's default, engine_can_merge false, starts and runs its first step.
        var allowed = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-3", "--repo", Repo);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
        while (forge.Resolve(repo, "ff/ticket/T-3") is null && DateTime.UtcNow < deadline)
            await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.NotNull(forge.Resolve(repo, "ff/ticket/T-3"));
        h.Cli("abort", allowed, "--reason", "control done");
    }

    /// <summary>
    /// std's <c>ticket_spec_rejected</c> on the memory providers: the specification is rejected once, and the route back to
    /// the planner carries <c>clean_up</c>, so a commit made on the work branch after planning started is removed and kept
    /// under a superseded reference before the planner runs again. The run goes on to an engine merge. Its history is the
    /// one replayed for the <c>m5-clean-up</c> and <c>m5-step-start</c> markers.
    /// </summary>
    [Fact]
    public async Task A_rejected_specification_resets_the_work_branch_to_where_planning_started()
    {
        var (h, forge, repo) = await StartAsync("forge-cleanup", policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }", autoApprove: false);
        await using var _ = h;
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-5", "--param", "engine_can_merge=true", "--repo", Repo);
        var approval = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(2));
        var planned = forge.Resolve(repo, "ff/ticket/T-5")!;          // the planner is read-only: where it started
        var stray = forge.Commit(repo, "refs/heads/ff/ticket/T-5", new Dictionary<string, string?> { ["stray.txt"] = "not wanted\n" });

        Assert.Equal(0, h.Cli("answer", id, approval!, "rejected", "--reason", "the specification omits the audit log").Exit);
        var again = await WaitForAnotherWaitAsync(h, id, approval!);

        // F5: the planner's second execution is told why, in the prompt a viewer reads through the artifact routes (review R1).
        Assert.Contains(await h.PromptsAsync(id, "plan.plan", 2), p => p.Contains("the specification omits the audit log", StringComparison.Ordinal));
        Assert.Equal(planned, forge.Resolve(repo, "ff/ticket/T-5"));
        Assert.Equal(stray, forge.Resolve(repo, $"refs/feature-flow/superseded/{id}/plan.plan/{stray[..12]}"));
        var trace = Trace(await h.ShowAsync(id));
        Assert.Contains(trace, d => d.StartsWith("clean_up: reset acme/api ff/ticket/T-5", StringComparison.Ordinal));

        Assert.Equal(0, h.Cli("answer", id, again, "approved").Exit);
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "240");
        Assert.True(exit == 0, end + error);
        Assert.Null(forge.ReadFile(repo, "main", "stray.txt"));
        Assert.NotNull(forge.ReadFile(repo, "main", $"work/{id}/implement.txt"));
        await h.RecordHistoryAsync(id, "ticket_clean_up");
    }

    [Fact]
    public async Task A_clean_up_the_remote_refuses_raises_an_intervention_at_the_step_it_was_for()
    {
        var (h, forge, repo) = await StartAsync("forge-cleanup-refused", autoApprove: false);
        await using var _ = h;
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-6", "--repo", Repo);
        var approval = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(2));
        var stray = forge.Commit(repo, "refs/heads/ff/ticket/T-6", new Dictionary<string, string?> { ["stray.txt"] = "not wanted\n" });
        forge.Fail("vcs.Push", new ProviderError("validation_failed", "the remote refuses the push"), times: 3);

        Assert.Equal(0, h.Cli("answer", id, approval!, "rejected").Exit);
        await WaitForAnotherWaitAsync(h, id, approval!);

        var wait = (await h.ShowAsync(id))["waits"]!.AsArray().Single()!;
        Assert.Equal(("intervention", "plan.plan"), (wait["kind"]!.ToString(), wait["step_id"]!.ToString()));
        Assert.Contains("its clean_up could not reset the work branch", wait["prompt"]!.ToString(), StringComparison.Ordinal);
        Assert.Equal(stray, forge.Resolve(repo, "ff/ticket/T-6"));
        h.Cli("abort", id, "--reason", "test done");
    }

    [Fact]
    public async Task Every_action_has_a_heartbeat_timeout()
    {
        var (h, _, _) = await StartAsync("forge-heartbeat", policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }");
        await using var __ = h;
        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-7", "--param", "engine_can_merge=true", "--repo", Repo);
        Assert.Equal(0, h.Cli("wait", id, "--timeout", "240").Exit);

        var heartbeats = await ActionHeartbeatsAsync(h, id);

        Assert.NotEmpty(heartbeats);
        Assert.All(heartbeats, t => Assert.Equal(TimeSpan.FromMinutes(2), t));
    }

    /// <summary>The heartbeat timeout of every RunAction activity the run's first workflow run scheduled.</summary>
    static async Task<List<TimeSpan?>> ActionHeartbeatsAsync(Harness h, string id)
    {
        var history = await h.Client.GetWorkflowHandle(id).FetchHistoryAsync();
        return history.Events.Where(e => e.ActivityTaskScheduledEventAttributes?.ActivityType.Name == "RunAction")
            .Select(e => e.ActivityTaskScheduledEventAttributes)
            .Select(a => a.HeartbeatTimeout is { } t && (t.Seconds > 0 || t.Nanos > 0) ? t.ToTimeSpan() : (TimeSpan?)null)
            .ToList();
    }

    static async Task<string> WaitForAnotherWaitAsync(Harness h, string id, string previous)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (DateTime.UtcNow < deadline)
        {
            if ((await h.ShowAsync(id))["waits"]!.AsArray().Select(w => w!["wait_id"]!.ToString()).FirstOrDefault(w => w != previous) is { } next)
                return next;
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"{id} opened no wait after {previous}");
    }

    static List<string> Trace(JsonNode view) =>
        view["timeline"]!.AsArray().Select(e => e!["detail"]?.ToString() ?? "").ToList();
}
