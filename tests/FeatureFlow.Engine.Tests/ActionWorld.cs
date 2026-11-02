using System.Text;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// One repository in an in-memory world (the <c>memory</c> VCS, code host and CI, M5), a scripted <c>memory</c>
/// sandbox, and local stores, with the engine actions over them. Steps run as a workflow would call them: the
/// artifacts and pull requests earlier steps produced are in the scope of later ones.
/// </summary>
public sealed class ActionWorld : IAsyncDisposable
{
    public const string WorkBranch = "ff/ticket/T-1";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-actions-" + Guid.NewGuid().ToString("N"));

    public InMemoryForge Forge { get; } = new();
    public RepoRef Repo { get; }
    public FileSystemArtifactStore Store { get; }
    public SqliteRecordStore Records { get; }
    public MemorySandbox Sandbox { get; }
    public EngineActions Actions { get; }
    public string RunId { get; } = "run-" + Guid.NewGuid().ToString("N")[..8];
    public List<ArtifactEntry> Artifacts { get; } = [];
    public List<string> PullRequests { get; } = [];
    public List<string> EngineMayMerge { get; } = [];
    public string? ParentBranch { get; set; }

    /// <summary>Executions per step id, so a step run again is a new execution with its own records.</summary>
    readonly Dictionary<string, int> executions = new(StringComparer.Ordinal);

    public ActionWorld(IReadOnlyDictionary<string, string>? files = null, bool sandbox = true, bool ci = true)
    {
        Directory.CreateDirectory(folder);
        Repo = Forge.CreateRepository("acme/api", files ?? new Dictionary<string, string> { ["README.md"] = "# api\n" });
        Store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        Records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        Sandbox = new MemorySandbox(() => Store, Forge);
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["vcs"] = One(new InMemoryVcs(Forge)),
            ["code_host"] = One(new InMemoryCodeHost(Forge)),
            ["artifact_store"] = One(Store),
            ["record_store"] = One(Records),
        };
        if (ci)
            instances["ci"] = One(new InMemoryCi(Forge, () => Store));
        if (sandbox)
            instances["sandbox"] = One(Sandbox);
        var providers = new ProviderSet(instances, instances.Keys.ToDictionary(k => k, _ => "one"), new Dictionary<string, string>());
        Actions = new EngineActions(providers, new ActionOptions(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(30)));
    }

    static IReadOnlyDictionary<string, IProvider> One(IProvider provider) => new Dictionary<string, IProvider> { ["one"] = provider };

    public string Main => Forge.Resolve(Repo, "main")!;

    public string? Work => Forge.Resolve(Repo, "refs/heads/" + WorkBranch);

    /// <summary>Puts a commit on the work branch, as an agent step would (creating the branch from main if needed).</summary>
    public string CommitWork(string path, string content) =>
        Forge.Commit(Repo, "refs/heads/" + WorkBranch, new Dictionary<string, string?> { [path] = content }, $"change {path}", actor: InMemoryForge.EngineActor);

    public string CommitMain(string path, string content) =>
        Forge.Commit(Repo, "main", new Dictionary<string, string?> { [path] = content }, $"someone changes {path}");

    public ActionScope Scope => new([Repo.FullName], Repo.FullName, WorkBranch, ParentBranch, Artifacts.ToList(), PullRequests.ToList(), EngineMayMerge.ToList());

    /// <summary>
    /// Runs one action step to its end: a wait is retried as Temporal would, up to <paramref name="waits"/> reads. The
    /// step's evidence becomes an artifact of the evidence type its action produces, and a pull request it opens is
    /// remembered, so later steps see them.
    /// </summary>
    public async Task<ActionResult> RunAsync(string action, string? inputs = null, string? stepId = null, int waits = 50, Action? beforeEachRead = null)
    {
        stepId ??= action.Replace('.', '_');
        var execution = executions[stepId] = executions.GetValueOrDefault(stepId) + 1;
        var call = Call(action, stepId, execution, inputs);
        ActionResult result;
        var reads = 0;
        while (true)
        {
            beforeEachRead?.Invoke();
            result = await Actions.RunAsync(call, Scope, () => { }, Ct);
            if (result.Kind != ActionResult.Wait || ++reads >= waits)
                break;
        }

        if (result.Report is { } report)
        {
            foreach (var (name, reference) in report.Artifacts)
                Artifacts.Add(new ArtifactEntry(stepId, name, EvidenceType(action), reference));
            if (report.Outputs.TryGetValue("pull_request", out var pr) && YamlWriter.ReadPlain(pr) is string text && text.Length > 0)
                PullRequests.Add(text);
        }

        return result;
    }

    public StepCall Call(string action, string stepId, int execution, string? inputs)
    {
        var contract = Model.Actions.All[action];
        var plain = inputs is null ? new Dictionary<string, object?>() : (Dictionary<string, object?>)YamlWriter.ReadPlain(inputs)!;
        var outputs = (contract.Outputs ?? []).ToDictionary(n => n, n => Model.Actions.EngineOutputTypes.GetValueOrDefault(n, "dyn"));
        var zero = outputs.ToDictionary(kv => kv.Key, kv => kv.Value switch { "boolean" => "false", "number" => "0", _ => "\"\"" });
        var stepContract = new StepContract(contract.Outcomes.Select(o => new OutcomeSpec(o.Name, o.Class.Name())).ToList(), outputs,
            EvidenceType(action) is null ? [] : contract.Outcomes.Select(o => new ArtifactSpec(o.Name, "evidence")).ToList(), zero);
        return new StepCall(new StepIdentity(RunId, "ticket_delivery", stepId, execution, $"{RunId}/{stepId}/{execution}"), "action", action,
            plain.ToDictionary(kv => kv.Key, kv => YamlWriter.WritePlain(kv.Value)), stepContract, false, null);
    }

    static string? EvidenceType(string action) => action switch
    {
        "test.run" or "test.integration" => "std:artifact_types/test_results",
        "ci.checks" => "std:artifact_types/check_results",
        "analysis.run" => "std:artifact_types/analysis_results",
        "scm.merge" => "std:artifact_types/merge_record",
        _ => null,
    };

    public async Task<string> ReadAsync(string reference)
    {
        await using var stream = (await Store.GetAsync(new ArtifactRef(reference), Ct)).Unwrap();
        return await new StreamReader(stream, Encoding.UTF8).ReadToEndAsync(Ct);
    }

    public async Task<IReadOnlyList<AgentEventRecord>> EventsAsync(string stepId, int execution = 1) =>
        (await Records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = $"{RunId}/{stepId}/{execution}" }, Ct)).Unwrap();

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

    // ---- Manifests and result files ----

    public const string UnitTrx = "tests/Unit/TestResults/unit.trx";

    /// <summary>A manifest with a unit tier in the sandbox (TRX), an integration tier in CI (JUnit), setup, and a SARIF analyser.</summary>
    public static string Manifest(string extra = "") => $$"""
        api_version: feature-flow/v1
        kind: repository
        setup:
          commands: ["dotnet tool restore"]
        after_changes:
          commands: ["dotnet format"]
        test_tiers:
          - name: unit
            tags: [unit, fast]
            command: dotnet test tests/Unit --logger "trx;LogFileName=unit.trx"
            result: { format: trx, path: {{UnitTrx}} }
            targets: [sandbox, ci]
          - name: integration
            tags: [integration]
            command: dotnet test tests/Integration
            result: { format: junit, path: "tests/Integration/*.xml" }
            targets: [ci]
            ci: { job: integration.yml }
        static_analysis:
          analyser: sarif
          command: dotnet build -p:ErrorLog=analysis.sarif
          sarif_path: analysis.sarif
        {{extra}}
        """;

    public static string Trx(int passed, params string[] failed) =>
        $"""
         <?xml version="1.0" encoding="utf-8"?>
         <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
           <Results>
             {string.Concat(Enumerable.Range(0, passed).Select(i => $"<UnitTestResult testName=\"Passes{i}\" outcome=\"Passed\" />"))}
             {string.Concat(failed.Select(f => $"<UnitTestResult testName=\"{f}\" outcome=\"Failed\"><Output><ErrorInfo><Message>{f} failed\nat line 3</Message></ErrorInfo></Output></UnitTestResult>"))}
             <UnitTestResult testName="Skipped" outcome="NotExecuted" />
           </Results>
         </TestRun>
         """;

    public static string JUnit(int passed, params string[] failed) =>
        $"""
         <testsuites>
           <testsuite name="Integration">
             {string.Concat(Enumerable.Range(0, passed).Select(i => $"<testcase classname=\"Api\" name=\"Works{i}\" />"))}
             {string.Concat(failed.Select(f => $"<testcase classname=\"Api\" name=\"{f}\"><failure message=\"{f} broke\">trace</failure></testcase>"))}
           </testsuite>
         </testsuites>
         """;

    public static string Sarif(params (string Rule, string Level, string Path, string Fingerprint)[] issues) =>
        $$"""
          { "version": "2.1.0", "runs": [ { "tool": { "driver": { "name": "csc", "rules": [] } }, "results": [
          {{string.Join(",\n", issues.Select(i => $$"""{ "ruleId": "{{i.Rule}}", "level": "{{i.Level}}", "message": { "text": "{{i.Rule}} here" }, "locations": [ { "physicalLocation": { "artifactLocation": { "uri": "{{i.Path}}" }, "region": { "startLine": 7 } } } ], "partialFingerprints": { "primaryLocationLineHash": "{{i.Fingerprint}}" } }"""))}}
          ] } ] }
          """;
}
