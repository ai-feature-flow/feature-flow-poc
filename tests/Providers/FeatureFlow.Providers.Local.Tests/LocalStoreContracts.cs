using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;

namespace FeatureFlow.Providers.Local.Tests;

/// <summary>The records every run writes against the SQLite record store (the solo profile).</summary>
public sealed class SqliteRecordStoreContract : RecordStoreContract, IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-records-" + Guid.NewGuid().ToString("N"));

    public SqliteRecordStoreContract() => Store = new SqliteRecordStore(Path.Combine(folder, "records.db"));

    protected override IRecordStoreProvider Store { get; }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
}

/// <summary>The artifact store on the local filesystem (the solo profile).</summary>
public sealed class FileSystemArtifactStoreContract : ArtifactStoreContract, IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-artifacts-" + Guid.NewGuid().ToString("N"));

    public FileSystemArtifactStoreContract() => Store = new FileSystemArtifactStore(folder);

    protected override IArtifactStoreProvider Store { get; }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }
}
