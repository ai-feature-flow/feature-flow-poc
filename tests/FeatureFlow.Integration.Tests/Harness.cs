using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Cli;
using FeatureFlow.Hosting;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.StdValidators;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using IOPath = System.IO.Path;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// A whole engine in this process against the Temporal dev server named by <c>FF_TEMPORAL_ADDRESS</c>: the
/// orchestration worker, the agent worker and the API, on the <c>memory</c> providers of a fresh world holding the fixture
/// repository <see cref="RepoName"/>, the standard validators, and the scripted runtime acting in the memory sandbox. Its
/// dry-run instances are the memory providers of a second world holding the same fixture, and the recording runtime. Each
/// harness has its own folder, task queue, record store and worlds, so tests do not share a worker (CONTRIBUTING.md,
/// "Working with helper agents").
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    public static string TemporalAddress =>
        Environment.GetEnvironmentVariable("FF_TEMPORAL_ADDRESS") is { Length: > 0 } address ? address : "localhost:7233";

    public static string TestLibraries => IOPath.Combine(Repository.Root, "tests", "FeatureFlow.Integration.Tests", "libraries");

    /// <summary>
    /// The provider types the harness configures on its worlds, besides <c>validator</c> (std) and <c>session_surface</c> (the
    /// local hand-off, and in a dry run the dry world's memory surface); <c>without:</c> leaves any of them out.
    /// </summary>
    public static readonly string[] MemoryTypes = ["vcs", "code_host", "ci", "sandbox", "tracker", "notifications"];

    /// <summary>The fixture repository, in both worlds, protected on <c>main</c>.</summary>
    public const string RepoName = "acme/api";

    /// <summary>The fixture repository's manifest: a unit tier read from TRX and SARIF analysis, which the worlds' sandboxes produce.</summary>
    public const string Manifest = """
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

    /// <summary>What <c>dotnet test</c> writes in the worlds' sandboxes: three passing tests.</summary>
    public const string Trx = """
        <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010"><Results>
          <UnitTestResult testName="Adds" outcome="Passed" /><UnitTestResult testName="Lists" outcome="Passed" />
          <UnitTestResult testName="Regression.fix" outcome="Passed" />
        </Results></TestRun>
        """;

    const string Sarif = """{ "version": "2.1.0", "runs": [ { "tool": { "driver": { "name": "csc" } }, "results": [] } ] }""";

    /// <summary>The implementer's claim of a test the TRX above reports passing, for a script that gives <c>implement</c> its own visit.</summary>
    public const string ImplementationClaims =
        "implementation_claims: { schema: 1, claims: [{ criterion: AC-1, verification: unit, tests: [{ repository: acme/api, tier: unit, id: Adds, change: new }] }] }";

    /// <summary>
    /// The fake script's <c>defaults</c>, the visits of steps a test's script does not name (M6): a plan the standard
    /// validators accept, a claim of a test the TRX reports passing, and std's five reviewers each reporting no findings.
    /// </summary>
    static readonly string DefaultVisits = $$"""
        {
          "plan.plan": { artifacts: {
            requirements: { schema: 1, scope: { kind: ticket, id: T-1 }, requirements: [
              { id: R-1, title: Widgets are added, behaviour: An added widget is listed., supports_reason: The ticket's own behaviour } ] },
            delivery_plan: { schema: 1, scope: { kind: ticket, id: T-1 }, acceptance_criteria: [
              { id: AC-1, title: Added widgets are listed, behaviour: Adding a widget then listing shows it., supports: [{ artifact: requirements, requirement: R-1 }],
                verification: [{ id: unit, kind: parsed_test, repository: acme/api, tier: unit, semantic_review: required }] } ] } } },
          implement: { artifacts: { {{ImplementationClaims}} } },
          {{string.Join(",\n  ", Enumerable.Range(0, 5).Select(i => $"verify.review.review_{i}.assess: {{ artifacts: {{ findings_report: {{ schema: 1, findings: [] }} }} }}"))}}
        }
        """;

    public string Folder { get; }
    public string TokenFile { get; }
    public FeatureFlowHost Host { get; private set; } = null!;
    public ITemporalClient Client => Host.Client;
    public string Api => Host.ApiAddress!;

    /// <summary>The world the run's providers act on, and the one its dry runs act on.</summary>
    public string World { get; }
    public string DryWorld { get; }
    public InMemoryForge Forge => InMemoryForge.Named(World);
    public InMemoryForge DryForge => InMemoryForge.Named(DryWorld);
    public RepoRef Repo { get; } = new(RepoName);

    /// <summary>The world's sandbox provider, for a test to add command rules (<see cref="MemorySandbox.On"/>).</summary>
    public MemorySandbox Sandbox => MemorySandbox.Named(World, () => throw new InvalidOperationException("created by the host"));
    public InMemoryTracker Tracker => InMemoryTracker.Named(World);

    /// <summary>The dry world's session surface, where a dry run's sessions are prepared.</summary>
    public InMemorySessionSurface DrySessions => InMemorySessionSurface.Named(DryWorld);

    Harness(string folder, string world)
    {
        Folder = folder;
        TokenFile = IOPath.Combine(folder, "token");
        World = world;
        DryWorld = world + "-dry";
    }

    /// <summary>
    /// Starts an engine. <paramref name="script"/> is the fake script's <c>steps</c> section in YAML, beside its defaults
    /// (<see cref="DefaultVisits"/>); <paramref name="autoApprove"/> sets the organisation's approval rule for every decision type;
    /// <paramref name="without"/> leaves provider types of <see cref="MemoryTypes"/>, or <c>validator</c>, unconfigured, so a
    /// test can configure its own in <paramref name="providers"/> or run without one; <paramref name="noEdits"/> names steps
    /// whose agent changes nothing although it may write.
    /// </summary>
    /// <para>
    /// <paramref name="providers"/> are further lines of the installation's <c>providers</c> section; <paramref name="policy"/>
    /// further lines of the organisation's <c>policy</c>; <paramref name="installation"/> further top-level installation lines; and
    /// <paramref name="factories"/> provider factories beside the local and standard validator ones; <paramref name="agentRuntime"/>
    /// replaces the <c>agent_runtime</c> type's configuration; <paramref name="organisation"/> further top-level organisation
    /// lines; <paramref name="fromChat"/> the approval rule's <c>from_chat</c>; <paramref name="temporal"/> further keys of the
    /// installation's <c>temporal</c> map, after a comma; <paramref name="secrets"/> further secrets beside the API token;
    /// <paramref name="librarySources"/> further entries of the organisation's <c>libraries</c>, as YAML maps;
    /// <paramref name="agentWorker"/> false starts no agent worker; <paramref name="temporalAddress"/> another Temporal than
    /// <see cref="TemporalAddress"/>.
    /// </para>
    public static async Task<Harness> StartAsync(string name, string script = "{}", bool autoApprove = false, string[]? without = null,
        string[]? libraries = null, string providers = "", string policy = "", string installation = "", IEnumerable<FeatureFlow.Providers.Abstractions.ProviderFactory>? factories = null,
        string? agentRuntime = null, string organisation = "", bool fromChat = false, string temporal = "", IReadOnlyDictionary<string, string>? secrets = null,
        string[]? librarySources = null, bool agentWorker = true, string? temporalAddress = null, string[]? noEdits = null)
    {
        var folder = IOPath.Combine(IOPath.GetTempPath(), $"ff-it-{name}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(folder);
        var harness = new Harness(folder, $"it-{name}-{Guid.NewGuid():N}");
        var token = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(harness.TokenFile, token);
        await File.WriteAllTextAsync(IOPath.Combine(folder, "secrets.json"), JsonSerializer.Serialize(
            new Dictionary<string, string>(secrets ?? new Dictionary<string, string>()) { ["api_token"] = token }));
        await File.WriteAllTextAsync(IOPath.Combine(folder, "script.yaml"), $"api_version: feature-flow/v1\nkind: fake_script\nsteps: {script}\ndefaults: {DefaultVisits}\n");
        var client = IOPath.Combine(folder, "ff-sandbox");
        await File.WriteAllTextAsync(client, "not run in a memory sandbox");
        foreach (var world in new[] { harness.World, harness.DryWorld })
            Seed(world);

        var left = without ?? [];
        var memory = string.Join("\n", MemoryTypes.Except(left).Select(t =>
            $"  {t}: {{ default: memory, dry_run: dry, instances: {{ memory: {{ world: {harness.World} }}, dry: {{ provider: memory, world: {harness.DryWorld} }} }} }}"));
        var validator = left.Contains("validator") ? "" : "  validator: { default: std, dry_run: std, instances: { std: {} } }";
        var sessions = left.Contains("session_surface") ? ""
            : $"  session_surface: {{ default: local, dry_run: dry, instances: {{ local: {{}}, dry: {{ provider: memory, world: {harness.DryWorld} }} }} }}";
        var edits = $"[{string.Join(", ", noEdits ?? [])}]";
        agentRuntime ??= $"{{ default: scripted, dry_run: recording, instances: {{ scripted: {{ script: script.yaml, world: {harness.World}, no_edits: {edits} }}, "
                         + $"recording: {{ folder: recorded, script: script.yaml, world: {harness.DryWorld}, no_edits: {edits} }} }} }}";
        var queue = $"ff-{name}-{Guid.NewGuid():N}"[..30] + "-orchestration";
        await File.WriteAllTextAsync(IOPath.Combine(folder, "installation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: installation
            development: true
            temporal: { address: "{{temporalAddress ?? TemporalAddress}}", namespace: default, task_queue: {{queue}}{{temporal}} }
            providers:
              artifact_store: { default: filesystem, instances: { filesystem: { root: artifacts } } }
              record_store: { default: sqlite, instances: { sqlite: { path: records.db } } }
              secrets: { default: file, instances: { file: { path: secrets.json } } }
              agent_runtime: {{agentRuntime}}
            {{memory}}
            {{validator}}
            {{sessions}}
            {{providers}}
            agent_worker: { sandbox_client: "{{client}}" }
            {{installation}}
            api:
              auth: { mode: local_token, token_ref: secrets/api_token, actor: tester, roles: [viewer, approver, steerer, admin] }
            """);
        var sources = string.Join("\n", (libraries ?? []).Prepend(IOPath.Combine(Repository.Root, "library", "std"))
            .Select(p => $"  - {{ name: {IOPath.GetFileName(p)}, source: directory, path: \"{p}\" }}").Concat((librarySources ?? []).Select(l => "  - " + l)));
        await File.WriteAllTextAsync(IOPath.Combine(folder, "organisation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: organisation
            name: integration
            libraries:
            {{sources}}
            policy:
              approval:
                - { match: "*", roles: [approver], auto: {{(autoApprove ? "true" : "false")}}, from_chat: {{(fromChat ? "true" : "false")}} }
            {{policy}}
            {{organisation}}
            """);
        var configuration = FeatureFlowConfiguration.Load(IOPath.Combine(folder, "installation.yaml"), IOPath.Combine(folder, "organisation.yaml"));
        harness.Host = await FeatureFlowHost.StartAsync(configuration, LocalProviders.Factories.Concat(StdValidatorProviders.Factories).Concat(factories ?? []),
            true, agentWorker, "http://127.0.0.1:0");
        // The sandboxes the host composed: tests pass with a TRX, analysis is clean.
        foreach (var world in new[] { harness.World, harness.DryWorld })
            MemorySandbox.Named(world, () => throw new InvalidOperationException("created by the host"))
                .On(new SandboxRule("dotnet test", Files: new Dictionary<string, string> { ["tests/Unit/unit.trx"] = Trx }))
                .On(new SandboxRule("dotnet build", Files: new Dictionary<string, string> { ["analysis.sarif"] = Sarif }));
        return harness;
    }

    /// <summary>A world's fixture repository, with its manifest, protected on <c>main</c>.</summary>
    static void Seed(string world)
    {
        var forge = InMemoryForge.Named(world);
        var repo = forge.CreateRepository(RepoName, new Dictionary<string, string> { ["feature-flow.yaml"] = Manifest, ["src/App.cs"] = "class App {}\n" });
        forge.Protect(repo, new BranchProtection("main", true, true, 1, [], false));
    }

    /// <summary>Runs <c>ff run ...</c> in this process against this engine's API.</summary>
    public (int Exit, string Output, string Error) Cli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = CliApp.Run(["run", .. args, "--api", Api, "--token-file", TokenFile], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    public string Start(string template, params string[] args)
    {
        var (exit, output, error) = Cli(["start", template, .. args]);
        Assert.True(exit == 0, error);
        return output.Trim().Split(' ')[1];
    }

    /// <summary>The run as the API shows it: <c>run</c>, open <c>waits</c>, <c>timeline</c> and <c>children</c>.</summary>
    public async Task<JsonNode> ShowAsync(string runId)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", await File.ReadAllTextAsync(TokenFile));
        return JsonNode.Parse(await http.GetStringAsync($"{Api}/runs/{runId}"))!;
    }

    /// <summary>Waits until the run's record shows an open wait, or the run ends. Returns the first open wait's id, or null.</summary>
    public async Task<string?> WaitForWaitAsync(string runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var view = await ShowAsync(runId);
            if (view["waits"]!.AsArray().FirstOrDefault() is { } wait)
                return wait["wait_id"]!.ToString();
            if (view["run"]!["status"]!.ToString() is not ("starting" or "running"))
                return null;
            await Task.Delay(200);
        }

        throw new TimeoutException($"{runId} opened no wait within {timeout}");
    }

    /// <summary>The timeline's started steps, in order.</summary>
    public static List<string> StartedSteps(JsonNode view) =>
        view["timeline"]!.AsArray().Where(e => e!["kind"]!.ToString() == "started").Select(e => e!["step_id"]!.ToString()).ToList();

    /// <summary>Every run of a workflow id, oldest first: one per continue-as-new.</summary>
    public async Task<List<WorkflowExecution>> RunsOfAsync(string workflowId)
    {
        var list = new List<WorkflowExecution>();
        await foreach (var execution in Client.ListWorkflowsAsync($"WorkflowId = '{workflowId}'"))
            list.Add(execution);
        return list.OrderBy(e => e.StartTime).ToList();
    }

    /// <summary>
    /// Every WorkflowTaskFailed event across every run of a workflow id (SP1's C5), each with its run, event id,
    /// cause and failure message, so a failure on CI says why the task failed.
    /// </summary>
    public async Task<List<string>> WorkflowTaskFailuresAsync(string workflowId)
    {
        var failed = new List<string>();
        var runs = await RunsOfAsync(workflowId);
        for (var i = 0; i < runs.Count; i++)
        {
            var history = await Client.GetWorkflowHandle(workflowId, runs[i].RunId).FetchHistoryAsync();
            failed.AddRange(history.Events.Where(e => e.EventType == EventType.WorkflowTaskFailed).Select(e =>
                $"run {i} of {runs.Count}, event {e.EventId} of {history.Events.Count}: {e.WorkflowTaskFailedEventAttributes.Cause}: {e.WorkflowTaskFailedEventAttributes.Failure?.Message}"));
        }

        return failed;
    }

    /// <summary>
    /// Writes the first run's history of a workflow to <c>FF_RECORD_HISTORIES</c>, when set, for the replay tests.
    /// The history must not hold the API token (docs/09 section 4.1: histories pass a secret scan).
    /// </summary>
    public async Task RecordHistoryAsync(string workflowId, string name)
    {
        if (Environment.GetEnvironmentVariable("FF_RECORD_HISTORIES") is not { Length: > 0 } folder)
            return;
        var first = (await RunsOfAsync(workflowId)).First();
        var history = await Client.GetWorkflowHandle(workflowId, first.RunId).FetchHistoryAsync();
        var json = history.ToJson();
        Assert.DoesNotContain(await File.ReadAllTextAsync(TokenFile), json);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(IOPath.Combine(folder, name + ".json"), json);
    }

    /// <summary>Writes the history of a workflow's run at <paramref name="index"/> (0 the first) to <c>FF_RECORD_HISTORIES</c>, when set.</summary>
    public async Task RecordRunHistoryAsync(string workflowId, int index, string name)
    {
        if (Environment.GetEnvironmentVariable("FF_RECORD_HISTORIES") is not { Length: > 0 } folder)
            return;
        var run = (await RunsOfAsync(workflowId))[index];
        var json = (await Client.GetWorkflowHandle(workflowId, run.RunId).FetchHistoryAsync()).ToJson();
        Assert.DoesNotContain(await File.ReadAllTextAsync(TokenFile), json);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(IOPath.Combine(folder, name + ".json"), json);
    }

    /// <summary>
    /// Writes, when <c>FF_RECORD_HISTORIES</c> is set, the history of the first run of a workflow that resumed from
    /// a hand-over carrying child ends not yet confirmed: the resumed path the replay tests must cover. Returns
    /// whether the workflow had such a run.
    /// </summary>
    public async Task<bool> RecordResumedHistoryAsync(string workflowId, string name)
    {
        foreach (var run in await RunsOfAsync(workflowId))
        {
            var history = await Client.GetWorkflowHandle(workflowId, run.RunId).FetchHistoryAsync();
            var input = history.Events.First().WorkflowExecutionStartedEventAttributes.Input.Payloads_[0].Data.ToStringUtf8();
            var start = JsonSerializer.Deserialize<FeatureFlow.Interpreter.Wire.RunStart>(input)!;
            if (start.Carried is not { PendingChildEnds.Count: > 0, Core.Children.Count: > 0 })
                continue;
            if (Environment.GetEnvironmentVariable("FF_RECORD_HISTORIES") is { Length: > 0 } folder)
            {
                var json = history.ToJson();
                Assert.DoesNotContain(await File.ReadAllTextAsync(TokenFile), json);
                await File.WriteAllTextAsync(IOPath.Combine(folder, name + ".json"), json);
            }

            return true;
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
