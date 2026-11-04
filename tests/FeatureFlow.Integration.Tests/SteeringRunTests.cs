using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// A person steering a running run on a Temporal dev server (docs/05 section 5): the <c>override</c> update with the variants
/// the caller holds, breakpoints, skips, grants, amends and role changes, mid-run library overrides reaching future steps
/// and running children (docs/08 section 5.2), an agent's proposed library override (docs/05 section 6), re-running a failed
/// branch of a decided join, aborting with the run's pull requests closed (m6-results follow-up 3), and an override
/// arriving while the run hands over.
/// </summary>
[Trait("Category", "Integration")]
public class SteeringRunTests
{
    static readonly string[] Library = [Path.Combine(Harness.TestLibraries, "m7steer")];
    const string Repo = "acme/api";

    static OverrideArgs Override(string key, string? step, Dictionary<string, string>? change = null, string actor = "ann", params string[] variants) =>
        new(Guid.NewGuid().ToString("N"), key, actor, $"testing {key}", step, change ?? [], variants);

    static Task<AnswerReply> SendAsync(Harness h, string id, OverrideArgs o) =>
        h.Client.GetWorkflowHandle(id).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Override, [o]);

    static async Task<string> RefusedAsync(Harness h, string id, OverrideArgs o)
    {
        var e = await Assert.ThrowsAsync<WorkflowUpdateFailedException>(() => SendAsync(h, id, o));
        return e.InnerException?.Message ?? e.Message;
    }

    static async Task UntilAsync(Func<Task<bool>> done, string what, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (await done())
                    return;
            }
            catch (HttpRequestException)
            {
                // A child the parent has not started yet is not in the record store.
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(what);
    }

    static async Task<JsonNode> OpenWaitAsync(Harness h, string id, string kind)
    {
        JsonNode? found = null;
        await UntilAsync(async () => (found = (await h.ShowAsync(id))["waits"]!.AsArray().FirstOrDefault(w => w!["kind"]!.ToString() == kind)) is not null,
            $"{id} opened no {kind}");
        return found!;
    }

    static List<(string Kind, string Step, string Detail)> Timeline(JsonNode view) =>
        view["timeline"]!.AsArray().Select(e => (e!["kind"]!.ToString(), e["stepId"]!.ToString(), e["detail"]?.ToString() ?? "")).ToList();

    /// <summary>The task text each agent step was given, from the workflow's own history: exactly what it resolved.</summary>
    static async Task<List<(string Step, string Task)>> TasksAsync(Harness h, string workflowId)
    {
        var tasks = new List<(string, string)>();
        foreach (var run in await h.RunsOfAsync(workflowId))
            foreach (var e in (await h.Client.GetWorkflowHandle(workflowId, run.RunId).FetchHistoryAsync()).Events
                         .Where(e => e.EventType == EventType.ActivityTaskScheduled && e.ActivityTaskScheduledEventAttributes.ActivityType.Name == "RunAgentStep"))
            {
                var call = JsonNode.Parse(e.ActivityTaskScheduledEventAttributes.Input.Payloads_[0].Data.ToStringUtf8())!;
                tasks.Add((call["Step"]!["StepId"]!.ToString(), call["Agent"]?["TaskText"]?.ToString() ?? ""));
            }

        return tasks;
    }

    [Fact]
    public async Task A_breakpoint_pauses_a_run_and_a_skip_of_a_protected_step_needs_its_variant()
    {
        await using var h = await Harness.StartAsync("steer", """{ "steer_line:prepare": { delay_from: wait_ms } }""", libraries: Library);
        var id = h.Start("m7steer:templates/steer_line", "--param", "wait_ms=3000", "--repo", Repo);
        await UntilAsync(async () => Harness.StartedSteps(await h.ShowAsync(id)).Contains("prepare"), "prepare did not start");

        Assert.StartsWith("applied", (await SendAsync(h, id, Override("breakpoint", "build"))).Result);
        Assert.Contains("skip_protected", await RefusedAsync(h, id, Override("skip", "review", new() { ["outcome"] = "accepted" })));
        Assert.Contains("not an outcome", await RefusedAsync(h, id, Override("skip", "review", new() { ["outcome"] = "maybe" }, "ann", "skip_protected")));
        Assert.Contains("is running", await RefusedAsync(h, id, Override("skip", "prepare", new() { ["outcome"] = "sent" })));
        var skip = Override("skip", "review", new() { ["outcome"] = "accepted" }, "root", "skip_protected");
        Assert.Equal("applied: skipped when the run reaches it (protected: review)", (await SendAsync(h, id, skip)).Result);
        Assert.StartsWith("duplicate", (await SendAsync(h, id, skip)).Result);

        var paused = await OpenWaitAsync(h, id, "breakpoint");
        Assert.Equal(("build", "ticket.breakpoint"), (paused["stepId"]!.ToString(), paused["decisionType"]!.ToString()));
        Assert.DoesNotContain("build", Harness.StartedSteps(await h.ShowAsync(id)));
        var (answered, output, error) = h.Cli("answer", id, paused["waitId"]!.ToString(), "continue");
        Assert.True(answered == 0, output + error);

        var (exit, end, _) = h.Cli("wait", id, "--timeout", "60");
        var view = await h.ShowAsync(id);
        Assert.True(end.Trim() == $"run {id} completed", end + view.ToJsonString());
        var timeline = Timeline(view);
        Assert.Equal(["prepare", "build", "ship"], Harness.StartedSteps(view));
        Assert.Contains(timeline, e => e is { Kind: "skipped", Step: "review", Detail: "accepted" });
        Assert.Contains(timeline, e => e.Kind == "override" && e.Step == "review" && e.Detail.StartsWith("skip by root: testing skip", StringComparison.Ordinal));
        Assert.Contains(timeline, e => e is { Kind: "breakpoint_set", Step: "build", Detail: "by ann" });
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
        await h.RecordHistoryAsync(id, "steering_line");
    }

    [Fact]
    public async Task A_persons_amend_role_change_and_grant_are_bounded_by_their_variants()
    {
        await using var h = await Harness.StartAsync("steerpatch", """{ "steer_line:prepare": { delay_from: wait_ms } }""", libraries: Library,
            policy: "  attempts: { ceiling: { per_step: 3 }, hard_ceiling: { per_step: 4, per_run: 300 } }");
        var id = h.Start("m7steer:templates/steer_line", "--param", "wait_ms=4000", "--repo", Repo);
        await UntilAsync(async () => Harness.StartedSteps(await h.ShowAsync(id)).Contains("prepare"), "prepare did not start");

        const string limits = "{ api_version: feature-flow/v1, kind: patch, base_revision: 1, changes: [{ op: set, target: review, field: limits, value: { attempts: 2 } }] }";
        Assert.Contains("amend_protected", await RefusedAsync(h, id, Override("amend", null, new() { ["patch"] = limits })));
        Assert.Equal("applied: plan revision 2", (await SendAsync(h, id, Override("amend", null, new() { ["patch"] = limits }, "root", "amend_protected"))).Result);
        const string role = "{ api_version: feature-flow/v1, kind: patch, base_revision: 2, changes: [{ op: replace, target: build, step: { id: build, agent: { role: std:roles/designer, task: m7steer:tasks/build }, outcomes: { built: success, stuck: blocked } } }] }";
        Assert.Contains("change_role", await RefusedAsync(h, id, Override("amend", null, new() { ["patch"] = role }, "root", "amend_protected")));
        Assert.Equal("applied: plan revision 3", (await SendAsync(h, id, Override("change_role", "build", new() { ["role"] = "std:roles/designer" }, "root"))).Result);

        Assert.Contains("above the hard ceiling of 4", await RefusedAsync(h, id, Override("grant_attempts", "build", new() { ["attempts"] = "2" })));
        Assert.Equal("applied", (await SendAsync(h, id, Override("grant_attempts", "build", new() { ["attempts"] = "2" }, "root", "grant_attempts_beyond"))).Result);
        Assert.Contains("not delivered as an override", await RefusedAsync(h, id, Override("recover", "build")));
        Assert.Contains("not a run-time override key", await RefusedAsync(h, id, Override("teleport", "build")));

        var (_, end, _) = h.Cli("wait", id, "--timeout", "60");
        var view = await h.ShowAsync(id);
        Assert.True(end.Trim() == $"run {id} completed", end + view.ToJsonString());
        var timeline = Timeline(view);
        Assert.Contains(timeline, e => e is { Kind: "role_changed", Step: "build" } && e.Detail.StartsWith("std:roles/implementer -> std:roles/designer by root", StringComparison.Ordinal));
        Assert.Contains(timeline, e => e is { Kind: "attempts_granted", Step: "build", Detail: "2 attempt(s) by root" });
        Assert.Contains(timeline, e => e.Kind == "patch_rejected" && e.Detail.Contains("change_role", StringComparison.Ordinal));
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    [Fact]
    public async Task A_library_override_reaches_future_steps_and_a_running_child_while_steps_in_flight_keep_theirs()
    {
        await using var h = await Harness.StartAsync("steerlib", """{ "steer_line:prepare": { delay_from: wait_ms }, "steer_line:build": { delay: 3s } }""", libraries: Library);
        var id = h.Start("m7steer:templates/steer_line", "--param", "wait_ms=0", "--repo", Repo);
        await SendAsync(h, id, Override("breakpoint", "review"));
        await UntilAsync(async () => Harness.StartedSteps(await h.ShowAsync(id)).Contains("build"), "build did not start");

        // build is in flight: it keeps the task it resolved. review, protected, resolves the review task: an administrator's.
        var build = Override("override_library", null, new() { ["item"] = "m7steer:tasks/build", ["with"] = "Build the change. BUILD-V2\n" });
        Assert.StartsWith("applied: sha256:", (await SendAsync(h, id, build)).Result);
        var review = new Dictionary<string, string> { ["item"] = "m7steer:tasks/review", ["with"] = "Review the change. REVIEW-V2\n" };
        Assert.Contains("override_library_protected", await RefusedAsync(h, id, Override("override_library", null, review)));
        Assert.Contains("not templates", await RefusedAsync(h, id, Override("override_library", null, new() { ["item"] = "m7steer:templates/steer_line", ["with"] = "x" })));
        Assert.EndsWith("(protected: review)", (await SendAsync(h, id, Override("override_library", null, review, "root", "override_library_protected"))).Result);

        var paused = await OpenWaitAsync(h, id, "breakpoint");
        h.Cli("answer", id, paused["waitId"]!.ToString(), "continue");
        var (_, end, _) = h.Cli("wait", id, "--timeout", "60");
        var view = await h.ShowAsync(id);
        Assert.True(end.Trim() == $"run {id} completed", end + view.ToJsonString());
        var tasks = await TasksAsync(h, id);
        Assert.Contains("BUILD-V1", tasks.Single(t => t.Step == "build").Task);
        Assert.Contains("REVIEW-V2", tasks.Single(t => t.Step == "review").Task);
        var entries = Timeline(view).Where(e => e.Kind == "library_overridden").ToList();
        Assert.Equal(2, entries.Count);
        Assert.Contains("in flight, keeping theirs: build", entries[0].Detail);
        Assert.Contains("by root: testing override_library", entries[1].Detail);
        Assert.Contains("protected steps affected: review", entries[1].Detail);

        // The run's lock is its last one: both files replaced, by content hash (docs/08 section 5.1).
        var records = h.Host.Providers.Required<IRecordStoreProvider>("record_store");
        var locked = Assert.Single((await records.QueryAsync<LockRecord>(Queries.Lock, new Dictionary<string, string> { ["run_id"] = id }, TestContext.Current.CancellationToken)).Value!);
        var lockText = await ReadAsync(h, locked.LockRef);
        Assert.Contains(FeatureFlow.Library.LibraryFiles.Hash("Review the change. REVIEW-V2\n"), lockText);
        Assert.Contains(FeatureFlow.Library.LibraryFiles.Hash("Build the change. BUILD-V2\n"), lockText);

        // A parent's override reaches its running child, whose later steps resolve it.
        var parent = h.Start("m7steer:templates/steer_parent", "--param", "wait_ms=4000", "--repo", Repo);
        var child = $"{parent}.kid.1";
        await UntilAsync(async () => (await h.ShowAsync(child))["timeline"]!.AsArray().Any(e => e!["kind"]!.ToString() == "started"), "the child did not start");
        await SendAsync(h, parent, Override("override_library", null, new() { ["item"] = "m7steer:tasks/build", ["with"] = "Build the change. BUILD-V3\n" }));
        var (_, parentEnd, _) = h.Cli("wait", parent, "--timeout", "90");
        Assert.True(parentEnd.Trim() == $"run {parent} completed", parentEnd + (await h.ShowAsync(child)).ToJsonString());
        Assert.Contains("BUILD-V3", (await TasksAsync(h, child)).Single(t => t.Step == "build").Task);
        Assert.Contains(Timeline(await h.ShowAsync(child)), e => e.Kind == "library_overridden" && e.Detail.Contains("from the parent run", StringComparison.Ordinal));
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
        Assert.Empty(await h.WorkflowTaskFailuresAsync(child));
    }

    [Fact]
    public async Task An_agents_library_override_waits_for_a_persons_approval_and_is_recorded_as_theirs()
    {
        const string script = """
            {
              "steer_planner:plan": { library_override: { item: "m7steer:tasks/build", with: "Build the change. BUILD-V4" } },
              "steer_planner:build": { library_override: { item: "m7steer:tasks/plan", with: "anything" } }
            }
            """;
        await using var h = await Harness.StartAsync("steerprop", script, libraries: Library);
        var id = h.Start("m7steer:templates/steer_planner", "--repo", Repo);
        await SendAsync(h, id, Override("breakpoint", "build"));

        var approval = await OpenWaitAsync(h, id, "patchapproval");
        Assert.Equal(("plan", "ticket.patch_approval"), (approval["stepId"]!.ToString(), approval["decisionType"]!.ToString()));
        Assert.Contains("m7steer:tasks/build", approval["reason"]!.ToString());
        var (answered, output, error) = h.Cli("answer", id, approval["waitId"]!.ToString(), "approve");
        Assert.True(answered == 0, output + error);
        await UntilAsync(async () => Timeline(await h.ShowAsync(id)).Any(e => e.Kind == "library_overridden"), "the approved override was not applied");
        var paused = await OpenWaitAsync(h, id, "breakpoint");
        h.Cli("answer", id, paused["waitId"]!.ToString(), "continue");

        var (_, end, _) = h.Cli("wait", id, "--timeout", "60");
        var view = await h.ShowAsync(id);
        Assert.True(end.Trim() == $"run {id} completed", end + view.ToJsonString());
        Assert.Contains("BUILD-V4", (await TasksAsync(h, id)).Single(t => t.Step == "build").Task);
        var timeline = Timeline(view);
        Assert.Contains(timeline, e => e.Kind == "library_overridden" && e.Detail.Contains("by tester: approved proposal of plan", StringComparison.Ordinal));
        // The build step has no planning authority: its proposal is refused and recorded, never applied.
        Assert.Contains(timeline, e => e is { Kind: "library_override_rejected", Step: "build" } && e.Detail.Contains("no planning authority", StringComparison.Ordinal));
        Assert.Single(timeline, e => e.Kind == "library_overridden");
    }

    [Fact]
    public async Task Re_running_a_failed_branch_of_a_decided_join_lets_the_epic_complete()
    {
        await using var h = await Harness.StartAsync("steerfan", "{}", libraries: Library);
        var id = h.Start("m7steer:templates/steer_fan", "--repo", Repo);
        var a = await OpenWaitAsync(h, $"{id}.kid_a.1", "decision");
        var b = await OpenWaitAsync(h, $"{id}.kid_b.1", "decision");
        Assert.Equal(0, h.Cli("answer", $"{id}.kid_a.1", a["waitId"]!.ToString(), "go").Exit);
        Assert.Equal(0, h.Cli("answer", $"{id}.kid_b.1", b["waitId"]!.ToString(), "stop").Exit);

        var escalation = await OpenWaitAsync(h, id, "intervention");
        Assert.Equal("fan", escalation["stepId"]!.ToString());
        var (recovered, output, error) = h.Cli("recover", id, escalation["waitId"]!.ToString(), "go_to", "kid_b");
        Assert.True(recovered == 0, output + error);
        var again = await OpenWaitAsync(h, $"{id}.kid_b.2", "decision");
        Assert.Equal(0, h.Cli("answer", $"{id}.kid_b.2", again["waitId"]!.ToString(), "go").Exit);

        var (_, end, _) = h.Cli("wait", id, "--timeout", "60");
        var view = await h.ShowAsync(id);
        Assert.True(end.Trim() == $"run {id} completed", end + view.ToJsonString());
        var started = Harness.StartedSteps(view);
        Assert.Equal(1, started.Count(s => s == "kid_a"));
        Assert.Equal(2, started.Count(s => s == "kid_b"));
        Assert.Contains(Timeline(view), e => e is { Kind: "reopened", Step: "fan" });
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
        await h.RecordHistoryAsync(id, "steering_rerun_branch");
    }

    [Fact]
    public async Task Aborting_with_close_pull_requests_closes_the_run_trees_open_pull_requests_and_without_leaves_them()
    {
        var world = $"it-steer-{Guid.NewGuid():N}";
        var forge = InMemoryForge.Named(world);
        var repo = forge.CreateRepository(Repo, new Dictionary<string, string> { ["src/App.cs"] = "class App {}\n" });
        forge.Protect(repo, new BranchProtection("main", true, true, 1, [], false));
        forge.Commit(repo, "refs/heads/ff/steer-pr", new Dictionary<string, string?> { ["src/App.cs"] = "class App { }\n" });
        await using var h = await Harness.StartAsync("steerpr", """{ "steer_pr:hold": { delay_from: wait_ms } }""", omitTypes: ["vcs", "code_host", "ci"], libraries: Library,
            providers: string.Join("\n", new[] { "vcs", "code_host", "ci" }.Select(t => $"  {t}: {{ instances: {{ memory: {{ world: {world} }} }} }}")));
        var host = h.Host.Providers.Required<ICodeHostProvider>("code_host");

        // A run, or a parent whose child opened the pull request: the abort reaches the child, which closes its own.
        async Task<PullRequestState> AbortAsync(bool close, string template = "steer_pr")
        {
            // The hold's activity sends no heartbeat, so the abort waits for it to end (M3 follow-up 9): keep it short.
            var id = h.Start($"m7steer:templates/{template}", "--param", "wait_ms=6000", "--repo", Repo);
            var opener = template == "steer_pr" ? id : $"{id}.kid.1";
            await UntilAsync(async () => Harness.StartedSteps(await h.ShowAsync(opener)).Contains("hold"), "hold did not start");
            var opened = Timeline(await h.ShowAsync(opener)).Single(e => e.Kind == "agent" && e.Detail.StartsWith("pull request ", StringComparison.Ordinal)).Detail;
            var named = opened.Split(' ')[2];
            var pr = new PullRequestRef(new RepoRef(named[..named.IndexOf('#', StringComparison.Ordinal)]), int.Parse(named[(named.IndexOf('#', StringComparison.Ordinal) + 1)..], System.Globalization.CultureInfo.InvariantCulture));
            await h.Client.GetWorkflowHandle(id).ExecuteUpdateAsync<AnswerReply>(RunWorkflowNames.Abort, [new AbortArgs("ann", "no longer wanted", close)]);
            Assert.Equal("aborted", await h.Client.GetWorkflowHandle(id).GetResultAsync<string>());
            Assert.Equal("aborted", await h.Client.GetWorkflowHandle(opener).GetResultAsync<string>());
            var timeline = Timeline(await h.ShowAsync(opener));
            Assert.Equal(close, timeline.Any(e => e.Kind == "pull_request_closed" && e.Detail == $"{named}: closed"));
            return (await host.GetPullRequestAsync(pr, TestContext.Current.CancellationToken)).Value!;
        }

        Assert.Equal(PullRequestStates.Closed, (await AbortAsync(close: true)).State);
        Assert.Equal(PullRequestStates.Open, (await AbortAsync(close: false)).State);
        forge.Commit(repo, "refs/heads/ff/steer-pr", new Dictionary<string, string?> { ["src/App.cs"] = "class App { } \n" });
        Assert.Equal(PullRequestStates.Closed, (await AbortAsync(close: true, "steer_pr_parent")).State);
    }

    [Fact]
    public async Task An_override_arriving_while_the_run_hands_over_is_carried_into_the_next_run()
    {
        await using var h = await Harness.StartAsync("steerdrain", """{ "steer_long:*": { delay_from: wait_ms } }""", libraries: Library);
        var work = "[" + string.Join(", ", Enumerable.Repeat(600, 40)) + "]";
        var id = h.Start("m7steer:templates/steer_long", "--param", $"work={work}", "--repo", Repo, "--continue-as-new-events", "100");
        var handle = h.Client.GetWorkflowHandle(id);

        // Sent the moment the run reports it is draining, as SP1's answers were; the handler applies it during the drain.
        var sent = false;
        var until = DateTime.UtcNow.AddSeconds(90);
        while (!sent && DateTime.UtcNow < until)
        {
            var view = await handle.QueryAsync<RunView>(RunWorkflowNames.View, []);
            if (view.Status is not ("starting" or "running"))
                break;
            if (view.Draining)
            {
                await SendAsync(h, id, Override("breakpoint", "final"));
                sent = (await handle.QueryAsync<RunView>(RunWorkflowNames.View, [])).AnswersWhileDraining > 0;
                if (!sent)
                    await SendAsync(h, id, Override("breakpoint", "final", new() { ["set"] = "false" }));
            }

            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        Assert.True(sent, "no override reached the run while it drained");
        var paused = await OpenWaitAsync(h, id, "breakpoint");
        Assert.Equal("final", paused["stepId"]!.ToString());
        var after = await handle.QueryAsync<RunView>(RunWorkflowNames.View, []);
        Assert.True(after.HandOvers.Count >= 1, "the run did not hand over");
        Assert.Equal(0, h.Cli("answer", id, paused["waitId"]!.ToString(), "continue").Exit);
        var (_, end, _) = h.Cli("wait", id, "--timeout", "120");
        Assert.True(end.Trim() == $"run {id} completed", end + (await h.ShowAsync(id)).ToJsonString());
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    static async Task<string> ReadAsync(Harness h, string reference)
    {
        var got = await h.Host.Providers.Required<IArtifactStoreProvider>("artifact_store").GetAsync(new ArtifactRef(reference), TestContext.Current.CancellationToken);
        await using var stream = got.Value!;
        return await new StreamReader(stream).ReadToEndAsync(TestContext.Current.CancellationToken);
    }
}
