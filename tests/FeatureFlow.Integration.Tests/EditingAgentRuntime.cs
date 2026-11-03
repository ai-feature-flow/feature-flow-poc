using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using IOPath = System.IO.Path;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// The scripted runtime (M3) made to act in a <c>memory</c> sandbox (M5): a step with write access changes the
/// checkout, one file per execution, as an agent's edit would, and the artifacts the script reports are written to
/// the sandbox's output folder, so the agent step's real path (commit, push to the hidden reference, move the branch,
/// copy artifacts out) runs on the in-memory world. It also records the hook calls of an agent that reads every file of
/// the checkout, as the hook endpoint would, so std's reviewers do not re-read for ever (<c>changed_files_unread</c>).
/// Kind <c>editing</c>, settings <c>script</c> and <c>world</c>.
/// </summary>
public sealed class EditingAgentRuntime(ScriptedAgentRuntime inner, InMemoryForge forge, MemorySandbox sandboxes, Func<IArtifactStoreProvider> store,
    Func<IRecordStoreProvider> records) : IAgentRuntimeProvider
{
    readonly Dictionary<string, (SandboxHandle Sandbox, AgentTask Task)> sessions = [];

    public static ProviderFactory Factory { get; } = new("agent_runtime", "editing", ["script", "world"], c =>
    {
        var store = () => (IArtifactStoreProvider)c.Provider("artifact_store")!;
        var world = c.Setting("world") ?? "default";
        var script = c.PathSetting("script") is { } path ? FakeScript.Read(path) : FakeScript.Empty;
        return new EditingAgentRuntime(new ScriptedAgentRuntime(script, store), InMemoryForge.Named(world), MemorySandbox.Named(world, store), store,
            () => (IRecordStoreProvider)c.Provider("record_store")!);
    });

    public ProviderDescription Describe() => inner.Describe();

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

    public async Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken)
    {
        if (task.Paths is { } paths && forge.WorkingFiles(new WorkingCopyRef(sandbox, paths.Checkout)) is { } files)
        {
            List<string> read;
            lock (files)
            {
                if (task.WriteAccess != "none")
                    files[$"work/{task.Step.StepId}.txt"] = $"{task.Step.StepId} execution {task.Step.Execution}, session {task.Session}\n";
                read = files.Keys.ToList();
            }

            var at = DateTimeOffset.UtcNow;
            var hooks = read.Select(p => new AgentEventRecord(task.Step.IdempotencyKey, "hook_post_tool_use", $"Read {paths.Checkout}/{p}", at))
                .Prepend(new AgentEventRecord(task.Step.IdempotencyKey, "hook_pre_tool_use", "Read", at)).ToList();
            (await records().AppendAsync(new RecordBatch(AgentEvents: hooks), cancellationToken)).Unwrap();
        }

        var started = await inner.StartAsync(sandbox, task, limits, cancellationToken);
        if (started.Ok)
            lock (sessions)
                sessions[started.Value!.Id] = (sandbox, task);
        return started;
    }

    public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
        inner.ResumeAsync(sandbox, sessionRef, cancellationToken);

    public Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken) => inner.GetProgressAsync(session, cancellationToken);

    public async Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken)
    {
        var result = await inner.GetResultAsync(session, cancellationToken);
        if (!result.Ok || result.Value!.Report is not { } report)
            return result;
        (SandboxHandle Sandbox, AgentTask Task) started;
        lock (sessions)
            started = sessions[session.Id];
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, reference) in report.Artifacts)
        {
            await using var stream = (await store().GetAsync(new ArtifactRef(reference), cancellationToken)).Unwrap();
            var content = TriageEachOpen(started, await new StreamReader(stream).ReadToEndAsync(cancellationToken));
            sandboxes.WriteFile(started.Sandbox, $"{started.Task.Paths!.Output}/{name}.json", content);
            paths[name] = $"{name}.json";
        }

        return ProviderResult<AgentResult>.Success(result.Value with { Report = report with { Artifacts = paths } });
    }

    /// <summary>
    /// A scripted artifact <c>{ triage_each_open: &lt;state&gt; }</c> becomes a triage of every open finding in the ledger
    /// view the engine staged for the step (LOG 63), as a triager or verifying reviewer would write from it.
    /// </summary>
    string TriageEachOpen((SandboxHandle Sandbox, AgentTask Task) started, string content)
    {
        if (!content.Contains("triage_each_open", StringComparison.Ordinal))
            return content;
        var state = (string)((IDictionary<string, object?>)FeatureFlow.Model.Yaml.YamlWriter.ReadPlain(content)!)["triage_each_open"]!;
        var staged = sandboxes.ReadFile(started.Sandbox, "/ff/inputs/artifacts/" + FindingsLedger.ViewName)
                     ?? throw new InvalidOperationException($"{started.Task.Step.StepId}: no ledger view was staged");
        var view = ArtifactShapes.ReadLibrary<FindingsReport>(staged, "the staged ledger view");
        return ArtifactShapes.WriteLibrary(new Triage(1, view.Findings.Where(f => f.State == FindingStates.Open)
            .Select(f => new TriageEntry(f.LedgerId!, state, $"scripted: {state}")).ToList()));
    }

    public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken) => inner.StopAsync(session, cancellationToken);

    /// <summary>A stand-in for the sandbox client executable, which a memory sandbox never runs.</summary>
    public static string SandboxClient(string folder)
    {
        var path = IOPath.Combine(folder, "ff-sandbox");
        File.WriteAllText(path, "not run in a memory sandbox");
        return path;
    }
}
