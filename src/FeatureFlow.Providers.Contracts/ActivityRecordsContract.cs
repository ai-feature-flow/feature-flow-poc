using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The record store's contract for an agent step's live activity: an entry is kept once per run, step, execution, session
/// and sequence, so a re-read appends nothing; <c>activity</c> answers for one run and step, optionally one execution,
/// ordered by execution, session and sequence; and a batch of activity changes each of its steps once (<c>step_changed</c>,
/// with the step), and no run. A record store's test project subclasses it; each test uses its own ids, so it can run
/// against a shared database.
/// </summary>
public abstract class ActivityRecordsContract : ContractBase
{
    protected abstract IRecordStoreProvider Store { get; }

    readonly string unique = "ff-contract-" + Guid.NewGuid().ToString("N")[..12];
    static readonly DateTimeOffset At = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);

    string Id(string name) => $"{unique}-{name}";

    Task<ProviderResult<Unit>> Append(RecordBatch batch) => Store.AppendAsync(batch, Ct);

    async Task<IReadOnlyList<T>> Query<T>(string query, params (string Key, string Value)[] parameters) =>
        Ok(await Store.QueryAsync<T>(query, parameters.ToDictionary(p => p.Key, p => p.Value), Ct));

    ActivityRecord Entry(string run, string step, int execution, int session, int sequence, string kind = ActivityKinds.Message, string? text = "Looking") =>
        new(Id(run), step, execution, session, sequence, kind, null, null, text, null, false, At.AddSeconds(sequence));

    [Fact]
    public async Task Activity_reads_back_whole_ordered_by_execution_session_and_sequence_for_its_own_run_and_step()
    {
        var tool = new ActivityRecord(Id("run"), "implement", 1, 1, 4, ActivityKinds.ToolFinished, "call-1", "bash", null, "one\n", true, At);
        Ok(await Append(new RecordBatch(Activity: [Entry("run", "implement", 2, 1, 1), Entry("run", "implement", 1, 2, 1), tool,
            Entry("run", "implement", 1, 1, 2), Entry("run", "review", 1, 1, 1), Entry("other", "implement", 1, 1, 1)])));
        Ok(await Append(new RecordBatch(Activity: [Entry("run", "implement", 1, 1, int.MaxValue, ActivityKinds.Truncated, null)])));

        var all = await Query<ActivityRecord>(Queries.Activity, ("run_id", Id("run")), ("step_id", "implement"));

        Assert.Equal([(1, 1, 2), (1, 1, 4), (1, 1, int.MaxValue), (1, 2, 1), (2, 1, 1)], all.Select(a => (a.Execution, a.Session, a.Sequence)));
        Assert.Equal(tool, all[1]);
        Assert.Equal([(2, 1, 1)], (await Query<ActivityRecord>(Queries.Activity, ("run_id", Id("run")), ("step_id", "implement"), ("execution", "2")))
            .Select(a => (a.Execution, a.Session, a.Sequence)));
    }

    [Fact]
    public async Task An_activity_entry_is_kept_once_per_key_so_a_reread_adds_nothing()
    {
        Ok(await Append(new RecordBatch(Activity: [Entry("run", "implement", 1, 1, 1, text: "first"), Entry("run", "implement", 1, 1, 2)])));
        Ok(await Append(new RecordBatch(Activity: [Entry("run", "implement", 1, 1, 1, text: "again"), Entry("run", "implement", 1, 1, 2), Entry("run", "implement", 1, 1, 3)])));

        var all = await Query<ActivityRecord>(Queries.Activity, ("run_id", Id("run")), ("step_id", "implement"));

        Assert.Equal([(1, "first"), (2, "Looking"), (3, "Looking")], all.Select(a => (a.Sequence, a.Text)));
    }

    [Fact]
    public async Task Activity_changes_each_of_its_steps_once_and_no_run()
    {
        var before = (await Query<ChangeRecord>(Queries.LatestChange)).SingleOrDefault()?.Sequence ?? 0;

        Ok(await Append(new RecordBatch(Activity: [Entry("a", "implement", 1, 1, 1), Entry("a", "implement", 1, 1, 2), Entry("a", "review", 1, 1, 1),
            Entry("b", "implement", 1, 1, 1)])));

        var changes = (await Query<ChangeRecord>(Queries.Changes, ("after", before.ToString(System.Globalization.CultureInfo.InvariantCulture))))
            .Where(c => c.RunId.StartsWith(unique, StringComparison.Ordinal));
        Assert.Equal(
            [(Id("a"), ChangeKinds.StepChanged, "implement", (string?)null), (Id("a"), ChangeKinds.StepChanged, "review", null), (Id("b"), ChangeKinds.StepChanged, "implement", null)],
            changes.Select(c => (c.RunId, c.Kind, c.StepId, c.WaitId)));
    }
}
