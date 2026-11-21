using FeatureFlow.Providers.Postgres;
using FeatureFlow.Providers.Tests;
using Npgsql;

namespace FeatureFlow.Api.Tests;

/// <summary><c>GET /events</c> over a Postgres record store (docs/06 section 3.12): the change log read as the SQLite one is.</summary>
[Trait("Category", "Integration")]
public sealed class PostgresEventsTests : IAsyncLifetime
{
    Container? container;
    string connectionString = "";

    public async ValueTask InitializeAsync()
    {
        var password = "ff-" + Guid.NewGuid().ToString("N")[..16];
        container = await Container.StartAsync("postgres:16-alpine", 5432, ["-e", "POSTGRES_PASSWORD=" + password, "-e", "POSTGRES_DB=ff", "--tmpfs", "/var/lib/postgresql/data"]);
        connectionString = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = container.Port, Database = "ff", Username = "postgres", Password = password, Timeout = 5 }.ToString();
        // The image initialises the database with a server on its socket only, then starts the real one on TCP.
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (true)
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
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

    [Fact]
    public async Task A_wait_opening_is_sent_to_a_viewer_of_its_run_and_not_to_others()
    {
        await using var store = new PostgresRecordStore(_ => Task.FromResult(Providers.Abstractions.ProviderResult<string>.Success(connectionString)));
        await using var world = new ApiWorld(EventsTests.TwoTeams(), records: store);
        await EventsTests.A_wait_opening_reaches_its_runs_viewers_only(world, "postgres");
    }
}
