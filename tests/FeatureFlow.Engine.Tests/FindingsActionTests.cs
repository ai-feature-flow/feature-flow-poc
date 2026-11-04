using System.Text;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// <c>findings.record</c> and <c>findings.triage</c> (docs/04 section 11) on the in-memory world with the SQLite record
/// store: fingerprint matching, re-raises and <c>reopen_on</c>, children's ledgers, retries, external sources, triage and
/// its validation, posted declines, and the <c>findings</c> namespace each action returns.
/// </summary>
public sealed class FindingsActionTests : IAsyncDisposable
{
    const string File = "src/Orders.cs";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly ActionWorld world;
    readonly InMemoryFindingsSource source;

    public FindingsActionTests()
    {
        InMemoryFindingsSource? made = null;
        world = new ActionWorld(new Dictionary<string, string> { ["README.md"] = "# api\n", [File] = "class Orders { void Add() { if (x) { } } }\n", ["src/Other.cs"] = "class Other {}\n" },
            findingsSource: store => made = new InMemoryFindingsSource(store));
        source = made!;
        world.CommitWork("notes.md", "work starts");
    }

    public ValueTask DisposeAsync() => world.DisposeAsync();

    static ReportedFinding Finding(string rule = "null-check", bool blocking = true, string basis = FindingBases.Reasoned, string file = File, string snippet = "if (x) { }") =>
        new(rule, $"Title {rule}", "Description", "high", blocking, basis, new FindingLocation("acme/api", file, "Orders.Add", snippet), "Check x");

    /// <summary>A reviewer step's <c>findings_report</c> in the run's artifacts, as the workflow would list it.</summary>
    async Task Reviewed(string stepId, params ReportedFinding[] findings) => await Produced(stepId, "findings_report", ArtifactShapes.WriteLibrary(new FindingsReport(1, findings)));

    /// <summary>An artifact a step produced; by default it replaces earlier ones of the name, so a test's latest is the one read.</summary>
    async Task Produced(string stepId, string name, string content, bool keepOthers = false)
    {
        var reference = (await world.Store.PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(content)), Ct)).Unwrap();
        world.Artifacts.RemoveAll(a => a.Name == name && (!keepOthers || a.StepId == stepId));
        world.Artifacts.Add(new ArtifactEntry(stepId, name, "std:artifact_types/" + (name.StartsWith("fix_verification", StringComparison.Ordinal) ? "triage" : name), reference.Id));
    }

    async Task Triaged(string stepId, params TriageEntry[] entries) => await Produced(stepId, "triage", ArtifactShapes.WriteLibrary(new Triage(1, entries)));

    Task<ActionResult> Record(string reopenOn = "code_change", string stepId = "review.record", string source = "findings_report") =>
        world.RunAsync("findings.record", $"{{ source: {source}, reopen_on: {reopenOn} }}", stepId);

    Task<ActionResult> Triage(string from = "triage", string stepId = "apply_triage", bool postDeclines = true, string? states = null) =>
        world.RunAsync("findings.triage", $"{{ from: {from}, post_declines: {(postDeclines ? "true" : "false")}{(states is null ? "" : ", states: " + states)} }}", stepId);

    async Task<IReadOnlyList<FindingRecord>> Ledger(string? runId = null) =>
        (await world.Records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = runId ?? world.RunId }, Ct)).Unwrap();

    async Task<IReadOnlyList<FindingEventRecord>> History(FindingRecord f) =>
        (await world.Records.QueryAsync<FindingEventRecord>(Queries.FindingEvents, new Dictionary<string, string> { ["run_id"] = f.RunId, ["finding_id"] = f.FindingId }, Ct)).Unwrap();

    static long Output(ActionResult result, string name) => Convert.ToInt64(YamlWriter.ReadPlain(result.Report!.Outputs[name]), System.Globalization.CultureInfo.InvariantCulture);

    static void Outcome(ActionResult result, string outcome) =>
        Assert.True(result.Report?.Outcome == outcome, $"expected {outcome}, got {result.Kind} {result.Report?.Outcome} {result.Reason} {string.Join(" | ", result.Events)}");

    /// <summary>A fixer's commit, in a file of its own, so the finding's file is unchanged by it.</summary>
    string Fixer() => world.CommitWork($"fixes/{Guid.NewGuid():N}.md", "a fix");

    /// <summary>Fixes the finding by a fixer's commit and a triage, as the fix loop's fixer and verifier would.</summary>
    async Task Fix(FindingRecord f, string stepId)
    {
        Fixer();
        await Triaged(stepId, new TriageEntry(f.FindingId, FindingStates.Fixed, "fixed"));
        Outcome(await Triage(stepId: stepId + ".apply"), "applied");
    }

    // ---- findings.record ----

    [Fact]
    public async Task A_reviewers_finding_is_recorded_open_with_its_history_and_the_namespace_counts_it()
    {
        await Reviewed("review.assess", Finding(), Finding("style", blocking: false, basis: FindingBases.Demonstrated, snippet: "void Add()"));

        var result = await Record();

        Outcome(result, "recorded");
        Assert.Equal((2L, 0L, 0L, 0L), (Output(result, "new"), Output(result, "reraised"), Output(result, "reopened"), Output(result, "repeat_fixes")));
        var ledger = await Ledger();
        Assert.Equal(2, ledger.Count);
        Assert.All(ledger, f => Assert.Equal((FindingStates.Open, "review.assess", world.Work), (f.State, f.Source, f.RaisedRevision)));
        var first = ledger.Single(f => f.Finding.Rule == "null-check");
        Assert.Equal(Fingerprints.LedgerId(world.RunId, Fingerprints.Of(Finding())), first.FindingId);
        var raised = Assert.Single(await History(first));
        Assert.Equal((FindingEventKinds.Raised, "review.assess", "review.record"), (raised.Kind, raised.Actor, raised.StepId));
        Assert.Equal(2, result.Findings!["open"]);
        Assert.Equal(1, result.Findings["open_blocking"]);
        Assert.Equal(1, result.Findings["open_demonstrated"]);
    }

    [Fact]
    public async Task The_same_finding_raised_again_is_a_re_raise_not_a_new_finding_and_line_moves_do_not_matter()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Reviewed("review.assess", Finding(snippet: "\n  if (x)   {   }  \n\n"));

        var again = await Record();

        Assert.Equal((0L, 1L), (Output(again, "new"), Output(again, "reraised")));
        var finding = Assert.Single(await Ledger());
        Assert.Equal((FindingStates.Open, 1), (finding.State, finding.Reraises));
        Assert.Equal([FindingEventKinds.Raised, FindingEventKinds.Reraised], (await History(finding)).Select(e => e.Kind));
    }

    [Fact]
    public async Task A_fixed_finding_re_raised_on_unchanged_code_stays_fixed_by_default_and_counts_as_reraised_unchanged()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Fix(Assert.Single(await Ledger()), "verify");
        await Reviewed("review.assess", Finding());

        var again = await Record();

        Assert.Equal((1L, 0L), (Output(again, "reraised"), Output(again, "reopened")));
        var finding = Assert.Single(await Ledger());
        Assert.Equal((FindingStates.Fixed, true), (finding.State, finding.ReraisedUnchanged));
        Assert.Equal(1, again.Findings!["reraised_unchanged"]);
        Assert.Equal(0, again.Findings["open_blocking"]);
        Assert.False((await History(finding)).Last().CodeChanged);
    }

    [Fact]
    public async Task A_fixed_finding_re_raised_after_its_file_changed_reopens_with_code_change()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Fix(Assert.Single(await Ledger()), "verify");
        world.CommitWork(File, "class Orders { void Add() { if (x) { } return; } }\n");
        await Reviewed("review.assess", Finding());

        var again = await Record();

        Assert.Equal((1L, 1L), (Output(again, "reraised"), Output(again, "reopened")));
        var finding = Assert.Single(await Ledger());
        Assert.Equal((FindingStates.Open, 1, 1, world.Work), (finding.State, finding.Reopens, finding.Fixes, finding.RaisedRevision));
        var reopened = (await History(finding)).Last();
        Assert.Equal((FindingEventKinds.Reopened, true), (reopened.Kind, reopened.CodeChanged));
        Assert.Equal(1, again.Findings!["open_blocking"]);
    }

    [Fact]
    public async Task A_change_to_another_file_is_not_a_change_at_the_finding()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Fix(Assert.Single(await Ledger()), "verify");
        world.CommitWork("src/Other.cs", "class Other { int y; }\n");
        await Reviewed("review.assess", Finding());

        var again = await Record();

        Assert.Equal(0L, Output(again, "reopened"));
        Assert.Equal(FindingStates.Fixed, Assert.Single(await Ledger()).State);
    }

    [Theory]
    [InlineData("always", false, FindingStates.Open)]
    [InlineData("never", true, FindingStates.Declined)]
    [InlineData("code_change", true, FindingStates.Open)]
    [InlineData("code_change", false, FindingStates.Declined)]
    public async Task Reopen_on_decides_whether_a_re_raised_declined_finding_reopens(string reopenOn, bool changeTheFile, string expected)
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Triaged("triager", new TriageEntry(Assert.Single(await Ledger()).FindingId, FindingStates.Declined, "out of scope"));
        Outcome(await Triage(stepId: "triager.apply"), "applied");
        if (changeTheFile)
            world.CommitWork(File, "class Orders { }\n");
        await Reviewed("review.assess", Finding());

        await Record(reopenOn);

        Assert.Equal(expected, Assert.Single(await Ledger()).State);
    }

    [Fact]
    public async Task A_finding_reopened_after_two_fixes_is_a_repeat_fix()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        for (var round = 1; round <= 2; round++)
        {
            await Fix(Assert.Single(await Ledger()), $"verify{round}");
            world.CommitWork(File, $"class Orders {{ int round{round}; }}\n");
            await Reviewed("review.assess", Finding());
            var again = await Record();
            Assert.Equal(round == 2 ? 1L : 0L, Output(again, "repeat_fixes"));
            Assert.Equal(round == 2 ? 1L : 0L, again.Findings!["repeat_fixes"]);
        }

        Assert.Equal((FindingStates.Open, 2, 2), (Assert.Single(await Ledger()).State, (await Ledger())[0].Fixes, (await Ledger())[0].Reopens));
    }

    [Fact]
    public async Task A_retried_record_changes_nothing_more_and_reports_the_same()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Fix(Assert.Single(await Ledger()), "verify");
        world.CommitWork(File, "class Orders { }\n");
        await Reviewed("review.assess", Finding(), Finding("other", snippet: "class Orders"));
        var call = world.Call("findings.record", "review.record", 9, "{ source: findings_report, reopen_on: code_change }");

        var first = await world.Actions.RunAsync(call, world.Scope, () => { }, Ct);
        var retried = await world.Actions.RunAsync(call, world.Scope, () => { }, Ct);

        Assert.Equal(first.Report!.Outputs, retried.Report!.Outputs);
        Assert.Equal((1L, 1L, 1L), (Output(retried, "new"), Output(retried, "reraised"), Output(retried, "reopened")));
        var reopened = (await Ledger()).Single(f => f.Finding.Rule == "null-check");
        Assert.Equal((1, 1), (reopened.Reraises, reopened.Reopens));
        Assert.Single(await History(reopened), e => e.Key == call.Step.IdempotencyKey);
    }

    [Fact]
    public async Task A_parents_review_re_raises_a_childs_finding_in_the_childs_ledger()
    {
        var child = world.RunId + ".implement.1";
        var at = DateTimeOffset.UtcNow;
        (await world.Records.AppendAsync(new RecordBatch(Runs:
        [
            new RunRecord(world.RunId, null, "epic_delivery", "running", "me", ["acme/api"], false, at, null),
            new RunRecord(child, world.RunId, "ticket_delivery", "completed", "me", ["acme/api"], false, at, null),
        ]), Ct)).Unwrap();
        var fingerprint = Fingerprints.Of(Finding());
        var childsId = Fingerprints.LedgerId(child, fingerprint);
        (await world.Records.AppendAsync(new RecordBatch(Findings:
            [new FindingRecord(child, childsId, fingerprint, Finding(), "child.review", null, FindingStates.Fixed, world.Work, world.Work, 0, 1, 0, false, at)]), Ct)).Unwrap();
        await Reviewed("review.assess", Finding());

        var result = await Record();

        Assert.Equal((0L, 1L, 0L), (Output(result, "new"), Output(result, "reraised"), Output(result, "reopened")));
        var finding = Assert.Single(await Ledger());
        Assert.Equal((child, childsId, 1), (finding.RunId, finding.FindingId, finding.Reraises));
        Assert.Empty(await Ledger(world.RunId + ".other"));
    }

    [Fact]
    public async Task A_recorder_in_a_for_each_branch_reads_its_own_branchs_report()
    {
        await Reviewed("verify.review.review_0.assess", Finding("from-0"));
        await Produced("verify.review.review_1.assess", "findings_report", ArtifactShapes.WriteLibrary(new FindingsReport(1, [Finding("from-1")])), keepOthers: true);

        await Record(stepId: "verify.review.review_1.record");

        Assert.Equal("from-1", Assert.Single(await Ledger()).Finding.Rule);
    }

    [Theory]
    [InlineData("schema: 1\nfindings:\n  - { rule: r, title: t, description: d, severity: urgent, blocking: true, basis: reasoned, location: { repository: acme/api, file: a.cs } }", "severity")]
    [InlineData("schema: 1\nfindings:\n  - { rule: r, title: t, description: d, severity: high, blocking: true, basis: reasoned }", "location")]
    [InlineData("{ fake: placeholder }", "findings must be a list")]
    [InlineData("not: [valid", "does not parse")]
    public async Task A_report_that_is_not_a_findings_report_stops_the_step_for_a_person(string content, string expected)
    {
        await Produced("review.assess", "findings_report", content);

        var result = await Record();

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains(expected, result.Reason, StringComparison.Ordinal);
        Assert.Empty(await Ledger());
    }

    [Fact]
    public async Task A_source_that_is_neither_an_artifact_nor_a_provider_stops_the_step_for_a_person()
    {
        var result = await Record(source: "sonarcloud");

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("neither an artifact", result.Reason, StringComparison.Ordinal);
    }

    // ---- External sources ----

    [Fact]
    public async Task An_external_review_is_requested_once_polled_until_ready_and_recorded_from_the_source()
    {
        source.Review(world.Repo, world.Work!, new FindingsReport(1, [Finding("copilot")]), pollsUntilReady: 3);

        var result = await Record(source: "one");

        Outcome(result, "recorded");
        Assert.Equal(1, source.Requests);
        var finding = Assert.Single(await Ledger());
        Assert.Equal(("copilot", "one"), (finding.Finding.Rule, finding.Source));
        Assert.Equal(1, result.Findings!["open_blocking"]);
    }

    [Fact]
    public async Task A_source_that_cannot_review_is_unavailable_and_records_nothing()
    {
        var result = await Record(source: "one");

        Outcome(result, "unavailable");
        Assert.Empty(await Ledger());
        Assert.Contains(result.Events, e => e.Contains("unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_review_pending_past_the_timeout_is_unavailable()
    {
        source.Review(world.Repo, world.Work!, new FindingsReport(1, [Finding()]), pollsUntilReady: 1000);
        var actions = new EngineActions(world.Providers, new ActionOptions(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(30))
        {
            ReviewTimeout = TimeSpan.FromMilliseconds(200),
        });
        var call = world.Call("findings.record", "external_review", 1, "{ source: one }");

        ActionResult result;
        var reads = 0;
        do
        {
            result = await actions.RunAsync(call, world.Scope, () => { }, Ct);
            await Task.Delay(20, Ct);
        }
        while (result.Kind == ActionResult.Wait && ++reads < 200);

        Outcome(result, "unavailable");
        Assert.True(reads > 1, "it waited before giving up");
        Assert.Equal(1, source.Requests);
        Assert.Empty(await Ledger());
    }

    // ---- findings.triage ----

    [Fact]
    public async Task A_triage_applies_each_state_with_its_actor_and_reason_and_the_namespace_follows()
    {
        await Reviewed("review.assess", Finding("a"), Finding("b", snippet: "void Add()"), Finding("c", snippet: "class Orders"));
        await Record();
        var ledger = await Ledger();
        string Id(string rule) => ledger.Single(f => f.Finding.Rule == rule).FindingId;
        Fixer();
        await Triaged("fix.triage",
            new TriageEntry(Id("a"), FindingStates.Fixed, "done"),
            new TriageEntry(Id("b"), FindingStates.Declined, "by design"),
            new TriageEntry(Id("c"), FindingStates.Escalated, "a product question"));

        var result = await Triage(stepId: "fix.apply_triage");

        Outcome(result, "applied");
        var after = await Ledger();
        Assert.Equal((FindingStates.Fixed, 1, world.Work), (after.Single(f => f.FindingId == Id("a")).State, after.Single(f => f.FindingId == Id("a")).Fixes,
            after.Single(f => f.FindingId == Id("a")).StateRevision));
        Assert.Equal(FindingStates.Declined, after.Single(f => f.FindingId == Id("b")).State);
        var triaged = (await History(after.Single(f => f.FindingId == Id("b")))).Last();
        Assert.Equal((FindingEventKinds.Triaged, FindingStates.Declined, "fix.triage", "by design", "fix.apply_triage"), (triaged.Kind, triaged.State, triaged.Actor, triaged.Reason, triaged.StepId));
        Assert.Equal((0L, 1L, 1L, 1L, 0L), (result.Findings!["open"], result.Findings["fixed"], result.Findings["declined"], result.Findings["escalated"], result.Findings["open_blocking"]));
    }

    [Theory]
    [InlineData("F-0000000000", "is not a finding in this run's ledger")]
    [InlineData("", "names no finding")]
    public async Task A_triage_naming_an_unknown_finding_is_invalid_and_applies_nothing(string unknown, string expected)
    {
        await Reviewed("review.assess", Finding());
        await Record();
        var known = Assert.Single(await Ledger());
        await Triaged("fix.triage", new TriageEntry(known.FindingId, FindingStates.Fixed, "done"), new TriageEntry(unknown, FindingStates.Fixed, "done"));

        var result = await Triage(stepId: "fix.apply_triage");

        Outcome(result, "invalid");
        Assert.Contains(result.Events, e => e.Contains(expected, StringComparison.Ordinal));
        Assert.Equal(FindingStates.Open, Assert.Single(await Ledger()).State);
        Assert.Single(await History(known));
    }

    [Theory]
    [InlineData("done", "resolved", "state must be one of")]
    [InlineData(" ", "fixed", "a reason is required")]
    public async Task A_triage_entry_with_a_bad_state_or_no_reason_is_invalid(string reason, string state, string expected)
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Triaged("fix.triage", new TriageEntry(Assert.Single(await Ledger()).FindingId, state, reason));

        var result = await Triage(stepId: "fix.apply_triage");

        Outcome(result, "invalid");
        Assert.Contains(result.Events, e => e.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_retried_triage_applies_nothing_again_even_after_the_finding_moved_on()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        var finding = Assert.Single(await Ledger());
        Fixer();
        await Triaged("verify_fix", new TriageEntry(finding.FindingId, FindingStates.Fixed, "verified"));
        var call = world.Call("findings.triage", "record_fixes", 1, "{ from: triage }");
        await world.Actions.RunAsync(call, world.Scope, () => { }, Ct);
        // Before the retry, a person reopens the finding: the retry must not fix it again.
        await Triaged("a_person", new TriageEntry(finding.FindingId, FindingStates.Open, "not fixed after all"));
        Outcome(await Triage(stepId: "a_person.apply"), "applied");
        await Triaged("verify_fix", new TriageEntry(finding.FindingId, FindingStates.Fixed, "verified"));

        var retried = await world.Actions.RunAsync(call, world.Scope, () => { }, Ct);

        Outcome(retried, "applied");
        var after = Assert.Single(await Ledger());
        Assert.Equal((FindingStates.Open, 1), (after.State, after.Fixes));
        Assert.Equal(3, (await History(finding)).Count);
    }

    [Fact]
    public async Task A_decline_is_posted_once_on_the_pull_request_and_not_when_post_declines_is_false()
    {
        await Reviewed("review.assess", Finding(), Finding("b", snippet: "void Add()"));
        await Record();
        Outcome(await world.RunAsync("scm.pull_request", "{ title: T-1 }"), "ready");
        var pr = ActionRunAccess.Pr(world.PullRequests.Single());
        var ledger = await Ledger();
        await Triaged("fix.triage", new TriageEntry(ledger[0].FindingId, FindingStates.Declined, "not a bug: x is never null here"));
        var call = world.Call("findings.triage", "fix.apply_triage", 1, "{ from: triage }");

        await world.Actions.RunAsync(call, world.Scope, () => { }, Ct);
        await world.Actions.RunAsync(call, world.Scope, () => { }, Ct);
        await Triaged("fix.triage", new TriageEntry(ledger[1].FindingId, FindingStates.Declined, "quiet"));
        Outcome(await Triage(stepId: "fix.apply_quietly", postDeclines: false), "applied");

        var comment = Assert.Single(world.Forge.Comments(pr));
        Assert.Contains($"Finding {ledger[0].FindingId} declined", comment, StringComparison.Ordinal);
        Assert.Contains("not a bug: x is never null here", comment, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_decline_before_a_pull_request_is_open_is_noted_not_posted()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Triaged("fix.triage", new TriageEntry(Assert.Single(await Ledger()).FindingId, FindingStates.Declined, "by design"));

        var result = await Triage(stepId: "fix.apply_triage");

        Outcome(result, "applied");
        Assert.Contains(result.Events, e => e.Contains("not posted: no pull request", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Check_31_refuses_an_unknown_reopen_on()
    {
        await Reviewed("review.assess", Finding());

        var result = await Record(reopenOn: "sometimes");

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("reopen_on", result.Reason, StringComparison.Ordinal);
    }

    // ---- Review of PR 11 ----

    [Theory]
    [InlineData(FindingStates.Fixed, "[open, declined, escalated]", "invalid")]
    [InlineData(FindingStates.Declined, "[fixed, open]", "invalid")]
    [InlineData(FindingStates.Fixed, "[fixed, open]", "applied")]
    public async Task A_triage_may_set_only_the_states_its_step_allows(string state, string states, string expected)
    {
        await Reviewed("review.assess", Finding());
        await Record();
        Fixer();
        await Triaged("fix.triage", new TriageEntry(Assert.Single(await Ledger()).FindingId, state, "because"));

        var result = await Triage(stepId: "fix.apply_triage", states: states);

        Outcome(result, expected);
        Assert.Equal(expected == "applied" ? state : FindingStates.Open, Assert.Single(await Ledger()).State);
        if (expected == "invalid")
            Assert.Contains(result.Events, e => e.Contains("this step may set only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_triage_counts_its_escalations_and_its_declines_of_blocking_or_demonstrated_findings_itself()
    {
        await Reviewed("review.assess", Finding("blocking"), Finding("demonstrated", blocking: false, basis: FindingBases.Demonstrated, snippet: "void Add()"),
            Finding("minor", blocking: false, snippet: "class Orders"), Finding("ask", snippet: "x"));
        await Record();
        var ledger = await Ledger();
        string Id(string rule) => ledger.Single(f => f.Finding.Rule == rule).FindingId;
        await Triaged("fix.triage", new TriageEntry(Id("blocking"), FindingStates.Declined, "no"), new TriageEntry(Id("demonstrated"), FindingStates.Declined, "no"),
            new TriageEntry(Id("minor"), FindingStates.Declined, "no"), new TriageEntry(Id("ask"), FindingStates.Escalated, "a person decides"));

        var result = await Triage(stepId: "fix.apply_triage");

        Assert.Equal((1L, 2L), (Output(result, "escalated"), Output(result, "declined_blocking")));
    }

    [Fact]
    public async Task A_weaker_re_raise_does_not_downgrade_the_finding()
    {
        await Reviewed("review.assess", Finding(basis: FindingBases.Demonstrated) with { Proof = "OrdersTests.Add_null fails", Severity = "critical" });
        await Record();
        await Reviewed("review.assess", Finding(blocking: false) with { Severity = "low", Description = "Reworded" });

        var again = await Record();

        var finding = Assert.Single(await Ledger()).Finding;
        Assert.Equal((true, FindingBases.Demonstrated, "critical", "OrdersTests.Add_null fails", "Reworded"),
            (finding.Blocking, finding.Basis, finding.Severity, finding.Proof, finding.Description));
        Assert.Equal(1, again.Findings!["open_blocking"]);
    }

    [Fact]
    public async Task Each_findings_action_returns_a_higher_ledger_version()
    {
        await Reviewed("review.assess", Finding());
        var first = await Record();
        var second = await Record();
        await Triaged("fix.triage", new TriageEntry(Assert.Single(await Ledger()).FindingId, FindingStates.Declined, "no"));
        var third = await Triage(stepId: "fix.apply_triage");

        Assert.True(first.FindingsVersion > 0 && second.FindingsVersion > first.FindingsVersion && third.FindingsVersion > second.FindingsVersion,
            $"{first.FindingsVersion}, {second.FindingsVersion}, {third.FindingsVersion}");
        Assert.Equal(third.FindingsVersion, third.ToOutcome().FindingsVersion);
    }

    [Fact]
    public async Task Two_problems_of_one_rule_in_one_file_with_no_symbol_or_snippet_stay_two_and_a_true_duplicate_is_noted()
    {
        ReportedFinding Bare(string title) => Finding() with { Title = title, Location = new FindingLocation("acme/api", File) };
        await Reviewed("review.assess", Bare("x may be null"), Bare("y may be null"), Bare("x may be null"));

        var result = await Record();

        Assert.Equal(2L, Output(result, "new"));
        Assert.Equal(2, (await Ledger()).Count);
        Assert.Contains(result.Events, e => e.Contains("\"x may be null\"", StringComparison.Ordinal) && e.Contains("recorded once", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_fix_moves_the_state_revision_so_only_a_later_change_reopens_it()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        var raisedAt = world.Work;
        var fixedAt = world.CommitWork(File, "class Orders { void Add() { if (x) { } } } // fixed\n");
        await Triaged("verify_fix", new TriageEntry(Assert.Single(await Ledger()).FindingId, FindingStates.Fixed, "verified"));
        Outcome(await Triage(stepId: "fix.record_fixes", states: "[fixed, open]"), "applied");
        Assert.Equal((fixedAt, raisedAt), (Assert.Single(await Ledger()).StateRevision, (await Ledger())[0].RaisedRevision));

        await Reviewed("review.assess", Finding());
        Assert.Equal(0L, Output(await Record(), "reopened"));
        Assert.Equal(FindingStates.Fixed, Assert.Single(await Ledger()).State);

        world.CommitWork(File, "class Orders { void Add() { if (x) { } } } // changed again\n");
        await Reviewed("review.assess", Finding());
        Assert.Equal(1L, Output(await Record(), "reopened"));
        Assert.Equal(FindingStates.Open, Assert.Single(await Ledger()).State);
    }

    [Fact]
    public async Task A_triage_naming_a_finding_twice_is_invalid()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        var id = Assert.Single(await Ledger()).FindingId;
        await Triaged("fix.triage", new TriageEntry(id, FindingStates.Declined, "no"), new TriageEntry(id, FindingStates.Open, "yes"));

        var result = await Triage(stepId: "fix.apply_triage");

        Outcome(result, "invalid");
        Assert.Contains(result.Events, e => e.Contains("named more than once", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_triage_of_another_schema_is_invalid()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Produced("fix.triage", "triage", ArtifactShapes.WriteLibrary(new Triage(2, [new TriageEntry(Assert.Single(await Ledger()).FindingId, FindingStates.Open, "x")])));

        var result = await Triage(stepId: "fix.apply_triage");

        Outcome(result, "invalid");
        Assert.Contains(result.Events, e => e.Contains("schema must be 1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_finding_raised_in_both_this_run_and_a_child_is_re_raised_in_this_runs_ledger()
    {
        var child = world.RunId + ".a.1";
        var at = DateTimeOffset.UtcNow;
        (await world.Records.AppendAsync(new RecordBatch(Runs: [new RunRecord(child, world.RunId, "ticket_delivery", "completed", "me", ["acme/api"], false, at, null)]), Ct)).Unwrap();
        var fingerprint = Fingerprints.Of(Finding());
        (await world.Records.AppendAsync(new RecordBatch(Findings:
        [
            new FindingRecord(child, Fingerprints.LedgerId(child, fingerprint), fingerprint, Finding(), "child", null, FindingStates.Open, world.Work, world.Work, 0, 0, 0, false, at),
            new FindingRecord(world.RunId, Fingerprints.LedgerId(world.RunId, fingerprint), fingerprint, Finding(), "own", null, FindingStates.Open, world.Work, world.Work, 0, 0, 0, false, at),
        ]), Ct)).Unwrap();
        await Reviewed("review.assess", Finding());

        await Record();

        var ledger = await Ledger();
        Assert.Equal((0, 1), (ledger.Single(f => f.RunId == child).Reraises, ledger.Single(f => f.RunId == world.RunId).Reraises));
    }

    [Fact]
    public async Task A_triage_repeating_a_findings_state_changes_nothing()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        var id = Assert.Single(await Ledger()).FindingId;
        Fixer();
        await Triaged("verify_fix", new TriageEntry(id, FindingStates.Fixed, "verified"));
        Outcome(await Triage(stepId: "fix.record_fixes"), "applied");
        var once = Assert.Single(await Ledger());
        world.CommitWork("src/Other.cs", "class Other { int z; }\n");

        Outcome(await Triage(stepId: "fix.record_fixes"), "applied");

        var twice = Assert.Single(await Ledger());
        Assert.Equal((1, once.StateRevision), (twice.Fixes, twice.StateRevision));
    }

    // ---- Re-check of PR 11 ----

    [Fact]
    public async Task A_finding_cannot_be_fixed_with_nothing_committed_since_it_was_raised()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        var id = Assert.Single(await Ledger()).FindingId;
        await Triaged("verify_fix", new TriageEntry(id, FindingStates.Fixed, "verified"));

        var noFix = await Triage(stepId: "fix.record_fixes", states: "[fixed, open]");
        Fixer();
        var afterAFix = await Triage(stepId: "fix.record_fixes", states: "[fixed, open]");

        Outcome(noFix, "invalid");
        Assert.Contains(noFix.Events, e => e.Contains("nothing has been committed", StringComparison.Ordinal));
        Outcome(afterAFix, "applied");
        Assert.Equal(FindingStates.Fixed, Assert.Single(await Ledger()).State);
    }

    [Fact]
    public async Task A_reopened_finding_needs_a_commit_after_its_reopening_to_be_fixed_again()
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Fix(Assert.Single(await Ledger()), "verify");
        world.CommitWork(File, "class Orders { void Add() { if (x) { } } } // changed\n");
        await Reviewed("review.assess", Finding());
        Assert.Equal(1L, Output(await Record(), "reopened"));
        await Triaged("verify_fix", new TriageEntry(Assert.Single(await Ledger()).FindingId, FindingStates.Fixed, "verified"));

        Outcome(await Triage(stepId: "fix.record_fixes"), "invalid");
    }

    // ---- Integration (LOG 190) ----

    [Theory]
    [InlineData("api")]
    [InlineData("Acme/API")]
    public async Task A_finding_naming_the_runs_repository_by_short_name_or_case_is_recorded_in_it_and_raised_at_its_head(string written)
    {
        await Reviewed("review.assess", Finding() with { Location = Finding().Location with { Repository = written } });

        Outcome(await Record(), "recorded");

        var finding = Assert.Single(await Ledger());
        Assert.Equal(("acme/api", world.Work), (finding.Finding.Location.Repository, finding.RaisedRevision));
        await Reviewed("review.assess", Finding());
        Assert.Equal(1L, Output(await Record(), "reraised"));
    }

    /// <summary>LOG 190 and 198 (review F4 of PR 14): one rule for findings and child runs; a name several repositories end with is kept.</summary>
    [Theory]
    [InlineData("api", "acme/api,acme/web", "acme/api")]
    [InlineData("ACME/Web", "acme/api,acme/web", "acme/web")]
    [InlineData("api", "acme/api,other/api", "api")]
    [InlineData("client", "acme/api", "client")]
    public void A_repository_is_named_as_the_run_names_it_only_when_exactly_one_matches(string written, string known, string expected) =>
        Assert.Equal(expected, RepositoryNames.As(written, known.Split(',')));

    [Fact]
    public async Task A_finding_in_a_repository_the_run_has_not_prepared_cannot_be_fixed_by_a_triage()
    {
        await Reviewed("review.assess", Finding() with { Location = Finding().Location with { Repository = "acme/elsewhere" } });
        await Record();
        var finding = Assert.Single(await Ledger());
        Assert.Null(finding.RaisedRevision);
        Fixer();
        await Triaged("verify_fix", new TriageEntry(finding.FindingId, FindingStates.Fixed, "verified"));

        var result = await Triage(stepId: "fix.record_fixes", states: "[fixed, open]");

        Outcome(result, "invalid");
        Assert.Contains(result.Events, e => e.Contains("which this run has not prepared", StringComparison.Ordinal));
        Assert.Equal(FindingStates.Open, Assert.Single(await Ledger()).State);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[open, resolved]")]
    public async Task Check_31_refuses_states_that_are_empty_or_not_finding_states(string states)
    {
        await Reviewed("review.assess", Finding());
        await Record();
        await Triaged("fix.triage", new TriageEntry(Assert.Single(await Ledger()).FindingId, FindingStates.Open, "to fix"));

        var result = await Triage(stepId: "fix.apply_triage", states: states);

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("'states' must name one or more of", result.Reason, StringComparison.Ordinal);
    }
}
