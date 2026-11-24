using FeatureFlow.Engine.Agent;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Testing;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Agent.Tests;

/// <summary>
/// A refused model key is a configuration fault, judged from the engine's own model records (LOG 499): a session that
/// ended <c>failed</c> whose model requests the upstream all refused with 401 or 403 stops the step for a person, whatever
/// the runtime printed. Copilot CLI in native mode prints such a refusal as plain text and exits 1.
/// </summary>
public sealed class ModelRefusalTests : IAsyncDisposable
{
    const string Key = "run-1/implement/1";

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-model-refusal-" + Guid.NewGuid().ToString("N"));
    readonly InMemoryForge forge = new();
    readonly RepoRef repo;
    readonly FileSystemArtifactStore store;
    readonly MemorySandbox sandbox;
    readonly SqliteRecordStore records;

    public ModelRefusalTests()
    {
        Directory.CreateDirectory(folder);
        repo = forge.CreateRepository("acme/api", new Dictionary<string, string> { ["README.md"] = "api" });
        store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        sandbox = new MemorySandbox(() => store, forge);
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(folder, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>A runtime whose session makes model requests the proxy records with these statuses, then exits with an error of its own.</summary>
    sealed class FailingRuntime(SqliteRecordStore records, IReadOnlyList<int> statuses) : IAgentRuntimeProvider
    {
        public ProviderDescription Describe() => new("agent_runtime", "failing", "test", new NoCapabilities());

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

        public async Task<ProviderResult<AgentSession>> StartAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, CancellationToken cancellationToken)
        {
            foreach (var status in statuses)
                (await records.AppendAsync(new RecordBatch(AgentEvents: [new AgentEventRecord(Key, "model",
                    $"POST /v1/messages {status} in=0 out=0 model=claude-haiku-4.5", DateTimeOffset.UtcNow)]), cancellationToken)).Unwrap();
            return ProviderResult<AgentSession>.Success(new AgentSession("session-1", "failing"));
        }

        public Task<ProviderResult<AgentSession>> ContinueAsync(SandboxHandle sandbox, AgentTask task, AgentLimits limits, string message, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<AgentSession>.Failure("not_found", "no continue"));

        public Task<ProviderResult<AgentSession>> ResumeAsync(SandboxHandle sandbox, string sessionRef, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<AgentSession>.Failure("not_found", "no resume"));

        public Task<ProviderResult<AgentProgress>> GetProgressAsync(AgentSession session, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<AgentProgress>.Success(new AgentProgress(true, 1)));

        public Task<ProviderResult<AgentResult>> GetResultAsync(AgentSession session, CancellationToken cancellationToken) =>
            Task.FromResult(ProviderResult<AgentResult>.Success(new AgentResult(null, SessionEnd.Failed, "copilot exited 1: ")));

        public Task<ProviderResult<Unit>> StopAsync(AgentSession session, CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    sealed class NoSignals : IRunSignals
    {
        public Task QuestionOpenedAsync(string workflowId, FeatureFlow.Interpreter.Wire.QuestionSignal question, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task ModelUnreachableAsync(string workflowId, FeatureFlow.Interpreter.Wire.ModelUnreachableSignal signal, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<FeatureFlow.Interpreter.Wire.AgentPlanView> PlanAsync(string workflowId, FeatureFlow.Interpreter.Wire.AgentPlanQuery query, CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public Task<FeatureFlow.Interpreter.Wire.PatchPreview> PreviewAsync(string workflowId, FeatureFlow.Interpreter.Wire.PatchPreviewQuery query, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<FeatureFlow.Interpreter.Wire.PatchPreview> ChildPreviewAsync(string workflowId, FeatureFlow.Interpreter.Wire.ChildPatchPreviewQuery query, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    static IReadOnlyDictionary<string, IProvider> One(IProvider provider) => new Dictionary<string, IProvider> { ["one"] = provider };

    async Task<StepOutcome> RunAsync(params int[] statuses)
    {
        var client = Path.Combine(folder, "ff-sandbox");
        await File.WriteAllTextAsync(client, "a stand-in for the sandbox client", TestContext.Current.CancellationToken);
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["vcs"] = One(new InMemoryVcs(forge)),
            ["sandbox"] = One(sandbox),
            ["agent_runtime"] = One(new FailingRuntime(records, statuses)),
            ["artifact_store"] = One(store),
            ["record_store"] = One(records),
            ["code_host"] = One(new InMemoryCodeHost(forge)),
        };
        var providers = new ProviderSet(instances, instances.Keys.ToDictionary(k => k, _ => "one"), new Dictionary<string, string>());
        var activities = new AgentActivities(providers, StepTokens.FromSecret("test"),
            new AgentWorkerOptions(["http://127.0.0.1:9"], "http://127.0.0.1:9/model", TimeSpan.FromSeconds(30), client, TimeSpan.FromMilliseconds(20)));
        var contract = new StepContract([new OutcomeSpec("done", "success")], new Dictionary<string, string>(), [], new Dictionary<string, string>());
        var spec = new AgentStepSpec("none", ["read"], "Implement it.", null, [], 0, 0, TimeSpan.FromHours(1), false, "commit", "ticket",
            new Dictionary<string, string> { [repo.FullName] = "ff/ticket/t1" }, new Dictionary<string, string>(), new Dictionary<string, string>(), "wf", "no-lock");
        var call = new StepCall(new StepIdentity("run-1", "ticket_delivery", "implement", 1, Key), "agent", "std:roles/implementer",
            new Dictionary<string, string>(), contract, false, TimeSpan.FromMinutes(5), spec);
        return await new ActivityEnvironment().RunAsync(() => activities.RunAgentStepAsync(call));
    }

    [Fact]
    public async Task A_failed_session_whose_requests_the_upstream_all_refused_stops_the_step_as_a_configuration_fault()
    {
        var outcome = await RunAsync(401, 403, 401);

        Assert.Equal(StepOutcome.CannotAnswer, outcome.Kind);
        Assert.Contains("the model refused the step's requests, a configuration fault: the model's upstream refused all 3", outcome.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(new[] { 401, 200, 401 })]
    [InlineData(new[] { 429, 429 })]
    [InlineData(new int[0])]
    public async Task A_failed_session_with_an_accepted_request_only_limits_or_no_requests_fails_the_attempt(int[] statuses)
    {
        var outcome = await RunAsync(statuses);

        Assert.Equal(StepOutcome.WorkFailed, outcome.Kind);
        Assert.Contains("the agent ended without reporting (failed: copilot exited 1: )", outcome.Reason, StringComparison.Ordinal);
    }
}
