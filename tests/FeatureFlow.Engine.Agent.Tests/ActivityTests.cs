using System.Text.Json.Nodes;
using FeatureFlow.Engine.Agent;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.ClaudeCode;
using FeatureFlow.Providers.CopilotCli;
using FeatureFlow.Providers.Local;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Agent.Tests;

/// <summary>
/// An agent step records its session's activity while the session runs (live activity): the real runtime reads the CLI's
/// output from a memory sandbox as it grows, and the agent activity appends the entries at most every flush and when the
/// session ends, at most <see cref="AgentActivities.ActivityCap"/> of them, then one <c>truncated</c> entry. Both runtimes.
/// </summary>
public sealed class ActivityTests : IAsyncDisposable
{
    const string Key = "run-1/implement/1";

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-activity-" + Guid.NewGuid().ToString("N"));
    readonly InMemoryForge forge = new();
    readonly RepoRef repo;
    readonly FileSystemArtifactStore store;
    readonly MemorySandbox sandbox;
    readonly Watched records;

    public ActivityTests()
    {
        Directory.CreateDirectory(folder);
        repo = forge.CreateRepository("acme/api", new Dictionary<string, string> { ["README.md"] = "api" });
        store = new FileSystemArtifactStore(Path.Combine(folder, "artifacts"));
        sandbox = new MemorySandbox(() => store, forge);
        records = new Watched(new SqliteRecordStore(Path.Combine(folder, "records.db")));
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(folder, recursive: true);
        return ValueTask.CompletedTask;
    }

    /// <summary>A record store that keeps every batch appended, in order, and refuses the first <see cref="RefuseActivity"/> batches of activity.</summary>
    sealed class Watched(SqliteRecordStore inner) : IRecordStoreProvider
    {
        readonly List<RecordBatch> batches = [];

        public int RefuseActivity { get; set; }

        public IReadOnlyList<RecordBatch> Batches
        {
            get
            {
                lock (batches)
                    return batches.ToList();
            }
        }

        public ProviderDescription Describe() => inner.Describe();

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => inner.CheckAsync(cancellationToken);

        public Task<ProviderResult<Unit>> AppendAsync(RecordBatch batch, CancellationToken cancellationToken)
        {
            lock (batches)
            {
                batches.Add(batch);
                if (batch.Activity is { Count: > 0 } && RefuseActivity > 0)
                {
                    RefuseActivity--;
                    return Task.FromResult(ProviderResult<Unit>.Failure("unreachable", "the record store is down"));
                }
            }
            return inner.AppendAsync(batch, cancellationToken);
        }

        public Task<ProviderResult<IReadOnlyList<T>>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
            inner.QueryAsync<T>(queryName, parameters, cancellationToken);
    }

    public static TheoryData<string> Runtimes => ["claude_code", "copilot_cli"];

    /// <summary>The CLI's output lines for a message, the shell calls <c>echo one</c> and <c>sleep 8</c>, and <paramref name="messages"/> more.</summary>
    static IReadOnlyList<string> Output(string runtime, int messages = 0)
    {
        JsonObject Message(string text) => runtime == "claude_code"
            ? new JsonObject { ["type"] = "assistant", ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }) } }
            : new JsonObject { ["type"] = "assistant.message", ["data"] = new JsonObject { ["content"] = text } };
        IEnumerable<JsonObject> Shell(string id, string command, string output) => runtime == "claude_code"
            ?
            [
                new JsonObject
                {
                    ["type"] = "assistant",
                    ["message"] = new JsonObject
                    {
                        ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_use", ["id"] = id, ["name"] = "Bash", ["input"] = new JsonObject { ["command"] = command } }),
                    },
                },
                new JsonObject
                {
                    ["type"] = "user",
                    ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = output }) },
                },
            ]
            :
            [
                new JsonObject { ["type"] = "tool.execution_start", ["data"] = new JsonObject { ["toolCallId"] = id, ["toolName"] = "bash", ["arguments"] = new JsonObject { ["command"] = command } } },
                new JsonObject { ["type"] = "tool.execution_complete", ["data"] = new JsonObject { ["toolCallId"] = id, ["success"] = true, ["result"] = new JsonObject { ["content"] = output } } },
            ];
        JsonObject[] events =
        [
            Message("Looking"),
            .. Shell("c1", "echo one", "one\n"),
            .. Shell("c2", "sleep 8", ""),
            .. Enumerable.Range(1, messages).Select(i => Message($"Message {i}")),
        ];
        return events.Select(e => e.ToJsonString() + "\n").ToList();
    }

    static IReadOnlyDictionary<string, IProvider> One(IProvider provider) => new Dictionary<string, IProvider> { ["one"] = provider };

    async Task RunAsync(string runtime, IReadOnlyList<string> chunks, TimeSpan flush)
    {
        sandbox.On(new SandboxRule(runtime == "claude_code" ? "--output-format.? .?stream-json" : "--output-format.? .?json", Chunks: chunks));
        var client = Path.Combine(folder, "ff-sandbox");
        await File.WriteAllTextAsync(client, "a stand-in for the sandbox client", TestContext.Current.CancellationToken);
        IProvider agent = runtime == "claude_code"
            ? new ClaudeCodeRuntime(new ClaudeCodeOptions("claude-haiku-4-5"), () => sandbox, () => store)
            : new CopilotCliRuntime(new CopilotCliOptions("claude-haiku-4.5"), () => sandbox, () => store);
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["vcs"] = One(new InMemoryVcs(forge)),
            ["sandbox"] = One(sandbox),
            ["agent_runtime"] = One(agent),
            ["artifact_store"] = One(store),
            ["record_store"] = One(records),
            ["code_host"] = One(new InMemoryCodeHost(forge)),
        };
        var providers = new ProviderSet(instances, instances.Keys.ToDictionary(k => k, _ => "one"), new Dictionary<string, string>());
        var activities = new AgentActivities(providers, StepTokens.FromSecret("test"),
            new AgentWorkerOptions(["http://127.0.0.1:9"], "http://127.0.0.1:9/model", TimeSpan.FromSeconds(30), client, TimeSpan.FromMilliseconds(5), flush));
        var contract = new StepContract([new OutcomeSpec("done", "success")], new Dictionary<string, string>(), [], new Dictionary<string, string>());
        var spec = new AgentStepSpec("none", ["read", "shell"], "Implement it.", null, [], 0, 0, TimeSpan.FromHours(1), false, "commit", "ticket",
            new Dictionary<string, string> { [repo.FullName] = "ff/ticket/t1" }, new Dictionary<string, string>(), new Dictionary<string, string>(), "wf", "no-lock");
        var call = new StepCall(new StepIdentity("run-1", "ticket_delivery", "implement", 1, Key), "agent", "std:roles/implementer",
            new Dictionary<string, string>(), contract, false, TimeSpan.FromMinutes(5), spec);
        // No report: the step's outcome is not what these tests are about.
        await new ActivityEnvironment().RunAsync(() => activities.RunAgentStepAsync(call));
    }

    async Task<IReadOnlyList<ActivityRecord>> ActivityAsync() =>
        (await records.QueryAsync<ActivityRecord>(Queries.Activity, new Dictionary<string, string> { ["run_id"] = "run-1", ["step_id"] = "implement" },
            TestContext.Current.CancellationToken)).Value!;

    /// <summary>The index of the batch that recorded the session's end.</summary>
    int Ended() => records.Batches.ToList().FindIndex(b => b.AgentEvents?.Any(e => e is { Kind: "session", Detail: "1 ended completed" }) == true);

    /// <summary>
    /// The output arrives a half line at a time while the CLI runs: each message and tool call is recorded once, in order,
    /// tagged with the run, step, execution and session, and recorded before the session ends, in more than one batch.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task Activity_is_recorded_while_the_session_runs(string runtime)
    {
        var chunks = Output(runtime).SelectMany(l => new[] { l[..(l.Length / 2)], l[(l.Length / 2)..] }).ToList();

        await RunAsync(runtime, chunks, TimeSpan.Zero);

        var activity = await ActivityAsync();
        Assert.Equal([
            (1, ActivityKinds.Message, null, "Looking", null),
            (2, ActivityKinds.ToolStarted, "c1", null, "{\n  \"command\": \"echo one\"\n}"),
            (3, ActivityKinds.ToolFinished, "c1", null, "one\n"),
            (4, ActivityKinds.ToolStarted, "c2", null, "{\n  \"command\": \"sleep 8\"\n}"),
            (5, ActivityKinds.ToolFinished, "c2", null, ""),
        ], activity.Select(a => (a.Sequence, a.Kind, a.CallId, a.Text, a.Detail)));
        Assert.All(activity, a => Assert.Equal(("run-1", "implement", 1, 1), (a.RunId, a.StepId, a.Execution, a.Session)));
        var batches = records.Batches.Select((b, i) => (b, i)).Where(x => x.b.Activity is { Count: > 0 }).Select(x => x.i).ToList();
        Assert.True(batches.Count > 1, $"the activity was recorded in {batches.Count} batch");
        Assert.True(batches[^1] < Ended(), "the activity was recorded after the session ended");
    }

    /// <summary>With a long flush, the session's activity is recorded once, when it ends.</summary>
    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task Activity_is_recorded_at_most_every_flush_and_when_the_session_ends(string runtime)
    {
        await RunAsync(runtime, Output(runtime), TimeSpan.FromHours(1));

        var batches = records.Batches.Where(b => b.Activity is { Count: > 0 }).ToList();
        Assert.Equal(5, Assert.Single(batches).Activity!.Count);
    }

    /// <summary>
    /// Activity is best effort: a refused write neither fails the attempt nor loses its entries, which the next flush
    /// records. The session still ends and is collected.
    /// </summary>
    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task A_refused_activity_write_is_retried_at_the_next_flush_and_the_step_goes_on(string runtime)
    {
        records.RefuseActivity = 1;

        await RunAsync(runtime, Output(runtime).SelectMany(l => new[] { l[..(l.Length / 2)], l[(l.Length / 2)..] }).ToList(), TimeSpan.Zero);

        Assert.Equal(0, records.RefuseActivity);
        Assert.True(Ended() >= 0, "the session's end was not recorded");
        Assert.Equal(Enumerable.Range(1, 5), (await ActivityAsync()).Select(a => a.Sequence));
    }

    /// <summary>Past the cap, one <c>truncated</c> entry at the last sequence, and nothing more; the transcript holds the rest.</summary>
    [Theory]
    [MemberData(nameof(Runtimes))]
    public async Task A_sessions_activity_is_capped_then_marked_truncated(string runtime)
    {
        var output = Output(runtime, messages: AgentActivities.ActivityCap + 100);

        await RunAsync(runtime, [string.Concat(output.Take(300)), string.Concat(output.Skip(300))], TimeSpan.Zero);

        var activity = await ActivityAsync();
        Assert.Equal(AgentActivities.ActivityCap + 1, activity.Count);
        Assert.Equal(Enumerable.Range(1, AgentActivities.ActivityCap), activity.Take(AgentActivities.ActivityCap).Select(a => a.Sequence));
        Assert.Equal((int.MaxValue, ActivityKinds.Truncated), (activity[^1].Sequence, activity[^1].Kind));
    }
}
