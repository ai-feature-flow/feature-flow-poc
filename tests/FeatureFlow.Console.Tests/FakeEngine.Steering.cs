using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The overrides (<c>POST /runs/{id}/overrides</c>): <c>breakpoint</c>, <c>skip</c>, <c>grant_attempts</c>, <c>amend</c>,
/// <c>go_to</c>, <c>replan</c> and <c>instruct</c>, refused where the core refuses them and otherwise traced as the core traces
/// them (<c>Steering.cs</c>, <c>Patches.cs</c>; build-console-b2 plan s1.2, s1.3), so the plan (<c>GET /runs/{id}/plan</c>)
/// shows the change. A breakpoint pauses the run only when it reaches the step (<see cref="ReachAsync"/>). A go to or re-plan
/// cancels every running step and withdraws every open wait, then the run reaches the target; child runs and containers'
/// branches are not modelled. An instruction changes only the timeline: the core keeps it as context for the step's next prompt.
/// </summary>
public sealed partial class FakeEngine
{
    partial void Steering() => On(SteerAsync);

    async Task<string?> SteerAsync(Update update)
    {
        if (update is not { Name: RunWorkflowNames.Override, Argument: OverrideArgs { Key: "breakpoint" or "skip" or "grant_attempts" or "amend" or "go_to" or "replan" or "instruct" } o })
            return null;
        lock (Overrides)
            Overrides.Add(o);
        var runId = update.RunId;
        var result = await SteerAsync(runId, o);
        await TraceAsync(runId, "override", o.StepId ?? "", $"{o.Key} by {o.Actor}: {o.Reason} -> {result}");
        // As the core: the moved run enters the target after the update, from its pending entries.
        if (o.Key is "go_to" or "replan")
            await ReachAsync(runId, o.StepId!);
        return result;
    }

    /// <summary>Applies an override as the core does (<c>RunCore.Override</c>) and returns its result, or throws the core's refusal.</summary>
    async Task<string> SteerAsync(string runId, OverrideArgs o)
    {
        var run = await RunRecordAsync(runId);
        if (run.Status != "running")
            throw new Refusal($"the run has ended ({run.Status})");
        var plan = await PlanAsync(runId);
        var stepId = o.StepId ?? "";
        Step? step = null;
        if (o.StepId is not null && plan is not null && (step = plan.Value.Plan.StepById(stepId)) is null)
            throw new Refusal($"{stepId} is not a step of plan revision {plan.Value.Revision}");
        var (status, waits) = await StepAsync(runId, stepId);
        switch (o.Key)
        {
            case "breakpoint":
                var set = !o.Change.TryGetValue("set", out var value) || value != "false";
                if (set && (status is "running" || waits.Any(w => w.Kind != "patch_approval")))
                    throw new Refusal($"{stepId} is running or waiting; a breakpoint is set on a step before it starts");
                if (set != (await BreakpointsAsync(runId)).Contains(stepId))
                    await TraceAsync(runId, set ? "breakpoint_set" : "breakpoint_cleared", stepId, $"by {o.Actor}");
                return "applied";
            case "skip":
                var outcome = o.Change["outcome"];
                if (step is not null)
                {
                    var outcomes = step.Outcomes().Where(x => x.Name != Actions.AttemptsExhausted).Select(x => x.Name).ToList();
                    if (!outcomes.Contains(outcome))
                        throw new Refusal($"{stepId}: '{outcome}' is not an outcome of the step ({string.Join(", ", outcomes)})");
                    if (step.Protected && o.Variants?.Contains(Model.Overrides.SkipProtected) != true)
                        throw new Refusal($"{stepId} is protected: skipping it needs {Model.Overrides.SkipProtected}");
                }

                if (status is "running")
                    throw new Refusal($"{stepId} is running; a running step cannot be skipped");
                await TraceAsync(runId, "skip_set", stepId, $"{outcome} by {o.Actor}: {o.Reason}");
                // A step at a decision, breakpoint or intervention is skipped at once: its waits withdrawn, the step ended.
                var withdrawn = waits.Where(w => w.Kind is "gate" or "breakpoint" or "intervention").ToList();
                foreach (var wait in withdrawn)
                    await CloseWaitAsync(runId, wait.WaitId);
                if (withdrawn.Count > 0)
                {
                    await TraceAsync(runId, "skipped", stepId, outcome);
                    await TraceAsync(runId, "completed", stepId, outcome);
                }

                return "applied: skipped when the run reaches it";
            case "grant_attempts":
                var attempts = long.Parse(o.Change["attempts"], System.Globalization.CultureInfo.InvariantCulture);
                if (attempts <= 0)
                    throw new Refusal("grant attempts needs a positive number of attempts");
                if (step is { ConsumesAttempts: false })
                    throw new Refusal($"{stepId} uses no attempts");
                await TraceAsync(runId, "attempts_granted", stepId, $"{attempts} attempt(s){(o.StepId is null ? " to the run ceiling" : "")} by {o.Actor}");
                return "applied";
            case "go_to" or "replan":
                var cleanUp = o.Change.TryGetValue("clean_up", out var clean) && clean == "true";
                if (o.StepId is null)
                    throw new Refusal($"{(o.Key == "go_to" ? "go to" : "re-plan")}: name the step");
                if (o.Key == "replan" && step?.Agent?.Planning is null)
                    throw new Refusal($"re-plan: {stepId} is not an agent step with planning authority");
                if (cleanUp && step?.Form == StepForm.Decision)
                    throw new Refusal($"go to: {stepId} is a decision, which touches no repository; clean_up resets to where a step started, so go to the step after it");
                var cancelled = new List<string>();
                foreach (var running in await RunningStepsAsync(runId))
                {
                    await TraceAsync(runId, "cancelled", running);
                    cancelled.Add(running);
                }

                foreach (var wait in await OpenWaitsAsync(runId))
                {
                    await CloseWaitAsync(runId, wait.WaitId);
                    await TraceAsync(runId, "wait_closed", wait.StepId, wait.WaitId);
                }

                return $"applied: continues at {stepId}; {(cancelled.Count == 0 ? "nothing was running" : "cancelled " + string.Join(", ", cancelled))}";
            case "instruct":
                if (!o.Change.TryGetValue("text", out var text) || string.IsNullOrWhiteSpace(text))
                    throw new Refusal("instruct needs the instructions as text");
                if (step is not null && step.Form != StepForm.Agent)
                    throw new Refusal($"{stepId} is not an agent step; instructions go into an agent's next prompt");
                if (step is { Protected: true } && o.Variants?.Contains(Model.Overrides.InstructProtected) != true)
                    throw new Refusal($"{stepId} is protected: instructing it needs {Model.Overrides.InstructProtected}");
                return "applied";
            default:
                return $"applied: plan revision {await AmendAsync(runId)}";
        }
    }

    /// <summary>
    /// A step's state as the core sees it for steering: <c>running</c> (started, resumed or to be retried) or another, and
    /// its open waits.
    /// </summary>
    async Task<(string Status, IReadOnlyList<WaitRecord> Waits)> StepAsync(string runId, string stepId) =>
        (StatusOf((await TimelineAsync(runId)).Where(t => t.StepId == stepId)), (await OpenWaitsAsync(runId)).Where(w => w.StepId == stepId).ToList());

    /// <summary>A step's status from its timeline entries, in order: <c>running</c>, <c>pending</c>, or how it last ended.</summary>
    static string StatusOf(IEnumerable<TimelineRecord> entries)
    {
        var status = "pending";
        foreach (var t in entries)
            status = t.Kind switch
            {
                "started" or "resumed" or "retried" or "attempt_failed" => "running",
                "completed" or "auto_resolved" or "interrupted" or "escalated" or "cancelled" or "exhausted" => t.Kind,
                _ => status,
            };
        return status;
    }

    /// <summary>The run's running steps, in the order they first appear on its timeline.</summary>
    async Task<IReadOnlyList<string>> RunningStepsAsync(string runId) =>
        (await TimelineAsync(runId)).Where(t => t.StepId.Length > 0).GroupBy(t => t.StepId, StringComparer.Ordinal)
        .Where(g => StatusOf(g) == "running").Select(g => g.Key).ToList();

    /// <summary>The breakpoints set on the run's steps, from its timeline, as the API reads them.</summary>
    async Task<HashSet<string>> BreakpointsAsync(string runId)
    {
        var breakpoints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in await TimelineAsync(runId))
            if (t.Kind == "breakpoint_set")
                breakpoints.Add(t.StepId);
            else if (t.Kind == "breakpoint_cleared")
                breakpoints.Remove(t.StepId);
        return breakpoints;
    }

    /// <summary>The run's latest indexed plan revision, or null when it has none.</summary>
    async Task<PlanSource?> LatestRevisionAsync(string runId)
    {
        var latest = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = runId }, Ct)).Unwrap()
            .Where(a => a is { StepId: "", Name: ArtifactNames.PlanRevision }).MaxBy(a => a.Execution);
        if (latest is null)
            return null;
        await using var stream = (await Artifacts.GetAsync(new ArtifactRef(latest.Ref), Ct)).Unwrap();
        return PlanSource.Read(await new StreamReader(stream).ReadToEndAsync(Ct));
    }

    /// <summary>The run's current plan, loaded with the organisation's libraries, or null when it has none.</summary>
    async Task<(Plan Plan, int Revision)?> PlanAsync(string runId) =>
        await LatestRevisionAsync(runId) is { } source && source.Load(Libraries, new DiagnosticBag()) is { } plan ? (plan, source.Revision) : null;

    /// <summary>
    /// An accepted amendment: the next plan revision indexed, and <c>patch_applied</c> traced; returns the revision. It carries
    /// the same patches as the one before, so it loads whatever the patch said; a test reads the patch from <see cref="Overrides"/>.
    /// </summary>
    async Task<int> AmendAsync(string runId)
    {
        var source = await LatestRevisionAsync(runId) ?? throw new InvalidOperationException($"run {runId} has no plan revision to amend");
        var next = source with { Revision = source.Revision + 1 };
        var stored = (await Artifacts.PutAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(next.Write())), Ct)).Unwrap();
        await AppendAsync(new RecordBatch(Artifacts:
            [new ArtifactRecord(runId, "", next.Revision, ArtifactNames.PlanRevision, ArtifactNames.PlanRevision, stored.Id, null, false, Now())]));
        await TraceAsync(runId, "patch_applied", "", $"revision {next.Revision}, by human");
        return next.Revision;
    }
}
