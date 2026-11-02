using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Validation;
using Temporalio.Activities;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FeatureFlow.Engine;

/// <summary>A library's content files, as workflow code receives them from the lock.</summary>
public sealed record LibrarySnapshot(string Name, IReadOnlyDictionary<string, string> Files);

/// <summary>What run start gives the workflow: the locked libraries, the run's policy, and any blocking diagnostics.</summary>
public sealed record Prepared(IReadOnlyList<LibrarySnapshot> Libraries, RunPolicy Policy, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings);

public sealed record ChildRead(string Status, string? Result);

/// <summary>
/// The engine's activities (docs/02 section 2.2). Provider errors are classified here (docs/06 section 1.1):
/// an infrastructure error is thrown, so Temporal retries it and no attempt is used; a work error is returned
/// for the core to count.
/// </summary>
public sealed class RunActivities(EngineServices services, ITemporalClient? client)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Run start (docs/07 section 7): load the locked libraries, then checks 18, 19, 21 and 22 against this worker's configuration.</summary>
    [Activity]
    public async Task<Prepared> PrepareAsync(RunStart start)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var snapshots = await LoadLibrariesAsync(start.LockRef);
        var policy = start.Policy ?? services.Policy();
        var d = new DiagnosticBag();
        var libraries = Build(snapshots, d);
        var template = LibraryReference.TryParse(start.Template);
        if (template is null)
            d.Add(Checks.ReferenceResolves, $"'{start.Template}' is not a template reference");
        else
        {
            var (core, loaded) = RunCore.Start(libraries, template, RunInfoOf(start), policy.ToOptions());
            d.AddRange(loaded);
            RunStartChecks.LibraryRequirements(libraries, EngineInfo.Version, d);
            if (core is not null)
            {
                RunStartChecks.Capabilities(core.Plan, libraries, services.Providers.Offered(start.DryRun), d);
                RunStartChecks.Caps(core.Plan, services.Organisation.Policy, core.Allowance, d);
            }
        }

        foreach (var type in new[] { "artifact_store", "record_store" })
            if (services.Providers.ForRun(type, start.DryRun) is { } provider && await provider.CheckAsync(ct) is { Ok: false } failed)
                throw new ApplicationFailureException($"{type} is not usable: {failed.Error!.Message}");

        return new Prepared(snapshots, policy,
            d.Where(x => x.Blocks).Select(x => x.ToString()).ToList(),
            d.Where(x => !x.Blocks).Select(x => x.ToString()).ToList());
    }

    /// <summary>The lock's libraries, each file resolved by its content hash from the artifact store (docs/08 section 3).</summary>
    [Activity]
    public async Task<IReadOnlyList<LibrarySnapshot>> LoadLibrariesAsync(string lockRef)
    {
        var lockText = await ReadTextAsync(lockRef);
        var lockDocument = JsonSerializer.Deserialize<LibraryLock>(lockText, Json) ?? throw new ApplicationFailureException("the lock does not read");
        var list = new List<LibrarySnapshot>();
        foreach (var library in lockDocument.Libraries)
        {
            var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var (path, hash) in library.Files)
                files[path] = await ReadTextAsync(hash);
            list.Add(new LibrarySnapshot(library.Name, files));
        }

        return list;
    }

    async Task<string> ReadTextAsync(string reference)
    {
        var ct = ActivityExecutionContext.HasCurrent ? ActivityExecutionContext.Current.CancellationToken : CancellationToken.None;
        var got = await services.ArtifactStore.GetAsync(new ArtifactRef(reference), ct);
        if (!got.Ok)
            throw new ApplicationFailureException($"artifact {reference}: {got.Error!.Message}", nonRetryable: got.Error.Class == ProviderErrorClass.Work);
        await using var stream = got.Value!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    public static LibrarySet Build(IEnumerable<LibrarySnapshot> snapshots, DiagnosticBag d) =>
        new(snapshots.Select(s => Library.Library.FromFiles(new LibraryFiles(s.Files), d)).OfType<Library.Library>());

    public static RunInfo RunInfoOf(RunStart start) =>
        new(start.RunId, start.Repositories,
            start.Parameters.ToDictionary(kv => kv.Key, kv => YamlWriter.Read(kv.Value, $"params.{kv.Key}")),
            start.ParentWorkflowId);

    // The agent step's activity, RunAgentStep, is the agent worker's: Engine.Agent's AgentActivities (M4).

    /// <summary>An engine action through the stand-in (LOG 81).</summary>
    [Activity]
    public async Task<StepOutcome> RunActionAsync(StepCall call)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var standIn = services.Providers.Required<IActionStandIn>("action_stand_in", call.DryRun);
        var result = await standIn.RunAsync(new ActionRequest(call.Step, call.What, call.Inputs, call.Contract), ct);
        return Classify(result.Error) ?? new StepOutcome(StepOutcome.Reported, result.Value, null);
    }

    /// <summary>
    /// An infrastructure error is thrown, so Temporal retries the activity and no attempt is used (docs/01
    /// section 4); a work error is returned for the core to count.
    /// </summary>
    static StepOutcome? Classify(ProviderError? error)
    {
        if (error is null)
            return null;
        if (error.Reason == "cannot_answer")
            return new StepOutcome(StepOutcome.CannotAnswer, null, error.Message);
        if (error.Class == ProviderErrorClass.Infrastructure)
            throw new ApplicationFailureException($"{error.Reason}: {error.Message}", errorType: "infrastructure");
        return new StepOutcome(StepOutcome.WorkFailed, null, $"{error.Reason}: {error.Message}");
    }

    /// <summary><c>plan.expand</c>'s read (docs/04 section 6.2): the list artifact's items, as JSON text, at most 500.</summary>
    [Activity]
    public async Task<string> ReadListAsync(string artifactRef)
    {
        var text = await ReadTextAsync(artifactRef);
        var d = new DiagnosticBag();
        var node = YamlText.Parse(text, artifactRef, d);
        var items = node switch
        {
            YSeq seq => seq,
            YMap map when map["items"] is YSeq seq => seq,
            _ => throw new ApplicationFailureException($"{artifactRef} holds no list of items", nonRetryable: true),
        };
        return YamlWriter.Write(items);
    }

    /// <summary>The parent's confirm read of a child (docs/02 section 3): its status as Temporal holds it. The signal was a hint.</summary>
    [Activity]
    public async Task<ChildRead> ReadChildAsync(string workflowId)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var handle = (client ?? throw new InvalidOperationException("no Temporal client")).GetWorkflowHandle(workflowId);
        string? result = null;
        try
        {
            // The child signals just before it returns, so it may still be running for a moment.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(60));
            result = await handle.GetResultAsync<string>(rpcOptions: new RpcOptions { CancellationToken = wait.Token });
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Not completed normally; the description says how it ended.
        }

        var description = await handle.DescribeAsync(new() { Rpc = new RpcOptions { CancellationToken = ct } });
        return new ChildRead(description.Status.ToString(), result);
    }

    /// <summary>Writes records for the API to read (docs/02 section 5).</summary>
    [Activity]
    public async Task RecordAsync(RecordBatch batch)
    {
        var result = await services.RecordStore.AppendAsync(batch, ActivityExecutionContext.Current.CancellationToken);
        if (!result.Ok)
            throw new ApplicationFailureException($"record store: {result.Error!.Message}");
    }
}
