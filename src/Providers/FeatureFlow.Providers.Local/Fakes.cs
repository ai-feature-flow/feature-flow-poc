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
/// </summary>
public sealed class FakeScript
{
    readonly Dictionary<string, List<ScriptVisit>> steps;

    FakeScript(Dictionary<string, List<ScriptVisit>> steps) => this.steps = steps;

    public static readonly FakeScript Empty = new([]);

    public static FakeScript Read(string path)
    {
        var d = new DiagnosticBag();
        var r = MapReader.Of(YamlText.Parse(File.ReadAllText(path), path, d), d, "the fake script");
        var steps = new Dictionary<string, List<ScriptVisit>>(StringComparer.Ordinal);
        if (r is not null)
        {
            r.String("api_version");
            if (r.String("kind") is { } kind && kind != "fake_script")
                r.Error($"kind must be fake_script, not '{kind}'", r.Map.Location);
            if (MapReader.Of(r.Node("steps"), d, "steps") is { } s)
                foreach (var (key, value) in s.Map.Entries)
                {
                    s.Node(key.Text);
                    var visits = value is YSeq seq ? seq.Items : [value];
                    steps[key.Text] = visits.Select(v => ReadVisit(v, d)).ToList();
                }

            r.Finish();
        }

        if (d.HasErrors)
            throw new InvalidOperationException("the fake script does not read: " + string.Join("; ", d.Where(x => x.Blocks)));
        return new FakeScript(steps);
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
            Duration(r.String("delay")),
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

    /// <summary>A duration such as <c>500ms</c>, <c>2s</c> or <c>1m</c>.</summary>
    public static TimeSpan Duration(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return TimeSpan.Zero;
        var unit = text.EndsWith("ms", StringComparison.Ordinal) ? "ms" : text[^1..];
        var number = double.Parse(text[..^unit.Length], CultureInfo.InvariantCulture);
        return unit switch
        {
            "ms" => TimeSpan.FromMilliseconds(number),
            "s" => TimeSpan.FromSeconds(number),
            "m" => TimeSpan.FromMinutes(number),
            "h" => TimeSpan.FromHours(number),
            _ => throw new FormatException($"'{text}' is not a duration"),
        };
    }

    public ScriptVisit? Visit(StepIdentity step)
    {
        var visits = steps.GetValueOrDefault($"{step.Template}:{step.StepId}") ?? steps.GetValueOrDefault(step.StepId)
                     ?? steps.GetValueOrDefault($"{step.Template}:*");
        return visits is null || visits.Count == 0 ? null : visits[Math.Min(step.Execution - 1, visits.Count - 1)];
    }
}

/// <summary>
/// Builds a step's report from a visit, writing scripted artifact content to the artifact store. Shared by the
/// scripted runtime and the scripted action stand-in.
/// </summary>
sealed class Reporter(Func<IArtifactStoreProvider> store)
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
                ? node is YScalar { Plain: false } text ? text.Text : YamlWriter.Write(node)
                : JsonSerializer.Serialize(new { fake = $"{step.RunId}/{step.StepId}/{step.Execution}/{name}" });
            var put = await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), cancellationToken);
            if (!put.Ok)
                return ProviderResult<StepReport>.Failure(put.Error!.Reason, put.Error.Message);
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
/// A fake agent runtime driven by a script (M3: "the agent step backed by a scripted fake runtime"). A session
/// finishes after the visit's delay; sessions are kept in memory, keyed by the step's idempotency key, so a
/// retried start re-attaches.
/// </summary>
public sealed class ScriptedAgentRuntime(FakeScript script, Func<IArtifactStoreProvider> store) : IAgentRuntimeProvider
{
    readonly Reporter reporter = new(store);
    readonly ConcurrentDictionary<string, (AgentTask Task, DateTimeOffset Started, TimeSpan Delay, ScriptVisit? Visit)> sessions = new();

    public ProviderDescription Describe() => new("agent_runtime", "scripted", "0.1.0", CapabilityRecords.All("agent_runtime"));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken)
    {
        var visit = script.Visit(task.Step);
        if (reporter.ScriptedError(task.Step, visit) is { } error && error.Reason != "validation_failed")
            return Task.FromResult(new ProviderResult<AgentSession>(null, error));
        var delay = Reporter.Delay(visit, task.Inputs);
        sessions.TryAdd(task.Step.IdempotencyKey, (task, DateTimeOffset.UtcNow, delay, visit));
        return Task.FromResult(ProviderResult<AgentSession>.Success(new AgentSession(task.Step.IdempotencyKey, "scripted")));
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

/// <summary>The action stand-in driven by a script (LOG 81): what an action reports comes from the script, else its first success outcome.</summary>
public sealed class ScriptedActionStandIn(FakeScript script, Func<IArtifactStoreProvider> store) : IActionStandIn
{
    readonly Reporter reporter = new(store);

    public ProviderDescription Describe() => new("action_stand_in", "scripted", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public async Task<ProviderResult<StepReport>> RunAsync(ActionRequest request, CancellationToken cancellationToken)
    {
        var visit = script.Visit(request.Step);
        if (reporter.ScriptedError(request.Step, visit) is { } error)
            return new ProviderResult<StepReport>(null, error);
        if (Reporter.Delay(visit, request.Inputs) is var delay && delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
        return await reporter.ReportAsync(request.Step, request.Contract, request.Inputs, visit, cancellationToken);
    }
}

/// <summary>
/// The recording providers a <c>dry_run</c> run uses (docs/07 section 5): each call is written, with its inputs, to
/// <c>&lt;folder&gt;/&lt;run id&gt;.jsonl</c> instead of acting, and reports the step's first success outcome.
/// </summary>
public sealed class Recorder(string folder, Func<IArtifactStoreProvider> store)
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
        return await reporter.ReportAsync(step, contract, inputs, null, cancellationToken);
    }

    static string SafeName(string runId) => string.Concat(runId.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}

public sealed class RecordingAgentRuntime(Recorder recorder) : IAgentRuntimeProvider
{
    readonly ConcurrentDictionary<string, StepReport> results = new();

    public ProviderDescription Describe() => new("agent_runtime", "recording", "0.1.0", CapabilityRecords.All("agent_runtime"));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public async Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken)
    {
        if (!results.ContainsKey(task.Step.IdempotencyKey))
        {
            var report = await recorder.RecordAsync("agent", task.Step, task.Role, task.Inputs, task.Contract, cancellationToken);
            if (!report.Ok)
                return new ProviderResult<AgentSession>(null, report.Error);
            results[task.Step.IdempotencyKey] = report.Value!;
        }

        return ProviderResult<AgentSession>.Success(new AgentSession(task.Step.IdempotencyKey, "recording"));
    }

    public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult<AgentSession>.Success(new AgentSession(sessionRef, "recording")));

    public Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken) =>
        Task.FromResult(ProviderResult<AgentProgress>.Success(new AgentProgress(true, 0)));

    public Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken) =>
        Task.FromResult(results.TryGetValue(session.Id, out var report)
            ? ProviderResult<AgentResult>.Success(new AgentResult(report, SessionEnd.Completed))
            : ProviderResult<AgentResult>.Failure("not_found", $"no session {session.Id}"));

    public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
}

public sealed class RecordingActionStandIn(Recorder recorder) : IActionStandIn
{
    public ProviderDescription Describe() => new("action_stand_in", "recording", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<StepReport>> RunAsync(ActionRequest request, CancellationToken cancellationToken) =>
        recorder.RecordAsync("action", request.Step, request.Action, request.Inputs, request.Contract, cancellationToken);
}

/// <summary>
/// A provider of any type that only describes itself, offering every capability of the type: it lets capability
/// negotiation (check 21) pass for a type whose real provider a later milestone builds. It is never called.
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
            new("agent_runtime", "scripted", ["script"], c => new ScriptedAgentRuntime(Script(c), () => Store(c))),
            new("action_stand_in", "scripted", ["script"], c => new ScriptedActionStandIn(Script(c), () => Store(c))),
            new("agent_runtime", "recording", ["folder"], c => new RecordingAgentRuntime(new Recorder(c.PathSetting("folder") ?? c.RequiredSetting("folder"), () => Store(c)))),
            new("action_stand_in", "recording", ["folder"], c => new RecordingActionStandIn(new Recorder(c.PathSetting("folder") ?? c.RequiredSetting("folder"), () => Store(c)))),
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
        };
        foreach (var type in ConfigurationReader.ProviderTypes.Where(t => t is not ("artifact_store" or "record_store" or "secrets" or "action_stand_in")))
            list.Add(new ProviderFactory(type, "fake", [], _ => new DescribedOnlyProvider(type)));
        return list;
    }
}
