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

/// <summary>The SQLite store's index for <c>open_waits</c>: on open waits only, as Postgres's is, so closed waits do not fill it.</summary>
public sealed class SqliteOpenWaitsIndexTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-open-waits-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void The_open_waits_index_is_partial_on_open()
    {
        var path = Path.Combine(folder, "records.db");
        _ = new SqliteRecordStore(path);
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND name = 'waits_open'";

        Assert.Equal("CREATE INDEX waits_open ON waits (org_id) WHERE open = 1", command.ExecuteScalar() as string);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
}
