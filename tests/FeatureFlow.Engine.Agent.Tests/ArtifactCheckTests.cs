using System.Text;
using System.Text.Json;
using FeatureFlow.Engine.Agent;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Agent.Tests;

/// <summary>
/// Report-time artifact checks with correction turns (docs/01 section 5.4, docs/04 section 3.1 step 5; LOG 246, 247): the
/// engine checks what an agent produced against its type when it reports, and a failure goes back to the same session
/// within the step's correction turns. The planner is scripted to write what M6's live haiku planners wrote (m6-results.md,
/// "Integration", seam 6: <c>owner</c> as a string), on the in-memory world with std's real library and validators.
/// </summary>
public sealed class ArtifactCheckTests : IAsyncDisposable
{
    const string Branch = "ff/project/invoicing";
    const string RequirementsType = "std:artifact_types/requirements";

    const string Valid = """
        schema: 1
        scope: { kind: project, id: invoicing }
        requirements:
          - id: R1
            title: Totals
            behaviour: Invoices show a total.
            supports_reason: The project's own goal
            owner: { kind: epic, id: billing }
        """;

    /// <summary>What M6's planners wrote (seam 6).</summary>
    static readonly string OwnerAsText = Valid.Replace("owner: { kind: epic, id: billing }", "owner: billing");

    /// <summary>Schema-valid, but breaks a rule only the type's validator states: ids unique within the document.</summary>
    static readonly string DuplicateIds = Valid + "\n" + Valid[Valid.IndexOf("  - id: R1", StringComparison.Ordinal)..];

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-artifact-check-" + Guid.NewGuid().ToString("N"));
    readonly InMemoryForge forge = new();
    readonly RepoRef repo;
    readonly FileSystemArtifactStore store;
    readonly MemorySandbox sandbox;
    readonly SqliteRecordStore records;
    readonly StepTokens tokens = StepTokens.FromSecret("test");

    public ArtifactCheckTests()
    {
        Directory.CreateDirectory(folder);
        repo = forge.CreateRepository("acme/api", new Dictionary<string, string> { ["README.md"] = "api" });
        store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        sandbox = new MemorySandbox(() => store, forge);
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(folder, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>One turn of the scripted planner: the file it writes (null: none), and whether it then calls <c>report</c>.</summary>
    sealed record Turn(string? Plan, bool Reports = true);

    /// <summary>
    /// A planner in a memory sandbox: each session (started, or continued with the engine's message) is one turn, which
    /// writes the plan to the output folder and calls the real <c>report</c> tool, as the sandbox client forwards it.
    /// </summary>
    sealed class PlannerRuntime(MemorySandbox sandboxes, AgentEndpointServices endpoint, IReadOnlyList<Turn> turns, bool file = false) : IAgentRuntimeProvider
    {
        const string Report = """{"outcome":"planned","summary":"Planned the project.","artifacts":{"requirements":"requirements.yaml"}}""";
        int next;
        string? reportFile;

        public int Started { get; private set; }
        public List<string> Continued { get; } = [];
        public List<string> Replies { get; } = [];

        public ProviderDescription Describe() => new("agent_runtime", "planner", "test", new NoCapabilities());

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

        public async Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken)
        {
            Started++;
            return await TurnAsync(sandbox, task, cancellationToken);
        }

        public async Task<ProviderResult<AgentSession>> ContinueAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, string message, CancellationToken cancellationToken)
        {
            Continued.Add(message);
            return await TurnAsync(sandbox, task, cancellationToken);
        }

        async Task<ProviderResult<AgentSession>> TurnAsync(SandboxHandle sandbox, AgentTask task, CancellationToken cancellationToken)
        {
            var turn = turns[Math.Min(next++, turns.Count - 1)];
            if (turn.Plan is { } plan)
                sandboxes.WriteFile(sandbox, $"{task.Paths!.Output}/requirements.yaml", plan);
            if (turn.Reports && file)
                // A runtime without the engine's tools writes report.json instead (docs/04 section 3.3).
                reportFile = Report;
            else if (turn.Reports)
            {
                var claims = endpoint.Tokens.Check(task.Engine!.StepToken)!;
                var report = JsonDocument.Parse("""
                    {"report":{"outcome":"planned","summary":"Planned the project.","artifacts":{"requirements":"requirements.yaml"}},"missing":[]}
                    """).RootElement;
                Replies.Add((await AgentEndpoint.ReportAsync(endpoint, claims, report, cancellationToken)).Message);
            }

            return ProviderResult<AgentSession>.Success(new AgentSession($"session-{next}", "planner"));
        }

        public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<AgentSession>.Failure("not_found", "no resume"));

        public Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<AgentProgress>.Success(new AgentProgress(true, 1)));

        public Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<AgentResult>.Success(new AgentResult(null, SessionEnd.Completed, ReportFile: reportFile)));

        public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    /// <summary>The run's lock over std, as run start writes it: every file by content hash, and the lock itself.</summary>
    async Task<string> LockAsync()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(root.FullName, "FeatureFlow.slnx")))
            root = root.Parent!;
        var std = FeatureFlow.Library.Library.Open(Path.Combine(root.FullName, "library", "std"), new DiagnosticBag())!;
        foreach (var (_, text) in std.Files.All)
            (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), CancellationToken.None)).Unwrap();
        var lockText = JsonSerializer.Serialize(new LibraryLock([LibraryLock.Pin(std)]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return (await store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(lockText)), CancellationToken.None)).Unwrap().Id;
    }

    async Task<(StepOutcome Outcome, PlannerRuntime Runtime)> RunAsync(IReadOnlyList<Turn> turns, bool validators = true, string type = RequirementsType, bool file = false)
    {
        var client = Path.Combine(folder, "ff-sandbox");
        await File.WriteAllTextAsync(client, "a stand-in for the sandbox client", TestContext.Current.CancellationToken);
        var endpoint = new AgentEndpointServices(tokens, records, new NoSignals(), null);
        var runtime = new PlannerRuntime(sandbox, endpoint, turns, file);
        var vcs = new InMemoryVcs(forge);
        var codeHost = new InMemoryCodeHost(forge);
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["vcs"] = One(vcs),
            ["sandbox"] = One(sandbox),
            ["agent_runtime"] = One(runtime),
            ["artifact_store"] = One(store),
            ["record_store"] = One(records),
            ["code_host"] = One(codeHost),
        };
        if (validators)
            instances["validator"] = One(new FeatureFlow.Providers.StdValidators.StdValidators(() => store, () => codeHost, () => vcs));
        var providers = new ProviderSet(instances, instances.Keys.ToDictionary(k => k, _ => "one"), new Dictionary<string, string>());
        var activities = new AgentActivities(providers, tokens,
            new AgentWorkerOptions(["http://127.0.0.1:9"], "http://127.0.0.1:9/model", TimeSpan.FromSeconds(30), client, TimeSpan.FromMilliseconds(20)));
        var contract = new StepContract([new OutcomeSpec("planned", "success"), new OutcomeSpec("blocked", "blocked")], new Dictionary<string, string>(),
            [new ArtifactSpec("planned", "requirements") { Type = type }], new Dictionary<string, string>());
        var spec = new AgentStepSpec("none", ["read", "search"], "Plan the project.", "std", [], 2, 0, TimeSpan.FromHours(1), false, "commit", "project",
            new Dictionary<string, string> { [repo.FullName] = Branch }, new Dictionary<string, string>(), new Dictionary<string, string>(), "wf", await LockAsync());
        var call = new StepCall(new StepIdentity("run-1", "project_delivery", "plan", 1, "run-1/plan/1"), "agent", "std:roles/planner",
            new Dictionary<string, string>(), contract, false, TimeSpan.FromMinutes(5), spec);
        var outcome = await new ActivityEnvironment().RunAsync(() => activities.RunAgentStepAsync(call));
        return (outcome, runtime);
    }

    sealed class NoSignals : IRunSignals
    {
        public Task QuestionOpenedAsync(string workflowId, FeatureFlow.Interpreter.Wire.QuestionSignal question, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    static IReadOnlyDictionary<string, IProvider> One(IProvider provider) => new Dictionary<string, IProvider> { ["one"] = provider };

    async Task<IReadOnlyList<ReportRecord>> ReportsAsync() =>
        (await records.QueryAsync<ReportRecord>(Queries.Reports, new Dictionary<string, string> { ["step_key"] = "run-1/plan/1" }, CancellationToken.None)).Unwrap();

    async Task<string> TextAsync(string reference)
    {
        await using var stream = (await store.GetAsync(new ArtifactRef(reference), CancellationToken.None)).Unwrap();
        return await new StreamReader(stream).ReadToEndAsync();
    }

    [Fact]
    public async Task An_M6_malformed_plan_is_corrected_in_place_in_the_same_session()
    {
        var (outcome, runtime) = await RunAsync([new Turn(OwnerAsText), new Turn(Valid)]);

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        // One session: started once, continued once with the field and the rule, not re-run.
        Assert.Equal(1, runtime.Started);
        var message = Assert.Single(runtime.Continued);
        Assert.Contains("artifact 'requirements' (requirements.yaml): requirements[0].owner (line 8): must be a map, not text 'billing' [type]", message, StringComparison.Ordinal);
        Assert.Contains("correction turn 1 of 2", message, StringComparison.Ordinal);
        // The endpoint accepted the report's shape twice; the engine rejected the first artifact in between.
        Assert.Equal([true, false, true], (await ReportsAsync()).Select(r => r.Accepted));
        Assert.Equal(Valid, await TextAsync(outcome.Report!.Artifacts["requirements"]));
    }

    [Fact]
    public async Task An_artifact_reported_through_report_json_is_checked_and_corrected_the_same_way()
    {
        // Review F6: the file fallback's report goes through the same artifact check.
        var (outcome, runtime) = await RunAsync([new Turn(OwnerAsText), new Turn(Valid)], file: true);

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        Assert.Contains("requirements[0].owner (line 8): must be a map", Assert.Single(runtime.Continued), StringComparison.Ordinal);
        Assert.Equal([true, false, true], (await ReportsAsync()).Select(r => r.Accepted));
        Assert.Equal(Valid, await TextAsync(outcome.Report!.Artifacts["requirements"]));
    }

    [Fact]
    public async Task A_rule_of_the_type_s_validators_goes_back_through_the_validator_provider()
    {
        var (outcome, runtime) = await RunAsync([new Turn(DuplicateIds), new Turn(Valid)]);

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        Assert.Contains("uses id R1 2 times [document_rules: duplicate_id]", Assert.Single(runtime.Continued), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Artifacts_still_invalid_after_the_correction_turns_fail_the_attempt()
    {
        var (outcome, runtime) = await RunAsync([new Turn(OwnerAsText)]);

        Assert.Equal(StepOutcome.WorkFailed, outcome.Kind);
        Assert.Contains("its artifacts still break their types", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(2, runtime.Continued.Count);
        Assert.Equal([true, false, true, false, true, false], (await ReportsAsync()).Select(r => r.Accepted));
    }

    [Fact]
    public async Task A_report_accepted_before_its_artifact_was_rejected_is_not_taken_when_the_agent_stops_without_reporting_again()
    {
        // The agent fixes nothing and does not report again: the last report counts (docs/04 section 3.3), and it was rejected.
        var (outcome, runtime) = await RunAsync([new Turn(OwnerAsText), new Turn(null, Reports: false)]);

        Assert.Equal(StepOutcome.WorkFailed, outcome.Kind);
        Assert.Contains("ended without reporting", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(3, runtime.Continued.Count);
    }

    [Fact]
    public async Task A_type_naming_a_validator_no_provider_offers_stops_the_step_for_a_person()
    {
        var (outcome, _) = await RunAsync([new Turn(Valid)], validators: false);

        Assert.Equal(StepOutcome.CannotAnswer, outcome.Kind);
        Assert.Contains("names validator document_rules: no validator provider is configured", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_type_the_run_s_libraries_do_not_hold_stops_the_step_for_a_person()
    {
        var (outcome, _) = await RunAsync([new Turn(Valid)], type: "std:artifact_types/no_such_type");

        Assert.Equal(StepOutcome.CannotAnswer, outcome.Kind);
        Assert.Contains("is not in the run's libraries", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_artifact_with_no_declared_type_is_kept_unchecked()
    {
        var (outcome, runtime) = await RunAsync([new Turn(OwnerAsText)], type: null!);

        Assert.Equal(StepOutcome.Reported, outcome.Kind);
        Assert.Empty(runtime.Continued);
    }
}
