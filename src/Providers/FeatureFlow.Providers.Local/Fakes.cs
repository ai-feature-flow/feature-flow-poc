using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// One visit of a scripted step: what it reports the n-th time it runs. Every field is optional; an empty visit
/// reports the step's first success outcome, every published output at its zero value, and the artifacts that
/// outcome promises with placeholder content.
/// </summary>
public sealed record ScriptVisit(
    string? Outcome,
    string? OutcomeFrom,
    YMap? Outputs,
    YMap? Artifacts,
    TimeSpan Delay,
    string? DelayFrom,
    string? Error,
    int Times,
    YMap? Patch,
    bool ToParent,
    string? Summary,
    YMap? LibraryOverride = null);

/// <summary>
/// A fake world's script (kind <c>fake_script</c>): visits per step, keyed <c>&lt;template&gt;:&lt;step id&gt;</c>
/// or <c>&lt;step id&gt;</c> for any template, then <c>&lt;template&gt;:*</c> for any other step of a template. A list's last visit repeats.
/// <c>defaults</c>, keyed the same way, holds the visits of steps <c>steps</c> does not name. A scripted artifact's text
/// may name a step input as <c>${name}</c>, which reports that input's value, and <c>${run_id}</c> or <c>${step_id}</c>.
/// </summary>
public sealed class FakeScript
{
    readonly Dictionary<string, List<ScriptVisit>> steps;
    readonly Dictionary<string, List<ScriptVisit>> defaults;

    FakeScript(Dictionary<string, List<ScriptVisit>> steps, Dictionary<string, List<ScriptVisit>> defaults) => (this.steps, this.defaults) = (steps, defaults);

    public static readonly FakeScript Empty = new([], []);

    public static FakeScript Read(string path)
    {
        var d = new DiagnosticBag();
        var r = MapReader.Of(YamlText.Parse(File.ReadAllText(path), path, d), d, "the fake script");
        var steps = new Dictionary<string, List<ScriptVisit>>(StringComparer.Ordinal);
        var defaults = new Dictionary<string, List<ScriptVisit>>(StringComparer.Ordinal);
        if (r is not null)
        {
            r.String("api_version");
            if (r.String("kind") is { } kind && kind != "fake_script")
                r.Error($"kind must be fake_script, not '{kind}'", r.Map.Location);
            ReadSteps(r, "steps", steps, d);
            ReadSteps(r, "defaults", defaults, d);
            r.Finish();
        }

        if (d.HasErrors)
            throw new InvalidOperationException("the fake script does not read: " + string.Join("; ", d.Where(x => x.Blocks)));
        return new FakeScript(steps, defaults);
    }

    static void ReadSteps(MapReader r, string section, Dictionary<string, List<ScriptVisit>> into, DiagnosticBag d)
    {
        if (MapReader.Of(r.Node(section), d, section) is not { } s)
            return;
        foreach (var (key, value) in s.Map.Entries)
        {
            s.Node(key.Text);
            var visits = value is YSeq seq ? seq.Items : [value];
            into[key.Text] = visits.Select(v => ReadVisit(v, d)).ToList();
        }
    }

    static ScriptVisit ReadVisit(YNode node, DiagnosticBag d)
    {
        if (node is YScalar { IsNull: false } s)
            return new ScriptVisit(s.Text, null, null, null, TimeSpan.Zero, null, null, 0, null, false, null);
        var r = MapReader.Of(node, d, "a visit")!;
        var visit = new ScriptVisit(
            r.String("outcome"),
            r.String("outcome_from"),
            r.Node("outputs") as YMap,
            r.Node("artifacts") as YMap,
            r.String("delay") is { Length: > 0 } delay ? Durations.Parse(delay) ?? throw new FormatException($"'{delay}' is not a duration") : TimeSpan.Zero,
            r.String("delay_from"),
            r.Enum("error", ["work", "infrastructure", "cannot_answer"]),
            (int)(r.Int("times") ?? 1),
            r.Node("patch") as YMap,
            r.Bool("to_parent") ?? false,
            r.String("summary"),
            r.Node("library_override") as YMap);
        r.Finish();
        return visit;
    }

    public ScriptVisit? Visit(StepIdentity step)
    {
        List<ScriptVisit>? Find(Dictionary<string, List<ScriptVisit>> visits) =>
            visits.GetValueOrDefault($"{step.Template}:{step.StepId}") ?? visits.GetValueOrDefault(step.StepId) ?? visits.GetValueOrDefault($"{step.Template}:*");
        var visits = Find(steps) ?? Find(defaults);
        return visits is null || visits.Count == 0 ? null : visits[Math.Min(step.Execution - 1, visits.Count - 1)];
    }
}

/// <summary>
/// Builds a step's report from a visit, writing scripted artifact content to the artifact store. Shared by the
/// scripted and recording runtimes.
/// </summary>
sealed partial class Reporter(Func<IArtifactStoreProvider> store)
{
    public async Task<ProviderResult<StepReport>> ReportAsync(StepIdentity step, StepContract contract, IReadOnlyDictionary<string, string> inputs,
        ScriptVisit? visit, CancellationToken cancellationToken)
    {
        var outcome = visit?.Outcome
                      ?? (visit?.OutcomeFrom is { } from && inputs.TryGetValue(from, out var given) ? YamlWriter.ReadPlain(given) as string : null)
                      ?? contract.FirstSuccess;
        if (outcome is null)
            return ProviderResult<StepReport>.Failure("validation_failed", $"{step.StepId} has no success outcome to report; script it");

        var outputs = new Dictionary<string, string>(contract.ZeroOutputs, StringComparer.Ordinal);
        foreach (var (key, value) in visit?.Outputs?.Entries ?? [])
            if (value is YScalar { IsNull: true })
                outputs.Remove(key.Text);
            else
                outputs[key.Text] = YamlWriter.Write(value);

        var artifacts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in contract.ArtifactsFor(outcome))
        {
            var content = visit?.Artifacts?[name] is { } node
                ? Fill(node is YScalar { Plain: false } text ? text.Text : YamlWriter.Write(node), step, inputs)
                : JsonSerializer.Serialize(new { fake = $"{step.RunId}/{step.StepId}/{step.Execution}/{name}" });
            var put = await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), cancellationToken);
            if (!put.Ok)
                return new ProviderResult<StepReport>(null, put.Error);
            artifacts[name] = put.Value!.Id;
        }

        return ProviderResult<StepReport>.Success(new StepReport(outcome, outputs, artifacts, visit?.Summary ?? $"{step.StepId}: {outcome}",
            visit?.Patch is { } patch ? YamlWriter.Write(patch) : null, visit?.ToParent ?? false)
        {
            LibraryOverride = visit?.LibraryOverride is { } proposed
                ? proposed.Entries.ToDictionary(e => e.Key.Text, e => e.Value.ScalarText() ?? YamlWriter.Write(e.Value))
                : null,
        });
    }

    /// <summary>
    /// Scripted artifact content with each <c>${name}</c> replaced by the step's input <c>name</c>, as plain text, so one
    /// visit serves steps whose inputs differ (each child ticket of an epic plans its own <c>subject_id</c>).
    /// <c>${run_id}</c> and <c>${step_id}</c> name the step's run and step, as the scripted edit's path does. A name the
    /// step has no input for is left as written.
    /// </summary>
    static string Fill(string text, StepIdentity step, IReadOnlyDictionary<string, string> inputs) =>
        Placeholder().Replace(text, m => m.Groups[1].Value switch
        {
            "run_id" => step.RunId,
            "step_id" => step.StepId,
            var name when inputs.TryGetValue(name, out var value) => Convert.ToString(YamlWriter.ReadPlain(value), CultureInfo.InvariantCulture) ?? value,
            _ => m.Value,
        });

    [System.Text.RegularExpressions.GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial System.Text.RegularExpressions.Regex Placeholder();

    /// <summary>The visit's delay: <c>delay</c>, or <c>delay_from</c>, an input holding milliseconds.</summary>
    public static TimeSpan Delay(ScriptVisit? visit, IReadOnlyDictionary<string, string> inputs) =>
        visit?.DelayFrom is { } from && inputs.TryGetValue(from, out var ms) && YamlWriter.ReadPlain(ms) is long n
            ? TimeSpan.FromMilliseconds(n)
            : visit?.Delay ?? TimeSpan.Zero;

    /// <summary>Infrastructure errors the script asks for, counted per step execution: the call fails <c>times</c> times, then goes on.</summary>
    readonly ConcurrentDictionary<string, int> failures = new();

    public ProviderError? ScriptedError(StepIdentity step, ScriptVisit? visit)
    {
        switch (visit?.Error)
        {
            case "work":
                return new ProviderError("validation_failed", $"scripted work failure at {step.StepId}");
            case "cannot_answer":
                return new ProviderError("cannot_answer", $"scripted: {step.StepId} cannot answer");
            case "infrastructure":
                var count = failures.AddOrUpdate(step.IdempotencyKey, 1, (_, n) => n + 1);
                return count <= visit.Times ? new ProviderError("unreachable", $"scripted infrastructure failure {count} of {visit.Times} at {step.StepId}") : null;
            default:
                return null;
        }
    }
}

/// <summary>
/// What the scripted and recording runtimes do in the sandbox an agent step runs in, through the sandbox provider's own
/// calls, so the step's real path (commit, push to the hidden reference, move the branch, copy artifacts out) runs as with
/// a real agent in any sandbox: a step with write access changes the checkout, one file per run and step
/// (<c>work/&lt;run id&gt;/&lt;step id&gt;.txt</c>, so sibling runs do not edit the same file) rewritten each execution, as an agent's edit
/// would (unless its step id is in <c>no_edits</c>, such as a fixer that commits no fix); the hook calls of an agent that
/// reads every file of the checkout are recorded, as the hook endpoint would, so std's reviewers do not re-read for ever
/// (<c>changed_files_unread</c>); and the artifacts the report names are copied into the sandbox's output folder. The
/// sandbox is the <c>memory</c> one of the runtime's <c>world</c> when that holds it, else the installation's default
/// sandbox provider's (LOG 371). With no sandbox (no step paths) the report keeps its artifact store references.
/// </summary>
sealed class SandboxActing(InMemoryForge forge, MemorySandbox memory, Func<ISandboxProvider?> installed, Func<IArtifactStoreProvider> store,
    Func<IRecordStoreProvider?> records, IReadOnlySet<string> noEdits)
{
    public static SandboxActing Of(ProviderContext c, Func<IArtifactStoreProvider> store)
    {
        var world = c.Setting("world") ?? "default";
        var noEdits = c.Instance.Settings["no_edits"] is YSeq steps ? steps.Items.Select(s => s.ScalarText() ?? "").ToHashSet(StringComparer.Ordinal) : [];
        return new SandboxActing(InMemoryForge.Named(world), MemorySandbox.Named(world, store), () => c.Provider("sandbox") as ISandboxProvider, store,
            () => c.Provider("record_store") as IRecordStoreProvider, noEdits);
    }

    ISandboxProvider? SandboxOf(SandboxHandle sandbox, AgentTask task) =>
        task.Paths is null || sandbox == SandboxHandle.None ? null : memory.Holds(sandbox) ? memory : installed();

    /// <summary>The agent's edit and reads, at the start of a session.</summary>
    public async Task<ProviderError?> StartAsync(SandboxHandle sandbox, AgentTask task, CancellationToken cancellationToken)
    {
        if (SandboxOf(sandbox, task) is not { } sandboxes)
            return null;
        var checkout = task.Paths!.Checkout;
        if (task.WriteAccess != "none" && !noEdits.Contains(task.Step.StepId))
        {
            var edit = await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes($"{task.Step.StepId} execution {task.Step.Execution}, session {task.Session}\n")),
                cancellationToken);
            if (!edit.Ok)
                return edit.Error;
            if ((await sandboxes.CopyInAsync(sandbox, edit.Value!, $"{checkout}/work/{task.Step.RunId}/{task.Step.StepId}.txt", readOnly: false, cancellationToken)).Error is { } failed)
                return failed;
        }

        var read = await FilesAsync(sandboxes, sandbox, task, cancellationToken);
        if (!read.Ok)
            return read.Error;
        if (records() is not { } recordStore)
            return null;
        var at = DateTimeOffset.UtcNow;
        var hooks = read.Value!.Select(p => new AgentEventRecord(task.Step.IdempotencyKey, "hook_post_tool_use", $"Read {checkout}/{p}", at))
            .Prepend(new AgentEventRecord(task.Step.IdempotencyKey, "hook_pre_tool_use", "Read", at)).ToList();
        return (await recordStore.AppendAsync(new RecordBatch(AgentEvents: hooks), cancellationToken)).Error;
    }

    /// <summary>The checkout's files: the in-memory working copy's, else what git lists in the sandbox (tracked and new).</summary>
    async Task<ProviderResult<IReadOnlyList<string>>> FilesAsync(ISandboxProvider sandboxes, SandboxHandle sandbox, AgentTask task, CancellationToken cancellationToken)
    {
        if (sandboxes == memory)
        {
            if (forge.WorkingFiles(new WorkingCopyRef(sandbox, task.Paths!.Checkout)) is not { } files)
                return ProviderResult<IReadOnlyList<string>>.Success([]);
            lock (forge.Gate)
                return ProviderResult<IReadOnlyList<string>>.Success(files.Keys.ToList());
        }

        var listed = await sandboxes.RunAsync(sandbox, new SandboxCommand(["git", "ls-files", "--cached", "--others", "--exclude-standard"], WorkingDirectory: task.Paths!.Checkout),
            $"scripted-files/{Guid.NewGuid():N}", cancellationToken);
        if (!listed.Ok)
            return new ProviderResult<IReadOnlyList<string>>(null, listed.Error);
        return listed.Value.Exit == 0
            ? ProviderResult<IReadOnlyList<string>>.Success(listed.Value.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            : ProviderResult<IReadOnlyList<string>>.Failure("internal_error", $"git ls-files in {task.Paths.Checkout} exited {listed.Value.Exit}: {listed.Value.Output}");
    }

    /// <summary>The report with its artifacts copied into the sandbox's output folder, named by their paths there.</summary>
    public async Task<ProviderResult<StepReport>> PlaceAsync(SandboxHandle sandbox, AgentTask task, StepReport report, CancellationToken cancellationToken)
    {
        if (SandboxOf(sandbox, task) is not { } sandboxes)
            return ProviderResult<StepReport>.Success(report);
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, reference) in report.Artifacts)
        {
            var source = new ArtifactRef(reference);
            var got = await store().GetAsync(source, cancellationToken);
            if (!got.Ok)
                return new ProviderResult<StepReport>(null, got.Error);
            string content;
            await using (var stream = got.Value!)
                content = await new StreamReader(stream).ReadToEndAsync(cancellationToken);
            var triaged = await TriageEachOpenAsync(sandboxes, sandbox, task, content, cancellationToken);
            if (triaged != content)
            {
                var put = await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(triaged)), cancellationToken);
                if (!put.Ok)
                    return new ProviderResult<StepReport>(null, put.Error);
                source = put.Value!;
            }

            if ((await sandboxes.CopyInAsync(sandbox, source, $"{task.Paths!.Output}/{name}.json", readOnly: false, cancellationToken)).Error is { } failed)
                return new ProviderResult<StepReport>(null, failed);
            paths[name] = $"{name}.json";
        }

        return ProviderResult<StepReport>.Success(report with { Artifacts = paths });
    }

    /// <summary>
    /// A scripted artifact <c>{ triage_each_open: &lt;state&gt; }</c> becomes a triage of every open finding in the ledger
    /// view the engine staged for the step (LOG 63), as a triager or verifying reviewer would write from it. With
    /// <c>non_blocking: &lt;state&gt;</c> too, open findings that are not blocking get that state instead.
    /// </summary>
    async Task<string> TriageEachOpenAsync(ISandboxProvider sandboxes, SandboxHandle sandbox, AgentTask task, string content, CancellationToken cancellationToken)
    {
        if (!content.Contains("triage_each_open", StringComparison.Ordinal))
            return content;
        var script = (IDictionary<string, object?>)YamlWriter.ReadPlain(content)!;
        var state = (string)script["triage_each_open"]!;
        var nonBlocking = script.TryGetValue("non_blocking", out var other) ? (string)other! : state;
        var copied = await sandboxes.CopyOutAsync(sandbox, "/ff/inputs/artifacts/" + FindingsLedger.ViewName, cancellationToken);
        if (!copied.Ok)
            throw new InvalidOperationException($"{task.Step.StepId}: no ledger view was staged: {copied.Error!.Message}");
        string staged;
        await using (var stream = (await store().GetAsync(copied.Value!, cancellationToken)).Value!)
            staged = await new StreamReader(stream).ReadToEndAsync(cancellationToken);
        var view = ArtifactShapes.ReadLibrary<FindingsReport>(staged, "the staged ledger view");
        return ArtifactShapes.WriteLibrary(new Triage(1, view.Findings.Where(f => f.State == FindingStates.Open)
            .Select(f => new TriageEntry(f.LedgerId!, f.Blocking ? state : nonBlocking, $"scripted: {(f.Blocking ? state : nonBlocking)}")).ToList()));
    }
}

/// <summary>
/// A fake agent runtime driven by a script (M3: "the agent step backed by a scripted fake runtime"). A session
/// finishes after the visit's delay; sessions are kept in memory, keyed by the step's idempotency key, so a
/// retried start re-attaches. In a <c>memory</c> sandbox of its <c>world</c> it acts as <see cref="SandboxActing"/> says.
/// </summary>
public sealed class ScriptedAgentRuntime(FakeScript script, Func<IArtifactStoreProvider> store) : IAgentRuntimeProvider
{
    readonly Reporter reporter = new(store);
    readonly ConcurrentDictionary<string, (AgentTask Task, DateTimeOffset Started, TimeSpan Delay, ScriptVisit? Visit, SandboxHandle Sandbox)> sessions = new();

    internal SandboxActing? Acting { get; init; }

    public ProviderDescription Describe() => new("agent_runtime", "scripted", "0.1.0", CapabilityRecords.All("agent_runtime"));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public async Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken)
    {
        var visit = script.Visit(task.Step);
        if (reporter.ScriptedError(task.Step, visit) is { } error && error.Reason != "validation_failed")
            return new ProviderResult<AgentSession>(null, error);
        if (Acting is not null && await Acting.StartAsync(sandbox, task, cancellationToken) is { } failed)
            return new ProviderResult<AgentSession>(null, failed);
        var delay = Reporter.Delay(visit, task.Inputs);
        sessions.TryAdd(task.Step.IdempotencyKey, (task, DateTimeOffset.UtcNow, delay, visit, sandbox));
        return ProviderResult<AgentSession>.Success(new AgentSession(task.Step.IdempotencyKey, "scripted"));
    }

    public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.ContainsKey(sessionRef)
            ? ProviderResult<AgentSession>.Success(new AgentSession(sessionRef, "scripted"))
            : ProviderResult<AgentSession>.Failure("not_found", $"no session {sessionRef}"));

    public Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken) =>
        Task.FromResult(sessions.TryGetValue(session.Id, out var s)
            ? ProviderResult<AgentProgress>.Success(new AgentProgress(DateTimeOffset.UtcNow - s.Started >= s.Delay, 1))
            : ProviderResult<AgentProgress>.Failure("not_found", $"no session {session.Id}"));

    public async Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!sessions.TryGetValue(session.Id, out var s))
            return ProviderResult<AgentResult>.Failure("not_found", $"no session {session.Id}");
        if (reporter.ScriptedError(s.Task.Step, s.Visit) is { Reason: "validation_failed" } work)
            return new ProviderResult<AgentResult>(null, work);
        var report = await reporter.ReportAsync(s.Task.Step, s.Task.Contract, s.Task.Inputs, s.Visit, cancellationToken);
        if (report.Ok && Acting is not null)
            report = await Acting.PlaceAsync(s.Sandbox, s.Task, report.Value!, cancellationToken);
        return report.Ok
            ? ProviderResult<AgentResult>.Success(new AgentResult(report.Value, SessionEnd.Completed))
            : new ProviderResult<AgentResult>(null, report.Error);
    }

    public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken)
    {
        sessions.TryRemove(session.Id, out _);
        return Task.FromResult(ProviderResult<Unit>.Success(default));
    }
}

/// <summary>
/// What the recording runtime a <c>dry_run</c> run uses writes (docs/07 section 5): each call, with its inputs, to
/// <c>&lt;folder&gt;/&lt;run id&gt;.jsonl</c>. It reports what its <c>script</c> says for the step, else the step's first
/// success outcome.
/// </summary>
public sealed class Recorder(string folder, Func<IArtifactStoreProvider> store, FakeScript? script = null)
{
    readonly Reporter reporter = new(store);
    static readonly object Gate = new();

    public string Folder { get; } = folder;

    public async Task<ProviderResult<StepReport>> RecordAsync(string kind, StepIdentity step, string what, IReadOnlyDictionary<string, string> inputs,
        StepContract contract, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);
        var line = JsonSerializer.Serialize(new { kind, run = step.RunId, template = step.Template, step = step.StepId, execution = step.Execution, what, inputs });
        lock (Gate)
            File.AppendAllText(Path.Combine(Folder, SafeName(step.RunId) + ".jsonl"), line + "\n");
        return await reporter.ReportAsync(step, contract, inputs, script?.Visit(step), cancellationToken);
    }

    static string SafeName(string runId) => string.Concat(runId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}

/// <summary>
/// The agent runtime a <c>dry_run</c> run uses (docs/07 section 5): it records each step it is given, with its inputs, and
/// reports as its <see cref="Recorder"/> says; in a <c>memory</c> sandbox of its <c>world</c> it acts as
/// <see cref="SandboxActing"/> says, so the dry run's own steps run on its memory world.
/// </summary>
public sealed class RecordingAgentRuntime(Recorder recorder) : IAgentRuntimeProvider
{
    readonly ConcurrentDictionary<string, (StepReport Report, SandboxHandle Sandbox, AgentTask Task)> results = new();

    internal SandboxActing? Acting { get; init; }

    public ProviderDescription Describe() => new("agent_runtime", "recording", "0.1.0", CapabilityRecords.All("agent_runtime"));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public async Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken)
    {
        if (!results.ContainsKey(task.Step.IdempotencyKey))
        {
            if (Acting is not null && await Acting.StartAsync(sandbox, task, cancellationToken) is { } failed)
                return new ProviderResult<AgentSession>(null, failed);
            var report = await recorder.RecordAsync("agent", task.Step, task.Role, task.Inputs, task.Contract, cancellationToken);
            if (!report.Ok)
                return new ProviderResult<AgentSession>(null, report.Error);
            results[task.Step.IdempotencyKey] = (report.Value!, sandbox, task);
        }

        return ProviderResult<AgentSession>.Success(new AgentSession(task.Step.IdempotencyKey, "recording"));
    }

    public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult<AgentSession>.Success(new AgentSession(sessionRef, "recording")));

    public Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult<AgentProgress>.Success(new AgentProgress(true, 0)));

    public async Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken)
    {
        if (!results.TryGetValue(session.Id, out var s))
            return ProviderResult<AgentResult>.Failure("not_found", $"no session {session.Id}");
        var report = Acting is null ? ProviderResult<StepReport>.Success(s.Report) : await Acting.PlaceAsync(s.Sandbox, s.Task, s.Report, cancellationToken);
        return report.Ok
            ? ProviderResult<AgentResult>.Success(new AgentResult(report.Value, SessionEnd.Completed))
            : new ProviderResult<AgentResult>(null, report.Error);
    }

    public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
}

/// <summary>
/// A provider of any type that only describes itself (kind <c>fake</c>), offering every capability of the type, so
/// capability negotiation (check 21) passes for a type the installation cannot serve. It is never called: a step that
/// reaches it stops at an intervention. It is used where a plan needs a type its runs never call: the k3d smoke
/// installation's <c>code_host</c> (<c>deploy/k3d/smoke</c>; std's templates need <c>code_host.pull_request</c>) and the
/// integration tests' agent harness (<c>AgentHarness</c>).
/// </summary>
public sealed class DescribedOnlyProvider(string type) : IProvider
{
    public ProviderDescription Describe() => new(type, "fake", "0.1.0", CapabilityRecords.All(type));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
}

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class LocalProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } = Build();

    static IReadOnlyList<ProviderFactory> Build()
    {
        IArtifactStoreProvider Store(ProviderContext c) =>
            c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("the fake and recording providers need an artifact store");
        FakeScript Script(ProviderContext c) => c.PathSetting("script") is { } path ? FakeScript.Read(path) : FakeScript.Empty;

        var list = new List<ProviderFactory>
        {
            new("artifact_store", "filesystem", ["root"], c => new FileSystemArtifactStore(c.PathSetting("root") ?? c.RequiredSetting("root"))),
            new("record_store", "sqlite", ["path"], c => new SqliteRecordStore(c.PathSetting("path") ?? c.RequiredSetting("path"))),
            new("secrets", "file", ["path", "from_environment"], c => new FileSecrets(c.PathSetting("path") ?? c.RequiredSetting("path"),
                c.Instance.Settings["from_environment"] is YSeq names ? names.Items.Select(n => n.ScalarText() ?? "").ToList() : null)),
            new("agent_runtime", "scripted", ["script", "world", "no_edits"], c => new ScriptedAgentRuntime(Script(c), () => Store(c)) { Acting = SandboxActing.Of(c, () => Store(c)) }),
            new("agent_runtime", "recording", ["folder", "script", "world", "no_edits"], c => new RecordingAgentRuntime(
                new Recorder(c.PathSetting("folder") ?? c.RequiredSetting("folder"), () => Store(c), Script(c)))
            {
                Acting = SandboxActing.Of(c, () => Store(c)),
            }),
            // M5: in-memory source control for engine tests; providers with the same world share it.
            new("vcs", "memory", ["world"], c => new InMemoryVcs(InMemoryForge.Named(c.Setting("world") ?? "default"))),
            new("code_host", "memory", ["world"], c => new InMemoryCodeHost(InMemoryForge.Named(c.Setting("world") ?? "default"))),
            new("ci", "memory", ["world"], c => new InMemoryCi(InMemoryForge.Named(c.Setting("world") ?? "default"), () => Store(c))),
            new("sandbox", "memory", ["world"], c => MemorySandbox.Named(c.Setting("world") ?? "default", () => Store(c))),
            // M6: in-memory tracker, findings source and validators for engine tests.
            new("tracker", "memory", ["world"], c => InMemoryTracker.Named(c.Setting("world") ?? "default")),
            new("findings_source", "memory", ["world"], c => InMemoryFindingsSource.Named(c.Setting("world") ?? "default", () => Store(c))),
            new("validator", "memory", [], _ => new InMemoryValidators()),
            // M7: in-memory notifications and session surface for engine tests.
            new("notifications", "memory", ["world"], c => InMemoryNotifications.Named(c.Setting("world") ?? "default")),
            new("session_surface", "memory", ["world"], c => InMemorySessionSurface.Named(c.Setting("world") ?? "default")),
            // M7: version 1's session surface, a hand-off to the person's own agent tool (RW18).
            new("session_surface", "local", [], _ => new LocalSessionSurface()),
        };
        foreach (var type in ConfigurationReader.ProviderTypes.Where(t => t is not ("artifact_store" or "record_store" or "secrets")))
            list.Add(new ProviderFactory(type, "fake", [], _ => new DescribedOnlyProvider(type)));
        return list;
    }
}
