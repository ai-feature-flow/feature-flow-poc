using System.Text;
using System.Text.Json;
using FeatureFlow.Engine.ActionSteps;
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
public sealed record Prepared(IReadOnlyList<LibrarySnapshot> Libraries, RunPolicy Policy, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, string>? DefaultBranches = null);

/// <summary>A child run as its parent reads it: its Temporal status, its result (its run status), and the latest artifact of each name it produced.</summary>
public sealed record ChildRead(string Status, string? Result, IReadOnlyDictionary<string, string>? Artifacts = null);

/// <summary>A <c>clean_up</c> reset: the step it resets for and how often it has run, the run's repositories and work branch, and every step that has run (their hidden references).</summary>
public sealed record CleanUpCall(string RunId, string StepId, int Executions, IReadOnlyList<string> Repositories, string WorkBranch, IReadOnlyList<string> StepIds,
    bool DryRun);

/// <summary>
/// The engine's activities (docs/02 section 2.2). Provider errors are classified here (docs/06 section 1.1):
/// an infrastructure error is thrown, so Temporal retries it and no attempt is used; a work error is returned
/// for the core to count.
/// </summary>
public sealed class RunActivities(EngineServices services, ITemporalClient? client)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    readonly EngineActions actions = new(services.Providers);

    /// <summary>Run start (docs/07 section 7): load the locked libraries, then checks 18, 19, 21 and 22 against this worker's configuration.</summary>
    [Activity]
    public async Task<Prepared> PrepareAsync(RunStart start)
    {
        var ct = ActivityExecutionContext.Current.CancellationToken;
        var snapshots = await LoadLibrariesAsync(start.LockRef);
        var policy = start.Policy ?? services.Policy();
        var d = new DiagnosticBag();
        IReadOnlyDictionary<string, string>? defaultBranches = null;
        var libraries = Build(snapshots, d);
        var template = LibraryReference.TryParse(start.Template);
        if (template is null)
            d.Add(Checks.ReferenceResolves, $"'{start.Template}' is not a template reference");
        else
        {
            // The branch pattern is rendered as a new run's workflow renders it (patch m4-branch-pattern), for the work-branch check.
            var (core, loaded) = RunCore.Start(libraries, template, RunInfoOf(start), policy.ToOptions() with { BranchFromPattern = true });
            d.AddRange(loaded);
            RunStartChecks.LibraryRequirements(libraries, EngineInfo.Version, d);
            if (core is not null)
            {
                RunStartChecks.Capabilities(core.Plan, libraries, services.Providers.Offered(start.DryRun), d);
                RunStartChecks.Caps(core.Plan, services.Organisation.Policy, core.Allowance, d);
                if (services.Providers.ForRun("code_host", start.DryRun) is ICodeHostProvider host)
                    defaultBranches = await RepositoryChecksAsync(host, core, start, libraries, policy, d, ct);
            }
        }

        foreach (var type in new[] { "artifact_store", "record_store" })
            if (services.Providers.ForRun(type, start.DryRun) is { } provider && await provider.CheckAsync(ct) is { Ok: false } failed)
                throw new ApplicationFailureException($"{type} is not usable: {failed.Error!.Message}");

        return new Prepared(snapshots, policy,
            d.Where(x => x.Blocks).Select(x => x.ToString()).ToList(),
            d.Where(x => !x.Blocks).Select(x => x.ToString()).ToList(), defaultBranches);
    }

    /// <summary>
    /// The run-start checks that read the code host (docs/09 section 2), for each repository of the run: 24, its default
    /// branch is protected; 20, the library section of <c>feature-flow.yaml</c> on the default branch adds only; and 23,
    /// F1, for a run whose parent branch is the default branch: a merge step asking for an engine merge
    /// needs the organisation's leave for that repository. The default branch is read from the code host, never the plan.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> RepositoryChecksAsync(ICodeHostProvider host, RunCore core, RunStart start, LibrarySet libraries, RunPolicy policy, DiagnosticBag d,
        CancellationToken ct)
    {
        var defaults = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in start.Repositories)
        {
            var repo = new RepoRef(name);
            var branch = await host.GetDefaultBranchAsync(repo, ct);
            if (Readable(branch, $"{name}: the default branch", Checks.BranchProtection, d) is not { } defaultBranch)
                continue;
            defaults[name] = defaultBranch;
            // Check 36: the engine writes to its work branch freely; one that is the default or parent branch would put
            // those writes, and a clean_up's reset, straight onto it (review 11).
            var work = core.WorkBranch();
            if (work == defaultBranch || work == start.ParentBranch)
                d.Add(Checks.WorkBranch, $"{name}: the run's work branch '{work}' is its {(work == defaultBranch ? "default" : "parent")} branch; the engine writes only to a branch of its own (metadata.branch_pattern)");
            if (Readable(await host.GetBranchProtectionAsync(repo, defaultBranch, ct), $"{name}: the protection of {defaultBranch}", Checks.BranchProtection, d) is { } protection)
                RunStartChecks.BranchProtection(name, defaultBranch, protection.Protected, d);
            if (Readable(await host.ReadFileAsync(repo, RepositoryManifest.FileName, defaultBranch, ct), $"{name}: {RepositoryManifest.FileName}",
                    Checks.RepositoryLibraryAddsOnly, d) is { } file)
            {
                var parsed = new DiagnosticBag();
                var node = YamlText.Parse(file.Content, $"{name}:{RepositoryManifest.FileName}", parsed);
                if (parsed.HasErrors)
                    d.Add(Checks.RepositoryLibraryAddsOnly, $"{name}: {RepositoryManifest.FileName} on {defaultBranch} does not read, so its library section cannot be checked: {string.Join("; ", parsed.Select(x => x.Message))}");
                else
                    RunStartChecks.RepositoryLibrary(name, (node as YMap)?["library"], libraries, d);
            }
        }

        foreach (var step in core.Plan.AllSteps.Where(s => s is { Form: Model.Plans.StepForm.Action, Action: "scm.merge" }))
        {
            IReadOnlyList<string> targets;
            bool? engineCanMerge;
            try
            {
                targets = core.ResolveRepository(step.Id) is { } one ? [one] : start.Repositories;
            }
            catch (EvaluationException)
            {
                targets = start.Repositories;
            }

            try
            {
                engineCanMerge = core.ResolveInputs(step.Id).GetValueOrDefault("engine_can_merge", true) as bool?;
            }
            catch (EvaluationException)
            {
                engineCanMerge = null;
            }

            // F1 governs merges into the default branch only: a run whose parent branch is another (a child's, or one its
            // start names, LOG 137) merges there.
            foreach (var name in targets.Where(n => defaults.TryGetValue(n, out var branch) && (start.ParentBranch ?? branch) == branch))
                RunStartChecks.EngineMerge(step.Id, name, defaults[name], engineCanMerge, policy.EngineMayMerge ?? [], step.Location, d);
        }

        return defaults;
    }

    /// <summary>
    /// A code host read's value. A configuration fault (docs/06 section 1.1), like any work error, is a blocking
    /// diagnostic of the check that needed it, so the start is refused; any other infrastructure error is retried.
    /// </summary>
    static T? Readable<T>(ProviderResult<T> result, string what, int check, DiagnosticBag d)
    {
        if (result.Ok)
            return result.Value;
        if (result.Error!.IsConfigurationFault)
        {
            d.Add(check, $"{what} cannot be read: {result.Error.DescribeFault()}");
            return default;
        }

        if (result.Error!.Class == ProviderErrorClass.Infrastructure)
            throw new ApplicationFailureException($"{what}: {result.Error.Reason}: {result.Error.Message}", errorType: "infrastructure");
        d.Add(check, $"{what} cannot be read: {result.Error.Reason}: {result.Error.Message}");
        return default;
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
            start.ParentWorkflowId, ParentBranch: start.ParentBranch);

    // The agent step's activity, RunAgentStep, is the agent worker's: Engine.Agent's AgentActivities (M4).

    /// <summary>
    /// An engine action. One M5 builds (<see cref="EngineActions.Built"/>) runs against the providers when the
    /// installation configures real source control (<see cref="EngineActions.HasSourceControl"/>); with only fake
    /// providers, or in a dry run, it goes to the stand-in like every action not yet built (LOG 81, LOG 121). An action
    /// still waiting fails retryably with its poll interval as the next delay, so it holds no worker slot between reads.
    /// </summary>
    [Activity]
    public async Task<StepOutcome> RunActionAsync(StepCall call, ActionScope? scope = null)
    {
        var context = ActivityExecutionContext.Current;
        var ct = context.CancellationToken;
        if (EngineActions.Built.Contains(call.What) && !call.DryRun && scope is not null && actions.HasSourceControl)
        {
            ActionResult result;
            try
            {
                result = await actions.RunAsync(call, scope, () => context.Heartbeat(), ct);
            }
            catch (ActionInfrastructureException e)
            {
                throw new ApplicationFailureException(e.Message, errorType: "infrastructure");
            }

            if (result.Kind == ActionResult.Wait)
                throw new ApplicationFailureException($"waiting: {result.Reason}", errorType: "waiting", nextRetryDelay: result.Delay);
            return result.ToOutcome();
        }

        var standIn = services.Providers.Required<IActionStandIn>("action_stand_in", call.DryRun);
        var stood = await standIn.RunAsync(new ActionRequest(call.Step, call.What, call.Inputs, call.Contract), ct);
        return Classify(stood.Error) ?? new StepOutcome(StepOutcome.Reported, stood.Value, null);
    }

    /// <summary>A <c>clean_up</c> route's branch reset before its target step runs again (docs/03 section 4.1; M3 follow-up 5).</summary>
    [Activity]
    public async Task<CleanUpResult> CleanUpAsync(CleanUpCall call)
    {
        if (call.DryRun)
            return new CleanUpResult([$"clean_up: {call.StepId} (dry run: nothing is reset)"], null);
        try
        {
            return await actions.CleanUpAsync(call.RunId, call.StepId, call.Executions, call.Repositories, call.WorkBranch, call.StepIds,
                ActivityExecutionContext.Current.CancellationToken);
        }
        catch (ActionInfrastructureException e)
        {
            // Only infrastructure is retried; a work error comes back in the result and the run asks a person.
            throw new ApplicationFailureException(e.Message, errorType: "infrastructure");
        }
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
        if (error.IsConfigurationFault)
            return new StepOutcome(StepOutcome.CannotAnswer, null, error.DescribeFault());
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
        // The child's own artifacts from the artifact index (its run id is its workflow id): the latest of each name.
        var indexed = await services.RecordStore.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = workflowId }, ct);
        if (!indexed.Ok)
            throw new ApplicationFailureException($"the artifact index cannot be read: {indexed.Error!.Message}", errorType: "infrastructure");
        var artifacts = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var record in indexed.Value!.Where(r => r.RunId == workflowId))
            artifacts[record.Name] = record.Ref;
        return new ChildRead(description.Status.ToString(), result, artifacts);
    }

    /// <summary>
    /// Watches child runs a parent carried over a hand-over, which it can no longer await as children (M3 follow-up 10):
    /// returns, with the ids of those that have closed, as soon as one has, however it ended. A child that ends without
    /// signalling (terminated, or failed before it could) is noticed this way. It holds no state, so the parent cancels it
    /// at its next hand-over and the next run starts it again.
    /// </summary>
    [Activity]
    public async Task<IReadOnlyList<string>> WatchChildrenAsync(IReadOnlyList<string> workflowIds)
    {
        var context = ActivityExecutionContext.Current;
        var ct = context.CancellationToken;
        var temporal = client ?? throw new InvalidOperationException("no Temporal client");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task<string> ClosedAsync(string id)
        {
            var handle = temporal.GetWorkflowHandle(id);
            while (true)
            {
                try
                {
                    await handle.GetResultAsync<string>(rpcOptions: new RpcOptions { CancellationToken = stop.Token });
                    return id;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    // Failed, terminated or cancelled ends the wait as a failure; a read error does not, so describe it.
                    var description = await handle.DescribeAsync(new() { Rpc = new RpcOptions { CancellationToken = stop.Token } });
                    if (description.Status != Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Running)
                        return id;
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token);
                }
            }
        }

        var watches = workflowIds.Select(ClosedAsync).ToList();
        var any = Task.WhenAny(watches);
        while (!any.IsCompleted)
        {
            context.Heartbeat();
            await Task.WhenAny(any, Task.Delay(TimeSpan.FromSeconds(10), ct));
            ct.ThrowIfCancellationRequested();
        }

        // A watch that failed to read is retried by Temporal, not taken for an end.
        await await any;
        await stop.CancelAsync();
        return watches.Where(w => w.IsCompletedSuccessfully).Select(w => w.Result).ToList();
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
