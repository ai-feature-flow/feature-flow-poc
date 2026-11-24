using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// What <see cref="RecordKindContract"/> expects of each declared record kind and query, stated here apart from the
/// declarations (<see cref="RecordKinds"/>, <see cref="RecordQueries"/>) so that a changed declaration fails the stores
/// that follow it. Every kind has a <see cref="Sample"/>: the rule it must keep, its records, and the query that reads the
/// first back. Every query has cases, at least one for each of its forms. A sample record is made from a run id, a number
/// <c>n</c> that picks its key, and a version that changes its body: odd versions of a wait or a question open it, even
/// ones close or answer it, and a later version of a run is newer.
/// </summary>
public static class RecordSamples
{
    static readonly DateTimeOffset At = new(2026, 11, 5, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Text whose ordinal order is <paramref name="n"/>'s and whose case-insensitive order is not: <c>X</c>, <c>a</c>, <c>b</c>, then <c>c4</c>, ...</summary>
    public static string Name(int n) => n switch { 1 => "X", 2 => "a", 3 => "b", _ => "c" + n };

    /// <summary>A kind's expected rule, its records, and the query (with its parameters for a run) that reads record 1 of that run back.</summary>
    public sealed record Sample(WriteRule Rule, Func<string, int, int, object> Make, string Query, Func<string, Dictionary<string, string>> Parameters);

    /// <summary>
    /// A query's case for a run: the records written in one batch, the parameters, and the records expected, in order.
    /// <see cref="Shared"/> marks a query that no parameter narrows to the run, so only the run's own records are compared.
    /// </summary>
    public sealed record QueryCase(string Name, Func<string, (IReadOnlyList<object> Write, Dictionary<string, string> Parameters, IReadOnlyList<object> Expect)> For, bool Shared = false);

    static Dictionary<string, string> P(params (string Key, string Value)[] parameters) => parameters.ToDictionary(p => p.Key, p => p.Value);

    static Sample S<T>(WriteRule rule, Func<string, int, int, T> make, string query, Func<string, Dictionary<string, string>> parameters) where T : notnull =>
        new(rule, (run, n, v) => make(run, n, v), query, parameters);

    public static IReadOnlyDictionary<Type, Sample> Kinds { get; } = new Dictionary<Type, Sample>
    {
        [typeof(RunRecord)] = S(WriteRule.ReplaceIfNotOlder, (run, n, v) => new RunRecord(n == 1 ? run : $"{run}-{Name(n)}", n == 1 ? null : run, "std:templates/t",
            $"status-{v}", "a-person", ["acme/api"], false, At.AddMinutes(v), null), Queries.Run, run => P(("run_id", run))),
        [typeof(WaitRecord)] = S(WriteRule.CloseKeepsOpening, (run, n, v) => new WaitRecord(run, $"wait-{Name(n)}", "gate", "approve", "t", $"prompt-{v}", ["approved"],
            v % 2 == 1, At.AddMinutes(v)), Queries.Waits, run => P(("run_id", run))),
        [typeof(TimelineRecord)] = S(WriteRule.KeepFirst, (run, n, v) => new TimelineRecord(run, n, "step_started", "implement", $"detail-{v}", At),
            Queries.Timeline, run => P(("run_id", run))),
        [typeof(AnswerRecord)] = S(WriteRule.Append, (run, n, v) => new AnswerRecord(run, $"wait-{Name(n)}", run + "-actor", $"answer-{v}", "sent", At.AddMinutes(n)),
            Queries.Answers, run => P(("run_id", run))),
        [typeof(LockRecord)] = S(WriteRule.Replace, (run, n, v) => new LockRecord(n == 1 ? run : $"{run}-{Name(n)}", $"sha256:lock-{v}", At), Queries.Lock, run => P(("run_id", run))),
        [typeof(AgentStepRecord)] = S(WriteRule.Replace, (run, n, v) => new AgentStepRecord($"{run}-step-{Name(n)}", run, "wf", "implement", "ticket", null!, v, 3, 60, At),
            Queries.AgentStep, run => P(("step_key", $"{run}-step-{Name(1)}"))),
        [typeof(QuestionRecord)] = S(WriteRule.KeepFirstWhileOpen, (run, n, v) => new QuestionRecord($"{run}-step", run, $"{run}-q-{Name(n)}", $"text-{v}", ["yes"],
            v % 2 == 1 ? QuestionRecord.Open : QuestionRecord.Answered, v % 2 == 1 ? null : "yes", At), Queries.Question, run => P(("question_id", $"{run}-q-{Name(1)}"))),
        [typeof(ReportRecord)] = S(WriteRule.Append, (run, n, v) => new ReportRecord($"{run}-step", n, v % 2 == 1, null, [$"error-{v}"], At),
            Queries.Reports, run => P(("step_key", $"{run}-step"))),
        [typeof(AgentEventRecord)] = S(WriteRule.Append, (run, n, v) => new AgentEventRecord($"{run}-step", $"kind-{Name(n)}", $"detail-{v}", At),
            Queries.AgentEvents, run => P(("step_key", $"{run}-step"))),
        [typeof(OverrideRecord)] = S(WriteRule.Append, (run, n, v) => new OverrideRecord(run, $"o-{Name(n)}", "attempts", "a-person", null, "implement",
            new Dictionary<string, string> { ["attempts"] = $"{v}" }, "sent", At), Queries.Overrides, run => P(("run_id", run))),
        [typeof(ArtifactRecord)] = S(WriteRule.KeepFirst, (run, n, v) => new ArtifactRecord(run, $"step-{Name(n)}", 1, "spec", "std:artifact_types/spec", $"sha256:{v}", null, false, At),
            Queries.Artifacts, run => P(("run_id", run))),
        [typeof(NotificationRecord)] = S(WriteRule.KeepFirst, (run, n, v) => new NotificationRecord($"{run}-n-{Name(n)}", run, null, "run_started",
            new MessageRef("#ff", $"m-{v}"), null, At), Queries.Notification, run => P(("key", $"{run}-n-{Name(1)}"))),
        [typeof(SessionRecord)] = S(WriteRule.Replace, (run, n, v) => new SessionRecord($"{run}-s-{Name(n)}", run, "wait", "decide", "a-person", "acme/api", "ff/s", "base",
            v == 1 ? SessionStatuses.Prepared : SessionStatuses.Submitted, null, null, At), Queries.Session, run => P(("session_id", $"{run}-s-{Name(1)}"))),
        [typeof(SessionCheckRecord)] = S(WriteRule.Replace, (run, n, v) => new SessionCheckRecord($"{run}-session", $"check-{Name(n)}", "review", "rev", "inputs",
            $"status-{v}", null, At), Queries.SessionChecks, run => P(("session_id", $"{run}-session"))),
        [typeof(FindingRecord)] = S(WriteRule.Replace, (run, n, v) => new FindingRecord(run, $"f-{Name(n)}", "fp", null!, "agent", null, $"state-{v}", null, null, 0, 0, 0, false, At),
            Queries.Findings, run => P(("run_id", run))),
        [typeof(FindingEventRecord)] = S(WriteRule.KeepFirst, (run, n, v) => new FindingEventRecord(run, "f", $"k-{Name(n)}", FindingEventKinds.Raised, $"state-{v}", "review",
            null, "review", null, null, At), Queries.FindingEvents, run => P(("run_id", run), ("finding_id", "f"))),
        [typeof(StepStateRecord)] = S(WriteRule.Replace, (run, n, v) => new StepStateRecord(run, $"step-{Name(n)}", v, v, 3, 0, At),
            Queries.StepStates, run => P(("run_id", run))),
        [typeof(PullRequestRecord)] = S(WriteRule.Replace, (run, n, v) => new PullRequestRecord(run, $"acme/{Name(n <= 2 ? 1 : 2)}", n <= 2 ? n : 1, $"step-{v}", null!, null, null, At),
            Queries.PullRequests, run => P(("run_id", run))),
        [typeof(RepositoryRecord)] = S(WriteRule.Replace, (run, n, v) => new RepositoryRecord($"{run}/{Name(n)}", "github", null, $"branch-{v}", "a-person", At),
            Queries.Repositories, _ => P()),
        [typeof(ActivityRecord)] = S(WriteRule.KeepFirst, (run, n, v) => new ActivityRecord(run, "implement", n == 4 ? 2 : 1, n == 3 ? 2 : 1, n == 2 ? 2 : n >= 5 ? n : 1,
            ActivityKinds.Message, null, null, $"text-{v}", null, false, At), Queries.Activity, run => P(("run_id", run), ("step_id", "implement"))),
    };

    public static object For(RecordKind kind, string run, int n, int version = 1) => Kinds[kind.Record].Make(run, n, version);

    static object R<T>(string run, int n, int version = 1) => For(RecordKinds.For(typeof(T)), run, n, version);

    static QueryCase Case(string name, Func<string, string, (object[] Write, Dictionary<string, string> Parameters, object[] Expect)> make, bool shared = false) =>
        new(name, run => make(run, run + "-other"), shared);

    /// <summary>A run, its child (run sample 2) and another run, for the queries that read a run tree.</summary>
    static object[] Tree(string run, string other) => [R<RunRecord>(run, 1), R<RunRecord>(run, 2), R<RunRecord>(other, 1)];

    static string Child(string run) => $"{run}-{Name(2)}";

    /// <summary>A far-off time, so the runs of the most recently updated first are the newest in a shared store.</summary>
    const int Later = 50_000_000;

    public static IReadOnlyDictionary<string, IReadOnlyList<QueryCase>> QueryCases { get; } = new Dictionary<string, IReadOnlyList<QueryCase>>
    {
        [Abstractions.Queries.Run] = [Case("by id", (run, other) => ([R<RunRecord>(run, 1), R<RunRecord>(run, 2)], P(("run_id", run)), [R<RunRecord>(run, 1)]))],
        [Abstractions.Queries.Runs] =
        [
            Case("a parent's children by id", (run, other) => ([R<RunRecord>(run, 1), R<RunRecord>(run, 3), R<RunRecord>(run, 2), R<RunRecord>(other, 2)],
                P(("parent_run_id", run)), [R<RunRecord>(run, 2), R<RunRecord>(run, 3)])),
            Case("most recently updated first, at most 200", (run, other) =>
                ([.. Enumerable.Range(1, 201).Select(n => R<RunRecord>(run, n, Later + n * 7 % 211))], P(),
                    [.. Enumerable.Range(1, 201).OrderByDescending(n => n * 7 % 211).Take(200).Select(n => R<RunRecord>(run, n, Later + n * 7 % 211))]), shared: true),
        ],
        [Abstractions.Queries.Waits] =
        [
            Case("a run's waits by id", (run, other) => ([R<WaitRecord>(run, 3), R<WaitRecord>(run, 1), R<WaitRecord>(run, 2, 2), R<WaitRecord>(other, 1)],
                P(("run_id", run)), [R<WaitRecord>(run, 1), R<WaitRecord>(run, 2, 2), R<WaitRecord>(run, 3)])),
            Case("open ones", (run, other) => ([R<WaitRecord>(run, 3), R<WaitRecord>(run, 1), R<WaitRecord>(run, 2, 2)],
                P(("run_id", run), ("open", "true")), [R<WaitRecord>(run, 1), R<WaitRecord>(run, 3)])),
        ],
        [Abstractions.Queries.Timeline] =
        [
            Case("in sequence", (run, other) => ([R<TimelineRecord>(run, 3), R<TimelineRecord>(run, 1), R<TimelineRecord>(run, 2), R<TimelineRecord>(other, 1)],
                P(("run_id", run)), [R<TimelineRecord>(run, 1), R<TimelineRecord>(run, 2), R<TimelineRecord>(run, 3)])),
            Case("after a sequence", (run, other) => ([R<TimelineRecord>(run, 3), R<TimelineRecord>(run, 1), R<TimelineRecord>(run, 2)],
                P(("run_id", run), ("after", "1")), [R<TimelineRecord>(run, 2), R<TimelineRecord>(run, 3)])),
        ],
        [Abstractions.Queries.Answers] = [Case("in insertion order", (run, other) => ([R<AnswerRecord>(run, 3), R<AnswerRecord>(run, 1), R<AnswerRecord>(other, 1)],
            P(("run_id", run)), [R<AnswerRecord>(run, 3), R<AnswerRecord>(run, 1)]))],
        [Abstractions.Queries.RecentAnswers] =
        [
            Case("an actor's, newest first", (run, other) => ([R<AnswerRecord>(run, 1), R<AnswerRecord>(other, 1), R<AnswerRecord>(run, 2), R<AnswerRecord>(run, 3)],
                P(("actor", run + "-actor")), [R<AnswerRecord>(run, 3), R<AnswerRecord>(run, 2), R<AnswerRecord>(run, 1)])),
            Case("at most the limit", (run, other) => ([R<AnswerRecord>(run, 1), R<AnswerRecord>(run, 2), R<AnswerRecord>(run, 3)],
                P(("actor", run + "-actor"), ("limit", "2")), [R<AnswerRecord>(run, 3), R<AnswerRecord>(run, 2)])),
        ],
        [Abstractions.Queries.Lock] = [Case("by run", (run, other) => ([R<LockRecord>(run, 1), R<LockRecord>(run, 2)], P(("run_id", run)), [R<LockRecord>(run, 1)]))],
        [Abstractions.Queries.AgentStep] = [Case("by step key", (run, other) => ([R<AgentStepRecord>(run, 1), R<AgentStepRecord>(run, 2)],
            P(("step_key", $"{run}-step-{Name(2)}")), [R<AgentStepRecord>(run, 2)]))],
        [Abstractions.Queries.Question] = [Case("by id", (run, other) => ([R<QuestionRecord>(run, 1), R<QuestionRecord>(run, 2)],
            P(("question_id", $"{run}-q-{Name(2)}")), [R<QuestionRecord>(run, 2)]))],
        [Abstractions.Queries.Questions] = [Case("a step's by id", (run, other) => ([R<QuestionRecord>(run, 3), R<QuestionRecord>(run, 1), R<QuestionRecord>(run, 2), R<QuestionRecord>(other, 1)],
            P(("step_key", $"{run}-step")), [R<QuestionRecord>(run, 1), R<QuestionRecord>(run, 2), R<QuestionRecord>(run, 3)]))],
        [Abstractions.Queries.Reports] = [Case("a step's in insertion order", (run, other) => ([R<ReportRecord>(run, 3), R<ReportRecord>(run, 1), R<ReportRecord>(other, 1)],
            P(("step_key", $"{run}-step")), [R<ReportRecord>(run, 3), R<ReportRecord>(run, 1)]))],
        [Abstractions.Queries.AgentEvents] = [Case("a step's in insertion order", (run, other) => ([R<AgentEventRecord>(run, 3), R<AgentEventRecord>(run, 1), R<AgentEventRecord>(other, 1)],
            P(("step_key", $"{run}-step")), [R<AgentEventRecord>(run, 3), R<AgentEventRecord>(run, 1)]))],
        [Abstractions.Queries.Overrides] = [Case("a run's in insertion order", (run, other) => ([R<OverrideRecord>(run, 3), R<OverrideRecord>(run, 1), R<OverrideRecord>(other, 1)],
            P(("run_id", run)), [R<OverrideRecord>(run, 3), R<OverrideRecord>(run, 1)]))],
        [Abstractions.Queries.Artifacts] = [Case("a run's and its descendants' in insertion order", (run, other) =>
            ([.. Tree(run, other), R<ArtifactRecord>(Child(run), 1), R<ArtifactRecord>(run, 3), R<ArtifactRecord>(run, 2), R<ArtifactRecord>(other, 1)],
                P(("run_id", run)), [R<ArtifactRecord>(Child(run), 1), R<ArtifactRecord>(run, 3), R<ArtifactRecord>(run, 2)]))],
        [Abstractions.Queries.Notification] = [Case("by key", (run, other) => ([R<NotificationRecord>(run, 1), R<NotificationRecord>(run, 2)],
            P(("key", $"{run}-n-{Name(2)}")), [R<NotificationRecord>(run, 2)]))],
        [Abstractions.Queries.Notifications] = [Case("a run's in insertion order", (run, other) =>
            ([R<NotificationRecord>(run, 3), R<NotificationRecord>(run, 1), R<NotificationRecord>(other, 2)], P(("run_id", run)), [R<NotificationRecord>(run, 3), R<NotificationRecord>(run, 1)]))],
        [Abstractions.Queries.Session] = [Case("by id", (run, other) => ([R<SessionRecord>(run, 1), R<SessionRecord>(run, 2)],
            P(("session_id", $"{run}-s-{Name(2)}")), [R<SessionRecord>(run, 2)]))],
        [Abstractions.Queries.Sessions] = [Case("a run's by id", (run, other) => ([R<SessionRecord>(run, 3), R<SessionRecord>(run, 1), R<SessionRecord>(run, 2), R<SessionRecord>(other, 1)],
            P(("run_id", run)), [R<SessionRecord>(run, 1), R<SessionRecord>(run, 2), R<SessionRecord>(run, 3)]))],
        [Abstractions.Queries.SessionChecks] = [Case("a session's by id", (run, other) =>
            ([R<SessionCheckRecord>(run, 3), R<SessionCheckRecord>(run, 1), R<SessionCheckRecord>(run, 2), R<SessionCheckRecord>(other, 1)],
                P(("session_id", $"{run}-session")), [R<SessionCheckRecord>(run, 1), R<SessionCheckRecord>(run, 2), R<SessionCheckRecord>(run, 3)]))],
        [Abstractions.Queries.Findings] = [Case("a run's and its descendants' by run, then id", (run, other) =>
            ([.. Tree(run, other), R<FindingRecord>(Child(run), 1), R<FindingRecord>(run, 3), R<FindingRecord>(run, 2), R<FindingRecord>(other, 1)],
                P(("run_id", run)), [R<FindingRecord>(run, 2), R<FindingRecord>(run, 3), R<FindingRecord>(Child(run), 1)]))],
        [Abstractions.Queries.FindingEvents] = [Case("a finding's in insertion order", (run, other) =>
            ([R<FindingEventRecord>(run, 3), R<FindingEventRecord>(run, 1), R<FindingEventRecord>(other, 2)], P(("run_id", run), ("finding_id", "f")),
                [R<FindingEventRecord>(run, 3), R<FindingEventRecord>(run, 1)])),
            Case("a run's, every finding's, in insertion order", (run, other) =>
                ([R<FindingEventRecord>(run, 3), new FindingEventRecord(run, "g", "k-g", FindingEventKinds.Raised, "state-1", "review", null, "review", null, null, At),
                    R<FindingEventRecord>(other, 2)], P(("run_id", run)),
                    [R<FindingEventRecord>(run, 3), new FindingEventRecord(run, "g", "k-g", FindingEventKinds.Raised, "state-1", "review", null, "review", null, null, At)]))],
        [Abstractions.Queries.StepStates] = [Case("a run's by step", (run, other) =>
            ([R<StepStateRecord>(run, 3), R<StepStateRecord>(run, 1), R<StepStateRecord>(run, 2), R<StepStateRecord>(other, 1)],
                P(("run_id", run)), [R<StepStateRecord>(run, 1), R<StepStateRecord>(run, 2), R<StepStateRecord>(run, 3)]))],
        [Abstractions.Queries.PullRequests] = [Case("a run's by repository, then number", (run, other) =>
            ([R<PullRequestRecord>(run, 3), R<PullRequestRecord>(run, 2), R<PullRequestRecord>(run, 1), R<PullRequestRecord>(other, 1)],
                P(("run_id", run)), [R<PullRequestRecord>(run, 1), R<PullRequestRecord>(run, 2), R<PullRequestRecord>(run, 3)]))],
        [Abstractions.Queries.Repositories] = [Case("by name", (run, other) =>
            ([R<RepositoryRecord>(run, 3), R<RepositoryRecord>(run, 1), R<RepositoryRecord>(run, 2)], P(),
                [R<RepositoryRecord>(run, 1), R<RepositoryRecord>(run, 2), R<RepositoryRecord>(run, 3)]), shared: true)],
        [Abstractions.Queries.Activity] =
        [
            Case("a step's by execution, session, then sequence", (run, other) =>
                ([R<ActivityRecord>(run, 4), R<ActivityRecord>(run, 3), R<ActivityRecord>(run, 2), R<ActivityRecord>(run, 1), R<ActivityRecord>(other, 1)],
                    P(("run_id", run), ("step_id", "implement")), [R<ActivityRecord>(run, 1), R<ActivityRecord>(run, 2), R<ActivityRecord>(run, 3), R<ActivityRecord>(run, 4)])),
            Case("one execution", (run, other) => ([R<ActivityRecord>(run, 4), R<ActivityRecord>(run, 1)],
                P(("run_id", run), ("step_id", "implement"), ("execution", "2")), [R<ActivityRecord>(run, 4)])),
        ],
    };
}
