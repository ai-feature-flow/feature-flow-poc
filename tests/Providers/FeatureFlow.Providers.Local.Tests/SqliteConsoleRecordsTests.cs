using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.Local.Tests;

/// <summary>The console's B1 records (step states, pull requests, repositories, recent answers) against the SQLite record store.</summary>
public sealed class SqliteConsoleRecordsContract : ConsoleRecordsContract, IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-console-" + Guid.NewGuid().ToString("N"));

    public SqliteConsoleRecordsContract() => Store = new SqliteRecordStore(Path.Combine(folder, "records.db"));

    protected override IRecordStoreProvider Store { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
}
