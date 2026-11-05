using System.Text.Json;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Tests;
using Npgsql;

[assembly: AssemblyFixture(typeof(FeatureFlow.Providers.Postgres.Tests.PostgresServer))]

namespace FeatureFlow.Providers.Postgres.Tests;

/// <summary>One Postgres in a container for the whole assembly; each test uses ids of its own (LOG 274).</summary>
public sealed class PostgresServer : IAsyncLifetime
{
    public const string Image = "postgres:16-alpine";

    readonly string password = "ff-" + Guid.NewGuid().ToString("N")[..16];
    Container? container;

    public string ConnectionString { get; private set; } = "";

    public string WithPassword(string other) => new NpgsqlConnectionStringBuilder(ConnectionString) { Password = other }.ToString();

    public async ValueTask InitializeAsync()
    {
        container = await Container.StartAsync(Image, 5432, ["-e", "POSTGRES_PASSWORD=" + password, "-e", "POSTGRES_DB=ff", "--tmpfs", "/var/lib/postgresql/data"]);
        ConnectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = container.Port, Database = "ff", Username = "postgres", Password = password, Timeout = 5,
        }.ToString();
        // The image initialises the database with a server on its socket only, then starts the real one on TCP.
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(ConnectionString);
                await connection.OpenAsync();
                return;
            }
            catch (NpgsqlException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
            await container.DisposeAsync();
    }

    public PostgresRecordStore Store(string? connectionString = null) =>
        new(_ => Task.FromResult(ProviderResult<string>.Success(connectionString ?? ConnectionString)));
}

[Trait("Category", "Integration")]
public sealed class PostgresRecordStoreContract(PostgresServer server) : RecordStoreContract, IAsyncDisposable
{
    readonly PostgresRecordStore store = server.Store();

    protected override IRecordStoreProvider Store => store;

    public ValueTask DisposeAsync() => store.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgresFindingsLedgerContract(PostgresServer server) : FindingsLedgerContract, IAsyncDisposable
{
    readonly PostgresRecordStore store = server.Store();

    protected override IRecordStoreProvider Store => store;

    public ValueTask DisposeAsync() => store.DisposeAsync();
}

[Trait("Category", "Integration")]
public sealed class PostgresSteeringRecordsContract(PostgresServer server) : SteeringRecordsContract, IAsyncDisposable
{
    readonly PostgresRecordStore store = server.Store();

    protected override IRecordStoreProvider Store => store;

    public ValueTask DisposeAsync() => store.DisposeAsync();
}

/// <summary>What the contract suites do not cover: the connection secret, the schema, and how failures are classified.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresRecordStoreTests(PostgresServer server) : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-pg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    /// <summary>The store as an installation configures it: <c>connection_ref</c> names a secret the secrets provider holds.</summary>
    IRecordStoreProvider Configured(string connectionString)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), JsonSerializer.Serialize(new Dictionary<string, string> { ["ff_db"] = connectionString }));
        var secrets = new FileSecrets(Path.Combine(folder, "secrets.json"));
        var settings = (YMap)YamlText.Parse("connection_ref: secrets/ff_db", "installation.yaml", new DiagnosticBag())!;
        var factory = Assert.Single(PostgresProviders.Factories);
        return (IRecordStoreProvider)factory.Create(new ProviderContext(new ProviderInstanceConfig("record_store", "postgres", "postgres", settings), folder,
            type => type == "secrets" ? secrets : null));
    }

    [Fact]
    public async Task The_connection_string_comes_from_the_secret_the_installation_names()
    {
        var store = Configured(server.ConnectionString);
        var id = "pg-secret-" + Guid.NewGuid().ToString("N");

        Assert.True((await store.CheckAsync(Ct)).Ok);
        Assert.True((await store.AppendAsync(new RecordBatch([new RunRecord(id, null, "std:templates/t", "running", "me", [], false, DateTimeOffset.UtcNow, null)]), Ct)).Ok);
        Assert.Single((await store.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = id }, Ct)).Unwrap());
    }

    [Fact]
    public async Task A_wrong_password_is_unauthenticated_and_an_unreachable_server_unreachable()
    {
        var wrong = await Configured(server.WithPassword("not-the-password")).CheckAsync(Ct);
        var closed = await server.Store(new NpgsqlConnectionStringBuilder(server.ConnectionString) { Port = 1, Timeout = 2 }.ToString()).CheckAsync(Ct);

        Assert.Equal("unauthenticated", wrong.Error?.Reason);
        Assert.Equal("unreachable", closed.Error?.Reason);
    }

    [Fact]
    public async Task A_missing_secret_is_the_secrets_providers_not_found()
    {
        var store = Configured(server.ConnectionString);
        File.WriteAllText(Path.Combine(folder, "secrets.json"), "{}");

        Assert.Equal("not_found", (await store.CheckAsync(Ct)).Error?.Reason);
    }

    [Fact]
    public async Task The_schema_is_created_once_and_every_table_is_keyed_by_organisation()
    {
        var database = await NewDatabaseAsync();
        var stores = Enumerable.Range(0, 4).Select(_ => server.Store(database)).ToList();

        // Hosts starting together: one applies the script, the rest find it applied.
        var checks = await Task.WhenAll(stores.Select(s => s.CheckAsync(Ct)));

        Assert.All(checks, c => Assert.True(c.Ok, c.Error?.Message));
        Assert.Equal([1], await ColumnAsync<int>(database, "SELECT version FROM ff_schema"));
        var tables = await ColumnAsync<string>(database, "SELECT table_name::text FROM information_schema.tables WHERE table_schema = 'public' AND table_name <> 'ff_schema'");
        var keyed = await ColumnAsync<string>(database, "SELECT table_name::text FROM information_schema.columns WHERE table_schema = 'public' AND column_name = 'org_id'");
        Assert.Equal(14, tables.Count);
        Assert.Equal(tables.Order(), keyed.Order());
        foreach (var s in stores)
            await s.DisposeAsync();
    }

    [Fact]
    public async Task A_schema_newer_than_this_build_is_refused()
    {
        var database = await NewDatabaseAsync();
        await using (var connection = new NpgsqlConnection(database))
        {
            await connection.OpenAsync(Ct);
            await new NpgsqlCommand("CREATE TABLE ff_schema (version integer NOT NULL); INSERT INTO ff_schema VALUES (99)", connection).ExecuteNonQueryAsync(Ct);
        }

        await using var store = server.Store(database);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => store.CheckAsync(Ct));
        Assert.Contains("version 99", refused.Message);
    }

    async Task<string> NewDatabaseAsync()
    {
        var name = "ff_" + Guid.NewGuid().ToString("N")[..12];
        await using (var connection = new NpgsqlConnection(server.ConnectionString))
        {
            await connection.OpenAsync(Ct);
            await new NpgsqlCommand($"CREATE DATABASE {name}", connection).ExecuteNonQueryAsync(Ct);
        }

        return new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = name }.ToString();
    }

    async Task<List<T>> ColumnAsync<T>(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(Ct);
        await using var reader = await new NpgsqlCommand(sql, connection).ExecuteReaderAsync(Ct);
        var list = new List<T>();
        while (await reader.ReadAsync(Ct))
            list.Add(reader.GetFieldValue<T>(0));
        return list;
    }
}
