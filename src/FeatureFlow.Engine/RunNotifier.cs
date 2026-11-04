using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Microsoft.Extensions.Logging;

namespace FeatureFlow.Engine;

/// <summary>
/// Sends a run's notifications (docs/06 section 3.10) from what the run records, in the record activity, never in
/// workflow code: an opened wait is <c>decision_needed</c> with its kind and choices, a closed one <c>resumed</c>, an
/// ended run <c>completed</c> or <c>failed</c>. Organisation configuration, read when sending, routes each event to a
/// channel and filters which are sent at all (<c>notifications.routing</c>, <c>notifications.events</c>, docs/07
/// section 3). Each message is sent once per <see cref="NotificationEvents.Key"/>, kept as a
/// <see cref="NotificationRecord"/> (LOG 201), so a retried record activity or a restart does not repeat it. All
/// messages of a root run and its children in one channel go in one thread, started by the first of them. A failed send
/// is logged and never affects the run; it is tried again only if the record activity itself is retried.
/// </summary>
public sealed class RunNotifier(EngineServices services, ILogger logger, TimeSpan? sendTimeout = null, TimeSpan? budget = null)
{
    /// <summary>
    /// The most one batch may spend sending, well inside the record activity's two minutes: what is left when it runs out
    /// is dropped and logged, so a chat that hangs can never fail the activity and hold the run.
    /// </summary>
    public static readonly TimeSpan DefaultBudget = TimeSpan.FromSeconds(45);

    readonly TimeSpan budgetFor = budget ?? DefaultBudget;

    /// <summary>How long one send may take before it is given up, so a slow chat cannot hold the record activity.</summary>
    public static readonly TimeSpan DefaultSendTimeout = TimeSpan.FromSeconds(10);

    readonly TimeSpan timeoutAfter = sendTimeout ?? DefaultSendTimeout;

    /// <summary>The <see cref="NotificationRecord.Event"/> of a thread started on a root run's behalf by one of its children.</summary>
    public const string ThreadEvent = "thread";

    /// <summary>The choices a chat button can answer alone: an intervention's other recoveries need a step, a count or a status.</summary>
    static readonly HashSet<string> InterventionButtons = ["retry"];

    IRecordStoreProvider Records => services.RecordStore;

    public async Task NotifyAsync(RecordBatch batch, CancellationToken cancellationToken)
    {
        var config = services.Organisation.Notifications;
        if (config.Events.Count == 0 || services.Providers.ForRun("notifications", false) is not INotificationsProvider provider)
            return;
        var deadline = DateTimeOffset.UtcNow + budgetFor;
        foreach (var item in await PendingAsync(batch, cancellationToken))
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                logger.LogWarning("notifications: {Key} and any after it were not sent: this batch used its {Budget} s", item.Key, budgetFor.TotalSeconds);
                return;
            }

            if (!config.Events.Contains(item.Event))
                continue;
            if (config.Routing.GetValueOrDefault(item.Event)?.Channel is not { Length: > 0 } channel)
            {
                logger.LogWarning("notifications: {Event} is sent but has no route in notifications.routing; {Key} is not sent", item.Event, item.Key);
                continue;
            }

            try
            {
                await SendAsync(provider, channel, item, deadline, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(e, "notifications: {Key} to {Channel} failed unexpectedly; the run carries on", item.Key, channel);
            }
        }
    }

    /// <summary>One message to send: its event and key, and either a notification or a decision request.</summary>
    sealed record Pending(string Event, string Key, string RunId, string? StepId, Notification? Message, DecisionRequest? Decision);

    async Task<List<Pending>> PendingAsync(RecordBatch batch, CancellationToken ct)
    {
        var list = new List<Pending>();
        foreach (var wait in batch.Waits ?? [])
        {
            if (wait.Open)
            {
                var choices = wait.Kind == "intervention" ? wait.Choices.Where(InterventionButtons.Contains).ToList() : wait.Choices.ToList();
                list.Add(new Pending(NotificationEvents.DecisionNeeded, NotificationEvents.Key(wait.RunId, wait.WaitId, 0, NotificationEvents.DecisionNeeded),
                    wait.RunId, wait.StepId, null, new DecisionRequest(wait.RunId, wait.WaitId, wait.Kind, wait.DecisionType, wait.Reason, choices, wait.StepId)));
                continue;
            }

            // A closing record keeps only the wait's id: what it was is in the opening record, and who answered in the answers.
            var opened = await OpenedAsync(wait, ct);
            var answer = await AnswerOfAsync(wait, batch, ct);
            var what = opened is null ? $"wait {wait.WaitId}" : $"{opened.Kind} {wait.WaitId} ({opened.DecisionType}) at {opened.StepId}";
            var text = answer is null ? $"{what} closed without an answer from a person" : $"{what} was answered: {answer}";
            list.Add(new Pending(NotificationEvents.Resumed, NotificationEvents.Key(wait.RunId, wait.WaitId, 0, NotificationEvents.Resumed),
                wait.RunId, opened?.StepId, new Notification(NotificationEvents.Resumed, wait.RunId, $"Run {wait.RunId} resumed", text, opened?.StepId), null));
        }

        foreach (var run in batch.Runs ?? [])
        {
            var ended = run.Status switch
            {
                "starting" or "running" => null,
                "completed" => NotificationEvents.Completed,
                _ => NotificationEvents.Failed,
            };
            if (ended is null)
                continue;
            var text = $"{run.Template}, requested by {run.Requester}" + (run.Repositories.Count == 0 ? "" : $", on {string.Join(", ", run.Repositories)}")
                       + (run.Detail is { Length: > 0 } detail ? $"\n{detail}" : "");
            list.Add(new Pending(ended, NotificationEvents.Key(run.RunId, null, 0, ended), run.RunId, null,
                new Notification(ended, run.RunId, $"Run {run.RunId} {run.Status}", text), null));
        }

        return list;
    }

    async Task<WaitRecord?> OpenedAsync(WaitRecord closed, CancellationToken ct) =>
        (await Records.QueryAsync<WaitRecord>(Queries.Waits, new Dictionary<string, string> { ["run_id"] = closed.RunId }, ct)).Value?
        .FirstOrDefault(w => w.WaitId == closed.WaitId && w.Kind.Length > 0);

    /// <summary>
    /// The answer as the run applied it, from its own timeline (<c>answered</c>: "approved by alice"; for an agent's
    /// question, <c>question_answered</c>), in this batch or recorded earlier. The API's answer records are not used: the
    /// run may record the closed wait before the API records the delivery.
    /// </summary>
    async Task<string?> AnswerOfAsync(WaitRecord closed, RecordBatch batch, CancellationToken ct)
    {
        static string? Of(TimelineRecord t, string waitId) => t.Kind switch
        {
            "answered" when t.StepId == waitId => t.Detail,
            "question_answered" when t.Detail?.StartsWith(waitId + " by ", StringComparison.Ordinal) == true => t.Detail[(waitId.Length + 1)..],
            _ => null,
        };

        if ((batch.Timeline ?? []).Select(t => Of(t, closed.WaitId)).LastOrDefault(a => a is not null) is { } inBatch)
            return inBatch;
        return (await Records.QueryAsync<TimelineRecord>(Queries.Timeline, new Dictionary<string, string> { ["run_id"] = closed.RunId, ["after"] = "0" }, ct)).Value?
            .Select(t => Of(t, closed.WaitId)).LastOrDefault(a => a is not null);
    }

    async Task SendAsync(INotificationsProvider provider, string channel, Pending item, DateTimeOffset deadline, CancellationToken ct)
    {
        if ((await Records.QueryAsync<NotificationRecord>(Queries.Notification, new Dictionary<string, string> { ["key"] = item.Key }, ct)).Unwrap().Count > 0)
            return;
        var root = await RootOfAsync(item.RunId, ct);
        var thread = await ThreadAsync(root, channel, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromTicks(Math.Max(0, Math.Min(timeoutAfter.Ticks, (deadline - DateTimeOffset.UtcNow).Ticks))));
        ProviderResult<MessageRef> sent;
        try
        {
            // WaitAsync as well as the token: a provider that ignores its token cannot hold the record activity either.
            sent = await (item.Decision is { } decision
                ? provider.PostDecisionAsync(channel, decision, thread, timeout.Token)
                : provider.SendAsync(channel, item.Message!, thread, timeout.Token)).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sent = ProviderResult<MessageRef>.Failure("timeout", $"no answer within {timeoutAfter.TotalSeconds:0} s");
        }

        if (!sent.Ok)
        {
            logger.LogWarning("notifications: {Key} to {Channel} was not sent: {Reason}: {Message}; the run carries on", item.Key, channel, sent.Error!.Reason, sent.Error.Message);
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var records = new List<NotificationRecord> { new(item.Key, item.RunId, item.StepId, item.Event, sent.Value!, thread, now) };
        // A child's message that starts the thread starts it for the root run, so the root and its other children reply in it.
        if (thread is null && root != item.RunId)
            records.Add(new NotificationRecord(NotificationEvents.Key(root, null, 0, $"{ThreadEvent}:{channel}"), root, null, ThreadEvent, sent.Value!, null, now));
        (await Records.AppendAsync(new RecordBatch(Notifications: records), ct)).Unwrap();
    }

    /// <summary>The thread a root run's messages in <paramref name="channel"/> reply in: the first top-level message recorded for it there.</summary>
    async Task<MessageRef?> ThreadAsync(string root, string channel, CancellationToken ct) =>
        (await Records.QueryAsync<NotificationRecord>(Queries.Notifications, new Dictionary<string, string> { ["run_id"] = root }, ct)).Unwrap()
        .FirstOrDefault(n => n.Thread is null && n.Message.Channel == channel)?.Message;

    /// <summary>The root of a run's tree, by its records' parents.</summary>
    async Task<string> RootOfAsync(string runId, CancellationToken ct)
    {
        var id = runId;
        for (var depth = 0; depth < 32; depth++)
        {
            var run = (await Records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap().FirstOrDefault();
            if (run?.ParentRunId is not { Length: > 0 } parent)
                return id;
            id = parent;
        }

        return id;
    }
}
