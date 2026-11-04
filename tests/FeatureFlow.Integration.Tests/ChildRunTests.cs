using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Interpreter.Wire;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// Child runs (docs/03 section 9): a child starts with its parent's resolved parameters for the names it declares (M3
/// follow-up 2); steps consume artifacts from a parent, as it was when the child started, and from a child, as it
/// ended (follow-up 3, check 32); and a child that ends without signalling still completes its parent's
/// <c>run.child</c> step, before or after a hand-over (follow-up 10).
/// </summary>
[Trait("Category", "Integration")]
public class ChildRunTests
{
    static readonly string[] Library = [Path.Combine(Harness.TestLibraries, "m6")];

    const string Family = """
        { "family_parent:write": written, "family_child:use": written, "family_parent:read": done }
        """;

    const string Silent = """
        { "silent_child:work": { delay: 600s }, "silent_parent:*": { delay_from: wait_ms } }
        """;

    [Fact]
    public async Task A_child_inherits_its_parents_parameters_and_artifacts_and_its_parent_consumes_the_childs()
    {
        await using var h = await Harness.StartAsync("family", Family, libraries: Library);
        var id = h.Start("m6:templates/family_parent", "--param", "ticket_id=T-9", "--repo", "acme/api");

        var (exit, end, error) = h.Cli("wait", id, "--timeout", "120");
        var view = await h.ShowAsync(id);
        var childId = $"{id}.child.1";
        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), end + error + view.ToJsonString() + (await h.ShowAsync(childId)).ToJsonString());

        // The child completes only with ticket_id and colour from its parent and size from its step (its route guard).
        Assert.Equal("completed", await h.Client.GetWorkflowHandle(childId).GetResultAsync<string>());
        var start = await StartOfAsync(h, childId);
        Assert.Equal(["colour", "size", "ticket_id"], start.Parameters.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(("T-9", "blue"), (start.Parameters["ticket_id"].Trim('"'), start.Parameters["colour"].Trim('"')));

        // The parent's notes as they were when the child started; the parent's read consumed the child's summary (check 32 passed).
        var notes = Assert.Single(start.ParentArtifacts!);
        Assert.Equal("notes", notes.Key);
        Assert.Equal(start.ParentArtifacts, start.RootArtifacts);
        Assert.Contains(view["timeline"]!.AsArray(), e => e!["kind"]!.ToString() == "completed" && e["stepId"]!.ToString() == "read");

        // The artifact index: each run's artifacts by step and execution, a parent's view including its children's.
        var index = (await h.Host.Providers.Required<Providers.Abstractions.IRecordStoreProvider>("record_store").QueryAsync<Providers.Abstractions.ArtifactRecord>(
            Providers.Abstractions.Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = id }, TestContext.Current.CancellationToken)).Value!;
        var againId = $"{id}.again.1";
        Assert.Equal([(id, "write", "notes"), (childId, "use", "notes"), (childId, "use", "summary"), (againId, "use", "notes"), (againId, "use", "summary")],
            index.Select(a => (a.RunId, a.StepId, a.Name)));
        Assert.All(index, a => Assert.Equal(("m6:artifact_types/notes", null, false, 1), (a.Type, a.Action, a.Evidence, a.Execution)));

        // Review F2: the first child's notes are the child's, not this run's. A later child still gets this run's own
        // notes as its parent's, and nothing else (the run.child step that holds the first child's artifacts is skipped).
        var own = index.Single(a => a.RunId == id).Ref;
        Assert.Equal(own, notes.Value);
        Assert.NotEqual(own, index.First(a => a.RunId == childId && a.Name == "notes").Ref);
        var again = await StartOfAsync(h, againId);
        Assert.Equal(new Dictionary<string, string> { ["notes"] = own }, again.ParentArtifacts);
        await h.RecordHistoryAsync(id, "family_parent");
        await h.RecordHistoryAsync(childId, "family_child");
    }

    [Fact]
    public async Task Check_32_stops_a_step_whose_artifact_from_its_parent_does_not_exist()
    {
        // The child template started on its own: it has no parent, so its parent's notes do not exist.
        await using var h = await Harness.StartAsync("family-alone", Family, libraries: Library);
        var id = h.Start("m6:templates/family_child", "--param", "ticket_id=T-9", "--param", "size=3", "--repo", "acme/api");

        var waitId = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(1));

        Assert.NotNull(waitId);
        var wait = (await h.ShowAsync(id))["waits"]!.AsArray().Single()!;
        Assert.Equal("use", wait["stepId"]!.ToString());
        Assert.Contains("check 32", wait["reason"]!.ToString(), StringComparison.Ordinal);
        Assert.Contains("parent/notes", wait["reason"]!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_child_ended_without_signalling_completes_the_parents_step_in_the_run_that_started_it()
    {
        await using var h = await Harness.StartAsync("silent", Silent, libraries: Library);
        var id = h.Start("m6:templates/silent_parent", "--param", "work=[]", "--repo", "acme/api");
        var childId = await RunningChildAsync(h, id);

        await h.Client.GetWorkflowHandle(childId).TerminateAsync("ended from outside, without signalling its parent");

        var (exit, end, error) = h.Cli("wait", id, "--timeout", "120");
        var view = await h.ShowAsync(id);
        Assert.True(end.Trim() == $"run {id} failed", $"the parent did not notice its child's end: {end}{error} {view.ToJsonString()}");
        Assert.Equal(["failed"], Completions(view, "child"));
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    [Fact]
    public async Task A_child_ended_without_signalling_after_its_parent_handed_over_completes_the_parents_step()
    {
        await using var h = await Harness.StartAsync("silent-later", Silent, libraries: Library);
        var work = "[" + string.Join(", ", Enumerable.Repeat(500, 40)) + "]";
        var id = h.Start("m6:templates/silent_parent", "--param", $"work={work}", "--repo", "acme/api", "--continue-as-new-events", "100");
        var childId = await RunningChildAsync(h, id);

        // Only after the run that started the child has handed over: that run could have awaited the child itself.
        var handle = h.Client.GetWorkflowHandle(id);
        var until = DateTime.UtcNow.AddSeconds(90);
        RunView view;
        do
        {
            await Task.Delay(300, TestContext.Current.CancellationToken);
            view = await handle.QueryAsync<RunView>(RunWorkflowNames.View, []);
        }
        while ((view.HandOvers.Count == 0 || view.Draining) && DateTime.UtcNow < until);
        Assert.True(view.HandOvers.Count >= 1 && !view.Draining, $"no hand-over: {view.HandOvers.Count}, draining {view.Draining}");
        await h.Client.GetWorkflowHandle(childId).TerminateAsync("ended from outside, without signalling its parent");

        var (exit, end, error) = h.Cli("wait", id, "--timeout", "120");
        var record = await h.ShowAsync(id);
        Assert.True(end.Trim() == $"run {id} failed", $"the parent did not notice its child's end: {end}{error} {record.ToJsonString()}");
        Assert.Equal(["failed"], Completions(record, "child"));
        var timeline = record["timeline"]!.AsArray().Select(e => (Kind: e!["kind"]!.ToString(), Step: e["stepId"]!.ToString())).ToList();
        Assert.True(timeline.FindIndex(e => e is { Kind: "child_started", Step: "child" }) < timeline.FindIndex(e => e.Kind == "continued_as_new"),
            "the child was not started before the first hand-over");
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
        await h.RecordRunHistoryAsync(id, 1, "silent_parent_resumed");
    }

    /// <summary>
    /// Review F3: a child's end that the core refuses (its run.child step promised an artifact the child did not produce)
    /// stops the step for a person instead of leaving it running for ever.
    /// </summary>
    [Fact]
    public async Task A_childs_end_that_cannot_be_applied_raises_an_intervention()
    {
        await using var h = await Harness.StartAsync("claims", """{ "silent_child:work": done }""", libraries: Library);
        var id = h.Start("m6:templates/claims_parent", "--repo", "acme/api");

        var waitId = await h.WaitForWaitAsync(id, TimeSpan.FromMinutes(1));

        Assert.NotNull(waitId);
        var wait = (await h.ShowAsync(id))["waits"]!.AsArray().Single()!;
        Assert.Equal("child", wait["stepId"]!.ToString());
        Assert.Contains("the child's end could not be applied", wait["reason"]!.ToString(), StringComparison.Ordinal);
        Assert.Contains("claimed", wait["reason"]!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>The id of the parent's child once the child is running.</summary>
    static async Task<string> RunningChildAsync(Harness h, string parentId)
    {
        var childId = $"{parentId}.child.1";
        var until = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < until)
        {
            try
            {
                var description = await h.Client.GetWorkflowHandle(childId).DescribeAsync();
                if (description.Status == Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Running)
                    return childId;
            }
            catch (Temporalio.Exceptions.RpcException)
            {
                // Not started yet.
            }

            await Task.Delay(200);
        }

        throw new TimeoutException($"{childId} did not start");
    }

    static List<string> Completions(JsonNode view, string step) =>
        view["timeline"]!.AsArray().Where(e => e!["kind"]!.ToString() == "completed" && e["stepId"]!.ToString() == step).Select(e => e!["detail"]!.ToString()).ToList();

    /// <summary>A run's input as it first started.</summary>
    static async Task<RunStart> StartOfAsync(Harness h, string workflowId)
    {
        var first = (await h.RunsOfAsync(workflowId)).First();
        var history = await h.Client.GetWorkflowHandle(workflowId, first.RunId).FetchHistoryAsync();
        var input = history.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_[0].Data.ToStringUtf8();
        return JsonSerializer.Deserialize<RunStart>(input)!;
    }

    /// <summary>
    /// Integration seam (LOG 191), found by the live epic: an engine action in a child reads what its step consumes from
    /// the parent. The child's artifact.validate checks its requirements against its parent's, which its planner names
    /// parent/requirements as its sandbox does, on the forge's real validators, and passes.
    /// </summary>
    [Fact]
    public async Task A_childs_artifact_validate_reads_its_parents_requirements_and_a_parent_link_resolves()
    {
        const string Script = """
            {
              "validated_parent:design": { artifacts: { requirements: { schema: 1, scope: { kind: epic, id: E }, requirements: [
                { id: E-1, title: Widgets are valid, behaviour: An invalid widget is refused., supports_reason: The epic's goal } ] } } },
              "validated_child:plan": { artifacts: { requirements: { schema: 1, scope: { kind: ticket, id: T-1 }, requirements: [
                { id: R-1, title: Names are short, behaviour: A long name is refused., supports: [{ artifact: parent/requirements, requirement: E-1 }] } ] } } }
            }
            """;
        var (h, _, _) = await ForgeTicketTests.StartAsync("validated-child", script: Script, libraries: Library);
        await using var _h = h;

        var id = h.Start("m6:templates/validated_parent", "--repo", "acme/api");
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "180");

        var childId = $"{id}.child.1";
        var child = await h.ShowAsync(childId);
        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), end + error + (await h.ShowAsync(id)).ToJsonString() + child.ToJsonString());
        Assert.Contains(child["timeline"]!.AsArray(), e => e!["stepId"]!.ToString() == "validate" && e["detail"]?.ToString() == "requirement_support (std): valid");
    }

    /// <summary>
    /// LOG 196, found by the live project: a child's parent branch is its parent's work branch, which exists only where the
    /// parent's own steps have touched a repository. Starting a child in another repository makes it there first, from the
    /// parent's parent branch (here the default branch), so the child's first step finds it. The child's repository,
    /// written short, is named as the run names it (LOG 198).
    /// </summary>
    [Fact]
    public async Task A_child_in_a_repository_its_parent_never_touched_finds_its_parent_branch_there()
    {
        var (h, forge, _) = await ForgeTicketTests.StartAsync("branching", script: """{ "branching_child:work": written }""", libraries: Library);
        await using var _h = h;
        var client = forge.CreateRepository("acme/client", new Dictionary<string, string> { ["README.md"] = "# client\n" });
        forge.Protect(client, new Providers.Abstractions.BranchProtection("main", true, true, 1, [], false));
        var main = forge.Resolve(client, "main")!;

        var id = h.Start("m6:templates/branching_parent", "--repo", "acme/api", "--repo", "acme/client");
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "120");

        var view = await h.ShowAsync(id);
        Assert.True((exit, end.Trim()) == (0, $"run {id} completed"), end + error + view.ToJsonString() + (await h.ShowAsync($"{id}.child.1")).ToJsonString());
        var created = Assert.Single(view["timeline"]!.AsArray(), e => e!["detail"]?.ToString().StartsWith("created branch", StringComparison.Ordinal) == true)!;
        var parentBranch = created["detail"]!.ToString().Split(' ')[2];
        Assert.Equal(main, forge.Resolve(client, parentBranch));
        Assert.Equal(["acme/client"], (await StartOfAsync(h, $"{id}.child.1")).Repositories);
    }
}
