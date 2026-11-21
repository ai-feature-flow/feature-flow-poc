using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// Reminders on a Temporal dev server (F21; docs/04 section 4): a run whose organisation sets a reminder interval reminds
/// its open decision once per interval, numbered, each sent once as a <c>reminder</c>, and stops when it is answered. The
/// interval is 2 s, below the 1 m configuration accepts, set on the loaded configuration so the test waits seconds in real
/// time; Temporal's time-skipping environment is not used, since the harness runs a host against the dev server.
/// </summary>
[Trait("Category", "Integration")]
public class ReminderTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly string[] Events = [NotificationEvents.DecisionNeeded, NotificationEvents.Reminder, NotificationEvents.Resumed];

    static async Task<List<(string Kind, string Step, string? Detail)>> TimelineAsync(Harness h, string id) =>
        (await h.ShowAsync(id))["timeline"]!.AsArray().Select(e => (e!["kind"]!.ToString(), e["step_id"]!.ToString(), e["detail"]?.ToString())).ToList();

    [Fact]
    public async Task An_open_decision_is_reminded_once_per_interval_until_it_is_answered()
    {
        var name = "reminder";
        await using var h = await Harness.StartAsync(name, without: ["notifications"], libraries: [Path.Combine(Harness.TestLibraries, "m7s")],
            providers: $"  notifications: {{ instances: {{ memory: {{ world: {name}-{Guid.NewGuid():N} }} }} }}",
            organisation: "notifications: { routing: { " + string.Join(", ", Events.Select(e => e + ": { channel: \"#ff\" }")) + " }, events: [" + string.Join(", ", Events) + "] }",
            configure: c => c with
            {
                Organisation = c.Organisation with { Notifications = c.Organisation.Notifications with { ReminderInterval = TimeSpan.FromSeconds(2) } },
            });
        var id = h.Start("m7s:templates/decide");
        var waitId = await h.WaitForWaitAsync(id, TimeSpan.FromSeconds(60));
        Assert.NotNull(waitId);

        var until = DateTime.UtcNow.AddSeconds(60);
        while ((await TimelineAsync(h, id)).Count(e => e.Kind == "reminded") < 2)
        {
            Assert.True(DateTime.UtcNow < until, "no second reminder within 60 s: " + (await h.ShowAsync(id)).ToJsonString());
            await Task.Delay(200, Ct);
        }

        var (exit, _, error) = h.Cli("answer", id, waitId!, "approved");
        Assert.True(exit == 0, error);
        var (endExit, end, _) = h.Cli("wait", id, "--timeout", "60");
        Assert.True((endExit, end.Trim()) == (0, $"run {id} completed"), (await h.ShowAsync(id)).ToJsonString());
        // Two more intervals: a reminder armed before the answer finds the wait closed.
        await Task.Delay(TimeSpan.FromSeconds(4), Ct);

        var timeline = await TimelineAsync(h, id);
        var reminded = timeline.Where(e => e.Kind == "reminded").ToList();
        Assert.All(reminded, e => Assert.Equal(waitId, e.Step));
        Assert.Equal(Enumerable.Range(1, reminded.Count).Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)), reminded.Select(e => e.Detail));
        var answered = timeline.FindIndex(e => e.Kind == "answered" && e.Step == waitId);
        Assert.True(answered >= 0 && timeline.FindLastIndex(e => e.Kind == "reminded") < answered, string.Join("\n", timeline));

        var sent = (await h.Host.Providers.Required<IRecordStoreProvider>("record_store").QueryAsync<NotificationRecord>(Queries.Notifications,
            new Dictionary<string, string> { ["run_id"] = id }, Ct)).Unwrap().Where(n => n.Event == NotificationEvents.Reminder).Select(n => n.Key).ToList();
        Assert.Contains(NotificationEvents.Key(id, waitId, 1, NotificationEvents.Reminder), sent);
        Assert.Contains(NotificationEvents.Key(id, waitId, 2, NotificationEvents.Reminder), sent);
        Assert.Equal(sent.Distinct().Count(), sent.Count);
        Assert.Empty(await h.WorkflowTaskFailuresAsync(id));
        await h.RecordHistoryAsync(id, "reminder");
    }
}
