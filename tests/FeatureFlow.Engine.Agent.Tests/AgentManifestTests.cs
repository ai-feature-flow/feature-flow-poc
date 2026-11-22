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
    sealed class EditingRuntime(InMemoryForge forge, IAgentRuntimeProvider inner, IReadOnlyDictionary<string, string> edits, Action<SandboxHandle>? atStart = null,
        Func<AgentResult, AgentResult>? ending = null, List<string>? tokens = null)
        : IAgentRuntimeProvider
    {
        public ProviderDescription Describe() => inner.Describe();

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

        public Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken)
        {
            atStart?.Invoke(sandbox);
            tokens?.Add(task.Engine!.StepToken);
            if (forge.WorkingFiles(new WorkingCopyRef(sandbox, task.Paths!.Checkout)) is { } files)
                foreach (var (path, content) in edits)
                    files[path] = content;
            return inner.StartAsync(sandbox, task, limits, cancellationToken);
        }

        public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
            inner.ResumeAsync(sandbox, sessionRef, cancellationToken);

        public Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken) => inner.GetProgressAsync(session, cancellationToken);

        public async Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken)
        {
            var result = await inner.GetResultAsync(session, cancellationToken);
            return ending is null || !result.Ok ? result : ProviderResult<AgentResult>.Success(ending(result.Value!));
        }

        public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken) => inner.StopAsync(session, cancellationToken);
    }

    async Task<StepOutcome> RunAsync(IReadOnlyDictionary<string, string> edits, bool codeHost = true, string step = "implement", string branch = Branch,
        ITrackerProvider? tracker = null, string? trackerIssue = null, Action<SandboxHandle>? atStart = null, bool dryRun = false, string? roleRuntime = null,
        string? runContext = null, Func<AgentResult, AgentResult>? ending = null, string? causes = null, Model.Plans.PlanningGrant? planning = null)
    {
        var client = Path.Combine(folder, "ff-sandbox");
        await File.WriteAllTextAsync(client, "a stand-in for the sandbox client", TestContext.Current.CancellationToken);
        var records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        var runtime = new EditingRuntime(forge, new ScriptedAgentRuntime(FakeScript.Empty, () => store), edits, atStart, ending, startedTokens);
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["vcs"] = One(new InMemoryVcs(forge)),
            ["sandbox"] = One(sandbox),
            // A second runtime, which a role may map to; a dry run uses its dry_run instance in its place (LOG 384).
            ["agent_runtime"] = new Dictionary<string, IProvider>
            {
                ["one"] = runtime,
                ["real"] = new EditingRuntime(forge, new ScriptedAgentRuntime(FakeScript.Empty, () => store), new Dictionary<string, string>(), _ => realStarted = true, tokens: startedTokens),
            },
            ["artifact_store"] = One(store),
            ["record_store"] = One(records),
        };
        if (codeHost)
            instances["code_host"] = One(new InMemoryCodeHost(forge));
        if (tracker is not null)
            instances["tracker"] = One(tracker);
        var defaults = instances.Keys.ToDictionary(k => k, _ => "one");
        var providers = new ProviderSet(instances, defaults, dryRun ? defaults : new Dictionary<string, string>());
        var activities = new AgentActivities(providers, StepTokens.FromSecret("test"),
            new AgentWorkerOptions(["http://127.0.0.1:9"], "http://127.0.0.1:9/model", TimeSpan.FromSeconds(30), client, TimeSpan.FromMilliseconds(20)));
        var contract = new StepContract([new OutcomeSpec("implemented", "success")], new Dictionary<string, string> { ["commits"] = "number" }, [],
            new Dictionary<string, string> { ["commits"] = "0" });
        var spec = new AgentStepSpec("write", ["read", "edit"], null, null, [], 2, 0, TimeSpan.FromHours(1), true, "commit", "ticket",
            new Dictionary<string, string> { [repo.FullName] = branch }, new Dictionary<string, string>(), new Dictionary<string, string>(), "wf", "lock", trackerIssue, Runtime: roleRuntime, RunContext: runContext, Causes: causes, Planning: planning);
        var call = new StepCall(new StepIdentity("run-1", "ticket_delivery", step, 1, $"run-1/{step}/1"), "agent", "std:roles/implementer",
            new Dictionary<string, string>(), contract, dryRun, TimeSpan.FromMinutes(5), spec);
        return await new ActivityEnvironment().RunAsync(() => activities.RunAgentStepAsync(call));
    }

    static IReadOnlyDictionary<string, IProvider> One(IProvider provider) => new Dictionary<string, IProvider> { ["one"] = provider };

    string? Work => forge.Resolve(repo, "refs/heads/" + Branch);

    [Fact]
    public async Task A_failed_attempts_prompt_and_transcript_are_indexed_for_people_to_read()
    {
        var ct = TestContext.Current.CancellationToken;
        var transcript = (await store.PutAsync(new MemoryStream("the session's transcript"u8.ToArray()), ct)).Unwrap().Id;

        // The session ends without a report: the attempt fails, and its transcript is the one a person most needs (LOG 393).
        var outcome = await RunAsync(new Dictionary<string, string>(),
            ending: r => r with { Report = null, End = SessionEnd.Failed, Detail = "gave up", Transcript = transcript });

        Assert.Equal(StepOutcome.WorkFailed, outcome.Kind);
        var indexed = (await new SqliteRecordStore(Path.Combine(folder, "records.db"))
            .QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = "run-1" }, ct)).Unwrap();
        Assert.Equal([("prompt:1", "prompt", "implement", 1), ("transcript:1", "transcript", "implement", 1)],
            indexed.Select(a => (a.Name, a.Type!, a.StepId, a.Execution)).Order());
        Assert.Equal(transcript, indexed.Single(a => a.Name == "transcript:1").Ref);
    }

    [Fact]
    public async Task Setup_runs_before_the_agent_and_after_changes_runs_after_it_committed_and_is_committed_with_the_step()
    {
        sandbox.On(new SandboxRule("dotnet format", Edits: new Dictionary<string, string?> { ["src/A.cs"] = "class A { }" }));

        var outcome = await RunAsync(new Dictionary<string, string> { ["src/A.cs"] = "class A{int x;}", ["src/B.cs"] = "class B{}" });

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        Assert.Null(sandbox.Specs.Single().Image);
        var commands = sandbox.Commands.Select(c => c.Command).Where(c => c.Contains("dotnet", StringComparison.Ordinal)).ToList();
        Assert.Equal(["sh -c dotnet tool restore", "sh -c dotnet format"], commands);
        Assert.All(sandbox.Commands.Where(c => c.Command.Contains("dotnet", StringComparison.Ordinal)), c => Assert.Equal(AgentPaths.Default.Checkout, c.Directory));
        // The work branch holds the agent's change and the formatter's, in two commits of the step.
        Assert.Equal("class A { }", forge.ReadFile(repo, Work!, "src/A.cs"));
        Assert.Equal("class B{}", forge.ReadFile(repo, Work!, "src/B.cs"));
        Assert.Equal(2L, JsonSerializer.Deserialize<long>(outcome.Report!.Outputs["commits"]));
        Assert.Contains(outcome.Events!, e => e.StartsWith("committed after_changes", StringComparison.Ordinal));
    }

    /// <summary>F4: the agent's sandbox is made from the manifest's <c>environment.image</c>; with none, the sandbox's own (LOG 440).</summary>
    [Fact]
    public async Task The_agents_sandbox_is_made_from_the_manifests_environment_image()
    {
        forge.Commit(repo, "refs/heads/main", new Dictionary<string, string?> { ["feature-flow.yaml"] = Manifest + "\nenvironment: { image: acme/api-sandbox:1 }\n" });

        Assert.Equal(StepOutcome.Reported, (await RunAsync(new Dictionary<string, string>())).Kind);

        Assert.Equal("acme/api-sandbox:1", sandbox.Specs.Single().Image);
    }

    [Fact]
    public async Task The_runs_ticket_is_read_from_the_tracker_and_staged_for_the_agent()
    {
        var tracker = new InMemoryTracker();
        var issue = (await tracker.CreateAsync(new IssueSpec("Add totals", "Invoices show a total."), "seed", TestContext.Current.CancellationToken)).Unwrap();
        string? ticket = null, manifest = null;

        var outcome = await RunAsync(new Dictionary<string, string>(), tracker: tracker, trackerIssue: issue.Key, atStart: handle =>
        {
            ticket = sandbox.ReadFile(handle, "/ff/inputs/ticket.md");
            manifest = sandbox.ReadFile(handle, AgentPaths.Default.InputsManifest);
        });

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        Assert.StartsWith("# Add totals\n\nInvoices show a total.", ticket, StringComparison.Ordinal);
        Assert.Equal(issue.Key, JsonDocument.Parse(manifest!).RootElement.GetProperty("ticket").GetProperty("key").GetString());
    }

    [Fact]
    public async Task A_ticket_the_tracker_does_not_have_stops_the_step_for_a_person()
    {
        var outcome = await RunAsync(new Dictionary<string, string>(), tracker: new InMemoryTracker(), trackerIssue: "MEM-404");

        Assert.Equal(StepOutcome.CannotAnswer, outcome.Kind);
        Assert.Contains("MEM-404 does not exist", outcome.Reason, StringComparison.Ordinal);
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

    /// <summary>The step's token names the instance it runs on, as resolved: it picks the step's hook reading and model proxy.</summary>
    [Fact]
    public async Task A_role_mapped_runtime_runs_the_step_and_in_a_dry_run_the_dry_run_runtime_stands_in_for_it()
    {
        Assert.Equal(StepOutcome.Reported, (await RunAsync(new Dictionary<string, string>(), dryRun: true, roleRuntime: "real")).Kind);
        Assert.False(realStarted);

        Assert.Equal(StepOutcome.Reported, (await RunAsync(new Dictionary<string, string>(), roleRuntime: "real")).Kind);
        Assert.True(realStarted);
        Assert.Equal(["one", "real"], startedTokens.Select(t => StepTokens.FromSecret("test").Check(t)!.Runtime));
    }

    readonly List<string> startedTokens = [];
    bool realStarted;

    [Fact]
    public async Task The_run_context_is_written_into_the_stored_prompt_before_the_task()
    {
        const string context = "## Run context\n\n- ticket.spec_approval at approve_spec: rejected by ann: requirements omit the audit log\n";
        const string causes = "## Why this step runs again\n\n- record_fixes ended invalid: F-1: this step may set only fixed, open, not 'declined'\n";

        var outcome = await RunAsync(new Dictionary<string, string>(), runContext: context, causes: causes);

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        var reference = Assert.Single(outcome.Events!, e => e.StartsWith("prompt of session 1: ", StringComparison.Ordinal))["prompt of session 1: ".Length..];
        await using var stream = (await store.GetAsync(new ArtifactRef(reference), TestContext.Current.CancellationToken)).Unwrap();
        var prompt = await new StreamReader(stream).ReadToEndAsync(TestContext.Current.CancellationToken);
        Assert.Contains(context.TrimEnd(), prompt, StringComparison.Ordinal);
        Assert.Contains(causes.TrimEnd(), prompt, StringComparison.Ordinal);
        Assert.True(prompt.IndexOf("## This step", StringComparison.Ordinal) < prompt.IndexOf("## Why this step runs again", StringComparison.Ordinal));
        Assert.True(prompt.IndexOf("## Why this step runs again", StringComparison.Ordinal) < prompt.IndexOf("## Run context", StringComparison.Ordinal));
        Assert.EndsWith("Do the work of the std:roles/implementer role.", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("## Planning authority", prompt, StringComparison.Ordinal);
    }

    /// <summary>A step with <c>planning</c> is told its authority after the run context (design note s5), with the parts its grant has.</summary>
    [Fact]
    public async Task A_step_with_planning_is_told_its_authority_in_the_prompt()
    {
        const string context = "## Run context\n\n- something learned\n";
        var self = await PromptOfAsync(await RunAsync(new Dictionary<string, string>(), runContext: context, planning: new Model.Plans.PlanningGrant(["self"], "required")));
        var both = await PromptOfAsync(await RunAsync(new Dictionary<string, string>(), step: "plan", planning: new Model.Plans.PlanningGrant(["self", "parent"], "auto")));

        Assert.True(self.IndexOf("## Run context", StringComparison.Ordinal) < self.IndexOf("## Planning authority", StringComparison.Ordinal));
        Assert.True(self.IndexOf("## Planning authority", StringComparison.Ordinal) < self.IndexOf("Do the work of", StringComparison.Ordinal));
        Assert.Contains("Every proposal is reviewed by a person before it applies.", self, StringComparison.Ordinal);
        Assert.Contains("the run continues at its `resume_at`", self, StringComparison.Ordinal);
        Assert.DoesNotContain("the run that started", self, StringComparison.Ordinal);
        Assert.DoesNotContain("Every proposal is reviewed", both, StringComparison.Ordinal);
        Assert.Contains("this run's plan and to the plan of the run that started it", both, StringComparison.Ordinal);
        Assert.Contains("A change to the parent's plan goes to the parent run", both, StringComparison.Ordinal);
    }

    async Task<string> PromptOfAsync(StepOutcome outcome)
    {
        var reference = Assert.Single(outcome.Events!, e => e.StartsWith("prompt of session 1: ", StringComparison.Ordinal))["prompt of session 1: ".Length..];
        await using var stream = (await store.GetAsync(new ArtifactRef(reference), TestContext.Current.CancellationToken)).Unwrap();
        return await new StreamReader(stream).ReadToEndAsync(TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// An artifact lives only in the output folder (LOG 514): a step whose commits add a file at the checkout path of an
    /// artifact it reported fails with the reason, and the work branch does not move; one that commits other files passes.
    /// </summary>
    [Theory]
    [InlineData("claims.yaml", false)]
    [InlineData("src/B.cs", true)]
    public async Task A_step_that_commits_a_file_it_reported_as_an_artifact_fails(string committed, bool passes)
    {
        var outcome = await RunAsync(new Dictionary<string, string> { [committed] = "claims: []" },
            atStart: handle => sandbox.WriteFile(handle, $"{AgentPaths.Default.Output}/claims.yaml", "claims: []"),
            ending: r => r with { Report = new StepReport("implemented", new Dictionary<string, string>(), new Dictionary<string, string> { ["claims"] = "claims.yaml" }) });

        if (passes)
        {
            Assert.Equal(StepOutcome.Reported, outcome.Kind);
            Assert.Equal("claims: []", forge.ReadFile(repo, Work!, committed));
            return;
        }

        Assert.Equal(StepOutcome.WorkFailed, outcome.Kind);
        Assert.Contains("add claims.yaml to the checkout, which it also reported as an artifact", outcome.Reason, StringComparison.Ordinal);
        Assert.Null(forge.ReadFile(repo, Work!, "claims.yaml"));
    }

    static AgentResult ReportsClaims(AgentResult r) =>
        r with { Report = new StepReport("implemented", new Dictionary<string, string>(), new Dictionary<string, string> { ["claims"] = "claims.yaml" }) };

    /// <summary>A file of an artifact's name that the step brings in by merging its parent is the parent's, not the step's (LOG 514).</summary>
    [Fact]
    public async Task A_step_that_merges_its_parent_which_has_a_file_of_an_artifacts_name_passes()
    {
        forge.Commit(repo, "refs/heads/" + Branch, new Dictionary<string, string?> { ["src/W.cs"] = "class W{}" });
        var parent = forge.Commit(repo, "refs/heads/main", new Dictionary<string, string?> { ["claims.yaml"] = "the parent's file" });

        var outcome = await RunAsync(new Dictionary<string, string> { ["src/B.cs"] = "class B{}" },
            atStart: handle =>
            {
                sandbox.WriteFile(handle, $"{AgentPaths.Default.Output}/claims.yaml", "claims: []");
                forge.MergeIntoWorkingCopy(new WorkingCopyRef(handle, AgentPaths.Default.Checkout), parent);
            },
            ending: ReportsClaims);

        Assert.True(StepOutcome.Reported == outcome.Kind, outcome.Reason);
        Assert.Equal("the parent's file", forge.ReadFile(repo, Work!, "claims.yaml"));
    }

    /// <summary>A step that deletes a file of an artifact's name, an earlier attempt's, is not refused, with or without a code host (LOG 514).</summary>
    [Fact]
    public async Task A_step_that_deletes_a_committed_artifact_passes_without_a_code_host()
    {
        forge.Commit(repo, "refs/heads/" + Branch, new Dictionary<string, string?> { ["claims.yaml"] = "claims: []" });

        var outcome = await RunAsync(new Dictionary<string, string> { ["src/B.cs"] = "class B{}" }, codeHost: false,
            atStart: handle =>
            {
                sandbox.WriteFile(handle, $"{AgentPaths.Default.Output}/claims.yaml", "claims: []");
                forge.WorkingFiles(new WorkingCopyRef(handle, AgentPaths.Default.Checkout))!.Remove("claims.yaml");
            },
            ending: ReportsClaims);

        Assert.True(StepOutcome.Reported == outcome.Kind, outcome.Reason);
        Assert.Null(forge.ReadFile(repo, Work!, "claims.yaml"));
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
