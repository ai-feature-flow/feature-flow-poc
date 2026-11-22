using System.Reflection;
using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Api.Common.V1;
using Temporalio.Client;
using Temporalio.Client.Interceptors;
using Temporalio.Converters;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// A scripted engine in place of Temporal (LOG 461): a client interceptor that answers what the API sends the workflow
/// (start, and the answer, override and abort updates) by appending the records the workflow would write. It is
/// deterministic and has no timers; its writes go through the record store, so the change feed (<c>GET /events</c>) sees
/// them. Its connection is lazy and never used.
/// <para>
/// Each reaction is a <see cref="Reaction"/>. This file gives start, answer (closing the wait; a recovery at an intervention
/// or breakpoint traced as the core traces it) and abort; <c>FakeEngine.Steering.cs</c> gives the overrides, refusing what
/// the core refuses. A test moves a run on with <see cref="ReachAsync"/>, and may add a reaction with <see cref="On"/>. The
/// latest added is asked first, and a reaction returning null leaves the update to the next. A reaction refuses an update
/// by throwing <see cref="Refusal"/>, as the workflow's update validator does; the API answers 409 with its message.
/// </para>
/// <para>
/// Not modelled: an answered gate does not complete its step or route the run (the run stays <c>running</c>), and a run that
/// has ended refuses an update with the core's message rather than Temporal's (the API answers 409 either way).
/// </para>
/// </summary>
public sealed partial class FakeEngine : IClientInterceptor
{
    /// <summary>An update the API delivered: the run, the update's name (<see cref="RunWorkflowNames"/>) and its argument.</summary>
    public sealed record Update(string RunId, string Name, object? Argument);

    /// <summary>Answers an update with the workflow's reply, or null to leave it to the next reaction.</summary>
    public delegate Task<string?> Reaction(Update update);

    /// <summary>An update the workflow refuses, with the core's reason.</summary>
    public sealed class Refusal(string message) : Exception(message);

    /// <summary>Choices as the core lists them in a refusal (<c>Waits.cs</c>, <c>Or</c>).</summary>
    static string Or(IReadOnlyList<string> choices) =>
        choices.Count < 2 ? string.Join("", choices) : $"{string.Join(", ", choices.Take(choices.Count - 1))} or {choices[^1]}";

    readonly List<Reaction> reactions = [];
    readonly Dictionary<string, (long Timeline, int Waits)> counters = new(StringComparer.Ordinal);
    DateTimeOffset clock = DateTimeOffset.UtcNow;

    public FakeEngine(IRecordStoreProvider records, IArtifactStoreProvider artifacts, LibrarySet libraries)
    {
        Records = records;
        Artifacts = artifacts;
        Libraries = libraries;
        Client = new TemporalClient(TemporalConnection.CreateLazy(new TemporalConnectionOptions("127.0.0.1:1")), new TemporalClientOptions { Interceptors = [this] });
        On(AnswerAsync);
        On(AbortAsync);
        Decisions();
        Steering();
        Starting();
    }

    /// <summary>Task T5's reactions (<c>FakeEngine.Decisions.cs</c>).</summary>
    partial void Decisions();

    /// <summary>Task T6's reactions (<c>FakeEngine.Steering.cs</c>).</summary>
    partial void Steering();

    /// <summary>Task T8's additions to start (<c>FakeEngine.Starting.cs</c>), such as <see cref="StartedHook"/>.</summary>
    partial void Starting();

    public ITemporalClient Client { get; }

    public IRecordStoreProvider Records { get; }

    public IArtifactStoreProvider Artifacts { get; }

    public LibrarySet Libraries { get; }

    /// <summary>Every run started, as the API asked.</summary>
    public List<RunStart> Starts { get; } = [];

    /// <summary>Every answer delivered (<c>POST /runs/{id}/answers</c>), with the arguments the API built.</summary>
    public List<AnswerArgs> Answers { get; } = [];

    /// <summary>Every override delivered (<c>POST /runs/{id}/overrides</c>).</summary>
    public List<OverrideArgs> Overrides { get; } = [];

    /// <summary>Every abort delivered (<c>POST /runs/{id}/abort</c>).</summary>
    public List<AbortArgs> Aborts { get; } = [];

    /// <summary>Called after a run has started and its records are written; a later task may add to what start writes.</summary>
    public Func<RunStart, Task>? StartedHook { get; set; }

    public void On(Reaction reaction) => reactions.Insert(0, reaction);

    /// <summary>The engine's writes run on the API's request threads, not the test's, so they take no test token.</summary>
    static CancellationToken Ct => CancellationToken.None;

    /// <summary>A time later than any this engine gave before, so a run's newer record always replaces the older.</summary>
    public DateTimeOffset Now()
    {
        lock (counters)
            return clock = DateTimeOffset.UtcNow > clock ? DateTimeOffset.UtcNow : clock.AddTicks(1);
    }

    /// <summary>The run's next wait id of <paramref name="kind"/>, numbered as the engine numbers them (<c>gate-1</c>, <c>breakpoint-2</c>).</summary>
    public string NewWaitId(string runId, string kind)
    {
        lock (counters)
        {
            var c = counters.GetValueOrDefault(runId);
            counters[runId] = c with { Waits = c.Waits + 1 };
            return $"{kind}-{c.Waits + 1}";
        }
    }

    long NextTimeline(string runId)
    {
        lock (counters)
        {
            var c = counters.GetValueOrDefault(runId);
            counters[runId] = c with { Timeline = c.Timeline + 1 };
            return c.Timeline + 1;
        }
    }

    // What the engine writes. Each helper is one batch, so one change in the feed.

    /// <summary>Records a run, or a new state of it, as the engine would.</summary>
    public Task RunAsync(string runId, string template = "m7s:templates/decide", string status = "running", string requester = "local",
        IReadOnlyList<string>? repositories = null, string? parentRunId = null, string? detail = null) =>
        AppendAsync(new RecordBatch([new RunRecord(runId, parentRunId, template, status, requester, repositories ?? [], false, Now(), detail)]));

    /// <summary>
    /// Opens a wait of any kind (<c>gate</c>, <c>question</c>, <c>intervention</c>, <c>patch_approval</c>, <c>breakpoint</c>,
    /// <c>stalled</c>, <c>stuck</c>) and returns its id: by default the next of its kind, as the engine numbers them.
    /// </summary>
    public async Task<string> OpenWaitAsync(string runId, string kind = "gate", string stepId = "go_ahead", string decisionType = "m7s.go_ahead",
        string prompt = "Go ahead?", IReadOnlyList<string>? choices = null, string? waitId = null, IReadOnlyList<ConsumedRef>? consumed = null)
    {
        waitId ??= NewWaitId(runId, kind);
        await AppendAsync(new RecordBatch(Waits: [new WaitRecord(runId, waitId, kind, stepId, decisionType, prompt, choices ?? DefaultChoices(kind), true, Now(), consumed,
            ReasonRequired(kind))]));
        return waitId;
    }

    static IReadOnlyList<string> DefaultChoices(string kind) => kind switch
    {
        "gate" => ["approved", "rejected"],
        "intervention" => Recoveries.For(WaitKind.Intervention),
        "breakpoint" => Recoveries.For(WaitKind.Breakpoint),
        "patch_approval" => ["approve", "reject"],
        _ => [],
    };

    /// <summary>The choices that need a person's reason, as the core describes them (<c>RunCore.Describe</c>): a gate's failure outcome, ending the run at an intervention or a breakpoint.</summary>
    static IReadOnlyList<string> ReasonRequired(string kind) => kind switch
    {
        "gate" => ["rejected"],
        "intervention" or "breakpoint" => ["end"],
        _ => [],
    };

    /// <summary>Closes a wait as the engine does: a record with only its id and <c>open</c> false.</summary>
    public Task CloseWaitAsync(string runId, string waitId) =>
        AppendAsync(new RecordBatch(Waits: [new WaitRecord(runId, waitId, "", "", "", "", [], false, Now())]));

    /// <summary>Appends a timeline entry, numbered in the run's order.</summary>
    public Task TraceAsync(string runId, string kind, string stepId, string? detail = null) =>
        AppendAsync(new RecordBatch(Timeline: [new TimelineRecord(runId, NextTimeline(runId), kind, stepId, detail, Now())]));

    /// <summary>Stores <paramref name="content"/> and indexes it as the run's artifact; returns its reference.</summary>
    public async Task<string> ProduceAsync(string runId, string stepId, string name, string content, string? type = null, int execution = 1,
        string? action = null, bool evidence = false)
    {
        var stored = (await Artifacts.PutAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content)), Ct)).Unwrap();
        await AppendAsync(new RecordBatch(Artifacts: [new ArtifactRecord(runId, stepId, execution, name, type, stored.Id, action, evidence, Now())]));
        return stored.Id;
    }

    /// <summary>The run's open waits as the record store holds them.</summary>
    public async Task<IReadOnlyList<WaitRecord>> OpenWaitsAsync(string runId) =>
        (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string> { ["run_id"] = runId, ["open"] = "true" }, Ct)).Unwrap();

    public async Task AppendAsync(RecordBatch batch) => (await Records.AppendAsync(batch, Ct)).Unwrap();

    // Start: the run running, its plan indexed as the record activity indexes it, and for m7s:templates/decide its gate.

    async Task StartAsync(RunStart start)
    {
        lock (Starts)
            Starts.Add(start);
        await RunAsync(start.RunId, start.Template, "running", start.Requester, start.Repositories);
        var parameters = start.Parameters.ToDictionary(p => p.Key, p => YamlWriter.Read(p.Value));
        if (LibraryReference.TryParse(start.Template) is { } template
            && RunCore.Start(Libraries, template, new RunInfo(start.RunId, start.Repositories, parameters)).Core is { } core)
            foreach (var revision in core.TakeRevisions())
            {
                var stored = (await Artifacts.PutAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(revision.Write())), Ct)).Unwrap();
                await AppendAsync(new RecordBatch(Artifacts:
                    [new ArtifactRecord(start.RunId, "", revision.Revision, ArtifactNames.PlanRevision, ArtifactNames.PlanRevision, stored.Id, null, false, Now())]));
            }

        if (start.Template == "m7s:templates/decide")
        {
            await StartStepAsync(start.RunId, "go_ahead");
            await OpenWaitAsync(start.RunId);
        }

        if (StartedHook is { } hook)
            await hook(start);
    }

    // Answer: the wait closed and the answer on the timeline. A recovery at an intervention or breakpoint is traced as the
    // core traces it (Recovery.cs, Recover; Waits.cs, Answer): recovered, then answered, then the run moves on.

    async Task<string?> AnswerAsync(Update update)
    {
        if (update is not { Name: RunWorkflowNames.Answer, Argument: AnswerArgs answer })
            return null;
        lock (Answers)
            Answers.Add(answer);
        var wait = (await OpenWaitsAsync(update.RunId)).FirstOrDefault(w => w.WaitId == answer.WaitId);
        var said = $"{RunCore.Key(answer)} by {answer.Actor}{(string.IsNullOrWhiteSpace(answer.Reason) ? "" : ": " + answer.Reason)}";
        if (wait is not { Kind: "intervention" or "breakpoint" })
        {
            await CloseWaitAsync(update.RunId, answer.WaitId);
            await TraceAsync(update.RunId, "answered", answer.WaitId, said);
            return "recorded";
        }

        // A breakpoint's continue may come as its outcome, as the core reads it.
        var action = wait.Kind == "breakpoint" ? answer.Action ?? answer.Outcome : answer.Action;
        if (action is null || !wait.Choices.Contains(action))
            throw new Refusal($"{wait.WaitId} is a{(wait.Kind == "breakpoint" ? " breakpoint" : "n intervention")}: answer {Or(wait.Choices)}");
        await CloseWaitAsync(update.RunId, answer.WaitId);
        await TraceAsync(update.RunId, "recovered", wait.StepId, action switch
        {
            "go_to" or "replan" => $"{action.Replace("_", "")} {answer.StepId}{(answer.CleanUp ? " (clean_up)" : "")}",
            "grant_attempts" => $"grant {answer.Attempts} attempt(s)",
            "end" => $"end {answer.Status}",
            _ => action,
        });
        await TraceAsync(update.RunId, "answered", answer.WaitId, said);
        switch (action)
        {
            case "continue":
                // The paused step starts, past its breakpoint this once.
                await StartStepAsync(update.RunId, wait.StepId);
                break;
            case "retry" or "grant_attempts":
                await ReachAsync(update.RunId, wait.StepId);
                break;
            case "go_to" or "replan":
                await ReachAsync(update.RunId, answer.StepId!);
                break;
            case "end":
                var run = await RunRecordAsync(update.RunId);
                await RunAsync(run.RunId, run.Template, answer.Status!, run.Requester, run.Repositories, run.ParentRunId, "ended by a person at an intervention");
                break;
        }

        return "recorded";
    }

    /// <summary>
    /// The run reaches <paramref name="stepId"/>, as the core enters a step (<c>RunCore.Enter</c>): with a breakpoint set on it,
    /// it pauses before it (a breakpoint wait, traced <c>paused</c>; <c>Steering.cs</c>, <c>Paused</c>) and returns the wait's
    /// id; otherwise the step starts and this returns null.
    /// </summary>
    public async Task<string?> ReachAsync(string runId, string stepId)
    {
        if (!(await BreakpointsAsync(runId)).Contains(stepId))
        {
            await StartStepAsync(runId, stepId);
            return null;
        }

        var waitId = await OpenWaitAsync(runId, "breakpoint", stepId, "ticket.breakpoint", $"a person set a breakpoint before {stepId}");
        await TraceAsync(runId, "paused", stepId, waitId);
        return waitId;
    }

    /// <summary>The step's next execution starts.</summary>
    async Task StartStepAsync(string runId, string stepId) =>
        await TraceAsync(runId, "started", stepId, $"execution {(await TimelineAsync(runId)).Count(t => t is { Kind: "started" } && t.StepId == stepId) + 1}");

    /// <summary>The run's timeline, in order.</summary>
    async Task<IReadOnlyList<TimelineRecord>> TimelineAsync(string runId) =>
        (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string> { ["run_id"] = runId, ["after"] = "0" }, Ct)).Unwrap()
        .OrderBy(t => t.Sequence).ToList();

    async Task<RunRecord> RunRecordAsync(string runId) =>
        (await Records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = runId }, Ct)).Unwrap().Single();

    async Task<string?> AbortAsync(Update update)
    {
        if (update is not { Name: RunWorkflowNames.Abort, Argument: AbortArgs abort })
            return null;
        var run = await RunRecordAsync(update.RunId);
        if (run.Status != "running")
            throw new Refusal($"the run has ended ({run.Status})");
        lock (Aborts)
            Aborts.Add(abort);
        foreach (var wait in await OpenWaitsAsync(update.RunId))
            await CloseWaitAsync(update.RunId, wait.WaitId);
        await RunAsync(run.RunId, run.Template, "aborted", run.Requester, run.Repositories, run.ParentRunId, abort.Reason);
        // The abort update's reply (RunWorkflow.AbortAsync); closing the pull requests is the shell's.
        return "aborted";
    }

    async Task<string> ReactAsync(Update update)
    {
        foreach (var reaction in reactions.ToList())
            if (await reaction(update) is { } reply)
                return reply;
        // An override this engine has no script for is kept and applied, so a test can read it.
        if (update.Argument is OverrideArgs other)
        {
            lock (Overrides)
                Overrides.Add(other);
            return "applied";
        }

        throw new InvalidOperationException($"FakeEngine has no reaction to the {update.Name} update of {update.RunId}");
    }

    public ClientOutboundInterceptor InterceptClient(ClientOutboundInterceptor nextInterceptor) => new Outbound(this, nextInterceptor);

    static readonly PropertyInfo KnownOutcome = typeof(WorkflowUpdateHandle).GetProperty("KnownOutcome", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;

    sealed class Outbound(FakeEngine engine, ClientOutboundInterceptor next) : ClientOutboundInterceptor(next)
    {
        public override async Task<WorkflowHandle<TWorkflow, TResult>> StartWorkflowAsync<TWorkflow, TResult>(StartWorkflowInput input)
        {
            await engine.StartAsync((RunStart)input.Args.Single()!);
            return new WorkflowHandle<TWorkflow, TResult>(engine.Client, input.Options.Id!);
        }

        public override async Task<WorkflowUpdateHandle<TResult>> StartWorkflowUpdateAsync<TResult>(StartWorkflowUpdateInput input)
        {
            var handle = new WorkflowUpdateHandle<TResult>(engine.Client, input.Options.Id ?? "update", input.Id, input.RunId);
            try
            {
                var reply = await engine.ReactAsync(new Update(input.Id, input.Update, input.Args.SingleOrDefault()));
                var payload = DataConverter.Default.PayloadConverter.ToPayload(new AnswerReply(reply));
                KnownOutcome.SetValue(handle, new Temporalio.Api.Update.V1.Outcome { Success = new Payloads { Payloads_ = { payload } } });
            }
            catch (Refusal refusal)
            {
                // As the workflow's validator throws ApplicationFailureException: the handle's result is that failure.
                KnownOutcome.SetValue(handle, new Temporalio.Api.Update.V1.Outcome
                {
                    Failure = new Temporalio.Api.Failure.V1.Failure { Message = refusal.Message, ApplicationFailureInfo = new() },
                });
            }

            return handle;
        }
    }
}
