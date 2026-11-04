using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.Extensions.Logging.Abstractions;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// The engine's notifications (docs/06 section 3.10): what the run records becomes messages, routed and filtered by
/// organisation configuration, once per run, step, attempt and event, in one thread per root run per channel, and a
/// failed send never fails the record activity.
/// </summary>
public sealed class RunNotifierTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-notify-" + Guid.NewGuid().ToString("N"));
    readonly SqliteRecordStore records;
    readonly InMemoryNotifications chat = new InMemoryNotifications().AddChannel("#decisions").AddChannel("#runs");
    readonly DateTimeOffset now = DateTimeOffset.UtcNow;

    public RunNotifierTests()
    {
        Directory.CreateDirectory(folder);
        records = new SqliteRecordStore(Path.Combine(folder, "records.db"));
    }

    public void Dispose() => Directory.Delete(folder, recursive: true);

    static readonly NotificationsConfig Routed = new(new Dictionary<string, NotificationRoute>
    {
        [NotificationEvents.DecisionNeeded] = new("#decisions"),
        [NotificationEvents.Resumed] = new("#decisions"),
        [NotificationEvents.Completed] = new("#runs"),
        [NotificationEvents.Failed] = new("#runs"),
    }, [NotificationEvents.DecisionNeeded, NotificationEvents.Resumed, NotificationEvents.Completed, NotificationEvents.Failed]);

    RunNotifier Notifier(NotificationsConfig? config = null, IProvider? provider = null)
    {
        var providers = new ProviderSet(
            new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["record_store"] = new Dictionary<string, IProvider> { ["sqlite"] = records },
                ["notifications"] = new Dictionary<string, IProvider> { ["chat"] = provider ?? chat },
            },
            new Dictionary<string, string> { ["record_store"] = "sqlite", ["notifications"] = "chat" },
            new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "someone", []), 3, 10, Path.Combine(folder, "installation.yaml"));
        var organisation = new OrganisationConfig("t", [], new PolicyConfig([], null, null, null, null), Path.Combine(folder, "organisation.yaml"))
        {
            Notifications = config ?? Routed,
        };
        return new RunNotifier(new EngineServices(providers, installation, organisation), NullLogger.Instance, TimeSpan.FromMilliseconds(500));
    }

    RunRecord Run(string id, string status, string? parent = null) => new(id, parent, "std:templates/ticket_delivery", status, "pat", ["acme/api"], false, now, null);

    WaitRecord Wait(string run, string id, string kind = "decision", bool open = true, IReadOnlyList<string>? choices = null) =>
        open
            ? new WaitRecord(run, id, kind, "approve_spec", "ticket.spec_approval", "Approve the spec?", choices ?? ["approved", "rejected"], true, now)
            : new WaitRecord(run, id, "", "", "", "", [], false, now);

    /// <summary>What the run's record activity does: append, then notify.</summary>
    async Task RecordAsync(RunNotifier notifier, RecordBatch batch)
    {
        (await records.AppendAsync(batch, Ct)).Unwrap();
        await notifier.NotifyAsync(batch, Ct);
    }

    [Fact]
    public async Task Each_event_goes_to_its_routed_channel_and_an_event_not_listed_or_not_routed_is_not_sent()
    {
        await RecordAsync(Notifier(), new RecordBatch([Run("r1", "running")], [Wait("r1", "decision-1")]));
        await RecordAsync(Notifier(), new RecordBatch([Run("r1", "completed")]));

        var decision = Assert.Single(chat.Messages("#decisions")).Decision!;
        Assert.Equal(("r1", "decision-1", "decision", "ticket.spec_approval"), (decision.RunId, decision.WaitId, decision.Kind, decision.DecisionType));
        Assert.Equal(["approved", "rejected"], decision.Choices);
        Assert.Contains("[completed] Run r1 completed", Assert.Single(chat.Messages("#runs")).Text);

        // Filtered: failed is routed but not listed in events. Unrouted: decision_needed listed with no channel.
        var filtered = new NotificationsConfig(Routed.Routing, [NotificationEvents.Completed]);
        await RecordAsync(Notifier(filtered), new RecordBatch([Run("r2", "failed")], [Wait("r2", "decision-1")]));
        var unrouted = new NotificationsConfig(new Dictionary<string, NotificationRoute>(), [NotificationEvents.DecisionNeeded]);
        await RecordAsync(Notifier(unrouted), new RecordBatch(Waits: [Wait("r3", "decision-1")]));

        Assert.DoesNotContain(AllMessages(), m => m.Text.Contains("r2", StringComparison.Ordinal) || m.Text.Contains("r3", StringComparison.Ordinal));
        Assert.Empty(await NotificationsOf("r2"));
    }

    [Fact]
    public async Task A_batch_recorded_again_after_a_restart_sends_nothing_again()
    {
        var batch = new RecordBatch([Run("r1", "failed")], [Wait("r1", "intervention-2", "intervention", choices: ["retry", "go_to", "end"])]);
        await RecordAsync(Notifier(), batch);

        // A new notifier is a restarted worker; the records are the only memory of what was sent.
        await Notifier().NotifyAsync(batch, Ct);

        Assert.Single(AllMessages(), m => m.Decision is not null);
        Assert.Single(AllMessages(), m => m.Text.Contains("Run r1 failed", StringComparison.Ordinal));
        // An intervention offers as buttons only what needs no further input.
        Assert.Equal(["retry"], AllMessages().Single(m => m.Decision is not null).Decision!.Choices);
    }

    [Fact]
    public async Task A_root_run_and_its_children_post_in_one_thread_per_channel_whoever_posts_first()
    {
        // The child posts first in #decisions: its message starts the root's thread there.
        await RecordAsync(Notifier(), new RecordBatch([Run("root", "running"), Run("root.child.1", "running", "root")]));
        await RecordAsync(Notifier(), new RecordBatch(Waits: [Wait("root.child.1", "decision-1")]));
        await RecordAsync(Notifier(), new RecordBatch(Waits: [Wait("root", "decision-1")]));
        await RecordAsync(Notifier(), new RecordBatch(Waits: [Wait("root.child.1", "decision-2")]));
        // The root posts first in #runs.
        await RecordAsync(Notifier(), new RecordBatch([Run("root", "completed")]));
        await RecordAsync(Notifier(), new RecordBatch([Run("root.child.1", "completed", "root")]));

        var top = Assert.Single(chat.Messages("#decisions"));
        Assert.Equal(("root.child.1", "decision-1"), (top.Decision!.RunId, top.Decision.WaitId));
        Assert.Equal(["root:decision-1", "root.child.1:decision-2"], chat.Messages("#decisions", top.Ref).Select(m => $"{m.Decision!.RunId}:{m.Decision.WaitId}"));
        var runs = Assert.Single(chat.Messages("#runs"));
        Assert.Contains("Run root completed", runs.Text);
        Assert.Contains("Run root.child.1 completed", Assert.Single(chat.Messages("#runs", runs.Ref)).Text);
    }

    [Fact]
    public async Task A_closed_wait_is_resumed_naming_the_person_who_answered()
    {
        await RecordAsync(Notifier(), new RecordBatch([Run("r1", "running")], [Wait("r1", "decision-1")]));

        await RecordAsync(Notifier(), new RecordBatch(Waits: [Wait("r1", "decision-1", open: false)],
            Answers: [new AnswerRecord("r1", "decision-1", "alice", """{"waitId":"decision-1","actor":"alice","outcome":"approved"}""", "recorded", now)]));

        var thread = chat.Messages("#decisions").Single().Ref;
        var resumed = Assert.Single(chat.Messages("#decisions", thread));
        Assert.Contains("decision decision-1 (ticket.spec_approval) at approve_spec was answered by alice: approved", resumed.Text);
    }

    sealed class Broken(Func<Task<ProviderResult<MessageRef>>> send) : INotificationsProvider
    {
        public int Calls { get; private set; }

        public ProviderDescription Describe() => new("notifications", "broken", "0", new NotificationCapabilities([], true, true));

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

        public Task<ProviderResult<MessageRef>> SendAsync(string channel, Notification message, MessageRef? thread, CancellationToken cancellationToken)
        {
            Calls++;
            return send();
        }

        public Task<ProviderResult<MessageRef>> PostDecisionAsync(string channel, DecisionRequest request, MessageRef? thread, CancellationToken cancellationToken)
        {
            Calls++;
            return send();
        }
    }

    [Fact]
    public async Task A_send_that_fails_throws_or_hangs_never_fails_the_record_activity_and_records_nothing()
    {
        var batch = new RecordBatch([Run("r1", "failed")], [Wait("r1", "decision-1")]);
        var refused = new Broken(() => Task.FromResult(ProviderResult<MessageRef>.Failure("server_error", "down")));
        var throwing = new Broken(() => throw new InvalidOperationException("a bug in the provider"));
        var hanging = new Broken(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), Ct);
            return ProviderResult<MessageRef>.Success(new MessageRef("#runs", "late"));
        });

        await RecordAsync(Notifier(provider: refused), batch);
        await Notifier(provider: throwing).NotifyAsync(batch, Ct);
        var started = DateTime.UtcNow;
        await Notifier(provider: hanging).NotifyAsync(batch, Ct);

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(10), "a hanging send held the record activity");
        Assert.Equal((2, 2, 2), (refused.Calls, throwing.Calls, hanging.Calls));
        Assert.Empty(await NotificationsOf("r1"));

        // Not recorded, so a retried record activity sends it.
        await Notifier().NotifyAsync(batch, Ct);
        Assert.Equal(2, AllMessages().Count);
    }

    [Fact]
    public async Task With_no_notifications_provider_that_can_send_nothing_is_sent_and_nothing_fails()
    {
        await Notifier(provider: new DescribedOnlyProvider("notifications")).NotifyAsync(new RecordBatch([Run("r1", "failed")]), Ct);

        Assert.Empty(await NotificationsOf("r1"));
    }

    List<PostedMessage> AllMessages() =>
        new[] { "#decisions", "#runs" }.SelectMany(c => chat.Messages(c).SelectMany(m => chat.Messages(c, m.Ref).Prepend(m))).ToList();

    async Task<IReadOnlyList<NotificationRecord>> NotificationsOf(string run) =>
        (await records.QueryAsync<NotificationRecord>(Queries.Notifications, new Dictionary<string, string> { ["run_id"] = run }, Ct)).Unwrap();
}
