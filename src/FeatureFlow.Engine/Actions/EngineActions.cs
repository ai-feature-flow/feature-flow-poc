using System.Globalization;
using System.Text;
using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Validation;

namespace FeatureFlow.Engine.Actions;

/// <summary>
/// The engine actions (docs/04 sections 7 to 9 and 12), called by <c>RunActionAsync</c>. Each call runs against the
/// providers the run uses (docs/06) and records what it did; an action that cannot produce a trustworthy outcome raises
/// an intervention instead of guessing (docs/04 section 1).
/// </summary>
public sealed class EngineActions(ProviderSet providers, ActionOptions? options = null)
{
    readonly ActionOptions options = options ?? ActionOptions.Default;

    internal ProviderSet Providers => providers;

    internal ActionOptions Options => options;

    /// <summary>
    /// Runs one action. Step-start check 31 comes first: inputs that do not satisfy the action's contract stop the step
    /// for a person. <paramref name="heartbeat"/> is called while the action polls. A configuration fault stops the step
    /// for a person (<see cref="Must{T}"/>); every other infrastructure error is thrown for Temporal to retry.
    /// </summary>
    public async Task<ActionResult> RunAsync(StepCall call, ActionScope scope, Action heartbeat, CancellationToken cancellationToken)
    {
        var run = new ActionRun(this, call, scope, heartbeat, cancellationToken);
        var inputs = call.Inputs.ToDictionary(kv => kv.Key, kv => YamlWriter.ReadPlain(kv.Value), StringComparer.Ordinal);
        if (StepStartChecks.ActionInputs(call.What, inputs) is { Count: > 0 } errors)
            return ActionResult.CannotAnswer($"check 31 ({Checks.Title(Checks.StepInputs)}): {string.Join("; ", errors)}", []);
        run.Inputs = inputs;
        if (Handler(call.What) is not { } handler)
            return ActionResult.CannotAnswer($"{call.What} has no handler in this engine", []);
        try
        {
            var result = await handler(run);
            await run.IndexEvidenceAsync();
            return result;
        }
        catch (ActionStop stop)
        {
            return new ActionResult(stop.Kind, null, stop.Message, TimeSpan.Zero, run.Events);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or System.Net.Sockets.SocketException or TimeoutException)
        {
            // A provider should return these as infrastructure errors; one that lets them escape is still retried, not
            // stopped for a person (re-check R1).
            throw new ActionInfrastructureException($"{call.What}: {e.GetType().Name}: {e.Message}");
        }
        catch (Exception e) when (e is not (ActionInfrastructureException or OperationCanceledException))
        {
            // A fault the action did not expect is not retried for ever: a person looks (docs/04 section 1, "cannot answer").
            return ActionResult.CannotAnswer($"{call.What} failed unexpectedly: {e.GetType().Name}: {e.Message}", run.Events);
        }
    }

    /// <summary>
    /// The handler of each action the engine carries out (<see cref="ActionHandling.Engine"/>), bound by the descriptor's name
    /// (LOG 357). <c>test.integration</c> is <c>test.run</c> across repositories (LOG 120).
    /// </summary>
    static readonly IReadOnlyDictionary<string, Func<ActionRun, Task<ActionResult>>> Handlers = new Dictionary<string, Func<ActionRun, Task<ActionResult>>>(StringComparer.Ordinal)
    {
        ["scm.catch_up"] = SourceControl.CatchUpAsync,
        ["scm.pull_request"] = SourceControl.PullRequestAsync,
        ["scm.merge"] = SourceControl.MergeAsync,
        ["scm.revert"] = SourceControl.RevertAsync,
        ["ci.checks"] = Waiting.ChecksAsync,
        ["wait.condition"] = Waiting.ConditionAsync,
        ["test.run"] = Testing.TestRunAsync,
        ["test.integration"] = Testing.IntegrationAsync,
        ["analysis.run"] = Testing.AnalysisAsync,
        ["repo.inspect"] = Testing.InspectAsync,
        ["artifact.validate"] = Validating.ValidateAsync,
        ["findings.record"] = FindingsActions.RecordAsync,
        ["findings.triage"] = FindingsActions.TriageAsync,
        ["delivery.report"] = Delivery.ReportAsync,
        ["artifact.compose"] = Artifacts.ComposeAsync,
        ["artifact.import"] = Artifacts.ImportAsync,
        ["artifact.publish"] = Artifacts.PublishAsync,
        ["tracker.update"] = Tracker.UpdateAsync,
    };

    static readonly AsyncLocal<IReadOnlyDictionary<string, Func<ActionRun, Task<ActionResult>>>?> Extra = new();

    /// <summary>The actions this engine has a handler for.</summary>
    internal static IReadOnlyCollection<string> Handled => Handlers.Keys.Concat(Extra.Value?.Keys ?? []).ToHashSet(StringComparer.Ordinal);

    static Func<ActionRun, Task<ActionResult>>? Handler(string action) => Extra.Value?.GetValueOrDefault(action) ?? Handlers.GetValueOrDefault(action);

    /// <summary>
    /// Adds an action as one entry, its descriptor and its handler together, until the scope is disposed (both are
    /// <see cref="AsyncLocal{T}"/>s, so they do not cross into Temporal's worker threads). For tests (LOG 357).
    /// </summary>
    internal static IDisposable Register(ActionDescriptor descriptor, Func<ActionRun, Task<ActionResult>> handler)
    {
        var previous = Extra.Value;
        var registry = ActionRegistry.Use(ActionRegistry.Current.With(descriptor));
        Extra.Value = new Dictionary<string, Func<ActionRun, Task<ActionResult>>>(previous ?? new Dictionary<string, Func<ActionRun, Task<ActionResult>>>(), StringComparer.Ordinal)
        {
            [descriptor.Name] = handler,
        };
        return new Restore(() =>
        {
            Extra.Value = previous;
            registry.Dispose();
        });
    }

    sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }

    /// <summary>
    /// A <c>clean_up</c> route (docs/03 section 4.1): resets the work branch in each repository to the revision the step
    /// started from, recorded when it last touched the repository, by an expected-revision push (M3 follow-up 5). What
    /// the reset removes is kept under <c>refs/feature-flow/superseded/...</c>, and the hidden references of the run's
    /// agent steps that build beyond that revision are moved back to it, so a re-run step does not pick up discarded
    /// commits (LOG 96). A step that never touched a repository resets nothing. A work error (a refused push) is
    /// returned, so the run asks a person; only infrastructure errors are thrown, to be retried. A dry run resets on its dry-run VCS.
    /// </summary>
    public async Task<CleanUpResult> CleanUpAsync(string runId, string stepId, int executions, IReadOnlyList<string> repositories, string workBranch,
        IReadOnlyList<string> stepIds, CancellationToken ct, bool dryRun = false)
    {
        var events = new List<string>();
        try
        {
            return new CleanUpResult(await ResetAsync(runId, stepId, executions, repositories, workBranch, stepIds, events, dryRun, ct), null);
        }
        catch (ActionStop stop)
        {
            // The resets made before the stop are still reported.
            return new CleanUpResult(events, stop.Message);
        }
    }

    async Task<IReadOnlyList<string>> ResetAsync(string runId, string stepId, int executions, IReadOnlyList<string> repositories, string workBranch,
        IReadOnlyList<string> stepIds, List<string> events, bool dryRun, CancellationToken ct)
    {
        if (providers.ForRun("vcs", dryRun) is not IVcsProvider vcs)
            return ["clean_up: no VCS provider is configured; nothing to reset"];
        var starts = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var n = executions; n >= 1 && starts.Count == 0; n--)
            foreach (var e in await EventsOfAsync($"{runId}/{stepId}/{n}", ct))
                if (e.Kind == ActionRun.StartEvent && e.Detail.Split(' ') is [var repo, var revision])
                    starts.TryAdd(repo, revision);
        if (starts.Count == 0)
            return [$"clean_up: {stepId} recorded no start revision in any repository; nothing to reset"];

        foreach (var name in repositories)
        {
            if (!starts.TryGetValue(name, out var start))
                continue;
            var repo = new RepoRef(name);
            var reference = "refs/heads/" + workBranch;
            var current = Must(await vcs.ResolveAsync(repo, reference, ct), "read the work branch");
            if (current is not null && current != start)
            {
                await KeepAsync(vcs, repo, WorkRefs.Superseded(runId, stepId, current), current, ct);
                Must(await vcs.PushAsync(repo, reference, start, null, current, ct), "reset the work branch");
                events.Add($"clean_up: reset {name} {workBranch} from {Revisions.Short(current)} to {Revisions.Short(start)}, where {stepId} started");
            }

            foreach (var step in stepIds)
            {
                var hidden = WorkRefs.Hidden(runId, step);
                if (Must(await vcs.ResolveAsync(repo, hidden, ct), "read a hidden reference") is not { } at || at == start
                    || Must(await vcs.DescendsFromAsync(repo, start, at, ct), "compare a hidden reference"))
                    continue;
                await KeepAsync(vcs, repo, WorkRefs.Superseded(runId, step, at), at, ct);
                Must(await vcs.PushAsync(repo, hidden, start, null, at, ct), "move a hidden reference back");
                events.Add($"clean_up: moved {name} {hidden} back from {Revisions.Short(at)} to {Revisions.Short(start)}");
            }
        }

        return events.Count == 0 ? [$"clean_up: {workBranch} is already where {stepId} started"] : events;
    }

    static async Task KeepAsync(IVcsProvider vcs, RepoRef repo, string reference, string revision, CancellationToken ct)
    {
        if (Must(await vcs.ResolveAsync(repo, reference, ct), "read a superseded reference") is null)
            Must(await vcs.PushAsync(repo, reference, revision, null, null, ct), "keep superseded commits");
    }

    internal async Task<IReadOnlyList<AgentEventRecord>> EventsOfAsync(string stepKey, CancellationToken ct) =>
        Must(await providers.Required<IRecordStoreProvider>("record_store").QueryAsync<AgentEventRecord>(Queries.AgentEvents,
            new Dictionary<string, string> { ["step_key"] = stepKey }, ct), "read the step's events");

    /// <summary>
    /// A provider result's value. A configuration fault stops the step for a person (docs/06 section 1.1); any other
    /// infrastructure error is thrown for a retry; any other error ends the step as a work failure.
    /// </summary>
    internal static T Must<T>(ProviderResult<T> result, string what)
    {
        if (result.Ok)
            return result.Value!;
        var error = result.Error!;
        if (error.IsConfigurationFault)
            throw new ActionStop(StepOutcome.CannotAnswer, $"{what}: {error.DescribeFault()}");
        if (error.Class == ProviderErrorClass.Infrastructure)
            throw new ActionInfrastructureException($"{what}: {error.Reason}: {error.Message}");
        throw new ActionStop(error.Reason == "cannot_answer" ? StepOutcome.CannotAnswer : StepOutcome.WorkFailed, $"{what}: {error.Reason}: {error.Message}");
    }

    /// <summary>The name an action's own evidence has in the artifact index, apart from any artifact the step declares (LOG 180).</summary>
    public static string IndexName(string type) => "evidence:" + type;
}

/// <summary>What a <c>clean_up</c> reset did, and the work error that stopped it, if one did.</summary>
public sealed record CleanUpResult(IReadOnlyList<string> Events, string? Error);

/// <summary>How often actions poll, and their default limits. Tests shorten them.</summary>
public sealed record ActionOptions(TimeSpan PollInterval, TimeSpan CiPollInterval, TimeSpan CommandTimeout)
{
    public static readonly ActionOptions Default = new(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(15), TimeSpan.FromMinutes(30));

    /// <summary>How long an external reviewer may stay pending before <c>findings.record</c> reports it <c>unavailable</c> (LOG 163).</summary>
    public TimeSpan ReviewTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

/// <summary>Ends an action early: a work failure, or an intervention (cannot answer).</summary>
sealed class ActionStop(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;

    public static ActionStop CannotAnswer(string message) => new(StepOutcome.CannotAnswer, message);

    public static ActionStop Failed(string message) => new(StepOutcome.WorkFailed, message);
}

/// <summary>One action call: its inputs and scope, the providers it uses, and what it records.</summary>
sealed class ActionRun(EngineActions owner, StepCall call, ActionScope scope, Action heartbeat, CancellationToken ct)
{
    /// <summary>The event recording the revision a step started from in a repository, which a <c>clean_up</c> route resets to.</summary>
    public const string StartEvent = "start";

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public StepCall Call => call;
    public ActionScope Scope => scope;
    public CancellationToken Ct => ct;
    public ActionOptions Options => owner.Options;

    internal ProviderSet Providers => owner.Providers;
    public List<string> Events { get; } = [];
    public IReadOnlyDictionary<string, object?> Inputs { get; set; } = new Dictionary<string, object?>();
    public string Key => call.Step.IdempotencyKey;

    public void Heartbeat() => heartbeat();

    // ---- Inputs ----

    public string? String(string name) => Inputs.GetValueOrDefault(name) as string;

    public bool Bool(string name, bool fallback) => Inputs.GetValueOrDefault(name) as bool? ?? fallback;

    public IReadOnlyList<string> Strings(string name) =>
        (Inputs.GetValueOrDefault(name) as IEnumerable<object?> ?? []).Select(x => Convert.ToString(x, CultureInfo.InvariantCulture) ?? "").ToList();

    public TimeSpan Duration(string name, TimeSpan fallback) => String(name) is { } text && Durations.Parse(text) is { } d ? d : fallback;

    // ---- Providers ----

    public T Provider<T>(string type) where T : class =>
        owner.Providers.ForRun(type, call.DryRun) as T ?? throw ActionStop.CannotAnswer($"{call.What} needs a {type} provider, and none that can serve it is configured");

    public T? Optional<T>(string type) where T : class => owner.Providers.ForRun(type, call.DryRun) as T;

    /// <summary>A provider instance by name, such as the findings source a step names (docs/07 section 2).</summary>
    public T? Instance<T>(string type, string name) where T : class => owner.Providers.Instance(type, name) as T;

    public IVcsProvider Vcs => Provider<IVcsProvider>("vcs");

    public ICodeHostProvider CodeHost => Provider<ICodeHostProvider>("code_host");

    IArtifactStoreProvider Store => owner.Providers.Required<IArtifactStoreProvider>("artifact_store");

    IRecordStoreProvider Records => owner.Providers.Required<IRecordStoreProvider>("record_store");

    public static T Must<T>(ProviderResult<T> result, string what) => EngineActions.Must(result, what);

    // ---- The repository ----

    /// <summary>The repository the step names, else the run's first (docs/04: <c>repository</c> defaults to the step's).</summary>
    public RepoRef Repository(string? name = null) =>
        new(name ?? String("repository") ?? scope.Repository ?? scope.Repositories.FirstOrDefault()
            ?? throw ActionStop.CannotAnswer($"{call.What} names no repository and the run has none"));

    public string WorkReference => "refs/heads/" + scope.WorkBranch;

    readonly Dictionary<string, string> parentBranches = new(StringComparer.Ordinal);

    /// <summary>A child run's parent branch, else the repository's default branch, read from the code host (F10; M4 follow-up 3).</summary>
    public async Task<string> ParentBranchAsync(RepoRef repo)
    {
        if (scope.ParentBranch is { } given)
            return given;
        if (!parentBranches.TryGetValue(repo.FullName, out var branch))
            parentBranches[repo.FullName] = branch = Must(await CodeHost.GetDefaultBranchAsync(repo, ct), "read the default branch");
        return branch;
    }

    /// <summary>
    /// The work branch's revision, creating the branch from the parent branch the first time a step touches the
    /// repository (docs/04 section 7). Records the revision the step started from, for a later <c>clean_up</c>.
    /// </summary>
    public async Task<string> WorkHeadAsync(RepoRef repo)
    {
        var vcs = Vcs;
        for (var tries = 0; ; tries++)
        {
            var head = Must(await vcs.ResolveAsync(repo, WorkReference, ct), "read the work branch");
            if (head is null)
            {
                var parent = await ParentBranchAsync(repo);
                var start = Must(await vcs.ResolveAsync(repo, "refs/heads/" + parent, ct), "read the parent branch")
                            ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no branch {parent} to start the work branch from");
                var pushed = await vcs.PushAsync(repo, WorkReference, start, null, null, ct);
                if (pushed is { Ok: false, Error.Reason: "base_moved" } && tries < 3)
                    continue;
                Must(pushed, "create the work branch");
                Events.Add($"created branch {scope.WorkBranch} in {repo.FullName} at {Revisions.Short(start)} from {parent}");
                head = start;
            }

            await EventAsync(StartEvent, $"{repo.FullName} {head}");
            return head;
        }
    }

    /// <summary>
    /// The repository manifest at a revision, read through the code host and recorded by hash (RW11). Null when the
    /// repository has none; a manifest that does not read stops the step for a person.
    /// </summary>
    public async Task<RepositoryManifest?> ManifestAsync(RepoRef repo, string revision)
    {
        var file = Must(await CodeHost.ReadFileAsync(repo, RepositoryManifest.FileName, revision, ct), "read the repository manifest");
        if (file is null)
            return null;
        RepositoryManifest manifest;
        try
        {
            manifest = RepositoryManifest.Parse(file.Content, file.Hash, $"{repo.FullName}:{RepositoryManifest.FileName} at {Revisions.Short(revision)}");
        }
        catch (FormatException e)
        {
            throw ActionStop.CannotAnswer(e.Message);
        }

        await EventAsync("manifest", $"{repo.FullName} {file.Hash} at {revision}");
        Events.Add($"manifest of {repo.FullName} at {Revisions.Short(revision)}: {file.Hash}");
        return manifest;
    }

    /// <summary>The pull request a <c>scm.pull_request</c> step of this run opened in the repository, if any.</summary>
    public PullRequestRef? PullRequest(RepoRef repo) =>
        scope.PullRequests.Select(ParsePullRequest).LastOrDefault(p => p is not null && p.Repo.FullName == repo.FullName);

    public static PullRequestRef? ParsePullRequest(string? text) =>
        text?.LastIndexOf('#') is { } at && at > 0 && int.TryParse(text[(at + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? new PullRequestRef(new RepoRef(text[..at]), n)
            : null;

    public static string Format(PullRequestRef pr) => $"{pr.Repo.FullName}#{pr.Number.ToString(CultureInfo.InvariantCulture)}";

    // ---- Records, evidence and reports ----

    public async Task EventAsync(string kind, string detail) =>
        Must(await Records.AppendAsync(new RecordBatch(AgentEvents: [new AgentEventRecord(Key, kind, detail, DateTimeOffset.UtcNow)]), ct), "record an event");

    public Task<IReadOnlyList<AgentEventRecord>> EventsAsync() => owner.EventsOfAsync(Key, ct);

    /// <summary>
    /// The first time this execution of the step recorded <paramref name="kind"/>, recording <paramref name="detail"/> now if
    /// it never did: what a polling action keeps across its retries (the time it began waiting, the head it waits on).
    /// </summary>
    public Task<string> RememberAsync(string kind, string detail) => RememberAsync(kind, () => Task.FromResult(detail));

    /// <summary>As above, making the detail only when none was recorded: a CI run is triggered once per execution of the step.</summary>
    public async Task<string> RememberAsync(string kind, Func<Task<string>> make)
    {
        if ((await EventsAsync()).FirstOrDefault(e => e.Kind == kind) is { } recorded)
            return recorded.Detail;
        var detail = await make();
        await EventAsync(kind, detail);
        return detail;
    }

    /// <summary>When this execution of the step began waiting, kept across the activity's retries.</summary>
    public async Task<DateTimeOffset> SinceAsync() =>
        DateTimeOffset.Parse(await RememberAsync("since", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)), CultureInfo.InvariantCulture);

    public async Task<string> PutJsonAsync(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return Must(await Store.PutAsync(new MemoryStream(bytes), ct), "store evidence").Id;
    }

    public async Task<string> ReadTextAsync(string reference)
    {
        await using var stream = Must(await Store.GetAsync(new ArtifactRef(reference), ct), $"read artifact {reference}");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    /// <summary>
    /// The run's evidence of a type (by its name, such as <c>test_results</c>), read as <typeparamref name="T"/>: only
    /// artifacts of a type marked <c>evidence: true</c> that one of <paramref name="producers"/> made. An agent's
    /// artifact, or one of an ordinary type with the same name, is never evidence (docs/04 section 10.1, F7). Unreadable
    /// ones are skipped.
    /// </summary>
    public async Task<List<(ArtifactEntry Entry, T Value)>> EvidenceAsync<T>(string type, params string[] producers)
    {
        var list = new List<(ArtifactEntry, T)>();
        foreach (var entry in scope.Artifacts.Where(a => a.Evidence && a.Action is { } action && producers.Contains(action) && TypeName(a.Type) == type))
        {
            try
            {
                if (JsonSerializer.Deserialize<T>(await ReadTextAsync(entry.Ref), Json) is { } value)
                    list.Add((entry, value));
            }
            catch (JsonException)
            {
                Events.Add($"{entry.StepId}'s {entry.Name} is not {type} evidence; ignored");
            }
        }

        return list;
    }

    static string? TypeName(string? type) => type is null ? null : LibraryReference.TryParse(type)?.Name ?? type;

    string? reportedEvidence;

    /// <summary>
    /// Marks the action's evidence for the artifact index when the step declares no artifact for the outcome (an action that
    /// builds its own report calls this). It is indexed as the action's evidence type (<see cref="ActionDescriptor.Evidence"/>)
    /// under <see cref="EngineActions.IndexName"/>, so a check that reads evidence, such as <c>delivery.report</c> reading
    /// reverts, does not depend on how the template was written, and no evidence is indexed twice (LOG 180).
    /// </summary>
    public void IndexIfUndeclared(string outcome, string? evidence) =>
        reportedEvidence = evidence is not null && !call.Contract.ArtifactsFor(outcome).Any() ? evidence : null;

    /// <summary>Writes evidence the report carried but the step does not declare into the artifact index; once per execution.</summary>
    public async Task IndexEvidenceAsync()
    {
        // A session's pre-submit check is not a step's execution: its evidence is not the run's (LOG 238).
        if (reportedEvidence is not { } reference || Model.Actions.Get(call.What)?.Evidence is not { } type || SessionNames.IsCheckKey(call.Step.IdempotencyKey))
            return;
        var record = new ArtifactRecord(call.Step.RunId, call.Step.StepId, call.Step.Execution, EngineActions.IndexName(type), type, reference, call.What, true, DateTimeOffset.UtcNow);
        Must(await Records.AppendAsync(new RecordBatch(Artifacts: [record]), ct), "index the evidence");
    }

    /// <summary>This run's evidence of a type that <paramref name="action"/> made, from the artifact index, read as <typeparamref name="T"/>.</summary>
    public async Task<List<T>> IndexedEvidenceAsync<T>(string type, string action)
    {
        var records = Must(await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = call.Step.RunId }, ct),
            "read the artifact index");
        var list = new List<T>();
        foreach (var reference in records.Where(r => r.RunId == call.Step.RunId && r.Evidence && r.Action == action && r.Name == EngineActions.IndexName(type)).Select(r => r.Ref).Distinct())
            try
            {
                if (JsonSerializer.Deserialize<T>(await ReadTextAsync(reference), Json) is { } value)
                    list.Add(value);
            }
            catch (JsonException)
            {
                Events.Add($"indexed {type} {reference} does not read; ignored");
            }

        return list;
    }

    /// <summary>A report with an outcome, outputs (plain values), and the evidence as every artifact the outcome promises.</summary>
    public ActionResult Report(string outcome, IReadOnlyDictionary<string, object?>? outputs = null, string? evidence = null)
    {
        IndexIfUndeclared(outcome, evidence);
        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        if (evidence is not null)
            foreach (var name in call.Contract.ArtifactsFor(outcome))
                artifacts[name] = evidence;
        if (evidence is not null && artifacts.Count == 0)
            Events.Add($"evidence {evidence} is not kept as an artifact: the step declares none for {outcome}");
        var values = new Dictionary<string, string>(call.Contract.ZeroOutputs, StringComparer.Ordinal);
        foreach (var (key, value) in outputs ?? new Dictionary<string, object?>())
            values[key] = YamlWriter.WritePlain(value);
        return ActionResult.Reported(new StepReport(outcome, values, artifacts, $"{call.Step.StepId}: {outcome}"), Events);
    }

    public static JsonSerializerOptions JsonOptions => Json;
}
