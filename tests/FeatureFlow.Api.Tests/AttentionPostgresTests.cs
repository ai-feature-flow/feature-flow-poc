using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Postgres;
using FeatureFlow.Providers.Tests;
using Npgsql;

namespace FeatureFlow.Api.Tests;

/// <summary><c>GET /attention</c> over the Postgres record store (the hosted profile): its open-wait query behind the same route. Starts its own Postgres container.</summary>
[Trait("Category", "Integration")]
public sealed class AttentionPostgresTests : IAsyncLifetime
{
    Container? container;
    PostgresRecordStore? store;

    public async ValueTask InitializeAsync()
    {
        var password = "ff-" + Guid.NewGuid().ToString("N")[..16];
        container = await Container.StartAsync("postgres:16-alpine", 5432, ["-e", "POSTGRES_PASSWORD=" + password, "-e", "POSTGRES_DB=ff", "--tmpfs", "/var/lib/postgresql/data"]);
        var connectionString = new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = container.Port, Database = "ff", Username = "postgres", Password = password, Timeout = 5 }.ToString();
        // The image initialises the database with a server on its socket only, then starts the real one on TCP.
        for (var deadline = DateTime.UtcNow.AddMinutes(1); ; await Task.Delay(500))
        {
            try
            {
                await using var connection = new NpgsqlConnection(connectionString);
                await connection.OpenAsync();
                break;
            }
            catch (NpgsqlException) when (DateTime.UtcNow < deadline)
            {
            }
        }

        store = new PostgresRecordStore(_ => Task.FromResult(ProviderResult<string>.Success(connectionString)));
    }

    public async ValueTask DisposeAsync()
    {
        if (store is not null)
            await store.DisposeAsync();
        if (container is not null)
            await container.DisposeAsync();
    }

    [Fact]
    public async Task Attention_over_postgres_lists_an_open_gate_of_a_running_run_for_an_approver_only()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var world = new ApiWorld(new OrganisationConfig("acme", [], new PolicyConfig([new ApprovalRule("*", ["approver"], 1, false, new Cap<bool>(false, false))], null, null, null, null), ""),
            records: store);
        var now = DateTimeOffset.UtcNow;
        RunRecord Run(string id, string status) => new(id, null, "std:templates/ticket_delivery", status, "x", ["acme/api"], false, now, null);
        WaitRecord Gate(string run, string id, bool open, int minutes) => new(run, id, "gate", "spec", "ticket.spec_approval", "Approve?", ["approved"], open, now.AddMinutes(minutes));
        await store!.AppendAsync(new RecordBatch([Run("r1", "running"), Run("ended", "failed")],
            [Gate("r1", "gate-2", true, 2), Gate("r1", "gate-1", true, 1), Gate("r1", "gate-0", false, 0), Gate("ended", "gate-1", true, 0)]), ct);

        using var response = await world.As("ann", "approver").GetAsync("attention", ct);
        var list = JsonSerializer.Deserialize<AttentionList>(await response.Content.ReadAsStringAsync(ct), ApiJson.Options)!;
        using var viewer = await world.As("vic", "viewer").GetAsync("attention", ct);

        Assert.Equal([("r1", "gate-1"), ("r1", "gate-2")], list.Waits.Select(i => (i.Run.RunId, i.Wait.WaitId)));
        Assert.Empty(JsonSerializer.Deserialize<AttentionList>(await viewer.Content.ReadAsStringAsync(ct), ApiJson.Options)!.Waits);
    }
}
