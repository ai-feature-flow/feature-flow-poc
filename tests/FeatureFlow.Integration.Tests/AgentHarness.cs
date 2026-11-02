using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using FeatureFlow.Cli;
using FeatureFlow.Hosting;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.ClaudeCode;
using FeatureFlow.Providers.Docker;
using FeatureFlow.Providers.Git;
using FeatureFlow.Providers.GitHub;
using FeatureFlow.Providers.Local;
using IOPath = System.IO.Path;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// What every agent step test needs once: the sandbox image, built from the repository's Dockerfile if it is
/// not present, and the sandbox client published self-contained for the Docker host's Linux architecture (LOG 67).
/// </summary>
public static class AgentWorld
{
    public const string Image = "feature-flow-sandbox:m4";
    static readonly Lazy<Task<string>> Client = new(PrepareAsync);

    public static Task<string> SandboxClientAsync() => Client.Value;

    static async Task<string> PrepareAsync()
    {
        var root = Harness.RepositoryRoot;
        var (exit, _) = await RunAsync("docker", ["image", "inspect", Image]);
        if (exit != 0)
        {
            var dockerfile = IOPath.Combine(root, "src", "Providers", "FeatureFlow.Providers.Docker", "sandbox.Dockerfile");
            var (built, output) = await RunAsync("docker", ["build", "-q", "-t", Image, "-f", dockerfile, IOPath.GetDirectoryName(dockerfile)!]);
            if (built != 0)
                throw new InvalidOperationException("the sandbox image did not build:\n" + output);
        }

        var (_, arch) = await RunAsync("docker", ["info", "--format", "{{.Architecture}}"]);
        var rid = arch.Trim() is "aarch64" or "arm64" ? "linux-arm64" : "linux-x64";
        var folder = IOPath.Combine(IOPath.GetTempPath(), $"ff-sandbox-client-{rid}-{Guid.NewGuid():N}"[..48]);
        var (published, log) = await RunAsync("dotnet", ["publish", IOPath.Combine(root, "src", "FeatureFlow.SandboxClient"), "-c", "Release", "-r", rid,
            "--self-contained", "-p:PublishSingleFile=true", "-p:EnableCompressionInSingleFile=true", "-o", folder]);
        if (published != 0)
            throw new InvalidOperationException("the sandbox client did not publish:\n" + log);
        return IOPath.Combine(folder, "ff-sandbox");
    }

    public static async Task<(int Exit, string Output)> RunAsync(string file, IReadOnlyList<string> arguments, string? directory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory ?? "" };
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        foreach (var (k, v) in environment ?? new Dictionary<string, string>())
            start.Environment[k] = v;
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }
}

/// <summary>Settings an agent step test varies.</summary>
public sealed record AgentHarnessOptions
{
    /// <summary>Whether the agent worker runs in a host of its own, so a test can restart it alone (SP2).</summary>
    public bool SeparateAgentWorker { get; init; }

    public string CommitInterval { get; init; } = "2s";

    /// <summary>How long the hook command holds a call while no replica answers (R19).</summary>
    public int HookHoldSeconds { get; init; } = 60;

    /// <summary>How long the sandbox client retries an engine call before it ends the session (LOG 60).</summary>
    public int ClientRetrySeconds { get; init; } = 120;

    public bool EngineTools { get; init; } = true;

    /// <summary>The model's API; the scripted model's URL unless a test sets another.</summary>
    public string? ModelUpstream { get; init; }

    /// <summary>
    /// The Live category: the fixture repository on GitHub through the GitHub App, and the real model through the
    /// proxy with the subscription token from <c>CLAUDE_CODE_OAUTH_TOKEN</c>. The App's settings come from the file
    /// <c>FF_SECRETS_FILE</c> names, outside the repository; no value of it is printed.
    /// </summary>
    public bool Live { get; init; }
}

/// <summary>
/// An engine with the real agent worker: the Docker sandbox, the Claude Code runtime, the Git provider against a
/// local bare repository standing for the code host, the model proxy in front of a <see cref="ScriptedModel"/>,
/// and the scripted action stand-in. Nothing reaches GitHub or a real model.
/// </summary>
public sealed class AgentHarness : IAsyncDisposable
{
    public static IEnumerable<ProviderFactory> Factories => LocalProviders.Factories.Concat(DockerProviders.Factories).Concat(ClaudeCodeProviders.Factories)
        .Concat(GitProviders.Factories).Concat(GitHubProviders.Factories);

    public const string FixtureRepository = "ai-feature-flow/feature-flow-example";

    public string Repository { get; private set; } = "acme/app";

    public bool Live { get; private set; }

    public string Folder { get; }
    public string TokenFile { get; }
    public string Remote => IOPath.Combine(Folder, "remotes", Repository + ".git");
    public FeatureFlowHost Host { get; private set; } = null!;
    public FeatureFlowHost? AgentHost { get; private set; }
    public FeatureFlowConfiguration Configuration { get; private set; } = null!;
    public string Api => Host.ApiAddress!;
    public string MainRevision { get; private set; } = "";
    public int AgentPort { get; }

    AgentHarness(string folder)
    {
        Folder = folder;
        TokenFile = IOPath.Combine(folder, "token");
        AgentPort = ScriptedModel.FreePort();
    }

    public static async Task<AgentHarness> StartAsync(string name, ScriptedModel? model, AgentHarnessOptions? options = null)
    {
        options ??= new AgentHarnessOptions();
        var client = await AgentWorld.SandboxClientAsync();
        var folder = IOPath.Combine(IOPath.GetTempPath(), $"ff-m4-{name}-{Guid.NewGuid():N}"[..40]);
        Directory.CreateDirectory(folder);
        var h = new AgentHarness(folder) { Live = options.Live };
        string vcs, broker = "", modelAccess, secretsFromEnvironment = "";
        if (options.Live)
        {
            // CONTRIBUTING.md: a missing credential stops the test; nothing is substituted for it.
            var secretsFile = Environment.GetEnvironmentVariable("FF_SECRETS_FILE");
            Assert.True(secretsFile is not null && File.Exists(secretsFile), "the Live tests need FF_SECRETS_FILE naming the GitHub App's settings");
            Assert.True(Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN") is { Length: > 0 }, "the Live tests need CLAUDE_CODE_OAUTH_TOKEN for the model proxy");
            var app = JsonSerializer.Deserialize<Dictionary<string, string>>(await File.ReadAllTextAsync(secretsFile!))!;
            h.Repository = FixtureRepository;
            vcs = "{ default: git, instances: { git: { cache: clones } } }";
            broker = $$"""  credential_broker: { default: github_app, instances: { github_app: { app_id: "{{app["github-app-id"]}}", installation_id: "{{app["github-installation-id"]}}", key_path: "{{app["github-app-key"]}}", repositories: [{{FixtureRepository}}] } } }""";
            modelAccess = "{ mode: proxy, key_ref: secrets/CLAUDE_CODE_OAUTH_TOKEN, upstream: \"https://api.anthropic.com\", auth: oauth }";
            secretsFromEnvironment = ", from_environment: [CLAUDE_CODE_OAUTH_TOKEN]";
        }
        else
        {
            await h.CreateRemoteAsync();
            vcs = $$"""{ default: git, instances: { git: { remote: "file://{{folder}}/remotes/{repo}.git", cache: clones } } }""";
            modelAccess = $$"""{ mode: proxy, key_ref: secrets/model_key, upstream: "{{options.ModelUpstream ?? model!.Url}}", auth: api_key }""";
        }

        var token = Guid.NewGuid().ToString("N");
        await File.WriteAllTextAsync(h.TokenFile, token);
        await File.WriteAllTextAsync(IOPath.Combine(folder, "secrets.json"), JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["api_token"] = token,
            ["step_token_key"] = Guid.NewGuid().ToString("N"),
            ["model_key"] = model?.Credential ?? "",
        }));
        await File.WriteAllTextAsync(IOPath.Combine(folder, "script.yaml"), "api_version: feature-flow/v1\nkind: fake_script\nsteps: {}\n");
        var queue = $"ff-m4-{name}-{Guid.NewGuid():N}"[..30] + "-orchestration";
        var fakes = string.Join("\n", new[] { "code_host", "ci", "tracker", "validator", "notifications" }.Select(t => $"  {t}: {{ instances: {{ fake: {{}} }} }}"));
        await File.WriteAllTextAsync(IOPath.Combine(folder, "installation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: installation
            development: true
            temporal: { address: "{{Harness.TemporalAddress}}", namespace: default, task_queue: {{queue}} }
            agent_worker:
              listen: "http://0.0.0.0:{{h.AgentPort}}"
              urls: ["http://host.docker.internal:{{h.AgentPort}}"]
              token_key_ref: secrets/step_token_key
              commit_interval: {{options.CommitInterval}}
              sandbox_client: "{{client}}"
            providers:
              artifact_store: { default: filesystem, instances: { filesystem: { root: artifacts } } }
              record_store: { default: sqlite, instances: { sqlite: { path: records.db } } }
              secrets: { default: file, instances: { file: { path: secrets.json{{secretsFromEnvironment}} } } }
              sandbox: { default: docker, instances: { docker: { image: "{{AgentWorld.Image}}" } } }
              agent_runtime:
                default: claude_code
                instances:
                  claude_code:
                    model: haiku
                    max_retries: {{(options.Live ? 2 : 0)}}
                    hook_hold: {{options.HookHoldSeconds}}
                    client_retry: {{options.ClientRetrySeconds}}
                    engine_tools: {{(options.EngineTools ? "true" : "false")}}
                    model_access: {{modelAccess}}
              vcs: {{vcs}}
            {{broker}}
              action_stand_in: { default: scripted, instances: { scripted: { script: script.yaml } } }
            {{fakes}}
            api:
              auth: { mode: local_token, token_ref: secrets/api_token, actor: tester, roles: [viewer, approver, steerer, admin] }
            """);
        await File.WriteAllTextAsync(IOPath.Combine(folder, "organisation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: organisation
            name: integration
            libraries:
              - { name: std, source: directory, path: "{{IOPath.Combine(Harness.RepositoryRoot, "library", "std")}}" }
              - { name: m4, source: directory, path: "{{IOPath.Combine(Harness.TestLibraries, "m4")}}" }
            policy:
              approval:
                - { match: "*", roles: [approver], auto: false }
            """);
        h.Configuration = FeatureFlowConfiguration.Load(IOPath.Combine(folder, "installation.yaml"), IOPath.Combine(folder, "organisation.yaml"));
        h.Host = await FeatureFlowHost.StartAsync(h.Configuration, Factories, true, !options.SeparateAgentWorker, "http://127.0.0.1:0");
        if (options.SeparateAgentWorker)
            h.AgentHost = await FeatureFlowHost.StartAsync(h.Configuration, Factories, false, true, null);
        if (options.Live)
            h.MainRevision = (await h.RemoteRevisionAsync("refs/heads/main"))!;
        return h;
    }

    /// <summary>Stops the agent worker and its endpoint, as a crash or deploy would, then starts a new one on the same port.</summary>
    public async Task StopAgentWorkerAsync()
    {
        if (AgentHost is not null)
            await AgentHost.DisposeAsync();
        AgentHost = null;
    }

    public async Task StartAgentWorkerAsync() => AgentHost = await FeatureFlowHost.StartAsync(Configuration, Factories, false, true, null);

    /// <summary>A bare repository standing for the code host, with one commit on <c>main</c>.</summary>
    async Task CreateRemoteAsync()
    {
        Directory.CreateDirectory(Remote);
        await GitAsync(Remote, "init", "-q", "--bare", "-b", "main");
        var work = IOPath.Combine(Folder, "seed");
        await GitAsync(Folder, "init", "-q", "-b", "main", work);
        await File.WriteAllTextAsync(IOPath.Combine(work, "README.md"), "# app\n\nA fixture for the M4 tests.\n");
        await GitAsync(work, "add", "-A");
        await GitAsync(work, "-c", "user.name=seed", "-c", "user.email=seed@example.invalid", "commit", "-q", "-m", "Initial commit");
        await GitAsync(work, "push", "-q", Remote, "main");
        MainRevision = (await GitAsync(work, "rev-parse", "HEAD")).Trim();
    }

    public static async Task<string> GitAsync(string directory, params string[] arguments)
    {
        var (exit, output) = await AgentWorld.RunAsync("git", arguments, directory);
        if (exit != 0)
            throw new InvalidOperationException($"git {string.Join(" ", arguments)} failed: {output}");
        return output;
    }

    /// <summary>The revision a reference has on the code host (the stand-in, or GitHub in the Live category), or null.</summary>
    public async Task<string?> RemoteRevisionAsync(string reference) =>
        (await Host.Providers.Required<IVcsProvider>("vcs").ResolveAsync(new RepoRef(Repository), reference, CancellationToken.None)).Unwrap();

    /// <summary>Removes references a Live test pushed to the fixture repository, with the engine's credential.</summary>
    public async Task DeleteRemoteReferencesAsync(params string[] references)
    {
        var broker = Host.Providers.Required<ICredentialBroker>("credential_broker");
        var credential = (await broker.IssueAsync(Actor.Engine, "github", CredentialScope.Write(Repository), TimeSpan.FromMinutes(10), CancellationToken.None)).Unwrap();
        var header = "AUTHORIZATION: basic " + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("x-access-token:" + credential.Secret));
        var scratch = IOPath.Combine(Folder, "delete-refs");
        Directory.CreateDirectory(scratch);
        await GitAsync(scratch, "init", "-q");
        var present = new List<string>();
        foreach (var reference in references)
            if (await RemoteRevisionAsync(reference) is not null)
                present.Add(reference);
        if (present.Count == 0)
            return;
        var (exit, output) = await AgentWorld.RunAsync("git", ["push", "-q", $"https://github.com/{Repository}.git", .. present.Select(r => ":" + r)], scratch,
            new Dictionary<string, string> { ["GIT_CONFIG_COUNT"] = "1", ["GIT_CONFIG_KEY_0"] = "http.extraHeader", ["GIT_CONFIG_VALUE_0"] = header, ["GIT_TERMINAL_PROMPT"] = "0" });
        Assert.True(exit == 0, "removing the test's references failed: " + output.Split('\n').FirstOrDefault());
    }

    public async Task<string> RemoteMessageAsync(string revision) => (await GitAsync(Remote, "log", "-1", "--format=%s", revision)).Trim();

    public static string HiddenReference(string runId, string stepId) =>
        $"refs/feature-flow/wip/{Safe(runId)}/{Safe(stepId)}";

    static string Safe(string text) => string.Concat(text.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));

    public (int Exit, string Output, string Error) Cli(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = CliApp.Run(["run", .. args, "--api", Api, "--token-file", TokenFile], output, error);
        return (exit, output.ToString(), error.ToString());
    }

    public string Start(string ticket, string instruction, string template = "m4:templates/agent_step")
    {
        var (exit, output, error) = Cli("start", template, "--param", $"ticket_id={ticket}", "--param", $"instruction={instruction}",
            "--repo", Repository);
        Assert.True(exit == 0, error);
        var runId = output.Trim().Split(' ')[1];
        runs.Add(runId);
        return runId;
    }

    public async Task<JsonNode> ShowAsync(string runId)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new("Bearer", await File.ReadAllTextAsync(TokenFile));
        return JsonNode.Parse(await http.GetStringAsync($"{Api}/runs/{runId}"))!;
    }

    public async Task<string> WaitForEndAsync(string runId, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var status = (await ShowAsync(runId))["run"]!["status"]!.ToString();
            if (status is not ("starting" or "running"))
                return status;
            await Task.Delay(500);
        }

        throw new TimeoutException($"{runId} did not end within {timeout}:\n{await TimelineAsync(runId)}");
    }

    /// <summary>The run's open waits of a kind, from the API.</summary>
    public async Task<List<JsonNode>> WaitsAsync(string runId, string kind) =>
        (await ShowAsync(runId))["waits"]!.AsArray().Where(w => w!["kind"]!.ToString() == kind).Select(w => w!).ToList();

    public async Task<string> TimelineAsync(string runId) =>
        string.Join("\n", (await ShowAsync(runId))["timeline"]!.AsArray().Select(e => $"{e!["kind"]} {e["stepId"]}: {e["detail"]}"));

    /// <summary>The records the agent worker wrote for a step, by its key.</summary>
    public async Task<IReadOnlyList<T>> RecordsAsync<T>(string query, string stepKey)
    {
        var store = Host.Providers.Required<IRecordStoreProvider>("record_store");
        return (await store.QueryAsync<T>(query, new Dictionary<string, string> { ["step_key"] = stepKey }, CancellationToken.None)).Unwrap();
    }

    /// <summary>
    /// Writes the run's first history to <c>FF_RECORD_HISTORIES</c>, when set, for the replay tests. It must hold
    /// no API token and no model credential (docs/09 section 4.1: histories pass a secret scan).
    /// </summary>
    public async Task RecordHistoryAsync(string runId, string name, params string[] secrets)
    {
        if (Environment.GetEnvironmentVariable("FF_RECORD_HISTORIES") is not { Length: > 0 } folder)
            return;
        var history = await Host.Client.GetWorkflowHandle(runId).FetchHistoryAsync();
        var json = history.ToJson();
        foreach (var secret in secrets.Append(await File.ReadAllTextAsync(TokenFile)))
            Assert.DoesNotContain(secret, json);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(IOPath.Combine(folder, name + ".json"), json);
    }

    /// <summary>Waits until the agent worker has recorded an event for the step, such as its first session starting.</summary>
    public async Task WaitForEventAsync(string stepKey, Func<AgentEventRecord, bool> match, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await RecordsAsync<AgentEventRecord>(Queries.AgentEvents, stepKey)).Any(match))
                return;
            await Task.Delay(250);
        }

        throw new TimeoutException($"no matching event for {stepKey} within {timeout}");
    }

    /// <summary>Waits until the run has an open wait of a kind; returns its id.</summary>
    public async Task<string> WaitForWaitAsync(string runId, string kind, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if ((await WaitsAsync(runId, kind)).FirstOrDefault() is { } wait)
                return wait["waitId"]!.ToString();
            await Task.Delay(250);
        }

        throw new TimeoutException($"{runId} opened no {kind} within {timeout}:\n{await TimelineAsync(runId)}");
    }

    public async Task<string> ArtifactTextAsync(string reference)
    {
        var store = Host.Providers.Required<IArtifactStoreProvider>("artifact_store");
        await using var stream = (await store.GetAsync(new ArtifactRef(reference), CancellationToken.None)).Unwrap();
        return await new StreamReader(stream).ReadToEndAsync();
    }

    /// <summary>Every container this harness's steps created that is still there.</summary>
    public static async Task<List<string>> SandboxesAsync()
    {
        var (_, output) = await AgentWorld.RunAsync("docker", ["ps", "-a", "--filter", $"label={DockerSandbox.Label}=1", "--format", "{{.Names}}"]);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    readonly List<string> runs = [];

    public async ValueTask DisposeAsync()
    {
        if (AgentHost is not null)
            await AgentHost.DisposeAsync();
        await Host.DisposeAsync();
        // A test that failed part-way may leave its step's sandbox: remove it, so tests do not leak containers.
        foreach (var runId in runs)
            for (var execution = 1; execution <= 3; execution++)
                await AgentWorld.RunAsync("docker", ["rm", "-f", FeatureFlow.Providers.Docker.DockerSandbox.NameOf($"{runId}/implement/{execution}")]);
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
