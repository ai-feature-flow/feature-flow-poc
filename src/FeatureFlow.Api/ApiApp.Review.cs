using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model.Plans;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace FeatureFlow.Api;

public static partial class ApiApp
{
    /// <summary>A run's review report (A14): its review passes and angles, findings, external review and trace.</summary>
    static void MapReview(WebApplication app, Handlers api)
    {
        app.MapGet("/runs/{id}/review", (HttpContext c, string id) => api.Guard(c, caller => api.ReviewReportAsync(c, caller, id)))
            .Described<ReviewReport>("getReviewReport", 200, 404);
    }

    /// <summary>The name std's reviewers give their report, and the trace's (<c>std:fragments/trace</c>, the ticket's <c>trace</c> step).</summary>
    const string FindingsReportName = "findings_report";

    const string TraceReportName = "trace_report";

    /// <summary>A <c>trace_report</c> as the engine's <c>requirement_trace</c> validator writes it (engine JSON).</summary>
    sealed record TraceDocument(bool Traced, IReadOnlyList<TraceRequirement>? Requirements);

    sealed record TraceRequirement(string Id, bool Traced, IReadOnlyList<string>? Tests, IReadOnlyList<string>? Gaps);

    /// <summary>A review step of a plan: the <c>for_each</c> step, and per branch its angle and its reviewer (the agent step producing a findings report).</summary>
    sealed record ReviewStepPlan(string StepId, IReadOnlyList<(string Angle, string Reviewer)> Angles);

    sealed partial class Handlers
    {
        /// <summary>
        /// The run's review report (A14, plan s1.5), for a viewer of the run. Each pass is an <c>entered</c> entry of a review
        /// step (<see cref="ReviewStepsOf"/>), ended by the step's next end; each angle shows the latest findings report of a
        /// reviewer execution started within the pass, with the report's summary (E2) where it has one. The findings are the run's own
        /// ledger, each with the pass it was raised in and its latest triage's reason; those an external source raised are
        /// listed apart. The trace is the run's latest <c>trace_report</c>. A run with no review answers empty lists and a note.
        /// </summary>
        public async Task<IResult> ReviewReportAsync(HttpContext context, Caller caller, string id)
        {
            var ct = context.RequestAborted;
            if (await VisibleRunAsync(caller, id, ct) is not { } run)
                return Error(404, $"no run {id}");
            var p = new Dictionary<string, string> { ["run_id"] = id };
            var timeline = (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string>(p) { ["after"] = "0" }, ct)).Unwrap();
            var artifacts = (await Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, p, ct)).Unwrap().Where(a => a.RunId == id).ToList();
            var plan = (await LoadPlanAsync(run, null, null, ct)).Plan?.Plan;
            var reviews = plan is null ? [] : ReviewStepsOf(plan);

            var passes = new List<ReviewPassView>();
            foreach (var review in reviews)
            {
                var entries = timeline.Where(t => t.StepId == review.StepId).OrderBy(t => t.Sequence).ToList();
                var starts = entries.Where(t => t.Kind == "entered").ToList();
                for (var i = 0; i < starts.Count; i++)
                {
                    var (start, next) = (starts[i], i + 1 < starts.Count ? starts[i + 1] : null);
                    var ended = entries.FirstOrDefault(t => t.Sequence > start.Sequence && (next is null || t.Sequence < next.Sequence) && ExecutionEnds.Contains(t.Kind))?.At;
                    var angles = new List<ReviewAngleView>();
                    foreach (var (angle, reviewer) in review.Angles)
                    {
                        var executions = timeline.Where(t => t.StepId == reviewer && t.Kind == "started" && t.Sequence > start.Sequence && (next is null || t.Sequence < next.Sequence))
                            .Select(ExecutionOf).OfType<int>().ToHashSet();
                        var report = artifacts.LastOrDefault(a => a.StepId == reviewer && a.Name == FindingsReportName && executions.Contains(a.Execution));
                        angles.Add(await AngleViewAsync(angle, reviewer, report, ct));
                    }

                    passes.Add(new ReviewPassView(0, review.StepId, start.At, ended, angles));
                }
            }

            passes = [.. passes.OrderBy(x => x.StartedAt).Select((x, i) => x with { Pass = i + 1 })];

            var reads = new FindingReads(this, ct);
            var findings = new List<ReviewFindingView>();
            var external = new List<ReviewFindingView>();
            foreach (var f in (await Records.QueryAsync<FindingRecord>(Queries.Findings, p, ct)).Unwrap().Where(f => f.RunId == id))
            {
                var view = await reads.ViewAsync(f);
                var history = await reads.HistoryAsync(f);
                var raised = history.FirstOrDefault(e => e.Kind == FindingEventKinds.Raised)?.At;
                var triaged = history.LastOrDefault(e => e.Kind == FindingEventKinds.Triaged);
                var pass = raised is { } at ? passes.LastOrDefault(x => x.StartedAt <= at && (x.EndedAt is null || at <= x.EndedAt))?.Pass : null;
                var row = new ReviewFindingView(view, pass, triaged?.Reason, triaged?.Actor, triaged?.At);
                (view.RaisedBy?.Source == "external" ? external : findings).Add(row);
            }

            var note = plan is null ? "this run has no plan to read its review from"
                : reviews.Count == 0 ? "this run has no review step"
                : passes.Count == 0 ? "the review has not started"
                : null;
            return Results.Json(new ReviewReport(id, run.TrackerTitle, await PullRequestLinksAsync(id, ct), passes, findings, external,
                await TraceOfAsync(artifacts.LastOrDefault(a => a.Name == TraceReportName), ct), note), Json);
        }

        /// <summary>
        /// An angle of a pass from its reviewer's findings report: the execution, the summary's verdict and what it checked, and
        /// how many findings it holds and how many block. A report that does not read, or none, shows nothing but the angle.
        /// </summary>
        async Task<ReviewAngleView> AngleViewAsync(string angle, string reviewer, ArtifactRecord? report, CancellationToken ct)
        {
            if (report is null)
                return new ReviewAngleView(angle, reviewer, null, null, [], 0, 0, null);
            FindingsReport? read;
            try
            {
                read = ArtifactShapes.ReadLibrary<FindingsReport>(await ReadTextAsync(report.Ref, ct), FindingsReportName);
            }
            catch (FormatException)
            {
                read = null;
            }

            var listed = read?.Findings ?? [];
            return new ReviewAngleView(angle, reviewer, report.Execution, read?.Summary?.Verdict, read?.Summary?.Checked ?? [], listed.Count,
                listed.Count(f => f.Blocking), report.Ref);
        }

        /// <summary>The trace report read, or null when there is none or it does not read as one.</summary>
        async Task<TraceView?> TraceOfAsync(ArtifactRecord? trace, CancellationToken ct)
        {
            if (trace is null)
                return null;
            try
            {
                return JsonSerializer.Deserialize<TraceDocument>(await ReadTextAsync(trace.Ref, ct), WebJson) is { } read
                    ? new TraceView(trace.Ref, read.Traced, (read.Requirements ?? []).Select(r => new TracedRequirementView(r.Id, r.Traced, r.Tests ?? [], r.Gaps ?? [])).ToList())
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        /// <summary>
        /// The plan's review steps, in plan order: each <c>for_each</c> step one of whose branches holds an agent step that
        /// produces a <c>findings_report</c> (std's <c>standard_review</c>), with each such branch's angle (its item, else the
        /// group's label) and that agent step.
        /// </summary>
        static List<ReviewStepPlan> ReviewStepsOf(Plan plan)
        {
            var labels = GroupsOf(plan).ToDictionary(g => g.Id, g => g.Label, StringComparer.Ordinal);
            var reviews = new List<ReviewStepPlan>();
            foreach (var step in plan.AllSteps.Where(s => s.Container is { FromForEach: true }))
            {
                var angles = new List<(string, string)>();
                foreach (var branch in step.Container!.Branches)
                {
                    var group = $"{step.Id}#{branch.Index}";
                    if (plan.AllSteps.FirstOrDefault(s => s.Form == Model.Plans.StepForm.Agent && GroupIdOf(s.Scope) == group
                                                          && s.Produces.Any(a => a.Name == FindingsReportName)) is { } reviewer)
                        angles.Add((AngleOf(reviewer) ?? labels.GetValueOrDefault(group) ?? group, reviewer.Id));
                }

                if (angles.Count > 0)
                    reviews.Add(new ReviewStepPlan(step.Id, angles));
            }

            return reviews;
        }
    }
}
