using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.Local.Tests;

/// <summary>
/// The findings ledger contract, with the artifact index it shares a suite with, against the SQLite record store (it
/// replaces the shell builder's interim SQLite artifact test).
/// </summary>
public sealed class SqliteFindingsLedgerContract : FindingsLedgerContract, IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-ledger-" + Guid.NewGuid().ToString("N"));

    public SqliteFindingsLedgerContract() => Store = new SqliteRecordStore(Path.Combine(folder, "records.db"));

    protected override IRecordStoreProvider Store { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
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
