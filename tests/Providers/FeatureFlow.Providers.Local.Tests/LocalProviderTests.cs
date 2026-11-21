using System.Text;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local.Tests;

/// <summary>The local providers of the solo profile (docs/07 section 6) and the M3 fakes.</summary>
public sealed class LocalProviderTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-local-" + Guid.NewGuid().ToString("N"));

    public LocalProviderTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<string> Text(IArtifactStoreProvider store, ArtifactRef reference)
    {
        await using var stream = (await store.GetAsync(reference, Ct)).Unwrap();
        return await new StreamReader(stream).ReadToEndAsync(Ct);
    }

    [Fact]
    public async Task The_artifact_store_is_content_addressed()
    {
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var a = (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes("hello")), Ct)).Unwrap();
        var b = (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes("hello")), Ct)).Unwrap();
        var c = (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes("other")), Ct)).Unwrap();

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.Equal("sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", a.Id);
        Assert.Equal("hello", await Text(store, a));
        Assert.Equal("not_found", (await store.GetAsync(new ArtifactRef("sha256:" + new string('0', 64)), Ct)).Error!.Reason);
        Assert.Equal("not_found", (await store.GetAsync(new ArtifactRef("../../etc/passwd"), Ct)).Error!.Reason);
    }

    [Fact]
    public async Task The_record_store_keeps_runs_by_key_and_closes_waits_without_losing_them()
    {
        var store = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        var now = DateTimeOffset.UnixEpoch;
        await store.AppendAsync(new RecordBatch(
            [new RunRecord("r1", null, "std:templates/ticket_delivery", "starting", "me", ["acme/api"], false, now, null),
             new RunRecord("r1.kid.1", "r1", "std:templates/ticket_delivery", "running", "me", [], false, now, null)],
            [new WaitRecord("r1", "gate-1", "gate", "approve_spec", "ticket.spec_approval", "gate", ["approved", "rejected"], true, now)],
            [new TimelineRecord("r1", 1, "started", "plan", null, now), new TimelineRecord("r1", 2, "completed", "plan", "planned", now)]), Ct);
        await store.AppendAsync(new RecordBatch(
            [new RunRecord("r1", null, "std:templates/ticket_delivery", "completed", "me", ["acme/api"], false, now.AddMinutes(1), null)],
            [new WaitRecord("r1", "gate-1", "", "", "", "", [], false, now.AddMinutes(1))],
            [new TimelineRecord("r1", 2, "completed", "plan", "a retried append adds nothing", now)]), Ct);

        var p = new Dictionary<string, string> { ["run_id"] = "r1" };
        Assert.Equal("completed", (await store.QueryAsync<RunRecord>(Queries.Run, p, Ct)).Unwrap().Single().Status);
        Assert.Equal("r1.kid.1", (await store.QueryAsync<RunRecord>(Queries.Runs, new Dictionary<string, string> { ["parent_run_id"] = "r1" }, Ct)).Unwrap().Single().RunId);
        var wait = (await store.QueryAsync<WaitRecord>(Queries.Waits, p, Ct)).Unwrap().Single();
        Assert.Equal((false, "gate", "ticket.spec_approval"), (wait.Open, wait.Kind, wait.DecisionType));
        Assert.Empty((await store.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string>(p) { ["open"] = "true" }, Ct)).Unwrap());
        var timeline = (await store.QueryAsync<TimelineRecord>(Queries.Timeline, p, Ct)).Unwrap();
        Assert.Equal(["started", "completed"], timeline.Select(t => t.Kind));
        Assert.Equal("planned", timeline[1].Detail);
        await Assert.ThrowsAsync<ArgumentException>(() => store.QueryAsync<WaitRecord>(Queries.Run, p, Ct));
    }

    [Fact]
    public async Task File_secrets_resolve_by_name()
    {
        var path = Path.Combine(folder, "secrets.json");
        await File.WriteAllTextAsync(path, """{ "api_token": "t0k3n" }""", Ct);
        var secrets = new FileSecrets(path);
        Assert.Equal("t0k3n", (await secrets.ResolveAsync("secrets/api_token", Ct)).Unwrap());
        Assert.Equal("not_found", (await secrets.ResolveAsync("secrets/missing", Ct)).Error!.Reason);
    }

    static readonly StepContract Contract = new(
        [new OutcomeSpec("implemented", "success"), new OutcomeSpec("blocked", "blocked")],
        new Dictionary<string, string> { ["commits"] = "number" },
        [new ArtifactSpec("implemented", "claims")],
        new Dictionary<string, string> { ["commits"] = "0" });

    static StepIdentity Step(int execution, string step = "implement") => new("run-1", "ticket_delivery", step, execution, $"run-1/{step}/{execution}");

    async Task<StepReport> RunAgentAsync(IAgentRuntimeProvider runtime, StepIdentity step)
    {
        var session = (await runtime.StartAsync(SandboxHandle.None, new AgentTask(step, "implementer", null, new Dictionary<string, string>(), Contract), new AgentLimits(null, 2), Ct)).Unwrap();
        while (!(await runtime.GetProgressAsync(session, Ct)).Unwrap().Finished)
            await Task.Delay(20, Ct);
        return (await runtime.GetResultAsync(session, Ct)).Unwrap().Report!;
    }

    [Fact]
    public async Task The_scripted_runtime_reports_its_visits_and_otherwise_the_first_success()
    {
        var script = Path.Combine(folder, "script.yaml");
        await File.WriteAllTextAsync(script, """
            api_version: feature-flow/v1
            kind: fake_script
            steps:
              "ticket_delivery:implement":
                - { outcome: blocked, delay: 50ms }
                - { outcome: implemented, outputs: { commits: 2 }, artifacts: { claims: "claimed" } }
            """, Ct);
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var runtime = new ScriptedAgentRuntime(FakeScript.Read(script), () => store);

        Assert.Equal("blocked", (await RunAgentAsync(runtime, Step(1))).Outcome);
        var second = await RunAgentAsync(runtime, Step(2));
        Assert.Equal(("implemented", "2"), (second.Outcome, second.Outputs["commits"]));
        Assert.Equal("claimed", await Text(store, new ArtifactRef(second.Artifacts["claims"])));
        Assert.Equal("implemented", (await RunAgentAsync(runtime, Step(3))).Outcome);

        var unscripted = await RunAgentAsync(runtime, Step(1, "other"));
        Assert.Equal(("implemented", "0"), (unscripted.Outcome, unscripted.Outputs["commits"]));
    }

    [Fact]
    public async Task A_scripted_artifact_names_the_steps_inputs_and_reports_their_values()
    {
        var script = Path.Combine(folder, "script.yaml");
        await File.WriteAllTextAsync(script, """
            api_version: feature-flow/v1
            kind: fake_script
            steps: { implement: { artifacts: { claims: { criterion: "AC-${ticket}", size: "${size}", other: "${unknown}", file: "work/${run_id}/${step_id}.txt" } } } }
            """, Ct);
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var runtime = new ScriptedAgentRuntime(FakeScript.Read(script), () => store);
        var task = new AgentTask(Step(1), "implementer", null, new Dictionary<string, string> { ["ticket"] = "\"T-1\"", ["size"] = "3" }, Contract);

        var session = (await runtime.StartAsync(SandboxHandle.None, task, new AgentLimits(null, 2), Ct)).Unwrap();
        var report = (await runtime.GetResultAsync(session, Ct)).Unwrap().Report!;

        var text = await Text(store, new ArtifactRef(report.Artifacts["claims"]));
        Assert.Contains("AC-T-1", text);
        Assert.Contains("\"size\": \"3\"", text);
        Assert.Contains("${unknown}", text);
        Assert.Contains("work/run-1/implement.txt", text);
    }

    [Fact]
    public async Task A_scripted_infrastructure_error_fails_the_call_the_given_number_of_times_then_goes_on()
    {
        var script = Path.Combine(folder, "script.yaml");
        await File.WriteAllTextAsync(script, """
            api_version: feature-flow/v1
            kind: fake_script
            steps: { implement: { error: infrastructure, times: 2 } }
            """, Ct);
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var runtime = new ScriptedAgentRuntime(FakeScript.Read(script), () => store);
        var task = new AgentTask(Step(1), "implementer", null, new Dictionary<string, string>(), Contract);
        Assert.Equal(ProviderErrorClass.Infrastructure, (await runtime.StartAsync(SandboxHandle.None, task, new AgentLimits(null, 2), Ct)).Error!.Class);
        Assert.Equal(ProviderErrorClass.Infrastructure, (await runtime.StartAsync(SandboxHandle.None, task, new AgentLimits(null, 2), Ct)).Error!.Class);
        Assert.Equal("implemented", (await RunAgentAsync(runtime, Step(1))).Outcome);
    }

    [Fact]
    public async Task The_recording_runtime_writes_each_step_it_is_given_and_reports_the_first_success()
    {
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var runtime = new RecordingAgentRuntime(new Recorder(Path.Combine(folder, "recorded"), () => store));
        var session = (await runtime.StartAsync(SandboxHandle.None, new AgentTask(Step(1), "implementer", null, new Dictionary<string, string> { ["ticket"] = "\"T-1\"" }, Contract),
            new AgentLimits(null, 2), Ct)).Unwrap();
        Assert.Equal("implemented", (await runtime.GetResultAsync(session, Ct)).Unwrap().Report!.Outcome);
        var line = Assert.Single(await File.ReadAllLinesAsync(Path.Combine(folder, "recorded", "run-1.jsonl"), Ct));
        Assert.Contains("\"what\":\"implementer\"", line);
        Assert.Contains("\"ticket\":", line);
    }

    [Fact]
    public async Task The_scripted_runtime_acts_through_the_sandbox_provider_of_a_sandbox_its_world_does_not_hold()
    {
        // A sandbox provider the runtime's world does not hold stands for the Kubernetes or Docker one: the edit, the
        // listing of the checkout and the artifacts go through its calls (LOG A2-3b3a).
        var store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        var records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        var sandboxes = new MemorySandbox(() => store).On(new SandboxRule("^git ls-files", Output: "README.md\nwork/run-1/implement.txt\n"));
        var d = new Model.DiagnosticBag();
        var config = Model.ConfigurationReader.ReadInstallation($$"""
            api_version: feature-flow/v1
            kind: installation
            development: true
            temporal: { address: "localhost:7233", namespace: default, task_queue: q }
            providers:
              agent_runtime: { instances: { scripted: { world: test-{{Guid.NewGuid():N}} } } }
            """, "installation.yaml", d)!;
        Assert.DoesNotContain(d, x => x.Blocks);
        var runtime = (IAgentRuntimeProvider)LocalProviders.Factories.Single(f => f is { Type: "agent_runtime", Kind: "scripted" })
            .Create(new ProviderContext(config.Providers["agent_runtime"].Instances["scripted"], folder,
                type => type switch { "artifact_store" => store, "record_store" => records, "sandbox" => sandboxes, _ => null }));
        var sandbox = (await sandboxes.CreateAsync(new SandboxSpec("run-1/implement/1", "/ff/home", new Dictionary<string, string>(), []), Ct)).Unwrap();
        var task = new AgentTask(Step(1), "implementer", null, new Dictionary<string, string>(), Contract) { Paths = AgentPaths.Default, WriteAccess = "branch" };

        var session = (await runtime.StartAsync(sandbox, task, new AgentLimits(null, 2), Ct)).Unwrap();
        var report = (await runtime.GetResultAsync(session, Ct)).Unwrap().Report!;

        Assert.Equal("implement execution 1, session 1\n", sandboxes.ReadFile(sandbox, "/ff/work/work/run-1/implement.txt"));
        Assert.Equal("claims.json", report.Artifacts["claims"]);
        Assert.Contains("run-1/implement/1/claims", sandboxes.ReadFile(sandbox, "/ff/out/claims.json"));
        var hooks = (await records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = "run-1/implement/1" }, Ct)).Unwrap();
        Assert.Equal(["Read", "Read /ff/work/README.md", "Read /ff/work/work/run-1/implement.txt"], hooks.Select(h => h.Detail));
    }

    [Fact]
    public void A_described_only_provider_offers_every_capability_of_its_type()
    {
        var names = new DescribedOnlyProvider("code_host").Describe().CapabilityNames().ToList();
        Assert.Contains("code_host", names);
        Assert.Contains("code_host.pull_request", names);
        Assert.Contains("code_host.check_status", names);
    }
}
