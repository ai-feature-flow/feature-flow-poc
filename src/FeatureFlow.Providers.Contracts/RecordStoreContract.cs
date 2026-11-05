using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The record store's contract for the records every run writes (docs/02 section 5, docs/06 section 3.12): runs, waits,
/// the timeline, answers and locks, and the agent step's records (docs/02 section 4.2). Runs, waits, locks, agent steps
/// and questions are kept by key, the last write winning, except that a closing wait keeps what its opening record said
/// and a question once answered or timed out is not reopened; the timeline is kept once per run and sequence number, so a
/// retried append adds nothing; answers, reports and agent events are appended in order. Each query answers for its own
/// run or step only. The findings ledger and M7's records have their own suites (<see cref="FindingsLedgerContract"/>,
/// <see cref="SteeringRecordsContract"/>). A record store's test project subclasses it; each test uses its own ids, so it
/// can run against a shared database.
/// </summary>
public abstract class RecordStoreContract : ContractBase
{
    protected abstract IRecordStoreProvider Store { get; }

    readonly string unique = "ff-contract-" + Guid.NewGuid().ToString("N")[..12];
    static readonly DateTimeOffset At = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    string Id(string name) => $"{unique}-{name}";

    Task<ProviderResult<Unit>> Append(RecordBatch batch) => Store.AppendAsync(batch, Ct);

    async Task<IReadOnlyList<T>> Query<T>(string query, params (string Key, string Value)[] parameters) =>
        Ok(await Store.QueryAsync<T>(query, parameters.ToDictionary(p => p.Key, p => p.Value), Ct));

    RunRecord Run(string id, string status, string? parent = null, int minutes = 0) =>
        new(Id(id), parent is null ? null : Id(parent), "std:templates/ticket_delivery", status, "a-person", ["acme/api"], false, At.AddMinutes(minutes), null);

    WaitRecord Wait(string run, string wait, bool open, string reason = "Approve the spec?") =>
        open
            ? new(Id(run), wait, "decision", "approve_spec", "ticket.spec_approval", reason, ["approved", "rejected"], true, At)
            : new(Id(run), wait, "", "", "", "", [], false, At.AddMinutes(1));

    [Fact]
    public async Task A_run_is_kept_by_id_with_the_last_write_winning()
    {
        Ok(await Append(new RecordBatch(Runs: [Run("run", "starting")])));
        Ok(await Append(new RecordBatch(Runs: [Run("run", "running", minutes: 1) with { Detail = "on its way" }])));

        var run = Assert.Single(await Query<RunRecord>(Queries.Run, ("run_id", Id("run"))));
        Assert.Equal(("running", "on its way", "a-person", false), (run.Status, run.Detail, run.Requester, run.DryRun));
        Assert.Equal(["acme/api"], run.Repositories);
        Assert.Equal(At.AddMinutes(1), run.UpdatedAt);
        Assert.Empty(await Query<RunRecord>(Queries.Run, ("run_id", Id("none"))));
    }

    [Fact]
    public async Task Runs_list_a_parents_children_by_id_and_otherwise_the_most_recently_updated_first()
    {
        Ok(await Append(new RecordBatch(Runs: [Run("root", "running", minutes: 1), Run("root.b", "running", "root", 3), Run("root.a", "running", "root", 2)])));

        Assert.Equal([Id("root.a"), Id("root.b")], (await Query<RunRecord>(Queries.Runs, ("parent_run_id", Id("root")))).Select(r => r.RunId));
        var ours = (await Query<RunRecord>(Queries.Runs)).Where(r => r.RunId.StartsWith(unique, StringComparison.Ordinal)).Select(r => r.RunId);
        Assert.Equal([Id("root.b"), Id("root.a"), Id("root")], ours);
    }

    [Fact]
    public async Task A_closing_wait_keeps_what_its_opening_record_said_and_open_narrows_to_open_waits()
    {
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "decision-1", true), Wait("run", "decision-2", true), Wait("other", "decision-1", true)])));
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "decision-1", false)])));

        var waits = await Query<WaitRecord>(Queries.Waits, ("run_id", Id("run")));
        Assert.Equal(["decision-1", "decision-2"], waits.Select(w => w.WaitId));
        Assert.Equal((false, "decision", "approve_spec", "ticket.spec_approval", "Approve the spec?"),
            (waits[0].Open, waits[0].Kind, waits[0].StepId, waits[0].DecisionType, waits[0].Reason));
        Assert.Equal(["approved", "rejected"], waits[0].Choices);
        Assert.Equal("decision-2", Assert.Single(await Query<WaitRecord>(Queries.Waits, ("run_id", Id("run")), ("open", "true"))).WaitId);
    }

    [Fact]
    public async Task A_wait_opened_again_takes_its_new_record()
    {
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "decision-1", true)])));
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "decision-1", false)])));
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "decision-1", true, "Approve the revised spec?")])));

        var wait = Assert.Single(await Query<WaitRecord>(Queries.Waits, ("run_id", Id("run")), ("open", "true")));
        Assert.Equal("Approve the revised spec?", wait.Reason);
    }

    [Fact]
    public async Task The_timeline_is_in_order_once_per_sequence_and_after_skips_what_was_read()
    {
        TimelineRecord T(long sequence, string kind, string? detail = null) => new(Id("run"), sequence, kind, "plan", detail, At);
        Ok(await Append(new RecordBatch(Timeline: [T(2, "completed", "planned"), T(1, "started")])));
        Ok(await Append(new RecordBatch(Timeline: [T(2, "completed", "a retried append adds nothing"), T(3, "started"), new TimelineRecord(Id("other"), 1, "started", "x", null, At)])));

        var timeline = await Query<TimelineRecord>(Queries.Timeline, ("run_id", Id("run")));
        Assert.Equal([(1L, "started", (string?)null), (2L, "completed", "planned"), (3L, "started", null)], timeline.Select(t => (t.Sequence, t.Kind, t.Detail)));
        Assert.Equal([3L], (await Query<TimelineRecord>(Queries.Timeline, ("run_id", Id("run")), ("after", "2"))).Select(t => t.Sequence));
    }

    [Fact]
    public async Task Answers_are_appended_in_order_and_a_lock_keeps_its_last_write()
    {
        AnswerRecord A(string result) => new(Id("run"), "decision-1", "a-person", "approved", result, At);
        Ok(await Append(new RecordBatch(Answers: [A("sent")], Locks: [new LockRecord(Id("run"), "sha256:1", At)])));
        Ok(await Append(new RecordBatch(Answers: [A("recorded"), new AnswerRecord(Id("other"), "w", "x", "y", "sent", At)],
            Locks: [new LockRecord(Id("run"), "sha256:2", At.AddMinutes(1))])));

        Assert.Equal(["sent", "recorded"], (await Query<AnswerRecord>(Queries.Answers, ("run_id", Id("run")))).Select(a => a.Result));
        Assert.Equal("sha256:2", Assert.Single(await Query<LockRecord>(Queries.Lock, ("run_id", Id("run")))).LockRef);
    }

    [Fact]
    public async Task An_agent_steps_record_reads_back_whole_and_is_kept_by_key()
    {
        var contract = new StepContract([new OutcomeSpec("done", "success"), new OutcomeSpec("stuck", "blocked")],
            new Dictionary<string, string> { ["count"] = "int" }, [new ArtifactSpec(null, "summary") { Type = "std:artifact_types/summary" }],
            new Dictionary<string, string> { ["count"] = "0" }) { Reported = ["count"] };
        var step = new AgentStepRecord(Id("step"), Id("run"), "wf-1", "implement", "ticket", contract, 2, 3, 86400, At);
        Ok(await Append(new RecordBatch(AgentSteps: [step])));
        Ok(await Append(new RecordBatch(AgentSteps: [step with { Revoked = true }])));

        var read = Assert.Single(await Query<AgentStepRecord>(Queries.AgentStep, ("step_key", Id("step"))));
        Assert.Equal((Id("run"), "wf-1", "implement", "ticket", 2, 3, 86400L, true),
            (read.RunId, read.WorkflowId, read.StepId, read.Level, read.CorrectionTurns, read.QuestionsMax, read.QuestionsTimeoutSeconds, read.Revoked));
        Assert.Equal(["done", "stuck"], read.Contract.Outcomes.Select(o => o.Name));
        Assert.Equal("std:artifact_types/summary", Assert.Single(read.Contract.Artifacts).Type);
        Assert.Equal(["count"], read.Contract.Reported);
        Assert.Equal("0", read.Contract.ZeroOutputs["count"]);
        Assert.Empty(await Query<AgentStepRecord>(Queries.AgentStep, ("step_key", Id("none"))));
    }

    [Fact]
    public async Task Reports_and_agent_events_are_appended_in_order_per_step()
    {
        var report = new StepReport("done", new Dictionary<string, string> { ["count"] = "2" }, new Dictionary<string, string> { ["summary"] = "sha256:s" }, "Did it");
        Ok(await Append(new RecordBatch(
            Reports: [new ReportRecord(Id("step"), 1, false, null, ["outcome 'finished' is not declared"], At)],
            AgentEvents: [new AgentEventRecord(Id("step"), "hook", "allowed Bash", At)])));
        Ok(await Append(new RecordBatch(
            Reports: [new ReportRecord(Id("step"), 2, true, report, [], At), new ReportRecord(Id("other"), 1, true, report, [], At)],
            AgentEvents: [new AgentEventRecord(Id("step"), "push", "abc123", At), new AgentEventRecord(Id("other"), "hook", "x", At)])));

        var reports = await Query<ReportRecord>(Queries.Reports, ("step_key", Id("step")));
        Assert.Equal([(1, false), (2, true)], reports.Select(r => (r.Sequence, r.Accepted)));
        Assert.Equal(["outcome 'finished' is not declared"], reports[0].Errors);
        Assert.Equal(("done", "2", "sha256:s", "Did it"), (reports[1].Report!.Outcome, reports[1].Report!.Outputs["count"], reports[1].Report!.Artifacts["summary"], reports[1].Report!.Summary));
        Assert.Equal(["hook", "push"], (await Query<AgentEventRecord>(Queries.AgentEvents, ("step_key", Id("step")))).Select(e => e.Kind));
    }

    [Fact]
    public async Task A_question_answered_or_timed_out_is_not_reopened_by_a_late_open()
    {
        QuestionRecord Q(string id, string status, string? answer = null) =>
            new(Id("step"), Id("run"), Id(id), "Which table?", ["orders", "items"], status, answer, At);
        Ok(await Append(new RecordBatch(Questions: [Q("q1", QuestionRecord.Open), Q("q2", QuestionRecord.Open)])));
        Ok(await Append(new RecordBatch(Questions: [Q("q1", QuestionRecord.Answered, "orders"), Q("q2", QuestionRecord.TimedOut)])));
        Ok(await Append(new RecordBatch(Questions: [Q("q1", QuestionRecord.Open), Q("q2", QuestionRecord.Open)])));

        var q1 = Assert.Single(await Query<QuestionRecord>(Queries.Question, ("question_id", Id("q1"))));
        Assert.Equal((QuestionRecord.Answered, "orders", "Which table?"), (q1.Status, q1.Answer, q1.Text));
        Assert.Equal(["orders", "items"], q1.Options);
        Assert.Equal([(Id("q1"), QuestionRecord.Answered), (Id("q2"), QuestionRecord.TimedOut)],
            (await Query<QuestionRecord>(Queries.Questions, ("step_key", Id("step")))).Select(q => (q.QuestionId, q.Status)));
    }

    [Fact]
    public async Task A_query_for_another_record_type_than_it_returns_is_a_programming_error()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Store.QueryAsync<WaitRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = Id("run") }, Ct));
    }
}
