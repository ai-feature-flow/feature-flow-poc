namespace FeatureFlow.Providers.Abstractions;

// Every record kind and named query, declared once (docs/06 section 3.12, F17). A SQL record store reads these: it has one
// writer and one query builder, and only its dialect of its own. A new kind is one RecordBatch list and one declaration here.

/// <summary>How a record store writes a record of a kind over one already kept by the same key.</summary>
public enum WriteRule
{
    /// <summary>The last write wins.</summary>
    Replace,

    /// <summary>The last write wins unless the kept record is newer by its <c>updated_at</c> column (LOG 290).</summary>
    ReplaceIfNotOlder,

    /// <summary>The first write is kept, so a retried append adds nothing.</summary>
    KeepFirst,

    /// <summary>Every write is appended, in insertion order.</summary>
    Append,

    /// <summary>
    /// An opening record (<see cref="RecordKind.Opens"/>) replaces; a closing one keeps the kept body, setting its <c>open</c>
    /// false and its <c>updatedAt</c> to the closing record's.
    /// </summary>
    CloseKeepsOpening,

    /// <summary>An opening record (<see cref="RecordKind.Opens"/>) is kept once; any other replaces, so a late opening record does not reopen it.</summary>
    KeepFirstWhileOpen,
}

/// <summary>The change (<see cref="ChangeKinds"/>) a record of a kind makes.</summary>
public enum ChangeEffect { None, RunChanged, WaitOpenedOrClosed, StepChanged }

/// <summary>A physical column of a kind's table besides <c>org_id</c> and <c>body</c>: its name, value type and value.</summary>
public sealed record RecordColumn(string Name, Type Type, Func<object, object?> Value);

/// <summary>
/// A record kind: its record type and table (with the <c>kind</c> value of a table it shares), its key and other columns,
/// its write rule, the change it makes, its run, its list in a <see cref="RecordBatch"/>, and for the two rules that need it
/// whether a record opens. Every table's key starts with <c>org_id</c>; a kind with no key is appended with an identity
/// <c>id</c>, which is also the insertion order of a <see cref="WriteRule.KeepFirst"/> table that has one.
/// </summary>
public sealed record RecordKind(
    string Name,
    Type Record,
    string Table,
    string? Discriminator,
    IReadOnlyList<RecordColumn> Key,
    IReadOnlyList<RecordColumn> Columns,
    WriteRule Rule,
    Func<RecordBatch, IReadOnlyList<object>?> Of,
    ChangeEffect Raises,
    Func<object, string?> RunId,
    Func<object, bool>? Opens = null)
{
    /// <summary>The key columns, then the others.</summary>
    public IEnumerable<RecordColumn> AllColumns => Key.Concat(Columns);

    public RecordColumn Column(string name) => AllColumns.Single(c => c.Name == name);

    /// <summary>The rule for one record: what <see cref="WriteRule.CloseKeepsOpening"/> and <see cref="WriteRule.KeepFirstWhileOpen"/> come to.</summary>
    public WriteRule RuleFor(object record) => Rule switch
    {
        WriteRule.CloseKeepsOpening => Opens!(record) ? WriteRule.Replace : WriteRule.CloseKeepsOpening,
        WriteRule.KeepFirstWhileOpen => Opens!(record) ? WriteRule.KeepFirst : WriteRule.Replace,
        _ => Rule,
    };
}

/// <summary>How a query filter compares its parameter.</summary>
public enum FilterOp
{
    /// <summary>The field equals the parameter, which is required.</summary>
    Equal,

    /// <summary>The field is greater than the parameter, 0 when not given.</summary>
    After,

    /// <summary>The field equals the parameter when it is given.</summary>
    EqualWhenGiven,

    /// <summary>The field is true when the parameter is <c>true</c>.</summary>
    TrueWhenGiven,
}

/// <summary>A filter of a query: its parameter, the column or (with <see cref="BodyField"/>) body field it compares, and how.</summary>
public sealed record QueryFilter(string Parameter, string Field, bool BodyField = false, FilterOp Op = FilterOp.Equal);

/// <summary>
/// One form of a query, chosen when every parameter of <see cref="WhenGiven"/> is given (the first that matches): its
/// filters, its order (columns, or <see cref="Insertion"/>), and its limit, fixed or the <c>limit</c> parameter (at most
/// and by default 500). With <see cref="RunTree"/> the <c>run_id</c> filter matches that run and every run below it, by
/// parent links. Text columns order ordinally in every store.
/// </summary>
public sealed record QueryVariant(
    IReadOnlyList<string> WhenGiven,
    IReadOnlyList<QueryFilter> Filters,
    IReadOnlyList<string> OrderBy,
    bool Descending = false,
    int? Limit = null,
    bool LimitParameter = false,
    bool RunTree = false)
{
    public const string Insertion = "insertion";

    public int? LimitFor(IReadOnlyDictionary<string, string> parameters) => LimitParameter ? Page(parameters) : Limit;

    /// <summary>The <c>limit</c> parameter: at most and by default 500.</summary>
    public static int Page(IReadOnlyDictionary<string, string> parameters) =>
        Math.Clamp(int.Parse(parameters.GetValueOrDefault("limit", "500"), System.Globalization.CultureInfo.InvariantCulture), 1, 500);
}

/// <summary>A named query (<see cref="Queries"/>) over one kind, in its forms.</summary>
public sealed record RecordQuery(string Name, RecordKind Kind, IReadOnlyList<QueryVariant> Variants)
{
    public QueryVariant VariantFor(IReadOnlyDictionary<string, string> parameters) => Variants.First(v => v.WhenGiven.All(parameters.ContainsKey));

    /// <summary>A filter's parameter as its column's type, or null when an optional parameter does not apply.</summary>
    public object? Argument(QueryFilter filter, IReadOnlyDictionary<string, string> parameters)
    {
        var given = parameters.TryGetValue(filter.Parameter, out var text);
        switch (filter.Op)
        {
            case FilterOp.Equal when !given:
                throw new ArgumentException($"query {Name} needs {filter.Parameter}");
            case FilterOp.EqualWhenGiven when !given:
            case FilterOp.TrueWhenGiven when text != "true":
                return null;
            case FilterOp.TrueWhenGiven:
                return true;
            case FilterOp.After when !given:
                text = "0";
                break;
        }

        var type = filter.BodyField ? typeof(string) : Kind.Column(filter.Field).Type;
        return type == typeof(string) ? text : Convert.ChangeType(text, type, System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>Every record kind, in the order a batch's run changes are listed (<see cref="ChangeKinds.Of"/>).</summary>
public static class RecordKinds
{
    static RecordColumn C<T, TValue>(string name, Func<T, TValue> value) =>
        new(name, Nullable.GetUnderlyingType(typeof(TValue)) ?? typeof(TValue), r => value((T)r));

    static RecordKind K<T>(string table, Func<RecordBatch, IReadOnlyList<T>?> of, WriteRule rule, ChangeEffect raises, Func<T, string?> runId,
        RecordColumn[] key, RecordColumn[]? columns = null, string? discriminator = null, Func<T, bool>? opens = null) where T : class =>
        new(typeof(T).Name.Replace("Record", "", StringComparison.Ordinal), typeof(T), table, discriminator, key, columns ?? [], rule, of, raises,
            r => runId((T)r), opens is null ? null : r => opens((T)r));

    public static readonly RecordKind Runs = K<RunRecord>("runs", b => b.Runs, WriteRule.ReplaceIfNotOlder, ChangeEffect.RunChanged, r => r.RunId,
        [C<RunRecord, string>("run_id", r => r.RunId)], [C<RunRecord, string?>("parent_run_id", r => r.ParentRunId), C<RunRecord, DateTimeOffset>("updated_at", r => r.UpdatedAt)]);

    public static readonly RecordKind Waits = K<WaitRecord>("waits", b => b.Waits, WriteRule.CloseKeepsOpening, ChangeEffect.WaitOpenedOrClosed, w => w.RunId,
        [C<WaitRecord, string>("run_id", w => w.RunId), C<WaitRecord, string>("wait_id", w => w.WaitId)], [C<WaitRecord, bool>("open", w => w.Open)], opens: w => w.Open);

    public static readonly RecordKind Timeline = K<TimelineRecord>("timeline", b => b.Timeline, WriteRule.KeepFirst, ChangeEffect.RunChanged, t => t.RunId,
        [C<TimelineRecord, string>("run_id", t => t.RunId), C<TimelineRecord, long>("sequence", t => t.Sequence)]);

    public static readonly RecordKind Answers = K<AnswerRecord>("answers", b => b.Answers, WriteRule.Append, ChangeEffect.RunChanged, a => a.RunId,
        [], [C<AnswerRecord, string>("run_id", a => a.RunId)]);

    public static readonly RecordKind Locks = K<LockRecord>("locks", b => b.Locks, WriteRule.Replace, ChangeEffect.None, l => l.RunId,
        [C<LockRecord, string>("run_id", l => l.RunId)]);

    public static readonly RecordKind AgentSteps = K<AgentStepRecord>("agent_keyed", b => b.AgentSteps, WriteRule.Replace, ChangeEffect.None, s => s.RunId,
        [C<AgentStepRecord, string>("key", s => s.StepKey)], [C<AgentStepRecord, string>("step_key", s => s.StepKey)], "step");

    public static readonly RecordKind Questions = K<QuestionRecord>("agent_keyed", b => b.Questions, WriteRule.KeepFirstWhileOpen, ChangeEffect.None, q => q.RunId,
        [C<QuestionRecord, string>("key", q => q.QuestionId)], [C<QuestionRecord, string>("step_key", q => q.StepKey)], "question", q => q.Status == QuestionRecord.Open);

    public static readonly RecordKind Reports = K<ReportRecord>("agent_log", b => b.Reports, WriteRule.Append, ChangeEffect.None, _ => null,
        [], [C<ReportRecord, string>("step_key", r => r.StepKey)], "report");

    public static readonly RecordKind AgentEvents = K<AgentEventRecord>("agent_log", b => b.AgentEvents, WriteRule.Append, ChangeEffect.None, _ => null,
        [], [C<AgentEventRecord, string>("step_key", e => e.StepKey)], "event");

    public static readonly RecordKind Overrides = K<OverrideRecord>("overrides", b => b.Overrides, WriteRule.Append, ChangeEffect.RunChanged, o => o.RunId,
        [], [C<OverrideRecord, string>("run_id", o => o.RunId)]);

    public static readonly RecordKind Artifacts = K<ArtifactRecord>("artifacts", b => b.Artifacts, WriteRule.KeepFirst, ChangeEffect.RunChanged, a => a.RunId,
        [C<ArtifactRecord, string>("run_id", a => a.RunId), C<ArtifactRecord, string>("step_id", a => a.StepId), C<ArtifactRecord, int>("execution", a => a.Execution),
            C<ArtifactRecord, string>("name", a => a.Name)]);

    public static readonly RecordKind Notifications = K<NotificationRecord>("notifications", b => b.Notifications, WriteRule.KeepFirst, ChangeEffect.None, n => n.RunId,
        [C<NotificationRecord, string>("key", n => n.Key)], [C<NotificationRecord, string>("run_id", n => n.RunId)]);

    public static readonly RecordKind Sessions = K<SessionRecord>("sessions", b => b.Sessions, WriteRule.Replace, ChangeEffect.RunChanged, s => s.RunId,
        [C<SessionRecord, string>("session_id", s => s.SessionId)], [C<SessionRecord, string>("run_id", s => s.RunId)]);

    public static readonly RecordKind SessionChecks = K<SessionCheckRecord>("session_checks", b => b.SessionChecks, WriteRule.Replace, ChangeEffect.None, _ => null,
        [C<SessionCheckRecord, string>("session_id", c => c.SessionId), C<SessionCheckRecord, string>("check_id", c => c.CheckId)]);

    public static readonly RecordKind Findings = K<FindingRecord>("findings", b => b.Findings, WriteRule.Replace, ChangeEffect.RunChanged, f => f.RunId,
        [C<FindingRecord, string>("run_id", f => f.RunId), C<FindingRecord, string>("finding_id", f => f.FindingId)]);

    public static readonly RecordKind FindingEvents = K<FindingEventRecord>("finding_events", b => b.FindingEvents, WriteRule.KeepFirst, ChangeEffect.RunChanged, e => e.RunId,
        [C<FindingEventRecord, string>("run_id", e => e.RunId), C<FindingEventRecord, string>("finding_id", e => e.FindingId), C<FindingEventRecord, string>("key", e => e.Key)]);

    public static readonly RecordKind StepStates = K<StepStateRecord>("step_states", b => b.StepStates, WriteRule.Replace, ChangeEffect.RunChanged, s => s.RunId,
        [C<StepStateRecord, string>("run_id", s => s.RunId), C<StepStateRecord, string>("step_id", s => s.StepId)]);

    public static readonly RecordKind PullRequests = K<PullRequestRecord>("pull_requests", b => b.PullRequests, WriteRule.Replace, ChangeEffect.RunChanged, p => p.RunId,
        [C<PullRequestRecord, string>("run_id", p => p.RunId), C<PullRequestRecord, string>("repository", p => p.Repository), C<PullRequestRecord, int>("number", p => p.Number)]);

    public static readonly RecordKind Repositories = K<RepositoryRecord>("repositories", b => b.Repositories, WriteRule.Replace, ChangeEffect.None, _ => null,
        [C<RepositoryRecord, string>("name", r => r.Name)]);

    public static readonly RecordKind Activity = K<ActivityRecord>("activity", b => b.Activity, WriteRule.KeepFirst, ChangeEffect.StepChanged, a => a.RunId,
        [C<ActivityRecord, string>("run_id", a => a.RunId), C<ActivityRecord, string>("step_id", a => a.StepId), C<ActivityRecord, int>("execution", a => a.Execution),
            C<ActivityRecord, int>("session", a => a.Session), C<ActivityRecord, int>("sequence", a => a.Sequence)]);

    public static IReadOnlyList<RecordKind> All { get; } =
    [
        Runs, Waits, Timeline, Answers, Locks, AgentSteps, Questions, Reports, AgentEvents, Overrides, Artifacts, Notifications, Sessions, SessionChecks,
        Findings, FindingEvents, StepStates, PullRequests, Repositories, Activity,
    ];

    public static RecordKind For(Type record) => All.Single(k => k.Record == record);
}

/// <summary>Every named query (<see cref="Queries"/>) but the store-native ones, declared over its kind.</summary>
public static class RecordQueries
{
    /// <summary>The queries each store writes itself: a join of waits to runs, and the store's own change table.</summary>
    public static IReadOnlyList<string> StoreNative { get; } = [Queries.OpenWaits, Queries.Changes, Queries.LatestChange];

    static QueryFilter F(string parameter, string? field = null, FilterOp op = FilterOp.Equal) => new(parameter, field ?? parameter, Op: op);

    static QueryVariant V(QueryFilter[] filters, params string[] orderBy) => new([], filters, orderBy);

    static RecordQuery Q(string name, RecordKind kind, params QueryVariant[] variants) => new(name, kind, variants);

    static readonly QueryFilter RunId = F("run_id");
    static readonly QueryFilter StepKey = F("step_key");
    const string Insertion = QueryVariant.Insertion;

    public static IReadOnlyDictionary<string, RecordQuery> All { get; } = new[]
    {
        Q(Queries.Run, RecordKinds.Runs, V([RunId])),
        Q(Queries.Runs, RecordKinds.Runs,
            new QueryVariant(["parent_run_id"], [F("parent_run_id")], ["run_id"]),
            new QueryVariant([], [], ["updated_at"], Descending: true, Limit: 200)),
        Q(Queries.Waits, RecordKinds.Waits, V([RunId, F("open", op: FilterOp.TrueWhenGiven)], "wait_id")),
        Q(Queries.Timeline, RecordKinds.Timeline, V([RunId, F("after", "sequence", FilterOp.After)], "sequence")),
        Q(Queries.Answers, RecordKinds.Answers, V([RunId], Insertion)),
        Q(Queries.RecentAnswers, RecordKinds.Answers, new QueryVariant([], [new("actor", "actor", BodyField: true)], [Insertion], Descending: true, LimitParameter: true)),
        Q(Queries.Lock, RecordKinds.Locks, V([RunId])),
        Q(Queries.AgentStep, RecordKinds.AgentSteps, V([F("step_key", "key")])),
        Q(Queries.Question, RecordKinds.Questions, V([F("question_id", "key")])),
        Q(Queries.Questions, RecordKinds.Questions, V([StepKey], "key")),
        Q(Queries.Reports, RecordKinds.Reports, V([StepKey], Insertion)),
        Q(Queries.AgentEvents, RecordKinds.AgentEvents, V([StepKey], Insertion)),
        Q(Queries.Overrides, RecordKinds.Overrides, V([RunId], Insertion)),
        Q(Queries.Artifacts, RecordKinds.Artifacts, new QueryVariant([], [RunId], [Insertion], RunTree: true)),
        Q(Queries.Notification, RecordKinds.Notifications, V([F("key")])),
        Q(Queries.Notifications, RecordKinds.Notifications, V([RunId], Insertion)),
        Q(Queries.Session, RecordKinds.Sessions, V([F("session_id")])),
        Q(Queries.Sessions, RecordKinds.Sessions, V([RunId], "session_id")),
        Q(Queries.SessionChecks, RecordKinds.SessionChecks, V([F("session_id")], "check_id")),
        Q(Queries.Findings, RecordKinds.Findings, new QueryVariant([], [RunId], ["run_id", "finding_id"], RunTree: true)),
        Q(Queries.FindingEvents, RecordKinds.FindingEvents, V([RunId, F("finding_id")], Insertion)),
        Q(Queries.StepStates, RecordKinds.StepStates, V([RunId], "step_id")),
        Q(Queries.PullRequests, RecordKinds.PullRequests, V([RunId], "repository", "number")),
        Q(Queries.Repositories, RecordKinds.Repositories, V([], "name")),
        Q(Queries.Activity, RecordKinds.Activity, V([RunId, F("step_id"), F("execution", op: FilterOp.EqualWhenGiven)], "execution", "session", "sequence")),
    }.ToDictionary(q => q.Name, StringComparer.Ordinal);

    /// <summary>The query named, or an <see cref="ArgumentException"/> unless it is store-native; and that it returns <typeparamref name="T"/>.</summary>
    public static RecordQuery For<T>(string name)
    {
        if (!All.TryGetValue(name, out var query))
            throw new ArgumentException($"unknown query {name}");
        if (query.Kind.Record != typeof(T))
            throw new ArgumentException($"query {name} returns {query.Kind.Record.Name}, not {typeof(T).Name}");
        return query;
    }
}
