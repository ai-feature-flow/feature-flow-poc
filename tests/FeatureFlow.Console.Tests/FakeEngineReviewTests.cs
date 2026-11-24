using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The FakeEngine's build-console-b2 data (<c>FakeEngine.Review.cs</c>, <c>FakeEngine.Steering.cs</c>) is what the engine
/// would write: its reports fit std's <c>findings_report</c>, its ledger reads through the API as the scenario says, its
/// staged patch applies to the plan, and the new override keys behave as the core's (plan s1.2, s1.3). No browser.
/// </summary>
public sealed class FakeEngineReviewTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static async Task<T> GetAsync<T>(ConsoleWorld world, string path)
    {
        using var http = world.Http();
        using var response = await http.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode} {text}");
        return JsonSerializer.Deserialize<T>(text, ApiJson.Options)!;
    }

    static async Task<string> ReadAsync(ConsoleWorld world, string reference)
    {
        await using var stream = (await world.Engine.Artifacts.GetAsync(new ArtifactRef(reference), Ct)).Unwrap();
        return await new StreamReader(stream).ReadToEndAsync(Ct);
    }

    [Fact]
    public async Task The_review_scenario_s_reports_fit_std_s_type_and_its_ledger_reads_as_it_says()
    {
        await using var world = new ConsoleWorld();
        var s = await world.Engine.ReviewScenarioAsync();

        var artifacts = (await GetAsync<ArtifactList>(world, $"runs/{s.RunId}/artifacts")).Artifacts;
        var reports = artifacts.Where(a => a.Name == "findings_report").ToList();
        Assert.Equal(10, reports.Count);
        var type = world.Engine.Libraries.All.Select(l => l.ArtifactType("findings_report")).OfType<ArtifactTypeItem>().First();
        var summaries = 0;
        foreach (var report in reports)
        {
            var text = await ReadAsync(world, report.Ref);
            Assert.Empty(ArtifactContent.Check(type, text, $"{report.StepId}/{report.Execution}/findings.yaml"));
            summaries += ArtifactShapes.ReadLibrary<FindingsReport>(text, report.StepId).Summary is null ? 0 : 1;
        }

        Assert.Equal(9, summaries);
        Assert.Equal([1, 2], reports.Where(r => r.StepId == s.ReviewerSteps[0]).Select(r => r.Execution));

        var ledger = (await GetAsync<FindingsResponse>(world, $"runs/{s.RunId}/findings")).Findings.ToDictionary(f => f.LedgerId);
        Assert.Equal(("fixed", "declined", "declined", "open"),
            (ledger[s.Fixed].State, ledger[s.DeclinedByTriager].State, ledger[s.DeclinedByPerson].State, ledger[s.External].State));
        Assert.Equal(("correctness", "external"), (ledger[s.Fixed].RaisedBy!.Angle, ledger[s.External].RaisedBy!.Source));
        Assert.Equal((1, "local"), (ledger[s.DeclinedByTriager].Reraises, ledger[s.DeclinedByPerson].DecidedByPerson));
        var history = (await GetAsync<FindingDetail>(world, $"runs/{s.RunId}/findings/{s.DeclinedByPerson}")).History;
        Assert.Equal(["raised", "triaged", "triaged"], history.Select(h => h.Kind));
        Assert.Equal(["verify.review.review_2.record", "verify.fix.apply_triage", "verify.fix.apply_person_triage"], history.Select(h => h.StepId));

        var run = await GetAsync<RunDetail>(world, $"runs/{s.RunId}");
        Assert.Equal(2, run.Timeline.Count(t => t is { Kind: "entered", StepId: FakeEngine.ReviewStep }));
        var trace = JsonDocument.Parse(await ReadAsync(world, s.TraceRef)).RootElement;
        Assert.Equal((false, 2), (trace.GetProperty("traced").GetBoolean(), trace.GetProperty("requirements").GetArrayLength()));
        Assert.Contains(run.Waits, w => w is { WaitId: var id, Open: true } && id == s.Merge.WaitId);
    }

    /// <summary>For the planner inside the <c>plan</c> fragment and the top-level implementer alike.</summary>
    [Theory]
    [InlineData(FakeEngine.AgentStep, "plan.migration_check")]
    [InlineData("implement", "migration_check")]
    public async Task A_staged_patch_applies_to_the_ticket_plan_and_reads_as_the_agent_endpoint_writes_it(string stepId, string added)
    {
        await using var world = new ConsoleWorld();
        await world.Engine.AgentStepAsync("ticket");
        var patch = await world.Engine.StagedPatchAsync("ticket", stepId);

        var events = (await world.Records.QueryAsync<AgentEventRecord>(Queries.AgentEvents, new Dictionary<string, string> { ["step_key"] = $"ticket/{stepId}/1" }, Ct)).Unwrap();
        var staged = JsonDocument.Parse(Assert.Single(events).Detail).RootElement;
        Assert.Equal(("self", 1, patch), (staged.GetProperty("target").GetString(), staged.GetProperty("revision").GetInt32(), staged.GetProperty("patch").GetString()));

        var artifacts = (await world.Records.QueryAsync<ArtifactRecord>(Queries.Artifacts, new Dictionary<string, string> { ["run_id"] = "ticket" }, Ct)).Unwrap();
        await using var stream = (await world.Engine.Artifacts.GetAsync(new ArtifactRef(artifacts.Single(a => a is { StepId: "", Execution: 1, Name: ArtifactNames.PlanRevision }).Ref), Ct)).Unwrap();
        var source = PlanSource.Read(await new StreamReader(stream).ReadToEndAsync(Ct));
        var d = new DiagnosticBag();
        var plan = (source with { Patches = [.. source.Patches, patch] }).Load(world.Engine.Libraries, d);
        Assert.True(plan is not null && !d.Any(x => x.Blocks), string.Join("; ", d.Select(x => x.Message)));
        Assert.NotNull(plan.StepById(added));
    }

    [Fact]
    public async Task Go_to_cancels_what_runs_and_waits_and_the_run_continues_at_the_target()
    {
        await using var world = new ConsoleWorld();
        await world.Engine.AgentStepAsync("ticket");
        var question = await world.Engine.OpenWaitAsync("ticket", "question", FakeEngine.AgentStep, "", "CSV only?", ["yes", "no"]);
        using var http = world.Http();

        using var refused = await http.PostAsync("runs/ticket/overrides",
            Json("""{ "key": "replan", "step_id": "plan.validate", "reason": "wrong", "change": {} }"""), Ct);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("not an agent step with planning authority", await refused.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        using var moved = await http.PostAsync("runs/ticket/overrides",
            Json("""{ "key": "go_to", "step_id": "baseline.tests", "reason": "the baseline changed", "change": { "clean_up": "true" } }"""), Ct);
        var reply = JsonSerializer.Deserialize<OverrideResponse>(await moved.Content.ReadAsStringAsync(Ct), ApiJson.Options)!;
        Assert.Equal($"applied: continues at baseline.tests; cancelled {FakeEngine.AgentStep}", reply.Result);

        var run = await GetAsync<RunDetail>(world, "runs/ticket");
        Assert.Equal(["cancelled", "wait_closed", "override", "started"], run.Timeline.TakeLast(4).Select(t => t.Kind));
        Assert.Equal("baseline.tests", run.Timeline[^1].StepId);
        Assert.DoesNotContain(run.Waits, w => w.WaitId == question && w.Open);
    }

    [Fact]
    public async Task Instruct_needs_text_and_an_agent_step_and_the_variant_for_a_protected_one()
    {
        await using var world = new ConsoleWorld(roles: ["viewer", "steerer"]);
        await world.Engine.ReviewScenarioAsync();
        using var http = world.Http();

        async Task<(HttpStatusCode, string)> InstructAsync(string step, string change)
        {
            using var r = await http.PostAsync("runs/review/overrides", Json($$"""{ "key": "instruct", "step_id": "{{step}}", "change": {{change}} }"""), Ct);
            return (r.StatusCode, await r.Content.ReadAsStringAsync(Ct));
        }

        Assert.Equal(HttpStatusCode.OK, (await InstructAsync("implement", """{ "text": "Keep the export to CSV" }""")).Item1);
        Assert.Contains("needs the instructions as text", (await InstructAsync("implement", "{}")).Item2, StringComparison.Ordinal);
        Assert.Contains("not an agent step", (await InstructAsync("plan.validate", """{ "text": "x" }""")).Item2, StringComparison.Ordinal);
        Assert.Contains("instruct_protected", (await InstructAsync(FakeEngine.ReviewerStep(0), """{ "text": "x" }""")).Item2, StringComparison.Ordinal);
    }

    static StringContent Json(string body) => new(body, System.Text.Encoding.UTF8, "application/json");
}
