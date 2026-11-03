using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.Local.Tests;

/// <summary>
/// The findings ledger contract against the SQLite record store. The artifact index is the shell builder's (W1) and not
/// yet in the SQLite store on this branch, so its records and query go to the in-memory store, which shares the run tree;
/// when the SQLite store holds the artifact index, <see cref="Store"/> becomes the SQLite store alone.
/// </summary>
public sealed class SqliteFindingsLedgerContract : FindingsLedgerContract, IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-ledger-" + Guid.NewGuid().ToString("N"));

    public SqliteFindingsLedgerContract()
    {
        Sqlite = new SqliteRecordStore(Path.Combine(folder, "records.db"));
        Store = new LedgerOnly(Sqlite, new InMemoryRecordStore());
    }

    SqliteRecordStore Sqlite { get; }

    protected override IRecordStoreProvider Store { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    sealed class LedgerOnly(IRecordStoreProvider sqlite, IRecordStoreProvider artifacts) : IRecordStoreProvider
    {
        public ProviderDescription Describe() => sqlite.Describe();

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => sqlite.CheckAsync(cancellationToken);

        public async Task<ProviderResult<Unit>> AppendAsync(RecordBatch records, CancellationToken cancellationToken)
        {
            if (records.Artifacts is { Count: > 0 } || records.Runs is { Count: > 0 })
                (await artifacts.AppendAsync(new RecordBatch(Runs: records.Runs, Artifacts: records.Artifacts), cancellationToken)).Unwrap();
            return await sqlite.AppendAsync(records with { Artifacts = null }, cancellationToken);
        }

        public Task<ProviderResult<IReadOnlyList<T>>> QueryAsync<T>(string queryName, IReadOnlyDictionary<string, string> parameters, CancellationToken cancellationToken) =>
            (queryName == Queries.Artifacts ? artifacts : sqlite).QueryAsync<T>(queryName, parameters, cancellationToken);
    }
}

/// <summary>The SQLite ledger beyond the contract: it survives the store being opened again, as a worker restart opens it.</summary>
public sealed class SqliteLedgerTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-ledger-" + Guid.NewGuid().ToString("N"));

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task The_ledger_is_read_back_by_a_store_opened_again()
    {
        var path = Path.Combine(folder, "records.db");
        var at = DateTimeOffset.UnixEpoch;
        var reported = new ReportedFinding("R1", "Title", "Description", "high", true, FindingBases.Demonstrated,
            new FindingLocation("acme/api", "src/A.cs", "A.B", "x = 1;"), "Do y", "Test T fails");
        var fingerprint = Fingerprints.Of(reported);
        var finding = new FindingRecord("run-1", Fingerprints.LedgerId("run-1", fingerprint), fingerprint, reported, "review", null, FindingStates.Open,
            "rev1", "rev1", 0, 0, 0, false, at);
        (await new SqliteRecordStore(path).AppendAsync(new RecordBatch(
            Runs: [new RunRecord("run-1", null, "t", "running", "me", [], false, at, null)],
            Findings: [finding],
            FindingEvents: [new FindingEventRecord("run-1", finding.FindingId, "k", FindingEventKinds.Raised, FindingStates.Open, "review", null, "review", "rev1", null, at)]), Ct)).Unwrap();

        var again = new SqliteRecordStore(path);
        var stored = Assert.Single((await again.QueryAsync<FindingRecord>(Queries.Findings, new Dictionary<string, string> { ["run_id"] = "run-1" }, Ct)).Unwrap());
        var history = (await again.QueryAsync<FindingEventRecord>(Queries.FindingEvents,
            new Dictionary<string, string> { ["run_id"] = "run-1", ["finding_id"] = finding.FindingId }, Ct)).Unwrap();

        Assert.Equal(finding with { Finding = stored.Finding }, stored);
        Assert.Equal(reported, stored.Finding);
        Assert.Equal(FindingEventKinds.Raised, Assert.Single(history).Kind);
    }
}
