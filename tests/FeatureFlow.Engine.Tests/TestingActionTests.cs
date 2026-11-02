using System.Text.Json;
using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Engine.Tests;

/// <summary><c>test.run</c>, <c>test.integration</c>, <c>analysis.run</c> and <c>repo.inspect</c> (docs/04 sections 9 and 12).</summary>
public sealed class TestingActionTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    static string Outcome(ActionResult r) => r.Report?.Outcome ?? $"{r.Kind}: {r.Reason}";

    static ActionWorld WithManifest(string extra = "", bool sandbox = true, bool ci = true) =>
        new(new Dictionary<string, string> { ["feature-flow.yaml"] = ActionWorld.Manifest(extra), ["README.md"] = "# api\n" }, sandbox, ci);

    static async Task<TestEvidence> EvidenceAsync(ActionWorld w, ActionResult r) =>
        JsonSerializer.Deserialize<TestEvidence>(await w.ReadAsync(r.Report!.Artifacts["evidence"]), Json)!;

    [Fact]
    public async Task A_sandbox_tier_runs_setup_then_its_command_and_its_TRX_becomes_test_results_evidence()
    {
        await using var w = WithManifest();
        w.Sandbox.On(new SandboxRule("dotnet test tests/Unit", Files: new Dictionary<string, string> { [ActionWorld.UnitTrx] = ActionWorld.Trx(3) }));

        var result = await w.RunAsync("test.run", "{ tier_tags: [unit], baseline: true }", "baseline");

        Assert.Equal("passed", Outcome(result));
        var evidence = await EvidenceAsync(w, result);
        Assert.Equal((w.Work, true, 3, 0, 1), (evidence.Revision, evidence.Baseline, evidence.Tiers.Single().Passed, evidence.Tiers.Single().Failed, evidence.Tiers.Single().Skipped));
        Assert.Equal(["sh -c dotnet tool restore", "sh -c dotnet test tests/Unit --logger \"trx;LogFileName=unit.trx\""], w.Sandbox.Commands.Select(c => c.Command));
        Assert.All(w.Sandbox.Commands, c => Assert.Equal("/ff/work", c.Directory));
        Assert.Equal(0, w.Sandbox.Counts.Live);       // the sandbox is removed
        Assert.NotNull(w.Work);                      // the first step to touch the repository created the work branch
    }

    [Fact]
    public async Task Failed_tests_are_failed_and_a_baseline_that_is_not_clean_is_baseline_failed()
    {
        await using var w = WithManifest();
        w.Sandbox.On(new SandboxRule("dotnet test tests/Unit", Exit: 1, Files: new Dictionary<string, string> { [ActionWorld.UnitTrx] = ActionWorld.Trx(2, "Broken") }));

        var baseline = await w.RunAsync("test.run", "{ tier_tags: [unit], baseline: true }", "baseline");
        var later = await w.RunAsync("test.run", "{ tier_tags: [unit] }", "tests");

        Assert.Equal("baseline_failed", Outcome(baseline));
        Assert.Equal("failed", Outcome(later));
        Assert.Equal("Broken failed", (await EvidenceAsync(w, later)).Tiers.Single().Failures.Single().Message);
    }

    [Fact]
    public async Task A_command_that_fails_with_no_failed_test_still_fails_the_tier()
    {
        await using var w = WithManifest();
        w.Sandbox.On(new SandboxRule("dotnet test tests/Unit", Exit: 1, Output: "error CS1002: ; expected"));

        var result = await w.RunAsync("test.run", "{ tier_tags: [unit] }");

        Assert.Equal("failed", Outcome(result));
        Assert.Contains("CS1002", (await EvidenceAsync(w, result)).Tiers.Single().Failures.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_tier_that_exits_0_with_no_results_or_unparseable_results_raises_an_intervention()
    {
        await using var w = WithManifest();
        Assert.Equal(StepOutcome.CannotAnswer, (await w.RunAsync("test.run", "{ tier_tags: [unit] }", "a")).Kind);

        w.Sandbox.On(new SandboxRule("dotnet test tests/Unit", Files: new Dictionary<string, string> { [ActionWorld.UnitTrx] = "<not-trx/>" }));
        var garbled = await w.RunAsync("test.run", "{ tier_tags: [unit] }", "b");
        Assert.Equal(StepOutcome.CannotAnswer, garbled.Kind);
        Assert.Contains("do not parse", garbled.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results/></TestRun>", "ran no tests (0 skipped)")]
    [InlineData("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results><UnitTestResult testName=\"A\" outcome=\"NotExecuted\"/></Results></TestRun>", "ran no tests (1 skipped)")]
    public async Task A_tier_that_ran_no_tests_is_not_a_pass(string trx, string message)
    {
        await using var w = WithManifest();
        w.Sandbox.On(new SandboxRule("dotnet test tests/Unit", Files: new Dictionary<string, string> { [ActionWorld.UnitTrx] = trx }));

        var result = await w.RunAsync("test.run", "{ tier_tags: [unit] }");

        Assert.Equal("failed", Outcome(result));
        Assert.Contains(message, (await EvidenceAsync(w, result)).Tiers.Single().Failures.Single().Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_CI_tier_is_delegated_and_its_JUnit_results_parsed()
    {
        await using var w = WithManifest();
        w.Forge.ScriptCi("integration.yml", new CiScript(Conclusions.Failure,
            [new CiScriptFile("results.xml", ResultFormats.Junit, ActionWorld.JUnit(4, "Orders"))], PollsUntilDone: 2));

        var result = await w.RunAsync("test.run", "{ tier_tags: [integration] }");

        Assert.Equal("failed", Outcome(result));
        var tier = (await EvidenceAsync(w, result)).Tiers.Single();
        Assert.Equal(("integration", 4, 1, "Api.Orders"), (tier.Tier, tier.Passed, tier.Failed, tier.Failures.Single().Name));
        Assert.Empty(w.Sandbox.Commands);
    }

    [Fact]
    public async Task A_tier_with_both_targets_runs_in_CI_when_no_sandbox_is_configured()
    {
        await using var w = WithManifest(sandbox: false);
        await using var v = new ActionWorld(new Dictionary<string, string> { ["feature-flow.yaml"] = ActionWorld.Manifest().Replace("targets: [sandbox, ci]", "targets: [sandbox, ci]\n    ci: { job: unit.yml }", StringComparison.Ordinal) }, sandbox: false);
        v.Forge.ScriptCi("unit.yml", new CiScript(Conclusions.Success, [new CiScriptFile("unit.trx", ResultFormats.Trx, ActionWorld.Trx(5))]));

        Assert.Equal(StepOutcome.CannotAnswer, (await w.RunAsync("test.run", "{ tier_tags: [unit] }")).Kind);   // no ci.job: nowhere to run
        Assert.Equal("passed", Outcome(await v.RunAsync("test.run", "{ tier_tags: [unit] }")));
    }

    [Fact]
    public async Task Check_33_a_tag_that_matches_no_tier_raises_an_intervention_and_no_manifest_does_too()
    {
        await using var w = WithManifest();
        await using var bare = new ActionWorld();

        var unknown = await w.RunAsync("test.run", "{ tier_tags: [unit, e2e] }");
        var none = await bare.RunAsync("test.run");

        Assert.Equal(StepOutcome.CannotAnswer, unknown.Kind);
        Assert.Contains("check 33", unknown.Reason, StringComparison.Ordinal);
        Assert.Contains("'e2e'", unknown.Reason, StringComparison.Ordinal);
        Assert.Equal(StepOutcome.CannotAnswer, none.Kind);
        Assert.Contains("no feature-flow.yaml", none.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_manifest_with_an_unknown_key_stops_the_step_that_reads_it()
    {
        await using var w = WithManifest("static_analysys: { analyser: none }");

        var result = await w.RunAsync("test.run", "{ tier_tags: [unit] }");

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("unknown key 'static_analysys'", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failing_setup_command_fails_the_step()
    {
        await using var w = WithManifest();
        w.Sandbox.On(new SandboxRule("tool restore", Exit: 2, Output: "no manifest"));

        var result = await w.RunAsync("test.run", "{ tier_tags: [unit] }");

        Assert.Equal(StepOutcome.WorkFailed, result.Kind);
        Assert.Contains("setup command 'dotnet tool restore' exited 2", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_head_already_tested_with_the_same_tiers_adopts_the_result_and_runs_nothing()
    {
        await using var w = WithManifest();
        w.Sandbox.On(new SandboxRule("dotnet test tests/Unit", Files: new Dictionary<string, string> { [ActionWorld.UnitTrx] = ActionWorld.Trx(3) }));
        var first = await w.RunAsync("test.run", "{ tier_tags: [unit] }", "tests");
        var commands = w.Sandbox.Commands.Count;

        var again = await w.RunAsync("test.run", "{ tier_tags: [unit] }", "retest");
        Assert.Equal(first.Report!.Artifacts["evidence"], again.Report!.Artifacts["evidence"]);
        Assert.Equal(commands, w.Sandbox.Commands.Count);

        // Control: a new head is tested again.
        w.CommitWork("a.txt", "changed");
        var changed = await w.RunAsync("test.run", "{ tier_tags: [unit] }", "retest");
        Assert.NotEqual(first.Report!.Artifacts["evidence"], changed.Report!.Artifacts["evidence"]);
        Assert.True(w.Sandbox.Commands.Count > commands);
    }

    [Fact]
    public async Task Test_integration_is_test_run_of_the_integration_tiers()
    {
        await using var w = WithManifest();
        w.Forge.ScriptCi("integration.yml", new CiScript(Conclusions.Success, [new CiScriptFile("r.xml", ResultFormats.Junit, ActionWorld.JUnit(2))]));

        var result = await w.RunAsync("test.integration");

        Assert.Equal("passed", Outcome(result));
        Assert.Equal("integration", (await EvidenceAsync(w, result)).Tiers.Single().Tier);
    }

    // ---- analysis.run ----

    static ActionWorld AnalysisWorld(string sarif)
    {
        var w = WithManifest();
        w.Sandbox.On(new SandboxRule("dotnet build", Files: new Dictionary<string, string> { ["analysis.sarif"] = sarif }));
        return w;
    }

    [Fact]
    public async Task Analysis_fails_on_new_issues_against_the_baseline_and_not_on_old_ones()
    {
        var old = ("CA1000", "warning", "src/A.cs", "fp-old");
        await using var w = AnalysisWorld(ActionWorld.Sarif(old));
        Assert.Equal("passed", Outcome(await w.RunAsync("analysis.run", "{ baseline: true }", "baseline_analysis")));
        Assert.Equal("passed", Outcome(await w.RunAsync("analysis.run", null, "analysis")));

        w.Sandbox.On(new SandboxRule("dotnet build", Files: new Dictionary<string, string> { ["analysis.sarif"] = ActionWorld.Sarif(old, ("CA2000", "error", "src/B.cs", "fp-new")) }));
        var failed = await w.RunAsync("analysis.run", null, "analysis");

        Assert.Equal("failed", Outcome(failed));
        var evidence = JsonSerializer.Deserialize<AnalysisEvidence>(await w.ReadAsync(failed.Report!.Artifacts["evidence"]), Json)!;
        Assert.Equal(["primaryLocationLineHash:fp-new"], evidence.New);
    }

    [Fact]
    public async Task A_malformed_SARIF_raises_an_intervention_rather_than_a_retry()
    {
        await using var w = AnalysisWorld("""{ "runs": [1] }""");

        var result = await w.RunAsync("analysis.run", null, "analysis");

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("does not parse", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unexpected_exception_in_an_action_raises_an_intervention_rather_than_a_retry()
    {
        await using var w = new ActionWorld(throwingCodeHost: true);

        var result = await w.RunAsync("scm.catch_up");

        Assert.Equal(StepOutcome.CannotAnswer, result.Kind);
        Assert.Contains("failed unexpectedly: InvalidOperationException", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Analysis_honours_min_level_and_fail_on()
    {
        await using var w = AnalysisWorld(ActionWorld.Sarif(("IDE0001", "note", "src/A.cs", "fp-1")));

        Assert.Equal("passed", Outcome(await w.RunAsync("analysis.run", null, "a")));                                   // a note is below warning
        Assert.Equal("failed", Outcome(await w.RunAsync("analysis.run", "{ min_level: note }", "b")));
        Assert.Equal("passed", Outcome(await w.RunAsync("analysis.run", "{ min_level: note, fail_on: never }", "c")));
    }

    [Fact]
    public async Task No_analyser_declared_passes_with_a_note()
    {
        await using var w = new ActionWorld(new Dictionary<string, string> { ["feature-flow.yaml"] = "api_version: feature-flow/v1\nkind: repository\n" });

        var result = await w.RunAsync("analysis.run");

        Assert.Equal("passed", Outcome(result));
        Assert.Contains(result.Events, e => e.Contains("declares no static analyser", StringComparison.Ordinal));
    }

    // ---- repo.inspect ----

    [Fact]
    public async Task Repo_inspect_publishes_the_manifests_facts_and_its_hash()
    {
        await using var w = WithManifest("integration_environment: { image: api, port: 8080 }");

        var result = await w.RunAsync("repo.inspect");

        Assert.Equal("inspected", Outcome(result));
        string Out(string n) => YamlWriter.WritePlain(YamlWriter.ReadPlain(result.Report!.Outputs[n]));
        Assert.Equal("""["unit","integration"]""", Out("tiers"));
        Assert.Equal("true", Out("ci"));
        Assert.Equal("\"sarif\"", Out("analyser"));
        Assert.Equal("true", Out("integration_environment"));
        Assert.StartsWith("\"sha256:", Out("manifest_hash"), StringComparison.Ordinal);
        Assert.Null(w.Work);                          // inspecting creates no branch
    }
}
