using System.Text.Json.Nodes;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// std's review and fix loop with the real ledger (M6), on the in-memory world of <see cref="ForgeTicketTests"/>: a
/// reviewer's blocking finding is recorded, the merge bar (<c>findings.open_blocking</c>) sends the ticket into the fix
/// loop, the triager and the verifying reviewer work from the ledger view the engine stages for them, their triages are
/// applied, the second review finds nothing open, and the ticket merges.
/// </summary>
[Trait("Category", "Integration")]
public class FindingsLoopTests
{
    const string Blocking =
        "{ rule: null_dereference, title: App may dereference null, description: App has no guard, severity: high, blocking: true, basis: reasoned, "
        + "location: { repository: acme/api, file: src/App.cs, symbol: App, snippet: 'class App {}' } }";

    /// <summary>
    /// The fixer's claims: its regression test is in the file it wrote (the editing runtime writes work/&lt;step&gt;.txt) and
    /// passes in the engine's re-test (the TRX names it), so tests_added accepts it. Scripted wherever the fixer runs, since
    /// the engine checks its claims against their type when it reports (LOG 246).
    /// </summary>
    const string FixClaims = "verify.fix.fix: { artifacts: { fix_claims: { schema: 1, claims: [{ criterion: AC-1, verification: unit, tests: "
        + "[{ repository: acme/api, tier: unit, id: Regression.fix, change: modified, source: { file: work/verify.fix.fix.txt } }] }] }, "
        + "implementation_claims: { schema: 1, claims: [{ criterion: AC-1, verification: unit, tests: [{ repository: acme/api, tier: unit, id: Adds, change: new }] }] } } }";

    static string Report(string findings) => $"{{ artifacts: {{ findings_report: {{ schema: 1, findings: [{findings}] }} }} }}";

    [Fact]
    public async Task A_blocking_finding_goes_through_std_s_fix_loop_on_the_real_ledger_and_the_ticket_then_merges()
    {
        var script = "{ " + string.Join(", ",
        [
            $"verify.review.review_0.assess: [{Report(Blocking)}, {Report("")}]",
            .. Enumerable.Range(1, 4).Select(i => $"verify.review.review_{i}.assess: {Report("")}"),
            "verify.fix.triage: { artifacts: { triage: { triage_each_open: open } } }",
            "verify.fix.verify_fix: { outcome: verified, artifacts: { fix_verification: { triage_each_open: fixed } } }",
            FixClaims,
        ]) + " }";
        var (h, _, _) = await ForgeTicketTests.StartAsync("findings-loop", policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }", script: script);
        await using var _h = h;

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-1", "--param", "engine_can_merge=true", "--repo", "acme/api");
        var (exit, end, error) = h.Cli("wait", id, "--timeout", "240");

        var view = await h.ShowAsync(id);
        Assert.True(exit == 0, end + error + view.ToJsonString());
        Assert.Equal($"run {id} completed", end.Trim());
        var path = Harness.StartedSteps(view);
        Assert.Equal(2, path.Count(s => s == "verify.review.review_0.assess"));
        Assert.Equal(["verify.fix.triage", "verify.fix.apply_triage", "verify.fix.fix", "verify.fix.verify_fix", "verify.fix.record_fixes", "verify.fix.retest", "verify.fix.tests_added"],
            path.Where(s => s.StartsWith("verify.fix.", StringComparison.Ordinal)));
        Assert.Contains("deliver.merge", path);

        var records = new SqliteRecordStore(Path.Combine(h.Folder, "records.db"));
        var ct = TestContext.Current.CancellationToken;
        var finding = Assert.Single((await records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap());
        Assert.Equal((FindingStates.Fixed, 1, "verify.review.review_0.assess"), (finding.State, finding.Fixes, finding.Source));
        var history = (await records.QueryAsync<FindingEventRecord>(Queries.FindingEvents,
            new Dictionary<string, string> { ["run_id"] = id, ["finding_id"] = finding.FindingId }, ct)).Unwrap();
        Assert.Equal([(FindingEventKinds.Raised, FindingStates.Open, "verify.review.review_0.assess"), (FindingEventKinds.Triaged, FindingStates.Open, "verify.fix.triage"),
            (FindingEventKinds.Triaged, FindingStates.Fixed, "verify.fix.verify_fix")], history.Select(e => (e.Kind, e.State, e.Actor)));

        // The triager and verifier were shown the ledger with its ids: one finding, open.
        foreach (var step in new[] { "verify.fix.triage", "verify.fix.verify_fix" })
        {
            var events = (await records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = $"{id}/{step}/1" }, ct)).Unwrap();
            Assert.Contains(events, e => e.Kind == FindingsLedger.ViewName && e.Detail.StartsWith("1 findings, 1 open", StringComparison.Ordinal));
        }

        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    /// <summary>
    /// Review F1: the triager alone cannot clear a demonstrated, blocking finding. Every review raises it again on unchanged
    /// code; the triager marks it fixed, escalated or declined. None of these merges: fixed is not a state the triager may
    /// set (its triage goes back to it until its attempts run out), and an escalation or a declined blocking finding,
    /// counted by findings.triage itself, goes to a person: an intervention, or (LOG 248) the person's triage decision. Either
    /// way the run stops for a person before any merge.
    /// </summary>
    [Theory]
    [InlineData("fixed", "intervention", "verify.fix.triage")]
    [InlineData("escalated", "decision", "verify.fix.person_triage")]
    [InlineData("declined", "decision", "verify.fix.person_triage")]
    public async Task The_triager_alone_cannot_clear_a_demonstrated_blocking_finding(string state, string kind, string stopsAt)
    {
        var demonstrated = Blocking.Replace("basis: reasoned", "basis: demonstrated, proof: AppTests.Null fails", StringComparison.Ordinal);
        var script = "{ " + string.Join(", ",
        [
            $"verify.review.review_0.assess: {Report(demonstrated)}",
            .. Enumerable.Range(1, 4).Select(i => $"verify.review.review_{i}.assess: {Report("")}"),
            $"verify.fix.triage: {{ artifacts: {{ triage: {{ triage_each_open: {state} }} }} }}",
        ]) + " }";
        var (h, forge, repo) = await ForgeTicketTests.StartAsync($"findings-{state}", policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }", script: script);
        await using var _h = h;
        var main = forge.Resolve(repo, "main");

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-1", "--param", "engine_can_merge=true", "--repo", "acme/api");
        Assert.NotNull(await h.WaitForWaitAsync(id, TimeSpan.FromSeconds(240)));

        var view = await h.ShowAsync(id);
        var wait = view["waits"]!.AsArray().Single()!;
        Assert.Equal((kind, stopsAt), (wait["kind"]!.ToString(), wait["stepId"]!.ToString()));
        var path = Harness.StartedSteps(view);
        Assert.DoesNotContain(path, s => s.StartsWith("deliver.", StringComparison.Ordinal));
        Assert.DoesNotContain("verify.fix.fix", path);
        Assert.Equal(main, forge.Resolve(repo, "main"));
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    /// <summary>
    /// Re-check R1 and R2: nor can the verifier alone. A fixer that commits nothing, then a verifier writing fixed (R1), or
    /// a verifier declining after a real fix (R2): findings.triage refuses both as invalid, so verify_fix runs again until
    /// its attempts run out and a person decides. The finding stays open, and nothing merges.
    /// </summary>
    [Theory]
    [InlineData("fixed", true)]
    [InlineData("declined", false)]
    public async Task The_verifier_alone_cannot_clear_a_demonstrated_blocking_finding(string state, bool noFix)
    {
        var demonstrated = Blocking.Replace("basis: reasoned", "basis: demonstrated, proof: AppTests.Null fails", StringComparison.Ordinal);
        var script = "{ " + string.Join(", ",
        [
            $"verify.review.review_0.assess: {Report(demonstrated)}",
            .. Enumerable.Range(1, 4).Select(i => $"verify.review.review_{i}.assess: {Report("")}"),
            "verify.fix.triage: { artifacts: { triage: { triage_each_open: open } } }",
            $"verify.fix.verify_fix: {{ outcome: verified, artifacts: {{ fix_verification: {{ triage_each_open: {state} }} }} }}",
            FixClaims,
        ]) + " }";
        var (h, forge, repo) = await ForgeTicketTests.StartAsync($"verifier-{state}", policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }", script: script,
            noEdits: noFix ? ["verify.fix.fix"] : null);
        await using var _h = h;
        var main = forge.Resolve(repo, "main");

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-1", "--param", "engine_can_merge=true", "--repo", "acme/api");
        Assert.NotNull(await h.WaitForWaitAsync(id, TimeSpan.FromSeconds(240)));

        var view = await h.ShowAsync(id);
        var wait = view["waits"]!.AsArray().Single()!;
        Assert.Equal(("intervention", "verify.fix.verify_fix"), (wait["kind"]!.ToString(), wait["stepId"]!.ToString()));
        var path = Harness.StartedSteps(view);
        Assert.DoesNotContain(path, s => s.StartsWith("deliver.", StringComparison.Ordinal));
        Assert.Equal(3, path.Count(s => s == "verify.fix.record_fixes"));
        Assert.Equal(main, forge.Resolve(repo, "main"));
        var records = new SqliteRecordStore(Path.Combine(h.Folder, "records.db"));
        var finding = Assert.Single((await records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = id },
            TestContext.Current.CancellationToken)).Unwrap());
        Assert.Equal(FindingStates.Open, finding.State);
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    /// <summary>
    /// Integration seam (a): fix_loop leaves by <c>fixed</c> once <c>tests_added</c> is valid, without reading
    /// <c>findings.open</c> again. The caller's merge bar does. A non-blocking finding left open neither loops nor is lost:
    /// it stays open in the ledger and the ticket merges. A blocking finding the verifier leaves open sends the run round
    /// the fix loop again from the merge bar, and nothing merges.
    /// </summary>
    [Theory]
    [InlineData("fixed")]
    [InlineData("open")]
    public async Task After_the_fix_loop_the_merge_bar_decides_on_what_is_still_open(string verified)
    {
        var minor = "{ rule: maintainability, title: App is a vague name, description: App says little, severity: low, blocking: false, basis: reasoned, "
                    + "location: { repository: acme/api, file: src/App.cs, symbol: App, snippet: 'class App' } }";
        var script = "{ " + string.Join(", ",
        [
            $"verify.review.review_0.assess: [{Report(Blocking + ", " + minor)}, {Report("")}]",
            .. Enumerable.Range(1, 4).Select(i => $"verify.review.review_{i}.assess: {Report("")}"),
            "verify.fix.triage: { artifacts: { triage: { triage_each_open: open } } }",
            $"verify.fix.verify_fix: {{ outcome: verified, artifacts: {{ fix_verification: {{ triage_each_open: {verified}, non_blocking: open }} }} }}",
            FixClaims,
        ]) + " }";
        var (h, forge, repo) = await ForgeTicketTests.StartAsync($"merge-bar-{verified}", policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }", script: script);
        await using var _h = h;
        var main = forge.Resolve(repo, "main");

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-1", "--param", "engine_can_merge=true", "--repo", "acme/api");
        var records = new SqliteRecordStore(Path.Combine(h.Folder, "records.db"));
        var ct = TestContext.Current.CancellationToken;

        if (verified == "fixed")
        {
            var (exit, end, error) = h.Cli("wait", id, "--timeout", "240");
            var view = await h.ShowAsync(id);
            Assert.True(exit == 0 && end.Trim() == $"run {id} completed", end + error + view.ToJsonString());
            Assert.Single(Harness.StartedSteps(view), s => s == "verify.fix.triage");
            Assert.NotEqual(main, forge.Resolve(repo, "main"));
        }
        else
        {
            Assert.NotNull(await h.WaitForWaitAsync(id, TimeSpan.FromSeconds(240)));
            var view = await h.ShowAsync(id);
            Assert.Equal("intervention", view["waits"]!.AsArray().Single()!["kind"]!.ToString());
            var path = Harness.StartedSteps(view);
            Assert.True(path.Count(s => s == "verify.fix.triage") >= 2, string.Join(", ", path));
            Assert.DoesNotContain(path, s => s.StartsWith("deliver.", StringComparison.Ordinal));
            Assert.Equal(main, forge.Resolve(repo, "main"));
        }

        var ledger = (await records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap();
        Assert.Equal((verified, FindingStates.Open), (ledger.Single(f => f.Finding.Blocking).State, ledger.Single(f => !f.Finding.Blocking).State));
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }

    /// <summary>
    /// LOG 248, F8: the triager declines a demonstrated blocking finding, which it may not clear alone, so a person triages it
    /// in a decision. An answer without the triage is refused. The person reads the ledger, declines the finding with a reason
    /// through the CLI, and the decline is recorded as the authenticated person (the harness's token is "tester"), not as a
    /// step. Nothing is left open, so the fix loop ends without a fix, and the ticket merges.
    /// </summary>
    [Fact]
    public async Task A_person_triages_what_the_triager_may_not_clear_and_is_recorded_as_the_actor()
    {
        var demonstrated = Blocking.Replace("basis: reasoned", "basis: demonstrated, proof: AppTests.Null fails", StringComparison.Ordinal);
        var script = "{ " + string.Join(", ",
        [
            $"verify.review.review_0.assess: [{Report(demonstrated)}, {Report("")}]",
            .. Enumerable.Range(1, 4).Select(i => $"verify.review.review_{i}.assess: {Report("")}"),
            "verify.fix.triage: { artifacts: { triage: { triage_each_open: declined } } }",
        ]) + " }";
        var (h, _, _) = await ForgeTicketTests.StartAsync("person-triage", policy: "  merge: { default_branch: { engine_may_merge: [acme/api] } }", script: script);
        await using var _h = h;
        var ct = TestContext.Current.CancellationToken;

        var id = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=T-1", "--param", "engine_can_merge=true", "--repo", "acme/api");
        var wait = await h.WaitForWaitAsync(id, TimeSpan.FromSeconds(240));
        Assert.Equal(("decision", "verify.fix.person_triage"), ((await h.ShowAsync(id))["waits"]![0]!["kind"]!.ToString(), (await h.ShowAsync(id))["waits"]![0]!["stepId"]!.ToString()));

        var (refused, _, refusal) = h.Cli("answer", id, wait!, "triaged");
        Assert.NotEqual(0, refused);
        Assert.Contains("'triaged' must give artifact person_triage", refusal, StringComparison.Ordinal);

        var (read, ledger, readError) = h.Cli("findings", id);
        Assert.True(read == 0, readError);
        var finding = Assert.Single(ArtifactShapes.ReadLibrary<FindingsReport>(ledger, "the ledger").Findings);
        Assert.Equal(FindingStates.Declined, finding.State);
        var triage = Path.Combine(h.Folder, "person_triage.yaml");
        await File.WriteAllTextAsync(triage, $"schema: 1\nentries:\n  - {{ finding: {finding.LedgerId}, state: declined, reason: \"A known limitation; accepted for this release\" }}\n", ct);
        var (answered, _, answerError) = h.Cli("answer", id, wait!, "triaged", "--artifact", $"person_triage={triage}");
        Assert.True(answered == 0, answerError);

        var (exit, end, error) = h.Cli("wait", id, "--timeout", "240");
        var view = await h.ShowAsync(id);
        Assert.True(exit == 0, end + error + view.ToJsonString());
        var path = Harness.StartedSteps(view);
        Assert.Contains("verify.fix.apply_person_triage", path);
        Assert.DoesNotContain("verify.fix.fix", path);
        Assert.Contains("deliver.merge", path);

        var records = new SqliteRecordStore(Path.Combine(h.Folder, "records.db"));
        var recorded = Assert.Single((await records.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap());
        Assert.Equal(FindingStates.Declined, recorded.State);
        var history = (await records.QueryAsync<FindingEventRecord>(Queries.FindingEvents,
            new Dictionary<string, string> { ["run_id"] = id, ["finding_id"] = recorded.FindingId }, ct)).Unwrap();
        Assert.Equal([(FindingStates.Open, "verify.review.review_0.assess"), (FindingStates.Declined, "verify.fix.triage"), (FindingStates.Declined, "tester")],
            history.Select(e => (e.State, e.Actor)));
        Assert.Equal("A known limitation; accepted for this release", history.Last().Reason);
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
    }
}
