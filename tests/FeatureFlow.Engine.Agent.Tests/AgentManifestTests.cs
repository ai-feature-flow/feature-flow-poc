using System.Text.Json;
using FeatureFlow.Engine.Agent;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Agent.Tests;

/// <summary>
/// The agent step's M5 changes on the in-memory world, with no container: the parent branch read from the code host
/// (M4 follow-up 3), and the manifest's <c>setup</c> and <c>after_changes</c> commands run in the sandbox (M4
/// follow-up 1, docs/04 section 3.1 steps 1 and 6).
/// </summary>
public sealed class AgentManifestTests : IAsyncDisposable
{
    const string Branch = "ff/ticket/T-1";
    const string Manifest = """
        api_version: feature-flow/v1
        kind: repository
        setup: { commands: ["dotnet tool restore"] }
        after_changes: { commands: ["dotnet format"] }
        """;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-agent-manifest-" + Guid.NewGuid().ToString("N"));
    readonly InMemoryForge forge = new();
    readonly RepoRef repo;
    readonly FileSystemArtifactStore store;
    readonly MemorySandbox sandbox;

    public AgentManifestTests()
    {
        Directory.CreateDirectory(folder);
        repo = forge.CreateRepository("acme/api", new Dictionary<string, string> { ["feature-flow.yaml"] = Manifest, ["src/A.cs"] = "class A{}" });
        store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        sandbox = new MemorySandbox(() => store, forge);
    }

    /// <summary>A scripted runtime that edits the checkout as an agent would, when it is given <paramref name="edits"/>.</summary>
    sealed class EditingRuntime(InMemoryForge forge, IAgentRuntimeProvider inner, IReadOnlyDictionary<string, string> edits) : IAgentRuntimeProvider
    {
        public ProviderDescription Describe() => inner.Describe();

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

        public Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken)
        {
            if (forge.WorkingFiles(new WorkingCopyRef(sandbox, task.Paths!.Checkout)) is { } files)
                foreach (var (path, content) in edits)
                    files[path] = content;
            return inner.StartAsync(sandbox, task, limits, cancellationToken);
        }

        public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
            inner.ResumeAsync(sandbox, sessionRef, cancellationToken);

        public Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken) => inner.GetProgressAsync(session, cancellationToken);

        public Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken) => inner.GetResultAsync(session, cancellationToken);

        public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken) => inner.StopAsync(session, cancellationToken);
    }

    async Task<StepOutcome> RunAsync(IReadOnlyDictionary<string, string> edits, bool codeHost = true, string step = "implement", string branch = Branch)
    {
        var client = Path.Combine(folder, "ff-sandbox");
        await File.WriteAllTextAsync(client, "a stand-in for the sandbox client", TestContext.Current.CancellationToken);
        var records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        var runtime = new EditingRuntime(forge, new ScriptedAgentRuntime(FakeScript.Empty, () => store), edits);
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["vcs"] = One(new InMemoryVcs(forge)),
            ["sandbox"] = One(sandbox),
            ["agent_runtime"] = One(runtime),
            ["artifact_store"] = One(store),
            ["record_store"] = One(records),
        };
        if (codeHost)
            instances["code_host"] = One(new InMemoryCodeHost(forge));
        var providers = new ProviderSet(instances, instances.Keys.ToDictionary(k => k, _ => "one"), new Dictionary<string, string>());
        var activities = new AgentActivities(providers, StepTokens.FromSecret("test"),
            new AgentWorkerOptions(["http://127.0.0.1:9"], "http://127.0.0.1:9/model", TimeSpan.FromSeconds(30), client, TimeSpan.FromMilliseconds(20)));
        var contract = new StepContract([new OutcomeSpec("implemented", "success")], new Dictionary<string, string> { ["commits"] = "number" }, [],
            new Dictionary<string, string> { ["commits"] = "0" });
        var spec = new AgentStepSpec("write", ["read", "edit"], null, null, [], 2, 0, TimeSpan.FromHours(1), true, "commit", "ticket",
            new Dictionary<string, string> { [repo.FullName] = branch }, new Dictionary<string, string>(), new Dictionary<string, string>(), "wf", "lock");
        var call = new StepCall(new StepIdentity("run-1", "ticket_delivery", step, 1, $"run-1/{step}/1"), "agent", "std:roles/implementer",
            new Dictionary<string, string>(), contract, false, TimeSpan.FromMinutes(5), spec);
        return await new ActivityEnvironment().RunAsync(() => activities.RunAgentStepAsync(call));
    }

    static IReadOnlyDictionary<string, IProvider> One(IProvider provider) => new Dictionary<string, IProvider> { ["one"] = provider };

    string? Work => forge.Resolve(repo, "refs/heads/" + Branch);

    [Fact]
    public async Task Setup_runs_before_the_agent_and_after_changes_runs_after_it_committed_and_is_committed_with_the_step()
    {
        sandbox.On(new SandboxRule("dotnet format", Edits: new Dictionary<string, string?> { ["src/A.cs"] = "class A { }" }));

        var outcome = await RunAsync(new Dictionary<string, string> { ["src/A.cs"] = "class A{int x;}", ["src/B.cs"] = "class B{}" });

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        var commands = sandbox.Commands.Select(c => c.Command).Where(c => c.Contains("dotnet", StringComparison.Ordinal)).ToList();
        Assert.Equal(["sh -c dotnet tool restore", "sh -c dotnet format"], commands);
        Assert.All(sandbox.Commands.Where(c => c.Command.Contains("dotnet", StringComparison.Ordinal)), c => Assert.Equal(AgentPaths.Default.Checkout, c.Directory));
        // The work branch holds the agent's change and the formatter's, in two commits of the step.
        Assert.Equal("class A { }", forge.ReadFile(repo, Work!, "src/A.cs"));
        Assert.Equal("class B{}", forge.ReadFile(repo, Work!, "src/B.cs"));
        Assert.Equal(2L, JsonSerializer.Deserialize<long>(outcome.Report!.Outputs["commits"]));
        Assert.Contains(outcome.Events!, e => e.StartsWith("committed after_changes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task After_changes_does_not_run_when_the_step_committed_nothing()
    {
        var outcome = await RunAsync(new Dictionary<string, string>());

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        Assert.Equal(["sh -c dotnet tool restore"], sandbox.Commands.Select(c => c.Command).Where(c => c.Contains("dotnet", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task A_failing_setup_command_fails_the_attempt_before_the_agent_starts()
    {
        sandbox.On(new SandboxRule("tool restore", Exit: 1, Output: "restore failed"));

        var outcome = await RunAsync(new Dictionary<string, string> { ["src/B.cs"] = "class B{}" });

        Assert.Equal(StepOutcome.WorkFailed, outcome.Kind);
        Assert.Contains("setup command 'dotnet tool restore' exited 1", outcome.Reason, StringComparison.Ordinal);
        Assert.Null(forge.ReadFile(repo, Work!, "src/B.cs"));
    }

    /// <summary>docs/06 section 1.1 (LOG 147): a configuration fault stops the agent step for a person, as it does an action.</summary>
    [Theory]
    [InlineData("unauthenticated")]
    [InlineData("forbidden")]
    public async Task A_refused_credential_stops_the_agent_step_for_a_person(string fault)
    {
        forge.Fail("vcs.Push", new ProviderError(fault, "GitHub refused the request"));

        var outcome = await RunAsync(new Dictionary<string, string> { ["src/B.cs"] = "class B{}" });

        Assert.Equal(StepOutcome.CannotAnswer, outcome.Kind);
        Assert.Contains("configuration fault", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains(fault, outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_parent_branch_is_the_default_branch_the_code_host_names()
    {
        var trunk = forge.Commit(repo, "trunk", new Dictionary<string, string?> { ["trunk.txt"] = "only on trunk" });
        forge.SetDefaultBranch(repo, "trunk");

        var outcome = await RunAsync(new Dictionary<string, string>());

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        Assert.Equal(trunk, Work);                                   // created from trunk, not main
    }

    [Fact]
    public async Task Without_a_code_host_the_parent_branch_stays_main_and_no_manifest_is_read()
    {
        forge.Commit(repo, "trunk", new Dictionary<string, string?> { ["trunk.txt"] = "only on trunk" });
        forge.SetDefaultBranch(repo, "trunk");

        var outcome = await RunAsync(new Dictionary<string, string>(), codeHost: false);

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        Assert.Equal(forge.Resolve(repo, "main"), Work);
        Assert.DoesNotContain(sandbox.Commands, c => c.Command.Contains("dotnet", StringComparison.Ordinal));
    }

    public ValueTask DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(folder, recursive: true);
        }
        catch (IOException)
        {
        }

        return ValueTask.CompletedTask;
    }
}
