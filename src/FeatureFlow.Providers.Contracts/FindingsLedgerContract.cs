using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>
/// The record store's contract for the findings ledger and the artifact index (docs/02 section 5, docs/01 section 6):
/// finding records are kept per run and id with the last write winning; a run's view includes its descendants' ledgers
/// and artifacts, never its parent's or a sibling's; finding events keep their order and are appended once per key, as
/// artifacts are once per run, step, execution and name. A record store's test project subclasses it with a fresh store.
/// </summary>
public abstract class FindingsLedgerContract : ContractBase
{
    protected abstract IRecordStoreProvider Store { get; }

    readonly string unique = "ff-contract-" + Guid.NewGuid().ToString("N")[..12];
    static readonly DateTimeOffset At = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    string Run(string name) => $"{unique}-{name}";

    Task<ProviderResult<Unit>> Append(RecordBatch batch) => Store.AppendAsync(batch, Ct);

    async Task<IReadOnlyList<T>> Query<T>(string query, params (string Key, string Value)[] parameters) =>
        Ok(await Store.QueryAsync<T>(query, parameters.ToDictionary(p => p.Key, p => p.Value), Ct));

    /// <summary>root has children a and b; a has a child a1.</summary>
    async Task TreeAsync()
    {
        RunRecord R(string id, string? parent) => new(Run(id), parent is null ? null : Run(parent), "std:templates/epic_delivery", "running", "someone", [], false, At, null);
        Ok(await Append(new RecordBatch(Runs: [R("root", null), R("a", "root"), R("b", "root"), R("a1", "a")])));
    }

    static ReportedFinding Reported(string rule, string severity = "high") =>
        new(rule, $"Title {rule}", "Description", severity, true, FindingBases.Reasoned, new FindingLocation("acme/api", "src/Orders.cs", "Orders.Add", "if (x) {"));

    FindingRecord Finding(string run, string rule, string state = FindingStates.Open, string severity = "high")
    {
        var reported = Reported(rule, severity);
        var fingerprint = Fingerprints.Of(reported);
        return new FindingRecord(Run(run), Fingerprints.LedgerId(Run(run), fingerprint), fingerprint, reported, "review", "a-model", state, "rev1", "rev1", 0, 0, 0,
            false, At);
    }

    FindingEventRecord Event(FindingRecord f, string key, string kind, string state) =>
        new(f.RunId, f.FindingId, key, kind, state, "a-step", "because", "review", "rev1", null, At);

    [Fact]
    public async Task A_finding_written_again_keeps_the_last_write()
    {
        await TreeAsync();
        var open = Finding("root", "R1");

        Ok(await Append(new RecordBatch(Findings: [open])));
        Ok(await Append(new RecordBatch(Findings: [open with { State = FindingStates.Fixed, Fixes = 1, StateRevision = "rev2" }])));

        var stored = Assert.Single(await Query<FindingRecord>(Queries.Findings, ("run_id", Run("root"))));
        Assert.Equal((open.FindingId, FindingStates.Fixed, 1, "rev2"), (stored.FindingId, stored.State, stored.Fixes, stored.StateRevision));
        Assert.Equal(open.Finding.Location, stored.Finding.Location);
        Assert.Equal(open.Finding.Rule, stored.Finding.Rule);
    }

    [Fact]
    public async Task A_runs_view_holds_its_descendants_findings_and_not_its_parents_or_siblings()
    {
        await TreeAsync();
        Ok(await Append(new RecordBatch(Findings: [Finding("root", "R1"), Finding("a", "R2"), Finding("a1", "R3"), Finding("b", "R4")])));

        var fromRoot = await Query<FindingRecord>(Queries.Findings, ("run_id", Run("root")));
        var fromA = await Query<FindingRecord>(Queries.Findings, ("run_id", Run("a")));
        var fromA1 = await Query<FindingRecord>(Queries.Findings, ("run_id", Run("a1")));

        Assert.Equal(["R1", "R2", "R3", "R4"], fromRoot.Select(f => f.Finding.Rule).Order());
        Assert.Equal(["R2", "R3"], fromA.Select(f => f.Finding.Rule).Order());
        Assert.Equal(["R3"], fromA1.Select(f => f.Finding.Rule));
        Assert.Equal(fromRoot.OrderBy(f => f.RunId, StringComparer.Ordinal).ThenBy(f => f.FindingId, StringComparer.Ordinal).Select(f => f.FindingId),
            fromRoot.Select(f => f.FindingId));
    }

    [Fact]
    public async Task The_same_fingerprint_in_two_runs_is_two_findings()
    {
        await TreeAsync();
        var inA = Finding("a", "R1");
        var inB = Finding("b", "R1");

        Ok(await Append(new RecordBatch(Findings: [inA, inB])));

        Assert.Equal(inA.Fingerprint, inB.Fingerprint);
        Assert.NotEqual(inA.FindingId, inB.FindingId);
        Assert.Equal(2, (await Query<FindingRecord>(Queries.Findings, ("run_id", Run("root")))).Count);
    }

    [Fact]
    public async Task A_findings_history_keeps_its_order_and_a_repeated_key_adds_nothing()
    {
        await TreeAsync();
        var f = Finding("a", "R1");
        Ok(await Append(new RecordBatch(Findings: [f], FindingEvents: [Event(f, "k1", FindingEventKinds.Raised, FindingStates.Open)])));
        Ok(await Append(new RecordBatch(FindingEvents: [Event(f, "k2", FindingEventKinds.Triaged, FindingStates.Fixed)])));
        Ok(await Append(new RecordBatch(FindingEvents: [Event(f, "k1", FindingEventKinds.Raised, FindingStates.Open)])));
        Ok(await Append(new RecordBatch(FindingEvents: [Event(f, "k3", FindingEventKinds.Reraised, FindingStates.Open) with { CodeChanged = true }])));

        var history = await Query<FindingEventRecord>(Queries.FindingEvents, ("run_id", f.RunId), ("finding_id", f.FindingId));

        Assert.Equal(["k1", "k2", "k3"], history.Select(e => e.Key));
        Assert.Equal([FindingStates.Open, FindingStates.Fixed, FindingStates.Open], history.Select(e => e.State));
        Assert.Equal(("a-step", "because", true), (history[2].Actor, history[2].Reason, history[2].CodeChanged));
        Assert.Empty(await Query<FindingEventRecord>(Queries.FindingEvents, ("run_id", Run("b")), ("finding_id", f.FindingId)));
    }

    [Fact]
    public async Task A_runs_artifacts_include_its_descendants_and_are_kept_once_per_step_execution_and_name()
    {
        await TreeAsync();
        ArtifactRecord A(string run, string step, int execution, string name, string reference) =>
            new(Run(run), step, execution, name, "std:artifact_types/test_results", reference, "test.run", true, At);
        Ok(await Append(new RecordBatch(Artifacts: [A("a", "tests", 1, "test_results", "sha256:1"), A("a1", "tests", 1, "test_results", "sha256:2")])));
        Ok(await Append(new RecordBatch(Artifacts: [A("a", "tests", 1, "test_results", "sha256:1"), A("a", "tests", 2, "test_results", "sha256:3")])));
        Ok(await Append(new RecordBatch(Artifacts: [A("b", "tests", 1, "test_results", "sha256:4")])));

        var fromA = await Query<ArtifactRecord>(Queries.Artifacts, ("run_id", Run("a")));

        Assert.Equal(["sha256:1", "sha256:2", "sha256:3"], fromA.Select(r => r.Ref).Order());
        Assert.Equal(["sha256:1", "sha256:3"], fromA.Where(r => r.RunId == Run("a")).Select(r => r.Ref));
        Assert.All(fromA, r => Assert.True(r.Evidence));
        Assert.Equal(4, (await Query<ArtifactRecord>(Queries.Artifacts, ("run_id", Run("root")))).Count);
    }
}
