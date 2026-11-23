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
            ? new(Id(run), wait, "gate", "approve_spec", "ticket.spec_approval", reason, ["approved", "rejected"], true, At)
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
    public async Task An_older_run_record_does_not_replace_a_newer_one()
    {
        // A late write (reconcile's view of an open run, a timed-out activity finishing on another host) must not undo a newer one.
        Ok(await Append(new RecordBatch(Runs: [Run("late", "completed", minutes: 10)])));
        Ok(await Append(new RecordBatch(Runs: [Run("late", "running", minutes: 5) with { Detail = "stale" }])));

        var kept = Assert.Single(await Query<RunRecord>(Queries.Run, ("run_id", Id("late"))));
        Assert.Equal(("completed", (string?)null, At.AddMinutes(10)), (kept.Status, kept.Detail, kept.UpdatedAt));

        // A write at the same moment still replaces it: the rule refuses only what is older.
        Ok(await Append(new RecordBatch(Runs: [Run("late", "failed", minutes: 10) with { Detail = "same moment" }])));
        Assert.Equal("same moment", Assert.Single(await Query<RunRecord>(Queries.Run, ("run_id", Id("late")))).Detail);
    }

    [Fact]
    public async Task A_record_holding_a_nul_character_is_written_and_reads_back_with_it_kept_or_replaced()
    {
        // Text from outside can hold NUL; Postgres's jsonb refuses it, so a store may write U+FFFD instead. The rest is kept,
        // a literal backslash-u0000 included.
        const string text = "before\0after, and a literal \\u0000 too";
        Ok(await Append(new RecordBatch(Runs: [Run("nul", "failed") with { Detail = text }])));

        var detail = Assert.Single(await Query<RunRecord>(Queries.Run, ("run_id", Id("nul")))).Detail!;
        Assert.Equal(text.Replace('\0', '\uFFFD'), detail.Replace('\0', '\uFFFD'));
        Assert.Contains("\\u0000", detail, StringComparison.Ordinal);
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
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "gate-1", true), Wait("run", "gate-2", true), Wait("other", "gate-1", true)])));
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "gate-1", false)])));

        var waits = await Query<WaitRecord>(Queries.Waits, ("run_id", Id("run")));
        Assert.Equal(["gate-1", "gate-2"], waits.Select(w => w.WaitId));
        Assert.Equal((false, "gate", "approve_spec", "ticket.spec_approval", "Approve the spec?"),
            (waits[0].Open, waits[0].Kind, waits[0].StepId, waits[0].DecisionType, waits[0].Prompt));
        Assert.Equal(["approved", "rejected"], waits[0].Choices);
        Assert.Equal("gate-2", Assert.Single(await Query<WaitRecord>(Queries.Waits, ("run_id", Id("run")), ("open", "true"))).WaitId);
    }

    [Fact]
    public async Task A_wait_opened_again_takes_its_new_record()
    {
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "gate-1", true)])));
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "gate-1", false)])));
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "gate-1", true, "Approve the revised spec?")])));

        var wait = Assert.Single(await Query<WaitRecord>(Queries.Waits, ("run_id", Id("run")), ("open", "true")));
        Assert.Equal("Approve the revised spec?", wait.Prompt);
    }

    [Fact]
    public async Task A_waits_cause_and_patch_read_back_and_a_closing_record_keeps_them()
    {
        var cause = new WaitCause("implement", "failed", "attempts exhausted", "tests still fail", 3, 3,
            [new SentBack(1, "review", "changes_requested", "fix the null check"), new SentBack(2, null, null, "build failed")]);
        var patch = new WaitPatch("agent", "plan", "split the work", ["adds a step"], "implement", 2, true, "steps:\n  - id: extra\n");
        Ok(await Append(new RecordBatch(Waits:
        [
            new WaitRecord(Id("run"), "int-1", "intervention", "implement", "", "attempts exhausted", ["retry"], true, At, Cause: cause),
            new WaitRecord(Id("run"), "patch-1", "patch_approval", "plan", "", "approve the patch?", ["approve", "reject"], true, At, Patch: patch),
        ])));
        Ok(await Append(new RecordBatch(Waits: [Wait("run", "int-1", false)])));

        var waits = await Query<WaitRecord>(Queries.Waits, ("run_id", Id("run")));
        Assert.Equal(["int-1", "patch-1"], waits.Select(w => w.WaitId));
        Assert.False(waits[0].Open);
        Assert.Equal(cause with { SentBack = waits[0].Cause!.SentBack }, waits[0].Cause);
        Assert.Equal(cause.SentBack, waits[0].Cause!.SentBack);
        Assert.Null(waits[0].Patch);
        Assert.Equal(patch with { NeedsApproval = waits[1].Patch!.NeedsApproval }, waits[1].Patch);
        Assert.Equal(["adds a step"], waits[1].Patch!.NeedsApproval);
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
        AnswerRecord A(string result) => new(Id("run"), "gate-1", "a-person", "approved", result, At);
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

    // ---- Changes (docs/06 section 3.12, LOG 397). The store may be shared, so each test reads only its own runs' changes. ----

    async Task<long> LatestChange() => (await Query<ChangeRecord>(Queries.LatestChange)).SingleOrDefault()?.Sequence ?? 0;

    async Task<List<ChangeRecord>> ChangesAfter(long after) =>
        [.. (await Query<ChangeRecord>(Queries.Changes, ("after", after.ToString(System.Globalization.CultureInfo.InvariantCulture)))).Where(c => c.RunId.StartsWith(unique, StringComparison.Ordinal))];

    [Fact]
    public async Task Changes_are_written_with_the_records()
    {
        var before = await LatestChange();

        Ok(await Append(new RecordBatch(
            Runs: [Run("a", "running"), Run("b", "running")],
            Waits: [Wait("a", "gate-1", open: true)],
            Timeline: [new TimelineRecord(Id("a"), 1, "step_started", "s", null, At), new TimelineRecord(Id("b"), 1, "step_started", "s", null, At)],
            Answers: [new AnswerRecord(Id("a"), "gate-1", "a-person", "{}", "sent", At)])));
        Ok(await Append(new RecordBatch(Waits: [Wait("a", "gate-1", open: false)])));
        Ok(await Append(new RecordBatch(Locks: [new LockRecord(Id("a"), "sha256:x", At)])));

        var changes = await ChangesAfter(before);
        Assert.Equal(
            [(Id("a"), ChangeKinds.RunChanged, (string?)null), (Id("b"), ChangeKinds.RunChanged, null), (Id("a"), ChangeKinds.WaitOpened, "gate-1"), (Id("a"), ChangeKinds.WaitClosed, "gate-1")],
            changes.Select(c => (c.RunId, c.Kind, c.WaitId)));
        Assert.All(changes, c => Assert.True(c.Sequence > before && c.At > At, $"change {c.Sequence} at {c.At}"));
    }

    [Fact]
    public async Task Changes_follow_commit_order()
    {
        var before = await LatestChange();
        Ok(await Append(new RecordBatch(Runs: [Run("first", "running")])));
        Ok(await Append(new RecordBatch(Runs: [Run("second", "running")])));
        Ok(await Append(new RecordBatch(Runs: [Run("third", "running")])));

        var latest = await LatestChange();
        var all = await ChangesAfter(before);
        Assert.Equal([Id("first"), Id("second"), Id("third")], all.Select(c => c.RunId));
        Assert.True(all[0].Sequence < all[1].Sequence && all[1].Sequence < all[2].Sequence);
        Assert.True(latest >= all[2].Sequence);
        // A reader that has read up to a change is given only what was committed after it.
        Assert.Equal([Id("second"), Id("third")], (await ChangesAfter(all[0].Sequence)).Select(c => c.RunId));
        Assert.Equal([Id("first")], (await Query<ChangeRecord>(Queries.Changes, ("after", (all[0].Sequence - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)), ("limit", "1")))
            .Select(c => c.RunId));
    }

    [Fact]
    public async Task Open_waits_lists_only_open_waits_of_running_runs()
    {
        // Older than any other test's records, so a shared database's limit of 500 cannot leave these out.
        var old = At.AddDays(-30);
        WaitRecord W(string run, string wait, bool open, int minutes) =>
            new(Id(run), wait, "gate", "approve_spec", "ticket.spec_approval", "Approve the spec?", ["approved"], open, old.AddMinutes(minutes));
        Ok(await Append(new RecordBatch(
            Runs: [Run("going", "running"), Run("new", "starting"), Run("ended", "completed"), Run("failed", "failed")],
            Waits: [W("going", "gate-2", true, 3), W("going", "gate-1", true, 1), W("going", "gate-0", false, 0), W("new", "gate-1", true, 2),
                W("ended", "gate-1", true, 0), W("failed", "gate-1", true, 0), W("orphan", "gate-1", true, 0)])));

        var open = (await Query<OpenWaitRecord>(Queries.OpenWaits)).Where(o => o.Wait.RunId.StartsWith(unique, StringComparison.Ordinal)).ToList();

        Assert.Equal([(Id("going"), "gate-1"), (Id("new"), "gate-1"), (Id("going"), "gate-2")], open.Select(o => (o.Wait.RunId, o.Wait.WaitId)));
        Assert.Equal(("running", "Approve the spec?", "ticket.spec_approval"), (open[0].Run.Status, open[0].Wait.Prompt, open[0].Wait.DecisionType));
        Assert.Equal([Id("going"), Id("new"), Id("going")], open.Select(o => o.Run.RunId));
    }
}
