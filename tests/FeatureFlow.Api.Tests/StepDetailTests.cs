using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// <c>GET /runs/{id}/steps/{step}</c> (A3) on a ticket run's real plan: a step's executions with their artifacts, sessions,
/// reports and questions; its waits; its findings by its finding steps (plan s1.7); its merged events; the patch its agent has
/// staged while it runs, and the plan as that patch would make it (<c>?staged=</c>); and the instructions pending for its next attempt.
/// </summary>
public sealed class StepDetailTests : IAsyncDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;
    static readonly string[] Api = ["acme/api"];
    static readonly Dictionary<string, string> Ticket = new() { ["ticket_id"] = "\"T-1\"" };
    static readonly DateTimeOffset T0 = new(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
    const string Assess = "verify.review.review_0.assess", Record = "verify.review.review_0.record", OtherRecord = "verify.review.review_1.record";

    readonly ReadsWorld world = new();
    long sequence;

    public ValueTask DisposeAsync() => world.DisposeAsync();

    async Task<string> TicketAsync()
    {
        var run = await world.StartAsync("std:templates/ticket_delivery", "ticket", Ticket, Api);
        await world.IndexAsync(run, "std:templates/ticket_delivery", Api, Ticket);
        return run;
    }

    TimelineRecord Entry(string run, string kind, string step, string? detail, int minute) => new(run, ++sequence, kind, step, detail, T0.AddMinutes(minute));

    async Task<string> IndexAsync(string run, string step, int execution, string name, string? type, string text, int minute)
    {
        var reference = (await world.Artifacts.PutAsync(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)), Ct)).Unwrap().Id;
        await world.AppendAsync(new RecordBatch(Artifacts: [new ArtifactRecord(run, step, execution, name, type, reference, null, false, T0.AddMinutes(minute))]));
        return reference;
    }

    async Task<(HttpStatusCode Status, T? Body)> GetAsync<T>(string path) where T : class
    {
        using var response = await world.Http.GetAsync(path, Ct);
        var text = await response.Content.ReadAsStringAsync(Ct);
        return (response.StatusCode, response.IsSuccessStatusCode ? JsonSerializer.Deserialize<T>(text, ApiJson.Options) : null);
    }

    static FindingRecord Finding(string run, string id, string source, string state) =>
        new(run, id, "fp-" + id, new ReportedFinding("rule", "Title " + id, "d", "high", true, FindingBases.Reasoned, new FindingLocation("acme/api", "A.cs")),
            source, "m", state, "h1", "h1", 0, 0, 0, false, T0);

    static FindingEventRecord Event(string run, string id, string key, string kind, string state, string actor, string step, int minute, string? reason = null) =>
        new(run, id, key, kind, state, actor, reason, step, "h1", null, T0.AddMinutes(minute));

    static OverrideRecord Override(string run, string id, string key, string step, string result, int minute, string? text = null) =>
        new(run, id, key, "alice", "because", step, text is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["text"] = text }, result, T0.AddMinutes(minute));

    [Fact]
    public async Task A_steps_executions_carry_their_artifacts_sessions_reports_and_questions_with_how_each_ended_and_what_followed()
    {
        var run = await TicketAsync();
        await world.AppendAsync(new RecordBatch(Timeline:
        [
            Entry(run, "started", Assess, "execution 1", 1),
            Entry(run, "completed", Assess, "reviewed", 5),
            Entry(run, "started", Record, "execution 1", 6),
            Entry(run, "started", Assess, "execution 2", 8),
        ]));
        var prompt = await IndexAsync(run, Assess, 1, ArtifactNames.Prompt(1), ArtifactNames.PromptType, "the task", 1);
        var transcript = await IndexAsync(run, Assess, 1, ArtifactNames.Transcript(1), ArtifactNames.TranscriptType, "{}", 4);
        var report = await IndexAsync(run, Assess, 1, "findings_report", "std:artifact_types/findings_report", "schema: 1", 4);
        await IndexAsync(run, Record, 1, "elsewhere", null, "another step's", 6);
        var key = $"{run}/{Assess}/1";
        await world.AppendAsync(new RecordBatch(
            Reports:
            [
                new ReportRecord(key, 1, false, null, ["outputs.changed_files_unread is required"], T0.AddMinutes(3)),
                new ReportRecord(key, 2, true, new StepReport("reviewed", new Dictionary<string, string>(), new Dictionary<string, string>(), "No problems"), [], T0.AddMinutes(4)),
            ],
            Questions: [new QuestionRecord(key, run, "q-1", "CSV only?", ["yes", "no"], QuestionRecord.Answered, "yes", T0.AddMinutes(2))]));

        var (status, detail) = await GetAsync<StepDetail>($"runs/{run}/steps/{Assess}");

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal([1, 2], detail!.Executions.Select(e => e.Number));
        var first = detail.Executions[0];
        Assert.Equal((T0.AddMinutes(1), T0.AddMinutes(5), "reviewed", Record), (first.StartedAt, first.EndedAt, first.Outcome, first.Then));
        Assert.Equal([prompt, transcript, report], first.Artifacts.Select(a => a.Ref));
        Assert.Equal([new AgentSessionView(1, prompt, transcript)], first.Sessions);
        Assert.Equal([(1, false, (string?)null, (string?)null), (2, true, "reviewed", "No problems")], first.Reports.Select(r => (r.Sequence, r.Accepted, r.Outcome, r.Summary)));
        Assert.Equal(["outputs.changed_files_unread is required"], first.Reports[0].Errors);
        Assert.Equal(("q-1", "CSV only?", "answered", "yes"), (first.Questions.Single().QuestionId, first.Questions[0].Text, first.Questions[0].Status, first.Questions[0].Answer));
        var second = detail.Executions[1];
        Assert.Equal((T0.AddMinutes(8), (DateTimeOffset?)null, (string?)null, (string?)null), (second.StartedAt, second.EndedAt, second.Outcome, second.Then));
        Assert.Empty(second.Artifacts);
    }

    [Fact]
    public async Task A_reviewers_findings_are_those_its_record_step_wrote_and_another_angles_are_not()
    {
        var run = await TicketAsync();
        await world.AppendAsync(new RecordBatch(
            Findings: [Finding(run, "F-1", Assess, FindingStates.Open), Finding(run, "F-2", "verify.review.review_1.assess", FindingStates.Open)],
            FindingEvents:
            [
                Event(run, "F-1", "k1", FindingEventKinds.Raised, FindingStates.Open, Assess, Record, 2),
                Event(run, "F-2", "k2", FindingEventKinds.Raised, FindingStates.Open, "verify.review.review_1.assess", OtherRecord, 3),
                Event(run, "F-1", "k3", FindingEventKinds.Triaged, FindingStates.Declined, "verify.fix.triage", "verify.fix.apply_triage", 4, "out of scope"),
            ]));

        var (_, assess) = await GetAsync<StepDetail>($"runs/{run}/steps/{Assess}");
        var (_, triage) = await GetAsync<StepDetail>($"runs/{run}/steps/verify.fix.triage");

        Assert.Equal([Assess, Record], assess!.FindingSteps);
        Assert.Equal(["F-1"], assess.Findings.Select(f => f.LedgerId));
        Assert.Equal([(StepEventSource.Finding, "raised", (string?)Assess, (string?)"F-1")], assess.Events.Select(e => (e.Source, e.Kind, e.Actor, e.FindingId)));
        // The triager's findings are those its apply step changed; the apply step consumes its triage.
        Assert.Contains("verify.fix.apply_triage", triage!.FindingSteps);
        Assert.DoesNotContain("verify.fix.person_triage", triage.FindingSteps);
        Assert.Equal(["F-1"], triage.Findings.Select(f => f.LedgerId));
        Assert.Equal("Title F-1: declined: out of scope", triage.Events.Single().Text);
    }

    [Fact]
    public async Task Events_merge_the_timeline_agent_events_overrides_and_answers_oldest_first()
    {
        var run = await TicketAsync();
        var answer = JsonSerializer.Serialize(new Interpreter.Wire.AnswerArgs("w-1", "bob", Outcome: "yes"), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await world.AppendAsync(new RecordBatch(
            Waits: [new WaitRecord(run, "w-1", "question", Assess, "", "CSV only?", ["yes", "no"], false, T0.AddMinutes(5))],
            Timeline: [Entry(run, "started", Assess, "execution 1", 1), Entry(run, "completed", Assess, "reviewed", 9)],
            AgentEvents: [new AgentEventRecord($"{run}/{Assess}/1", "hook_pre_tool_use", "Edit allow", T0.AddMinutes(2))],
            Overrides:
            [
                Override(run, "o-1", Overrides.Breakpoint, Assess, "sent", 3),
                Override(run, "o-1", Overrides.Breakpoint, Assess, "applied", 3),
                Override(run, "o-2", Overrides.Breakpoint, "implement", "applied", 4),
            ],
            Answers:
            [
                new AnswerRecord(run, "w-1", "bob", answer, "sent", T0.AddMinutes(5)),
                new AnswerRecord(run, "w-1", "bob", answer, "recorded", T0.AddMinutes(5)),
            ]));

        var (_, detail) = await GetAsync<StepDetail>($"runs/{run}/steps/{Assess}");

        Assert.Equal(
            [
                (StepEventSource.Timeline, "started", (int?)1, "execution 1", (string?)null),
                (StepEventSource.Agent, "hook_pre_tool_use", 1, "Edit allow", null),
                (StepEventSource.Override, "breakpoint", null, "reason: because; applied", "alice"),
                (StepEventSource.Answer, "yes", null, "recorded", "bob"),
                (StepEventSource.Timeline, "completed", 1, "reviewed", null),
            ],
            detail!.Events.Select(e => (e.Source, e.Kind, e.Execution, e.Text, e.Actor)));
        Assert.Equal(["w-1"], detail.Waits.Select(w => w.WaitId));
    }

    static string Patch(string step) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        ["api_version"] = "feature-flow/v1",
        ["kind"] = "patch",
        ["metadata"] = new Dictionary<string, object> { ["reason"] = "an index first", ["proposed_by"] = new Dictionary<string, string> { ["type"] = "agent", ["step_id"] = step } },
        ["base_revision"] = 1,
        ["changes"] = new object[]
        {
            new Dictionary<string, object>
            {
                ["op"] = "insert",
                ["after"] = step,
                ["step"] = new Dictionary<string, object>
                    { ["id"] = "migration_check", ["action"] = "test.run", ["routes"] = new Dictionary<string, string> { ["failed"] = step, ["baseline_failed"] = step } },
            },
        },
    });

    static AgentEventRecord Staged(string run, string step, string patch, int minute, string target = "self") =>
        new($"{run}/{step}/1", "patch_staged", JsonSerializer.Serialize(new { target, patch, revision = 1 }), T0.AddMinutes(minute));

    [Fact]
    public async Task A_running_steps_staged_patch_shows_and_the_plan_reads_as_it_would_make_it_until_it_is_withdrawn()
    {
        var run = await TicketAsync();
        var patch = Patch("implement");
        await world.AppendAsync(new RecordBatch(Timeline: [Entry(run, "started", "implement", "execution 1", 1)], AgentEvents: [Staged(run, "implement", patch, 2)]));

        var (_, detail) = await GetAsync<StepDetail>($"runs/{run}/steps/implement");
        var (status, plan) = await GetAsync<PlanView>($"runs/{run}/plan?staged=implement");

        Assert.Equal(new StagedPatchView(1, "self", 1, patch, T0.AddMinutes(2)), detail!.StagedPatch);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((2, PlanChange.Added), (plan!.Revision, plan.Steps.Single(s => s.Id == "migration_check").Change!.Value));
        Assert.Equal((HttpStatusCode.NotFound, (PlanView?)null), await GetAsync<PlanView>($"runs/{run}/plan?staged=plan.plan"));
        // A staged and a proposed plan are two plans: asking for both is refused, not answered with one.
        Assert.Equal(HttpStatusCode.BadRequest, (await GetAsync<PlanView>($"runs/{run}/plan?staged=implement&proposed=w-1")).Status);

        await world.AppendAsync(new RecordBatch(AgentEvents: [new AgentEventRecord($"{run}/implement/1", "patch_withdrawn", "", T0.AddMinutes(3))]));

        Assert.Null((await GetAsync<StepDetail>($"runs/{run}/steps/implement")).Body!.StagedPatch);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync<PlanView>($"runs/{run}/plan?staged=implement")).Status);
    }

    [Fact]
    public async Task A_staged_patch_is_not_shown_once_its_execution_ended_nor_for_a_parent_and_one_that_does_not_apply_is_409()
    {
        var run = await TicketAsync();
        await world.AppendAsync(new RecordBatch(
            Timeline:
            [
                Entry(run, "started", "implement", "execution 1", 1), Entry(run, "completed", "implement", "implemented", 3),
                Entry(run, "started", "plan.plan", "execution 1", 4), Entry(run, "started", "verify.tests", "execution 1", 4),
            ],
            AgentEvents:
            [
                Staged(run, "implement", Patch("implement"), 2),
                Staged(run, "plan.plan", Patch("plan.plan"), 5, target: "parent"),
                Staged(run, "verify.tests", Patch("no_such_step"), 5),
            ]));

        Assert.Null((await GetAsync<StepDetail>($"runs/{run}/steps/implement")).Body!.StagedPatch);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync<PlanView>($"runs/{run}/plan?staged=implement")).Status);
        Assert.Equal("parent", (await GetAsync<StepDetail>($"runs/{run}/steps/plan.plan")).Body!.StagedPatch!.Target);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync<PlanView>($"runs/{run}/plan?staged=plan.plan")).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await GetAsync<PlanView>($"runs/{run}/plan?staged=verify.tests")).Status);
    }

    [Fact]
    public async Task Instructions_are_the_applied_instruct_overrides_since_the_step_last_succeeded()
    {
        var run = await TicketAsync();
        await world.AppendAsync(new RecordBatch(
            Timeline:
            [
                Entry(run, "started", "implement", "execution 1", 1), Entry(run, "completed", "implement", "implemented", 3),
                Entry(run, "started", "implement", "execution 2", 6), Entry(run, "completed", "implement", "blocked", 7),
            ],
            Overrides:
            [
                Override(run, "o-old", Overrides.Instruct, "implement", "applied", 0, "used by the first attempt"),
                Override(run, "o-1", Overrides.Instruct, "implement", "sent", 4, "keep the API stable"),
                Override(run, "o-1", Overrides.Instruct, "implement", "applied", 4, "keep the API stable"),
                Override(run, "o-2", Overrides.InstructProtected, "implement", "applied", 8, "add a test"),
                Override(run, "o-3", Overrides.Instruct, "implement", "refused: no", 8, "refused"),
                Override(run, "o-4", Overrides.Instruct, "plan.plan", "applied", 8, "another step's"),
            ]));

        var (_, detail) = await GetAsync<StepDetail>($"runs/{run}/steps/implement");

        // "blocked" is not a success: the instruction given before that attempt is still pending.
        Assert.Equal([("o-1", "alice", "keep the API stable"), ("o-2", "alice", "add a test")], detail!.Instructions.Select(i => (i.OverrideId, i.Actor, i.Text)));
    }

    [Fact]
    public async Task An_instruction_given_while_an_execution_runs_outlasts_that_executions_success_as_the_engine_keeps_it()
    {
        var run = await TicketAsync();
        await world.AppendAsync(new RecordBatch(
            Timeline:
            [
                Entry(run, "started", "implement", "execution 1", 1), Entry(run, "completed", "implement", "implemented", 5),
                Entry(run, "started", "verify.tests", "execution 1", 5),
            ],
            Overrides:
            [
                Override(run, "o-before", Overrides.Instruct, "implement", "applied", 0, "read by execution 1"),
                Override(run, "o-during", Overrides.Instruct, "implement", "applied", 3, "keep the API stable"),
            ]));

        var (_, detail) = await GetAsync<StepDetail>($"runs/{run}/steps/implement");

        // Execution 1 read the first and succeeded; the second came while it ran, so execution 2 reads it (RunCore.CausesOf).
        Assert.Equal(["o-during"], detail!.Instructions.Select(i => i.OverrideId));
    }

    [Fact]
    public async Task An_unknown_run_or_step_is_404_and_a_planned_step_not_yet_run_has_nothing_yet()
    {
        var run = await TicketAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync<StepDetail>("runs/nope/steps/implement")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await GetAsync<StepDetail>($"runs/{run}/steps/no_such_step")).Status);
        var (status, detail) = await GetAsync<StepDetail>($"runs/{run}/steps/implement");
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal((0, 0, 0, (StagedPatchView?)null), (detail!.Executions.Count, detail.Events.Count, detail.Findings.Count, detail.StagedPatch));
    }
}
