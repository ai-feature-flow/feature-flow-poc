using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// M5 on the in-memory world: std's <c>ticket_delivery</c> runs on a Temporal dev server with the <c>memory</c> VCS,
/// code host, CI and sandbox, the scripted runtime acting in the sandbox (<see cref="EditingAgentRuntime"/>), and the
/// engine's own actions, to a merge into its parent branch. And F1 at run start: a standalone ticket asking for an
/// engine merge into the default branch is refused unless the organisation allows it for that repository.
/// </summary>
[Trait("Category", "Integration")]
public class ForgeTicketTests
{
    const string Repo = "acme/api";
    const string Manifest = """
        api_version: feature-flow/v1
        kind: repository
        setup: { commands: ["dotnet tool restore"] }
        test_tiers:
          - name: unit
            tags: [unit]
            command: dotnet test tests/Unit
            result: { format: trx, path: tests/Unit/unit.trx }
        static_analysis: { analyser: sarif, command: dotnet build, sarif_path: analysis.sarif }
        """;

    const string Trx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
          <UnitTestResult testName="Adds" outcome="Passed" /><UnitTestResult testName="Lists" outcome="Passed" />
        </Results></TestRun>
        """;

    const string Sarif = """{ "version": "2.1.0", "runs": [ { "tool": { "driver": { "name": "csc" } }, "results": [] } ] }""";

    /// <summary>A fresh world with the fixture repository, protected, and an engine whose memory providers use it.</summary>
    static async Task<(Harness H, InMemoryForge Forge, RepoRef Repo)> StartAsync(string name, string policy = "")
    {
        var world = $"it-{name}-{Guid.NewGuid():N}";
        var forge = InMemoryForge.Named(world);
        var repo = forge.CreateRepository(Repo, new Dictionary<string, string> { ["feature-flow.yaml"] = Manifest, ["src/App.cs"] = "class App {}\n" });
        forge.Protect(repo, new BranchProtection("main", true, true, 1, [], false));
        var client = EditingAgentRuntime.SandboxClient(Directory.CreateTempSubdirectory("ff-client-").FullName);
        var h = await Harness.StartAsync(name, autoApprove: true, omitTypes: ["sandbox", "vcs", "code_host", "ci"],
            providers: string.Join("\n", new[] { "vcs", "code_host", "ci", "sandbox" }.Select(t => $"  {t}: {{ instances: {{ memory: {{ world: {world} }} }} }}")),
            agentRuntime: $"{{ default: editing, dry_run: recording, instances: {{ editing: {{ provider: editing, script: script.yaml, world: {world} }}, recording: {{ folder: recorded }} }} }}",
            installation: $"agent_worker: {{ sandbox_client: \"{client}\" }}",
            policy: policy,
            factories: [EditingAgentRuntime.Factory]);
        // The sandbox the engine composed for this world: tests pass with a TRX, analysis is clean.
        var sandbox = MemorySandbox.Named(world, () => throw new InvalidOperationException("created by the host"));
        sandbox.On(new SandboxRule("dotnet test", Files: new Dictionary<string, string> { ["tests/Unit/unit.trx"] = Trx }));
        sandbox.On(new SandboxRule("dotnet build", Files: new Dictionary<string, string> { ["analysis.sarif"] = Sarif }));
        return (h, forge, repo);
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
        Assert.StartsWith("implement execution 1", forge.ReadFile(repo, main, "work/implement.txt"), StringComparison.Ordinal);
        Assert.Contains($"Feature-Flow-Merge: {id}/deliver.merge", merged.Message, StringComparison.Ordinal);
        Assert.Equal(forge.GetCommit(repo, forge.Resolve(repo, "ff/ticket/T-1")!).Files, merged.Files);

        // The pull request was opened into main and, after the squash, closed with a link to the commit.
        var pr = new PullRequestRef(repo, 1);
        var state = (await new InMemoryCodeHost(forge).GetPullRequestAsync(pr, TestContext.Current.CancellationToken)).Unwrap();
        Assert.Equal(("main", "ff/ticket/T-1", PullRequestStates.Closed), (state.Base, state.Head, state.State));
        Assert.Contains(main, forge.Comments(pr).Single(), StringComparison.Ordinal);

        // The path: engine actions, not the stand-in, made these steps' outcomes.
        string[] expected = ["baseline.tests", "baseline.analysis", "plan.plan", "approve_spec", "implement", "verify.tests", "verify.analysis",
            "deliver.open_pr", "deliver.checks", "deliver.catch_up", "deliver.merge"];
        var path = Harness.StartedSteps(view);
        Assert.Equal(expected, path.Where(expected.Contains).Distinct().ToArray());
        var trace = Trace(view);
        Assert.Contains(trace, d => d.Contains("tier unit at", StringComparison.Ordinal) && d.Contains("2 passed, 0 failed", StringComparison.Ordinal));
        Assert.Contains(trace, d => d.Contains("pull request acme/api#1 from ff/ticket/T-1 into main", StringComparison.Ordinal));
        Assert.Contains(trace, d => d.StartsWith("merged ", StringComparison.Ordinal) && d.Contains("into main", StringComparison.Ordinal));
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
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
        Assert.StartsWith("implement execution 1", forge.ReadFile(repo, landed, "work/implement.txt"), StringComparison.Ordinal);
        Assert.Equal("the epic's work\n", forge.ReadFile(repo, landed, "epic.txt"));
        Assert.Equal(main, forge.Resolve(repo, "main"));
        var state = (await new InMemoryCodeHost(forge).GetPullRequestAsync(new PullRequestRef(repo, 1), TestContext.Current.CancellationToken)).Unwrap();
        Assert.Equal(("ff/epic/E-1", "ff/ticket/T-4"), (state.Base, state.Head));
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

    static List<string> Trace(JsonNode view) =>
        view["timeline"]!.AsArray().Select(e => e!["detail"]?.ToString() ?? "").ToList();
}
