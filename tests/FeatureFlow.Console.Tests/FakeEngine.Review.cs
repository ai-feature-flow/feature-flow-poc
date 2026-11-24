using System.Text;
using System.Text.Json;
using FeatureFlow.Model.Plans;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// What <see cref="FakeEngine.ReviewScenarioAsync"/> wrote: the ticket run, its review angles in plan order with each
/// angle's reviewer step, the findings by what happened to them, the trace report, and the open merge.
/// </summary>
public sealed record ReviewScenario(
    string RunId,
    IReadOnlyList<string> Angles,
    IReadOnlyList<string> ReviewerSteps,
    string Fixed,
    string DeclinedByTriager,
    string DeclinedByPerson,
    string External,
    string TraceRef,
    ScenarioWait Merge);

/// <summary>
/// The review records of build-console-b2 (plan s1.5, s1.7): a ticket run reviewed in two passes, findings raised, triaged,
/// fixed and re-raised, an external review, a trace report; and helpers for one finding event, one agent event and a staged
/// plan patch. Each record is written as the engine writes it (<c>findings.record</c> and <c>findings.triage</c>,
/// <c>Engine/Actions/Findings.cs</c>; the agent endpoint, <c>Engine.Agent/AgentEndpoint*.cs</c>; the timeline as the core
/// traces a <c>for_each</c>), in the shapes the live run <c>run-947be4435f5c</c> holds.
/// </summary>
public sealed partial class FakeEngine
{
    /// <summary>The ticket plan's review angles, in branch order (<c>verify.review.review#0..4</c>, std's <c>standard_review</c>).</summary>
    public static readonly IReadOnlyList<string> ReviewAngles = ["correctness", "design", "tests", "security", "specification"];

    /// <summary>The review's <c>for_each</c> step in a ticket plan.</summary>
    public const string ReviewStep = "verify.review.review";

    /// <summary>The reviewer step of angle <paramref name="i"/> (an agent), and the step that records its findings.</summary>
    public static string ReviewerStep(int i) => $"{ReviewStep}_{i}.assess";

    public static string RecordStep(int i) => $"{ReviewStep}_{i}.record";

    /// <summary>The external findings source of the scenario (<c>deliver.external_review</c>'s <c>external_findings</c>).</summary>
    public const string ExternalSource = "github_review";

    /// <summary>The work branch heads of the scenario: before the fix, and after it.</summary>
    const string HeadBefore = "706cc3b39e1f4c0d8a2b5e6f7a8b9c0d1e2f3a4b";

    const string HeadAfter = "9b1d2c3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c";

    /// <summary>
    /// A ticket run on <c>std:templates/ticket_delivery</c> reviewed twice. Pass 1: correctness raises a blocking finding
    /// (fixed later), tests a blocking one the triager declines and a person then declines, security a non-blocking one the
    /// triager declines; every report has a <c>summary</c> except security's (an older reviewer's shape). The fix loop runs
    /// (triage, person triage, fix, verify fix), the change is tested again, and pass 2 finds nothing new, every report with a
    /// summary; security re-raises its declined finding, which stays declined. Then satisfaction, a <c>trace_report</c> with
    /// one requirement traced and one with a gap, the pull request, an external review raising one finding, and the merge
    /// waiting on a person.
    /// </summary>
    public async Task<ReviewScenario> ReviewScenarioAsync(string runId = "review", string requester = "local")
    {
        string[] repositories = [ScenarioRepository];
        await RunWithAsync(runId, "std:templates/ticket_delivery", requester: requester, repositories: repositories, level: "ticket",
            trackerIssue: "INV-7", trackerTitle: "Ticket INV-7", trackerUrl: "https://acme.atlassian.net/browse/INV-7");
        await AppendAsync(new RecordBatch(Locks: [new LockRecord(runId, await LockAsync(), Now())]));
        await PlanAsync(runId, "std:templates/ticket_delivery", repositories, new() { ["ticket_id"] = "INV-7" });
        var plan = (await PlanAsync(runId))!.Value.Plan;
        var reviewers = ReviewAngles.Select((_, i) => ReviewerStep(i)).ToList();
        if (reviewers.Concat(ReviewAngles.Select((_, i) => RecordStep(i))).FirstOrDefault(id => plan.StepById(id) is null) is { } missing)
            throw new InvalidOperationException($"the ticket plan has no step {missing}; the scenario follows std's review fragment");

        await DoneAsync(runId, ("baseline.tests", "passed"), ("baseline.analysis", "passed"), ("plan.plan", "planned"), ("plan.validate", "valid"),
            ("approve_spec", "approved"), ("implement", "implemented"), ("verify.tests", "passed"), ("verify.analysis", "passed"));

        // Pass 1.
        var correctness = Finding("wrong_behaviour", "Page size is not applied", "GET /invoices returns every invoice whatever page_size says.", "high", true,
            "demonstrated", "src/Invoices/InvoiceEndpoints.cs", "ListInvoices", "return Results.Ok(invoices);", "AC-001", "Apply page_size with Take before returning",
            "InvoicesApiTests.Pages_are_limited fails: 250 returned, 50 expected");
        var tests = Finding("missing_test", "No unit test for the page size bound", "Nothing tests that page_size is capped at 100.", "medium", true, "reasoned",
            "src/Invoices/InvoiceQuery.cs", "InvoiceQuery.Clamp", "Math.Min(size, 100)", "AC-002", "Add a unit test for page_size 500");
        var security = Finding("security", "Invoice ids are sequential", "Sequential ids let a caller guess other invoices' ids.", "low", false, "reasoned",
            "src/Invoices/Invoice.cs", "Invoice", "public int Id { get; init; }", null, "Use opaque ids");
        IReadOnlyList<ReportedFinding>[] first = [[correctness], [], [tests], [security], []];
        FindingsSummary?[] firstSummaries =
        [
            new("The endpoint ignores page_size, so AC-001 is not met.", ["src/Invoices/InvoiceEndpoints.cs", "AC-001", "dotnet test --filter Invoices"]),
            new("The endpoint follows the existing minimal API pattern.", ["src/Invoices/InvoiceEndpoints.cs", "src/Program.cs"]),
            new("The integration tests cover listing; the cap has no unit test.", ["tests/Invoices.Tests/InvoicesApiTests.cs", "AC-002"]),
            null,
            new("Both criteria are addressed by the change.", ["requirements R-001, R-002", "AC-001", "AC-002"]),
        ];
        await ReviewPassAsync(runId, 1, first, firstSummaries, HeadBefore);
        var ids = new Dictionary<string, string>
        {
            ["correctness"] = LedgerIdOf(runId, correctness),
            ["tests"] = LedgerIdOf(runId, tests),
            ["security"] = LedgerIdOf(runId, security),
        };

        // The fix loop: the triager declines security's and tests' findings; tests' is blocking, so a person decides it; the
        // fixer fixes correctness's, the verifier confirms it, and the change is tested again.
        await TraceAsync(runId, "started", "verify.fix.triage", "execution 1");
        await ProduceAsync(runId, "verify.fix.triage", "triage", TriageYaml(
            (ids["correctness"], "open", "Demonstrated by a failing test"),
            (ids["tests"], "declined", "The integration test already bounds the page size"),
            (ids["security"], "declined", "Ids are scoped to the caller's organisation; guessing one reveals nothing")), "std:artifact_types/triage");
        await TraceAsync(runId, "completed", "verify.fix.triage", "triaged");
        await TraceAsync(runId, "started", "verify.fix.apply_triage", "execution 1");
        await FindingEventAsync(runId, ids["tests"], FindingEventKinds.Triaged, FindingStates.Declined, "verify.fix.triage", "verify.fix.apply_triage",
            "The integration test already bounds the page size", revision: HeadBefore);
        await FindingEventAsync(runId, ids["security"], FindingEventKinds.Triaged, FindingStates.Declined, "verify.fix.triage", "verify.fix.apply_triage",
            "Ids are scoped to the caller's organisation; guessing one reveals nothing", revision: HeadBefore);
        await TraceAsync(runId, "completed", "verify.fix.apply_triage", "applied");
        await TraceAsync(runId, "started", "verify.fix.person_triage", "execution 1");
        var triageWait = await OpenWaitAsync(runId, "gate", "verify.fix.person_triage", "findings.triage", "Triage the findings the agent may not clear", ["triaged", "escalate"]);
        // The person's answer as the API records it (sent, then the run's reply) with the triage they wrote, then the wait closed.
        const string personReason = "Covered by InvoicesApiTests.Pages_are_limited; a unit test would repeat it";
        var personTriage = await ProduceAsync(runId, "verify.fix.person_triage", "person_triage",
            TriageYaml((ids["tests"], "declined", personReason)), "std:artifact_types/triage");
        var answer = JsonSerializer.Serialize(new Interpreter.Wire.AnswerArgs(triageWait, requester, "triaged",
            Artifacts: new Dictionary<string, string> { ["person_triage"] = personTriage }, Reason: personReason), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await AppendAsync(new RecordBatch(Answers: [new AnswerRecord(runId, triageWait, requester, answer, "sent", Now(), personReason)]));
        await AppendAsync(new RecordBatch(Answers: [new AnswerRecord(runId, triageWait, requester, answer, "recorded", Now(), personReason)]));
        await CloseWaitAsync(runId, triageWait);
        await TraceAsync(runId, "answered", triageWait, $"triaged by {requester}: {personReason}");
        await TraceAsync(runId, "completed", "verify.fix.person_triage", "triaged");
        await TraceAsync(runId, "started", "verify.fix.apply_person_triage", "execution 1");
        await FindingEventAsync(runId, ids["tests"], FindingEventKinds.Triaged, FindingStates.Declined, requester, "verify.fix.apply_person_triage",
            personReason, revision: HeadBefore);
        await TraceAsync(runId, "completed", "verify.fix.apply_person_triage", "applied");
        await DoneAsync(runId, ("verify.fix.fix", "fixed"), ("verify.fix.verify_fix", "verified"));
        await TraceAsync(runId, "started", "verify.fix.record_fixes", "execution 1");
        await FindingEventAsync(runId, ids["correctness"], FindingEventKinds.Triaged, FindingStates.Fixed, "verify.fix.verify_fix", "verify.fix.record_fixes",
            "page_size is applied; InvoicesApiTests.Pages_are_limited passes", revision: HeadAfter);
        await TraceAsync(runId, "completed", "verify.fix.record_fixes", "applied");
        await DoneAsync(runId, ("verify.fix.retest", "passed"), ("verify.fix.tests_added", "valid"), ("verify.tests", "passed"), ("verify.analysis", "passed"));

        // Pass 2: nothing new; security re-raises its declined finding at unchanged code, which stays declined.
        IReadOnlyList<ReportedFinding>[] second = [[], [], [], [security], []];
        FindingsSummary?[] secondSummaries =
        [
            new("page_size is now applied and tested; no other defect found.", ["src/Invoices/InvoiceEndpoints.cs", "AC-001", "dotnet test --filter Invoices"]),
            new("No design concerns.", ["src/Invoices/InvoiceEndpoints.cs"]),
            new("The fix's test covers the bound.", ["tests/Invoices.Tests/InvoicesApiTests.cs"]),
            new("Sequential ids remain; declined before, and the code is unchanged.", ["src/Invoices/Invoice.cs", "authorisation of GET /invoices/{id}"]),
            new("Both criteria are met.", ["AC-001", "AC-002"]),
        ];
        await ReviewPassAsync(runId, 2, second, secondSummaries, HeadAfter);

        // Satisfaction, then the ticket's trace (plan s1.6): one requirement traced, one with a gap.
        await DoneAsync(runId, ("verify.satisfaction", "valid"));
        await TraceAsync(runId, "started", "trace", "execution 1");
        var trace = await ProduceAsync(runId, "trace", "trace_report", TraceJson(
            ("R-001", [$"{ScenarioRepository} unit InvoicesApiTests.Pages_are_limited at {Revisions.Short(HeadAfter)}"], []),
            ("R-002", [], ["AC-002: no test is claimed"])), "std:artifact_types/trace_report", action: "artifact.validate", evidence: true);
        await TraceAsync(runId, "completed", "trace", "invalid");

        // Deliver: the pull request, an external review raising one finding, checks, and the merge waiting on a person.
        await DoneAsync(runId, ("deliver.open_pr", "ready"));
        await PullRequestAsync(runId, ScenarioRepository, 207, "deliver.open_pr", title: "Page the invoice list (#207)", headRevision: HeadAfter);
        await TraceAsync(runId, "started", "deliver.external_review", "execution 1");
        var external = Finding("documentation", "The page_size parameter is not documented", "The OpenAPI description does not mention page_size or its cap.",
            "low", false, "reasoned", "src/Invoices/InvoiceEndpoints.cs", "ListInvoices", null, null, "Describe page_size in the endpoint's metadata");
        var externalId = await FindingEventAsync(runId, LedgerIdOf(runId, external), FindingEventKinds.Raised, FindingStates.Open, ExternalSource,
            "deliver.external_review", revision: HeadAfter, raised: external);
        await TraceAsync(runId, "completed", "deliver.external_review", "recorded");
        await DoneAsync(runId, ("deliver.checks", "passed"));
        await TraceAsync(runId, "started", "deliver.merge", "execution 1");
        var merge = await OpenWaitAsync(runId, "gate", "deliver.merge", "ticket.merge_action",
            $"may {ScenarioRepository}#207 be merged into main? Approve to ask a person to merge it on the code host; reject to decline");

        return new ReviewScenario(runId, ReviewAngles, reviewers, ids["correctness"], ids["security"], ids["tests"], externalId, trace,
            new ScenarioWait(runId, merge, "deliver.merge"));
    }

    /// <summary>
    /// One pass of the review, as the core runs the <c>for_each</c>: <c>entered</c>, each branch's reviewer started, its prompt,
    /// transcript (a real Copilot CLI session's, the same for every reviewer) and <c>findings_report</c> indexed and its findings recorded by the branch's record step, the branch ended,
    /// then the review step completed. <paramref name="findings"/> and <paramref name="summaries"/> are by angle; a finding
    /// already in the ledger is re-raised (the code unchanged, so its state stays), a new one raised.
    /// </summary>
    async Task ReviewPassAsync(string runId, int pass, IReadOnlyList<IReadOnlyList<ReportedFinding>> findings, IReadOnlyList<FindingsSummary?> summaries, string head)
    {
        await TraceAsync(runId, "entered", ReviewStep, $"{ReviewAngles.Count} branch(es), join all");
        for (var i = 0; i < ReviewAngles.Count; i++)
            await TraceAsync(runId, "started", ReviewerStep(i), $"execution {pass}");
        for (var i = 0; i < ReviewAngles.Count; i++)
        {
            var reviewer = ReviewerStep(i);
            await ProduceAsync(runId, reviewer, "prompt:1", $"Review the change from the {ReviewAngles[i]} angle (pass {pass}).", "prompt", pass);
            await ProduceAsync(runId, reviewer, "transcript:1", ArtifactTests.RealTranscript, "transcript", pass);
            await ProduceAsync(runId, reviewer, "findings_report", ReportYaml(ReviewAngles[i], findings[i], summaries[i]), "std:artifact_types/findings_report", pass);
            await TraceAsync(runId, "completed", reviewer, "reviewed");
            await TraceAsync(runId, "started", RecordStep(i), $"execution {pass}");
            var ledger = await LedgerAsync(runId);
            foreach (var finding in findings[i])
            {
                var id = LedgerIdOf(runId, finding);
                if (ledger.FirstOrDefault(f => f.FindingId == id) is { } existing)
                    await FindingEventAsync(runId, id, FindingEventKinds.Reraised, existing.State, reviewer, RecordStep(i),
                        $"re-raised while {existing.State}; the code is unchanged since its state last changed", pass, head, codeChanged: false);
                else
                    await FindingEventAsync(runId, id, FindingEventKinds.Raised, FindingStates.Open, reviewer, RecordStep(i), execution: pass, revision: head, raised: finding);
            }

            await TraceAsync(runId, "completed", RecordStep(i), "recorded");
            await TraceAsync(runId, "branch_ended", ReviewStep, $"branch {i}: success");
        }

        await TraceAsync(runId, "completed", ReviewStep, "completed");
    }

    /// <summary>
    /// One entry of a finding's history, with the finding's new state, in one batch as <c>findings.record</c> and
    /// <c>findings.triage</c> write them: the event keyed by the writing step's execution (<c>run/step/n</c>). A
    /// <see cref="FindingEventKinds.Raised"/> event needs <paramref name="raised"/>, the report entry, and adds the finding to
    /// the ledger under <paramref name="actor"/> as its source; any other changes the finding already recorded. Returns its ledger id.
    /// </summary>
    public async Task<string> FindingEventAsync(string runId, string findingId, string kind, string state, string actor, string stepId, string? reason = null,
        int execution = 1, string? revision = null, bool? codeChanged = null, ReportedFinding? raised = null)
    {
        var now = Now();
        FindingRecord record;
        if (kind == FindingEventKinds.Raised)
            record = new FindingRecord(runId, findingId, FingerprintOf(raised ?? throw new ArgumentException("a raised event needs the finding", nameof(raised))),
                raised, actor, actor.Contains(".assess", StringComparison.Ordinal) ? "claude-sonnet-5.5" : null, state, revision, revision, 0, 0, 0, false, now);
        else
        {
            var before = (await LedgerAsync(runId)).FirstOrDefault(f => f.FindingId == findingId)
                         ?? throw new InvalidOperationException($"{findingId} is not in {runId}'s ledger");
            record = before with
            {
                State = state,
                StateRevision = kind == FindingEventKinds.Triaged ? revision ?? before.StateRevision : before.StateRevision,
                Reraises = before.Reraises + (kind is FindingEventKinds.Reraised or FindingEventKinds.Reopened ? 1 : 0),
                Reopens = before.Reopens + (kind == FindingEventKinds.Reopened ? 1 : 0),
                Fixes = before.Fixes + (state == FindingStates.Fixed && before.State != FindingStates.Fixed ? 1 : 0),
                ReraisedUnchanged = kind == FindingEventKinds.Reraised ? codeChanged == false : before.ReraisedUnchanged,
                UpdatedAt = now,
            };
        }

        await AppendAsync(new RecordBatch(Findings: [record],
            FindingEvents: [new FindingEventRecord(runId, findingId, $"{runId}/{stepId}/{execution}", kind, state, actor, reason, stepId, revision, codeChanged, now)]));
        return findingId;
    }

    /// <summary>
    /// An agent event of a step's execution, as the agent endpoint appends it under the execution's step key (<c>run/step/n</c>):
    /// a hook call (<c>hook_pre_tool_use</c>, detail <c>Edit allow</c>), model use (<c>model</c>), a plan patch staged, refused or
    /// withdrawn (<c>patch_*</c>), and the like.
    /// </summary>
    public Task AgentEventAsync(string runId, string stepId, int execution, string kind, string detail) =>
        AppendAsync(new RecordBatch(AgentEvents: [new AgentEventRecord($"{runId}/{stepId}/{execution}", kind, detail, Now())]));

    /// <summary>
    /// A plan patch the step's agent stages (<c>propose_patch</c>, <c>AgentEndpoint.Planning.cs</c>): the <c>patch_staged</c>
    /// event whose detail is the staged patch as JSON (target, patch document text, revision checked against). By default the
    /// implementer of a ticket proposes a step after itself, a patch that applies to the ticket plan's revision 1. Withdraw it
    /// with <see cref="AgentEventAsync"/> and kind <c>patch_withdrawn</c>, detail empty. Returns the patch document.
    /// </summary>
    public async Task<string> StagedPatchAsync(string runId, string stepId = "implement", int execution = 1, string target = "self", int revision = 1, string? patch = null)
    {
        // A route names a step within the inserted step's own fragment: the agent step by its local id.
        var local = stepId[(stepId.LastIndexOf('.') + 1)..];
        patch ??= JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["api_version"] = "feature-flow/v1",
            ["kind"] = "patch",
            ["metadata"] = new Dictionary<string, object>
            {
                ["reason"] = "The invoices table needs an index before paging is fast enough",
                ["proposed_by"] = new Dictionary<string, string> { ["type"] = "agent", ["step_id"] = stepId },
            },
            ["base_revision"] = revision,
            ["changes"] = new object[]
            {
                new Dictionary<string, object>
                {
                    ["op"] = "insert",
                    ["after"] = stepId,
                    ["step"] = new Dictionary<string, object>
                    {
                        ["id"] = "migration_check",
                        ["action"] = "test.run",
                        ["routes"] = new Dictionary<string, string> { ["failed"] = local, ["baseline_failed"] = local },
                    },
                },
            },
        });
        var detail = JsonSerializer.Serialize(new Dictionary<string, object> { ["target"] = target, ["patch"] = patch, ["revision"] = revision });
        await AgentEventAsync(runId, stepId, execution, "patch_staged", detail);
        return patch;
    }

    /// <summary>The run's own ledger as the record store holds it.</summary>
    async Task<IReadOnlyList<FindingRecord>> LedgerAsync(string runId) =>
        (await Records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = runId }, Ct)).Unwrap().Where(f => f.RunId == runId).ToList();

    static ReportedFinding Finding(string rule, string title, string description, string severity, bool blocking, string basis, string file, string? symbol,
        string? snippet, string? criterion, string? correction, string? proof = null) =>
        new(rule, title, description, severity, blocking, basis, new FindingLocation(ScenarioRepository, file, symbol, snippet, criterion), correction, proof);

    /// <summary>The ledger id <c>findings.record</c> gives a new finding (its fingerprint, as the engine places one with neither symbol nor snippet by its title).</summary>
    static string LedgerIdOf(string runId, ReportedFinding f) => Fingerprints.LedgerId(runId, FingerprintOf(f));

    static string FingerprintOf(ReportedFinding f) =>
        string.IsNullOrWhiteSpace(f.Location.Symbol) && string.IsNullOrWhiteSpace(f.Location.Snippet)
            ? Fingerprints.Of(f with { Location = f.Location with { Snippet = "title: " + f.Title } })
            : Fingerprints.Of(f);

    /// <summary>A text as a YAML double-quoted scalar (JSON's string syntax is YAML's).</summary>
    static string Q(string text) => JsonSerializer.Serialize(text);

    /// <summary>A reviewer's <c>findings.yaml</c> as std's review skill shows it, with its <c>summary</c> when given.</summary>
    static string ReportYaml(string angle, IReadOnlyList<ReportedFinding> findings, FindingsSummary? summary)
    {
        var y = new StringBuilder($"schema: 1\nangle: {angle}\n");
        if (summary is not null)
        {
            y.Append($"summary:\n  verdict: {Q(summary.Verdict)}\n  checked:\n");
            foreach (var c in summary.Checked)
                y.Append($"    - {Q(c)}\n");
        }

        if (findings.Count == 0)
            return y.Append("findings: []\n").ToString();
        y.Append("findings:\n");
        foreach (var f in findings)
        {
            y.Append($"  - rule: {f.Rule}\n    title: {Q(f.Title)}\n    description: {Q(f.Description)}\n    severity: {f.Severity}\n");
            y.Append($"    blocking: {(f.Blocking ? "true" : "false")}\n    basis: {f.Basis}\n");
            if (f.RequiredCorrection is { } correction)
                y.Append($"    required_correction: {Q(correction)}\n");
            if (f.Proof is { } proof)
                y.Append($"    proof: {Q(proof)}\n");
            var l = f.Location;
            y.Append($"    location:\n      repository: {l.Repository}\n      file: {Q(l.File)}\n");
            if (l.Symbol is { } symbol)
                y.Append($"      symbol: {Q(symbol)}\n");
            if (l.Snippet is { } snippet)
                y.Append($"      snippet: {Q(snippet)}\n");
            if (l.Criterion is { } criterion)
                y.Append($"      criterion: {criterion}\n");
        }

        return y.ToString();
    }

    /// <summary>A <c>triage</c> document: per finding, its new state and the reason.</summary>
    static string TriageYaml(params (string Finding, string State, string Reason)[] entries) =>
        "schema: 1\nentries:\n" + string.Concat(entries.Select(e => $"  - finding: {e.Finding}\n    state: {e.State}\n    reason: {Q(e.Reason)}\n"));

    /// <summary>A <c>trace_report</c> as <c>requirement_trace</c> writes it (<c>TraceReport</c>, the engine's JSON).</summary>
    static string TraceJson(params (string Id, string[] Tests, string[] Gaps)[] requirements) =>
        JsonSerializer.Serialize(new
        {
            traced = requirements.All(r => r.Gaps.Length == 0),
            repositories = new Dictionary<string, string> { [ScenarioRepository] = HeadAfter },
            requirements = requirements.Select(r => new { id = r.Id, traced = r.Gaps.Length == 0, tests = r.Tests, gaps = r.Gaps }),
        }, new JsonSerializerOptions { WriteIndented = true });
}
