using System.Globalization;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>A step's detail (A3): its executions, waits, findings, events, staged patch and pending instructions, in one read.</summary>
    static void MapSteps(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/steps/{step}", (HttpContext c, string id, string step) => api.Guard(c, caller => api.StepDetailAsync(c, caller, id, step)))
            .Described<StepDetail>("getStepDetail", 200, 404);
    }

    /// <summary>The timeline kinds that end a step's execution, as the console's step page has read them (B1).</summary>
    static readonly HashSet<string> ExecutionEnds = new(StringComparer.Ordinal)
        { "completed", "auto_resolved", "attempt_failed", "retried", "exhausted", "escalated", "cancelled", "interrupted" };

    /// <summary>A staged patch as its <c>patch_staged</c> agent event's detail holds it (the agent endpoint's <c>StagedPatch</c>, web JSON).</summary>
    sealed record StagedPatchEvent(string Target, string Patch, int Revision);

    static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    const string PatchStaged = "patch_staged";
    const string PatchWithdrawn = "patch_withdrawn";

    sealed partial class Handlers
    {
        /// <summary>
        /// One step of a run (A3), for a viewer of the run. Its executions, oldest first, from the timeline (each <c>started</c>
        /// entry, numbered as its detail gives it) and the artifact index, each with its artifacts, agent sessions, report calls
        /// and questions (by step key <c>&lt;run&gt;/&lt;step&gt;/&lt;n&gt;</c>); its waits, open first; its findings and finding
        /// steps (<see cref="FindingStepsOf"/>); its events merged oldest first from the timeline, its agent events, the overrides
        /// on it, the answers to its waits and the finding events of its finding steps; the patch its agent has staged while the
        /// latest execution runs; and the <c>instruct</c> overrides the run applied that its next prompt still reads
        /// (<see cref="ExecutionsOver"/>). A step the run
        /// has neither recorded nor planned is 404.
        /// </summary>
        public async Task<IResult> StepDetailAsync(HttpContext context, Caller caller, string id, string step)
        {
            var ct = context.RequestAborted;
            if (await VisibleRunAsync(caller, id, ct) is not { } run)
                return Error(404, $"no run {id}");
            var p = new Dictionary<string, string> { ["run_id"] = id };
            var timeline = (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string>(p) { ["after"] = "0" }, ct)).Unwrap();
            var plan = (await LoadPlanAsync(run, null, null, ct)).Plan?.Plan;
            var planned = plan?.StepById(step);
            if (planned is null && timeline.All(t => t.StepId != step))
                return Error(404, $"no step {step} in run {id}");

            var artifacts = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, p, ct)).Unwrap().Where(a => a.RunId == id && a.StepId == step).ToList();
            var runs = RunsOf(timeline, step);
            var numbers = runs.Select(r => r.Number).Concat(artifacts.Select(a => a.Execution)).Distinct().Order().ToList();
            var agentEvents = new Dictionary<int, IReadOnlyList<AgentEventRecord>>();
            var executions = new List<StepExecutionView>();
            foreach (var n in numbers)
            {
                var key = new Dictionary<string, string> { ["step_key"] = $"{id}/{step}/{n.ToString(CultureInfo.InvariantCulture)}" };
                agentEvents[n] = (await Records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, key, ct)).Unwrap();
                var reports = (await Records.QueryAsync<ReportRecord>(Queries.Reports, key, ct)).Unwrap();
                var questions = (await Records.QueryAsync<QuestionRecord>(Queries.Questions, key, ct)).Unwrap();
                var ofExecution = artifacts.Where(a => a.Execution == n).ToList();
                var sessions = ofExecution.Select(a => SessionOf(a, ArtifactNames.PromptType) ?? SessionOf(a, ArtifactNames.TranscriptType)).OfType<int>().Distinct().Order()
                    .Select(s => new AgentSessionView(s, ofExecution.LastOrDefault(a => SessionOf(a, ArtifactNames.PromptType) == s)?.Ref,
                        ofExecution.LastOrDefault(a => SessionOf(a, ArtifactNames.TranscriptType) == s)?.Ref))
                    .ToList();
                var r = runs.LastOrDefault(x => x.Number == n);
                executions.Add(new StepExecutionView(n, r?.StartedAt, r?.EndedAt, r?.Outcome, r?.Then, ofExecution.Select(ArtifactViewOf).ToList(), sessions,
                    reports.Select(x => new StepReportView(x.Sequence, x.Accepted, x.Report?.Outcome, x.Report?.Summary, x.Errors, x.At)).ToList(),
                    questions.Select(q => new StepQuestionView(q.QuestionId, q.Text, q.Options, q.Status, q.Answer, q.At)).ToList()));
            }

            var waitRecords = (await Records.QueryAsync<WaitRecord>(Queries.Waits, p, ct)).Unwrap().Where(w => w.Kind != "" && w.StepId == step)
                .OrderByDescending(w => w.Open).ThenByDescending(w => w.Open ? DateTimeOffset.MinValue : w.UpdatedAt).ToList();
            var answers = (await Records.QueryAsync<AnswerRecord>(Queries.Answers, p, ct)).Unwrap();
            var waits = await WaitViewsAsync(caller, run, waitRecords, answers, ct);
            var overrides = (await Records.QueryAsync<OverrideRecord>(Queries.Overrides, p, ct)).Unwrap()
                .GroupBy(o => o.OverrideId).Select(g => g.Last()).Where(o => o.StepId == step).ToList();

            var findingSteps = FindingStepsOf(plan, step);
            var reads = new FindingReads(this, ct);
            reads.Know(id, plan, answers);
            var findings = new List<FindingView>();
            var findingEvents = new List<StepEventView>();
            var ledger = (await Records.QueryAsync<FindingRecord>(Queries.Findings, p, ct)).Unwrap().Where(f => f.RunId == id).ToList();
            var histories = await reads.RunHistoriesAsync(id, ledger);
            foreach (var f in ledger)
            {
                var ours = histories[f.FindingId].Where(e => e.StepId is { } s && findingSteps.Contains(s)).ToList();
                if (ours.Count == 0 && !findingSteps.Contains(f.Source))
                    continue;
                findings.Add(await reads.ViewAsync(f));
                findingEvents.AddRange(ours.Select(e => new StepEventView(e.At, StepEventSource.Finding, e.Kind, null,
                    e.Reason is { } why ? $"{f.Finding.Title}: {e.State}: {why}" : $"{f.Finding.Title}: {e.State}", e.Actor, f.FindingId)));
            }

            var waitIds = waitRecords.Select(w => w.WaitId).ToHashSet(StringComparer.Ordinal);
            var events = new List<StepEventView>();
            int? execution = null;
            foreach (var t in timeline.Where(t => t.StepId == step))
            {
                if (t.Kind is "started" or "resumed")
                    execution = ExecutionOf(t) ?? execution;
                events.Add(new StepEventView(t.At, StepEventSource.Timeline, t.Kind, execution, t.Detail ?? "", null));
            }

            events.AddRange(agentEvents.SelectMany(x => x.Value.Select(e => new StepEventView(e.At, StepEventSource.Agent, e.Kind, x.Key, e.Detail, null))));
            events.AddRange(overrides.Select(o => new StepEventView(o.At, StepEventSource.Override, o.Key, null, OverrideTextOf(o), o.Actor)));
            events.AddRange(answers.Where(a => waitIds.Contains(a.WaitId) && a.Result != "sent" && !a.Result.StartsWith(Pending, StringComparison.Ordinal)).Select(a =>
                new StepEventView(a.At, StepEventSource.Answer, ArgsOf(a.Answer) is { } args ? ChoiceOf(args) : "", null,
                    a.Reason is { } why ? $"{why} ({a.Result})" : a.Result, a.Actor)));
            events.AddRange(findingEvents);

            var latest = executions.LastOrDefault();
            var staged = latest is { StartedAt: not null, EndedAt: null } ? StagedOf(latest.Number, agentEvents[latest.Number]) : null;
            var (lastSuccess, executionAt) = ExecutionsOver(timeline, step, planned);
            var instructions = overrides
                .Where(o => o.Key is Overrides.Instruct or Overrides.InstructProtected && o.Result.StartsWith("applied", StringComparison.Ordinal)
                            && executionAt(o.At) >= lastSuccess && o.Change.TryGetValue("text", out _))
                .Select(o => new InstructionView(o.OverrideId, o.Actor, o.Change["text"], o.At)).ToList();

            return Results.Json(new StepDetail(id, step, executions, waits, findings, [.. findingSteps.Order(StringComparer.Ordinal)],
                [.. events.OrderBy(e => e.At)], staged, instructions), Json);
        }

        /// <summary>
        /// The step and its finding steps (plan s1.7): the action steps in its innermost group that consume an artifact it
        /// produces, since the engine's actions, not agents, write the ledger (<c>assess</c> → <c>record</c>, <c>triage</c> →
        /// <c>apply_triage</c>). Only the step itself when the plan cannot be read or does not hold it.
        /// </summary>
        static HashSet<string> FindingStepsOf(Plan? plan, string step)
        {
            var steps = new HashSet<string>(StringComparer.Ordinal) { step };
            if (plan?.StepById(step) is not { } s)
                return steps;
            var produced = s.Produces.Select(a => a.Name).ToHashSet(StringComparer.Ordinal);
            var group = GroupIdOf(s.Scope);
            foreach (var other in plan.AllSteps.Where(o => o.Form == Model.Plans.StepForm.Action && o.Id != step && GroupIdOf(o.Scope) == group
                                                       && o.Consumes.Any(c => produced.Contains(c.Name))))
                steps.Add(other.Id);
            return steps;
        }
    }

    /// <summary>An execution of a step as the timeline records it: its number, when it started and ended, its outcome and the step entered next.</summary>
    sealed record ExecutionRun(int Number, DateTimeOffset StartedAt, DateTimeOffset? EndedAt, string? Outcome, string? Then);

    /// <summary>
    /// The step's executions from the timeline: one per <c>started</c>, numbered as its detail (<c>execution &lt;n&gt;</c>) gives
    /// it, else in order; each ended by the first entry of <see cref="ExecutionEnds"/> after it, with its outcome as the console
    /// shows it (the outcome for <c>completed</c>, else the kind and its detail) and the next step the run entered.
    /// </summary>
    static List<ExecutionRun> RunsOf(IReadOnlyList<TimelineRecord> timeline, string step)
    {
        var ordered = timeline.OrderBy(t => t.Sequence).ToList();
        var list = new List<ExecutionRun>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var e = ordered[i];
            if (e.StepId != step)
                continue;
            if (e.Kind == "started")
                list.Add(new ExecutionRun(ExecutionOf(e) ?? list.Count + 1, e.At, null, null, null));
            else if (ExecutionEnds.Contains(e.Kind) && list.Count > 0 && list[^1].EndedAt is null)
            {
                var outcome = e.Kind == "completed" ? e.Detail ?? "completed" : e.Detail is { Length: > 0 } d ? $"{e.Kind}: {d}" : e.Kind;
                var next = ordered.Skip(i + 1).FirstOrDefault(n => n.StepId.Length > 0 && n.StepId != step)?.StepId;
                list[^1] = list[^1] with { EndedAt = e.At, Outcome = outcome, Then = next };
            }
        }

        return list;
    }

    /// <summary>
    /// The step's executions as the engine numbers them, for its pending instructions (<c>RunCore.CausesOf</c>): the number of
    /// its last execution that ended with a success-class outcome (any outcome when the plan does not hold the step), and the
    /// execution current at a moment, from the last <c>started</c> (or <c>skipped</c> or <c>entered</c>, which also begin
    /// one) at or before it, 0 before the first. The engine records an instruction at the execution current when it is
    /// given and keeps it while that is at least the last success's, so one given while an execution runs outlasts its success.
    /// </summary>
    static (int LastSuccess, Func<DateTimeOffset, int> ExecutionAt) ExecutionsOver(IReadOnlyList<TimelineRecord> timeline, string step, Step? planned)
    {
        var marks = new List<(DateTimeOffset At, int Execution)>();
        int current = 0, lastSuccess = 0;
        foreach (var t in timeline.Where(t => t.StepId == step).OrderBy(t => t.Sequence))
        {
            if (t.Kind == "started")
                current = ExecutionOf(t) ?? current + 1;
            else if (t.Kind is "skipped" or "entered")
                current++;
            else if (t.Kind == "completed" && (planned is null || planned.Outcomes().Any(o => o.Name == t.Detail && o.Class == OutcomeClass.Success)))
                lastSuccess = current;
            marks.Add((t.At, current));
        }

        return (lastSuccess, at => marks.LastOrDefault(m => m.At <= at).Execution);
    }

    /// <summary>The execution a <c>started</c> or <c>resumed</c> entry names (<c>execution &lt;n&gt;</c>), else null.</summary>
    static int? ExecutionOf(TimelineRecord t) =>
        t.Detail is { } d && d.StartsWith("execution ", StringComparison.Ordinal)
        && int.TryParse(d.AsSpan("execution ".Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n)
            ? n
            : null;

    /// <summary>An override as an event's text: its change, its reason and the run's reply.</summary>
    static string OverrideTextOf(OverrideRecord o) =>
        string.Join("; ", o.Change.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key}: {c.Value}")
            .Concat(o.Reason is { } why ? [$"reason: {why}"] : []).Append(o.Result));

    /// <summary>The patch an execution's agent has staged: its last <c>patch_staged</c> unless a <c>patch_withdrawn</c> came after; null otherwise or when its detail does not read.</summary>
    static StagedPatchView? StagedOf(int execution, IReadOnlyList<AgentEventRecord> events)
    {
        if (events.LastOrDefault(e => e.Kind is PatchStaged or PatchWithdrawn) is not { Kind: PatchStaged } last)
            return null;
        try
        {
            return JsonSerializer.Deserialize<StagedPatchEvent>(last.Detail, WebJson) is { Target: not null, Patch: not null } s
                ? new StagedPatchView(execution, s.Target, s.Revision, s.Patch, last.At)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
