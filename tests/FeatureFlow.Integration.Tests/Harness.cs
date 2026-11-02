using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Cli;
using FeatureFlow.Hosting;
using FeatureFlow.Providers.Local;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using IOPath = System.IO.Path;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// A whole engine in this process against the Temporal dev server named by <c>FF_TEMPORAL_ADDRESS</c>: the
/// orchestration worker, the agent worker and the API, with the local providers, a scripted fake runtime and the
/// scripted action stand-in. Each harness has its own folder, task queue and record store, so tests do not share
/// a worker (CONTRIBUTING.md, "Working with helper agents").
/// </summary>
public sealed class Harness : IAsyncDisposable
{
    public static string TemporalAddress =>
        Environment.GetEnvironmentVariable("FF_TEMPORAL_ADDRESS") is { Length: > 0 } address ? address : "localhost:7233";

    public static string RepositoryRoot { get; } = FindRoot();

    public static string TestLibraries => IOPath.Combine(RepositoryRoot, "tests", "FeatureFlow.Integration.Tests", "libraries");

    /// <summary>The types the stand-in and fake providers fill, so capability negotiation passes for std (check 21).</summary>
    public static readonly string[] FakeTypes = ["sandbox", "vcs", "code_host", "ci", "tracker", "validator", "notifications"];

    public string Folder { get; }
    public string TokenFile { get; }
    public FeatureFlowHost Host { get; private set; } = null!;
    public ITemporalClient Client => Host.Client;
    public string Api => Host.ApiAddress!;

    Harness(string folder)
    {
        Folder = folder;
        TokenFile = IOPath.Combine(folder, "token");
    }

    /// <summary>
    /// Starts an engine. <paramref name="script"/> is the fake script's <c>steps</c> section in YAML;
    /// <paramref name="autoApprove"/> sets the organisation's approval rule for every decision type;
    /// <paramref name="omitTypes"/> leaves provider types unconfigured.
    /// </summary>
    public static async Task<Harness> StartAsync(string name, string script = "{}", bool autoApprove = false, string[]? omitTypes = null,
        string[]? libraries = null)
    {
        var folder = IOPath.Combine(IOPath.GetTempPath(), $"ff-it-{name}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(folder);
        var harness = new Harness(folder);
        var token = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(harness.TokenFile, token);
        await File.WriteAllTextAsync(IOPath.Combine(folder, "secrets.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["api_token"] = token }));
        await File.WriteAllTextAsync(IOPath.Combine(folder, "script.yaml"), $"api_version: feature-flow/v1\nkind: fake_script\nsteps: {script}\n");
        var fakes = string.Join("\n", FakeTypes.Except(omitTypes ?? []).Select(t => $"  {t}: {{ instances: {{ fake: {{}} }} }}"));
        var queue = $"ff-{name}-{Guid.NewGuid():N}"[..30] + "-orchestration";
        await File.WriteAllTextAsync(IOPath.Combine(folder, "installation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: installation
            temporal: { address: "{{TemporalAddress}}", namespace: default, task_queue: {{queue}} }
            providers:
              artifact_store: { default: filesystem, instances: { filesystem: { root: artifacts } } }
              record_store: { default: sqlite, instances: { sqlite: { path: records.db } } }
              secrets: { default: file, instances: { file: { path: secrets.json } } }
              agent_runtime: { default: scripted, dry_run: recording, instances: { scripted: { script: script.yaml }, recording: { folder: recorded } } }
              action_stand_in: { default: scripted, dry_run: recording, instances: { scripted: { script: script.yaml }, recording: { folder: recorded } } }
            {{fakes}}
            api:
              auth: { mode: local_token, token_ref: secrets/api_token, actor: tester, roles: [viewer, approver, steerer] }
            """);
        var sources = string.Join("\n", (libraries ?? []).Prepend(IOPath.Combine(RepositoryRoot, "library", "std"))
            .Select(p => $"  - {{ name: {IOPath.GetFileName(p)}, source: directory, path: \"{p}\" }}"));
        await File.WriteAllTextAsync(IOPath.Combine(folder, "organisation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: organisation
            name: integration
            libraries:
            {{sources}}
            policy:
              approval:
                - { match: "*", roles: [approver], auto: {{(autoApprove ? "true" : "false")}} }
            """);
        var configuration = FeatureFlowConfiguration.Load(IOPath.Combine(folder, "installation.yaml"), IOPath.Combine(folder, "organisation.yaml"));
        harness.Host = await FeatureFlowHost.StartAsync(configuration, LocalProviders.Factories, true, true, "http://127.0.0.1:0");
        return harness;
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
                return wait["waitId"]!.ToString();
            if (view["run"]!["status"]!.ToString() is not ("starting" or "running"))
                return null;
            await Task.Delay(200);
        }

        throw new TimeoutException($"{runId} opened no wait within {timeout}");
    }

    /// <summary>The timeline's started steps, in order.</summary>
    public static List<string> StartedSteps(JsonNode view) =>
        view["timeline"]!.AsArray().Where(e => e!["kind"]!.ToString() == "started").Select(e => e!["stepId"]!.ToString()).ToList();

    /// <summary>Every run of a workflow id, oldest first: one per continue-as-new.</summary>
    public async Task<List<WorkflowExecution>> RunsOfAsync(string workflowId)
    {
        var list = new List<WorkflowExecution>();
        await foreach (var execution in Client.ListWorkflowsAsync($"WorkflowId = '{workflowId}'"))
            list.Add(execution);
        return list.OrderBy(e => e.StartTime).ToList();
    }

    /// <summary>The number of WorkflowTaskFailed events across every run of a workflow id (SP1's C5).</summary>
    public async Task<int> WorkflowTaskFailuresAsync(string workflowId)
    {
        var failed = 0;
        foreach (var run in await RunsOfAsync(workflowId))
        {
            var history = await Client.GetWorkflowHandle(workflowId, run.RunId).FetchHistoryAsync();
            failed += history.Events.Count(e => e.EventType == EventType.WorkflowTaskFailed);
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

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(IOPath.Combine(dir.FullName, "FeatureFlow.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("FeatureFlow.slnx not found above the test output folder");
    }
}
