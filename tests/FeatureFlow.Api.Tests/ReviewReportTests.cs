using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// <c>GET /runs/{id}/review</c> (A14, plan s1.5) on a ticket run's real plan: its review passes, each angle's verdict and what
/// it checked from its findings report (E2, null where not recorded), the findings with the pass they were raised in and
/// their latest triage's reason, external findings apart, and the trace; a run with no review says so.
/// </summary>
public sealed class ReviewReportTests : IAsyncDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly string[] Api = ["acme/api"];
    static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    const string Review = "verify.review.review";
    static readonly string[] Angles = ["correctness", "design", "tests", "security", "specification"];

    readonly ReadsWorld world = new();
    long sequence;

    public ValueTask DisposeAsync() => world.DisposeAsync();

    static string Reviewer(int i) => $"{Review}_{i}.assess";

    TimelineRecord Entry(string run, string kind, string step, string? detail, int minute) => new(run, ++sequence, kind, step, detail, T0.AddMinutes(minute));

    async Task<string> IndexAsync(string run, string step, int execution, string name, string? type, string text, int minute)
    {
        var reference = (await world.Artifacts.PutAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), Ct)).Unwrap().Id;
        await world.AppendAsync(new RecordBatch(Artifacts: [new ArtifactRecord(run, step, execution, name, type, reference, null, false, T0.AddMinutes(minute))]));
        return reference;
    }

    Task<string> ReportAsync(string run, int angle, int execution, string text, int minute) =>
        IndexAsync(run, Reviewer(angle), execution, "findings_report", "std:artifact_types/findings_report", text, minute);

    async Task<(HttpStatusCode Status, ReviewReport? Body)> GetAsync(string run)
    {
        using var response = await world.Http.GetAsync($"runs/{run}/review", Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, response.IsSuccessStatusCode ? JsonSerializer.Deserialize<ReviewReport>(text, ApiJson.Options) : null);
    }

    static ReportedFinding Reported(string title, bool blocking) =>
        new("rule", title, "d", "high", blocking, FindingBases.Reasoned, new FindingLocation("acme/api", "A.cs"));

    const string Correctness = """
        schema: 1
        angle: correctness
        summary:
          verdict: The endpoint ignores page_size.
          checked: [src/Invoices/InvoiceEndpoints.cs, AC-001]
        findings:
          - { rule: wrong_behaviour, title: Page size is not applied, description: d, severity: high, blocking: true, basis: demonstrated, location: { repository: acme/api, file: A.cs } }
          - { rule: style, title: A long method, description: d, severity: low, blocking: false, basis: reasoned, location: { repository: acme/api, file: A.cs } }
        """;

    [Fact]
    public async Task Each_pass_shows_every_angles_latest_report_with_its_verdict_and_what_it_checked_or_null_where_none_was_recorded()
    {
        var parameters = new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" };
        var run = await world.StartAsync("std:templates/ticket_delivery", "ticket", parameters, Api);
        await world.IndexAsync(run, "std:templates/ticket_delivery", Api, parameters);
        // Pass 1: every reviewer runs once; correctness reports twice (it read a changed file late), security's report does not read.
        await world.AppendAsync(new RecordBatch(Timeline:
        [
            Entry(run, "entered", Review, "5 branch(es), join all", 10),
            .. Angles.Select((_, i) => Entry(run, "started", Reviewer(i), "execution 1", 11)),
            Entry(run, "started", Reviewer(0), "execution 2", 13),
            Entry(run, "completed", Review, "completed", 20),
            // Pass 2: only correctness has reported so far.
            Entry(run, "entered", Review, "5 branch(es), join all", 30),
            Entry(run, "started", Reviewer(0), "execution 3", 31),
        ]));
        await ReportAsync(run, 0, 1, "schema: 1\nfindings: []\nsummary: { verdict: first look, checked: [] }\n", 12);
        var correctness = await ReportAsync(run, 0, 2, Correctness, 14);
        await ReportAsync(run, 1, 1, "schema: 1\nangle: design\nfindings: []\n", 14);
        await ReportAsync(run, 3, 1, ": not a report [", 14);
        var second = await ReportAsync(run, 0, 3, "schema: 1\nfindings: []\nsummary: { verdict: Now applied., checked: [AC-001] }\n", 32);

        var (status, report) = await GetAsync(run);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(report!.Note);
        Assert.Equal([(1, Review, T0.AddMinutes(10), (DateTimeOffset?)T0.AddMinutes(20)), (2, Review, T0.AddMinutes(30), null)],
            report.Passes.Select(p => (p.Pass, p.StepId, p.StartedAt, p.EndedAt)));
        var first = report.Passes[0].Angles;
        Assert.Equal(Angles, first.Select(a => a.Angle));
        Assert.Equal(Angles.Select((_, i) => Reviewer(i)), first.Select(a => a.StepId));
        Assert.Equal((2, "The endpoint ignores page_size.", 2, 1, correctness), (first[0].Execution!.Value, first[0].Verdict, first[0].Findings, first[0].Blocking, first[0].ReportRef));
        Assert.Equal(["src/Invoices/InvoiceEndpoints.cs", "AC-001"], first[0].Checked);
        // No summary recorded, no report, and a report that does not read.
        Assert.Equal((1, (string?)null, 0), (first[1].Execution!.Value, first[1].Verdict, first[1].Checked.Count));
        Assert.Equal(((int?)null, (string?)null, (string?)null), (first[2].Execution, first[2].Verdict, first[2].ReportRef));
        Assert.Equal((1, (string?)null, 0), (first[3].Execution!.Value, first[3].Verdict, first[3].Findings));
        var later = report.Passes[1].Angles;
        Assert.Equal((3, "Now applied.", second), (later[0].Execution!.Value, later[0].Verdict, later[0].ReportRef));
        Assert.All(later.Skip(1), a => Assert.Null(a.Execution));
    }

    [Fact]
    public async Task Findings_show_the_pass_they_were_raised_in_and_their_latest_triage_and_external_ones_are_listed_apart_with_the_trace()
    {
        var parameters = new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" };
        var run = await world.StartAsync("std:templates/ticket_delivery", "ticket", parameters, Api);
        await world.IndexAsync(run, "std:templates/ticket_delivery", Api, parameters);
        await world.AppendAsync(new RecordBatch(Timeline:
        [
            Entry(run, "entered", Review, "5 branch(es), join all", 10), Entry(run, "completed", Review, "completed", 20),
            Entry(run, "entered", Review, "5 branch(es), join all", 30), Entry(run, "completed", Review, "completed", 40),
        ]));
        FindingRecord F(string id, string source, string state, bool blocking = true) =>
            new(run, id, "fp-" + id, Reported("Title " + id, blocking), source, null, state, "h1", "h2", 0, 0, 0, false, T0);
        FindingEventRecord E(string id, string key, string kind, string state, string actor, int minute, string? reason = null) =>
            new(run, id, key, kind, state, actor, reason, "verify.review.review_0.record", "h1", null, T0.AddMinutes(minute));
        await world.AppendAsync(new RecordBatch(
            Findings: [F("F-1", Reviewer(0), FindingStates.Fixed), F("F-2", Reviewer(2), FindingStates.Open), F("F-3", "github_review", FindingStates.Open, blocking: false)],
            FindingEvents:
            [
                E("F-1", "k1", FindingEventKinds.Raised, FindingStates.Open, Reviewer(0), 15),
                E("F-1", "k2", FindingEventKinds.Triaged, FindingStates.Declined, "verify.fix.triage", 22, "not worth it"),
                E("F-1", "k3", FindingEventKinds.Triaged, FindingStates.Fixed, "verify.fix.verify_fix", 25, "page_size is applied"),
                E("F-2", "k1", FindingEventKinds.Raised, FindingStates.Open, Reviewer(2), 35),
                E("F-3", "k1", FindingEventKinds.Raised, FindingStates.Open, "github_review", 50),
            ]));
        var trace = await IndexAsync(run, "trace", 1, "trace_report", "std:artifact_types/trace_report", """
            { "traced": false, "repositories": { "acme/api": "h2" },
              "requirements": [ { "id": "R-001", "traced": true, "tests": ["acme/api unit Pages_are_limited at h2"], "gaps": [] },
                                { "id": "R-002", "traced": false, "tests": [], "gaps": ["AC-002: no test is claimed"] } ] }
            """, 45);

        var (_, report) = await GetAsync(run);

        Assert.Equal(
            [("F-1", (int?)1, (string?)"page_size is applied", (string?)"verify.fix.verify_fix", (DateTimeOffset?)T0.AddMinutes(25)), ("F-2", 2, null, null, null)],
            report!.Findings.Select(f => (f.Finding.LedgerId, f.RaisedInPass, f.Reason, f.ReasonBy, f.DecidedAt)));
        Assert.Equal([("F-3", (int?)null, "external")], report.External.Select(f => (f.Finding.LedgerId, f.RaisedInPass, f.Finding.RaisedBy!.Source)));
        Assert.Equal((trace, false), (report.Trace!.Ref, report.Trace.Traced));
        Assert.Equal(
            [("R-001", true, "acme/api unit Pages_are_limited at h2", (string?)null), ("R-002", false, null, "AC-002: no test is claimed")],
            report.Trace.Requirements.Select(r => (r.Id, r.Traced, r.Tests.SingleOrDefault(), r.Gaps.SingleOrDefault())));
    }

    [Fact]
    public async Task A_run_with_no_review_step_or_no_plan_answers_empty_lists_and_a_note_and_an_unknown_run_is_404()
    {
        var parameters = new Dictionary<string, string> { ["project_name"] = "\"acme\"" };
        var project = await world.StartAsync("std:templates/project_delivery", "project", parameters, Api);
        await world.IndexAsync(project, "std:templates/project_delivery", Api, parameters);
        var ticket = await world.StartAsync("std:templates/ticket_delivery", "ticket", new Dictionary<string, string> { ["ticket_id"] = "\"T-2\"" }, Api);
        var unplanned = await world.StartAsync("std:templates/ticket_delivery", "ticket", new Dictionary<string, string> { ["ticket_id"] = "\"T-3\"" }, Api);
        await world.IndexAsync(ticket, "std:templates/ticket_delivery", Api, new Dictionary<string, string> { ["ticket_id"] = "\"T-2\"" });

        var (_, none) = await GetAsync(project);
        var (_, notStarted) = await GetAsync(ticket);
        var (_, noPlan) = await GetAsync(unplanned);

        Assert.Equal(("this run has no review step", 0, 0, (TraceView?)null), (none!.Note, none.Passes.Count, none.Findings.Count, none.Trace));
        Assert.Equal("the review has not started", notStarted!.Note);
        Assert.Equal("this run has no plan to read its review from", noPlan!.Note);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync("nope")).Status);
    }
}
