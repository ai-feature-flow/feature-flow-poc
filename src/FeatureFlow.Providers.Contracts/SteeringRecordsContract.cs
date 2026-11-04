using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The record store's contract for M7's records (docs/05 section 5, docs/06 sections 3.10 and 3.11): overrides are an
/// audit trail, appended in order with the actor, reason and change kept, and never replaced; a notification is kept once
/// per de-duplication key with its first message, so a restarted sender finds it; a session and its checks keep their last
/// write. Each query answers for its own run or session only. A record store's test project subclasses it with a fresh store.
/// </summary>
public abstract class SteeringRecordsContract : ContractBase
{
    protected abstract IRecordStoreProvider Store { get; }

    readonly string unique = "ff-contract-" + Guid.NewGuid().ToString("N")[..12];
    static readonly DateTimeOffset At = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    string Id(string name) => $"{unique}-{name}";

    Task<ProviderResult<Unit>> Append(RecordBatch batch) => Store.AppendAsync(batch, Ct);

    async Task<IReadOnlyList<T>> Query<T>(string query, params (string Key, string Value)[] parameters) =>
        Ok(await Store.QueryAsync<T>(query, parameters.ToDictionary(p => p.Key, p => p.Value), Ct));

    OverrideRecord Override(string run, string id, string result) =>
        new(Id(run), Id(id), "skip_protected", "a-person", "the step is broken", "review", new Dictionary<string, string> { ["outcome"] = "approved" }, result, At);

    [Fact]
    public async Task Overrides_are_appended_in_order_with_actor_reason_and_change_and_never_replaced()
    {
        Ok(await Append(new RecordBatch(Overrides: [Override("run", "o1", "sent")])));
        Ok(await Append(new RecordBatch(Overrides: [Override("run", "o1", "recorded"), Override("other", "o2", "sent")])));

        var stored = await Query<OverrideRecord>(Queries.Overrides, ("run_id", Id("run")));

        Assert.Equal(["sent", "recorded"], stored.Select(o => o.Result));
        Assert.All(stored, o => Assert.Equal((Id("o1"), "skip_protected", "a-person", "the step is broken", "review"), (o.OverrideId, o.Key, o.Actor, o.Reason, o.StepId)));
        Assert.Equal("approved", stored[0].Change["outcome"]);
    }

    [Fact]
    public async Task A_notification_is_kept_once_per_key_with_its_first_message()
    {
        var key = NotificationEvents.Key(Id("run"), "review", 1, NotificationEvents.DecisionNeeded);
        NotificationRecord N(string k, string message, MessageRef? thread = null) =>
            new(k, Id("run"), "review", NotificationEvents.DecisionNeeded, new MessageRef("#ff", message), thread, At);

        Ok(await Append(new RecordBatch(Notifications: [N(key, "m1")])));
        Ok(await Append(new RecordBatch(Notifications: [N(key, "m2"), N(key + "x", "m3", new MessageRef("#ff", "m1"))])));

        Assert.Equal("m1", Assert.Single(await Query<NotificationRecord>(Queries.Notification, ("key", key))).Message.Id);
        Assert.Empty(await Query<NotificationRecord>(Queries.Notification, ("key", Id("none"))));
        var all = await Query<NotificationRecord>(Queries.Notifications, ("run_id", Id("run")));
        Assert.Equal(["m1", "m3"], all.Select(n => n.Message.Id));
        Assert.Equal(new MessageRef("#ff", "m1"), all[1].Thread);
    }

    [Fact]
    public void The_de_duplication_key_differs_by_run_step_attempt_and_event()
    {
        var keys = new[]
        {
            NotificationEvents.Key("r", "s", 1, "failed"), NotificationEvents.Key("r2", "s", 1, "failed"), NotificationEvents.Key("r", "s2", 1, "failed"),
            NotificationEvents.Key("r", "s", 2, "failed"), NotificationEvents.Key("r", "s", 1, "stuck"), NotificationEvents.Key("r", null, 0, "failed"),
        };

        Assert.Equal(keys.Length, keys.Distinct().Count());
    }

    [Fact]
    public async Task A_session_and_its_checks_keep_their_last_write_and_answer_for_their_own_run_and_session()
    {
        SessionRecord S(string id, string run, string status, string? submitted = null) =>
            new(Id(id), Id(run), "approve_spec", "approve_spec", "a-person", "acme/api", "ff/session/x", "rev1", status, submitted,
                submitted is null ? null : new Dictionary<string, string> { ["spec"] = "sha256:1" }, At);
        SessionCheckRecord C(string session, string check, string status) => new(Id(session), check, "review_spec", "rev2", "inputs-digest", status, null, At);

        Ok(await Append(new RecordBatch(Sessions: [S("s1", "run", SessionStatuses.Prepared), S("s2", "run", SessionStatuses.Prepared), S("s3", "other", SessionStatuses.Prepared)],
            SessionChecks: [C("s1", "c1", "running"), C("s1", "c2", "running"), C("s2", "c1", "running")])));
        Ok(await Append(new RecordBatch(Sessions: [S("s1", "run", SessionStatuses.Submitted, "rev3")], SessionChecks: [C("s1", "c1", "passed")])));

        var s1 = Assert.Single(await Query<SessionRecord>(Queries.Session, ("session_id", Id("s1"))));
        Assert.Equal((SessionStatuses.Submitted, "rev3", "sha256:1"), (s1.Status, s1.SubmittedRevision, s1.Artifacts!["spec"]));
        Assert.Equal([Id("s1"), Id("s2")], (await Query<SessionRecord>(Queries.Sessions, ("run_id", Id("run")))).Select(s => s.SessionId));
        var checks = await Query<SessionCheckRecord>(Queries.SessionChecks, ("session_id", Id("s1")));
        Assert.Equal([("c1", "passed"), ("c2", "running")], checks.Select(c => (c.CheckId, c.Status)));
        Assert.Equal("inputs-digest", checks[0].Inputs);
    }
}
