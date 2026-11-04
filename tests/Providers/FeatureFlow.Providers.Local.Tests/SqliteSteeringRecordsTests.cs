using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.Local.Tests;

/// <summary>M7's records (overrides, notification de-duplication, sessions) against the SQLite record store.</summary>
public sealed class SqliteSteeringRecordsContract : SteeringRecordsContract, IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-steering-" + Guid.NewGuid().ToString("N"));

    public SqliteSteeringRecordsContract() => Store = new SqliteRecordStore(Path.Combine(folder, "records.db"));

    protected override IRecordStoreProvider Store { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
}
