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
        "{ rule: null-check, title: App may dereference null, description: App has no guard, severity: high, blocking: true, basis: reasoned, "
        + "location: { repository: acme/api, file: src/App.cs, symbol: App, snippet: 'class App {}' } }";

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
            // M6 validators: the fixer's regression test is in the file it wrote (the editing runtime writes work/<step>.txt)
            // and passes in the engine's re-test (the TRX names it), so tests_added accepts it.
            "verify.fix.fix: { artifacts: { fix_claims: { schema: 1, claims: [{ criterion: AC-1, verification: unit, tests: "
            + "[{ repository: acme/api, tier: unit, id: Regression.fix, change: modified, source: { file: work/verify.fix.fix.txt } }] }] }, "
            + "implementation_claims: { schema: 1, claims: [{ criterion: AC-1, verification: unit, tests: [{ repository: acme/api, tier: unit, id: Adds, change: new }] }] } } }",
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
    /// counted by findings.triage itself, goes to a person. Either way the run stops at an intervention before any merge.
    /// </summary>
    [Theory]
    [InlineData("fixed", "verify.fix.triage")]
    [InlineData("escalated", "verify.fix.apply_triage")]
    [InlineData("declined", "verify.fix.apply_triage")]
    public async Task The_triager_alone_cannot_clear_a_demonstrated_blocking_finding(string state, string stopsAt)
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
        Assert.Equal(("intervention", stopsAt), (wait["kind"]!.ToString(), wait["stepId"]!.ToString()));
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
        var minor = "{ rule: naming, title: App is a vague name, description: App says little, severity: low, blocking: false, basis: reasoned, "
                    + "location: { repository: acme/api, file: src/App.cs, symbol: App, snippet: 'class App' } }";
        var script = "{ " + string.Join(", ",
        [
            $"verify.review.review_0.assess: [{Report(Blocking + ", " + minor)}, {Report("")}]",
            .. Enumerable.Range(1, 4).Select(i => $"verify.review.review_{i}.assess: {Report("")}"),
            "verify.fix.triage: { artifacts: { triage: { triage_each_open: open } } }",
            $"verify.fix.verify_fix: {{ outcome: verified, artifacts: {{ fix_verification: {{ triage_each_open: {verified}, non_blocking: open }} }} }}",
            "verify.fix.fix: { artifacts: { fix_claims: { schema: 1, claims: [{ criterion: AC-1, verification: unit, tests: "
            + "[{ repository: acme/api, tier: unit, id: Regression.fix, change: modified, source: { file: work/verify.fix.fix.txt } }] }] }, "
            + "implementation_claims: { schema: 1, claims: [{ criterion: AC-1, verification: unit, tests: [{ repository: acme/api, tier: unit, id: Adds, change: new }] }] } } }",
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
}
