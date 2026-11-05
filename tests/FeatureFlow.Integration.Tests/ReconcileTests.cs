using FeatureFlow.Hosting;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Microsoft.Data.Sqlite;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// The reconcile job (docs/02 section 5, recovery): with run records deleted, as after a database restored from an older
/// backup, the <c>reconcile</c> command rebuilds them from Temporal; a run terminated outside the engine, whose record
/// still says it is running, becomes <c>incomplete</c>; and a second pass writes nothing.
/// </summary>
[Trait("Category", "Integration")]
public class ReconcileTests
{
    static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    [Fact]
    public async Task Deleted_run_records_are_rebuilt_from_temporal_and_a_second_pass_writes_nothing()
    {
        await using var h = await Harness.StartAsync("reconcile");
        var completed = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-1", "--repo", "acme/api");
        var approval = await h.WaitForWaitAsync(completed, Timeout);
        Assert.True(h.Cli("answer", completed, approval!, "approved").Exit == 0);
        Assert.True(h.Cli("wait", completed, "--timeout", "120").Exit == 0);
        var open = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-2", "--repo", "acme/web");
        Assert.NotNull(await h.WaitForWaitAsync(open, Timeout));
        var terminated = h.Start("std:templates/ticket_delivery", "--param", "ticket_id=FF-3");
        Assert.NotNull(await h.WaitForWaitAsync(terminated, Timeout));
        await h.Client.GetWorkflowHandle(terminated).TerminateAsync("an operator ended it outside the engine");

        var before = new Dictionary<string, RunRecord>();
        foreach (var id in new[] { completed, open, terminated })
            before[id] = await RecordAsync(h, id) ?? throw new InvalidOperationException($"{id} has no record");
        Assert.Equal(("completed", "running", "running"), (before[completed].Status, before[open].Status, before[terminated].Status));
        await DeleteRunRecordsAsync(h, completed, open);
        await VisibleAsync(h, 3);
        Assert.Null(await RecordAsync(h, completed));

        var output = new StringWriter();
        Assert.Equal(0, await HostArguments.ReconcileAsync(h.Host.Configuration, LocalProviders.Factories, output));

        // Only this installation's task queue is read: the suite's other harnesses share the server.
        Assert.StartsWith($"reconcile: 3 runs in Temporal on {h.Host.Configuration.Installation.TaskQueue}, 3 records rebuilt", output.ToString());
        foreach (var (id, status) in new[] { (completed, "completed"), (open, "running"), (terminated, "incomplete") })
        {
            var rebuilt = await RecordAsync(h, id);
            Assert.Equal(before[id] with { Status = status, UpdatedAt = rebuilt!.UpdatedAt, Detail = RunIndex.Detail, Repositories = rebuilt.Repositories }, rebuilt);
            Assert.Equal(before[id].Repositories, rebuilt.Repositories);
        }

        var again = new StringWriter();
        await HostArguments.ReconcileAsync(h.Host.Configuration, LocalProviders.Factories, again);
        Assert.StartsWith($"reconcile: 3 runs in Temporal on {h.Host.Configuration.Installation.TaskQueue}, 0 records rebuilt", again.ToString());
    }

    /// <summary>Waits until Temporal's list (eventually consistent) shows the queue's runs, the terminated one closed: reconcile reads that list.</summary>
    static async Task VisibleAsync(Harness h, int count)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (true)
        {
            var listed = new List<Temporalio.Client.WorkflowExecution>();
            await foreach (var e in h.Client.ListWorkflowsAsync($"TaskQueue = '{h.Host.Configuration.Installation.TaskQueue}'"))
                listed.Add(e);
            if (listed.Count == count && listed.Count(e => e.Status == Temporalio.Api.Enums.V1.WorkflowExecutionStatus.Terminated) == 1)
                return;
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"Temporal lists {listed.Count} runs on the queue, not {count}");
            await Task.Delay(200);
        }
    }

    static async Task<RunRecord?> RecordAsync(Harness h, string id) =>
        (await h.Host.Providers.Required<IRecordStoreProvider>("record_store")
            .QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = id }, TestContext.Current.CancellationToken)).Unwrap().SingleOrDefault();

    static async Task DeleteRunRecordsAsync(Harness h, params string[] ids)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(h.Folder, "records.db")};Pooling=false");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        foreach (var id in ids)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM runs WHERE run_id = $r";
            command.Parameters.AddWithValue("$r", id);
            Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        }
    }
}
