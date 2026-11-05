using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Converters;

namespace FeatureFlow.Hosting;

/// <summary>What one reconcile did: the runs Temporal holds for the installation, and the records rebuilt.</summary>
public sealed record ReconcileResult(int Runs, IReadOnlyList<RunRecord> Rebuilt);

/// <summary>
/// The reconcile job (docs/02 section 5, recovery): rebuilds the run index, the record store's <see cref="RunRecord"/>s,
/// from Temporal, for example after a database is restored from a backup older than its runs. It reads every run
/// workflow on the installation's task queue and writes a record for each run the store lacks, and for each the store
/// shows open that Temporal has closed. A record that agrees is left alone, so a second reconcile writes nothing. A run's
/// template, parent, requester, repositories and dry run come from the input its workflow started with; its status from
/// Temporal: an open workflow is <c>running</c>, a completed one has its result (the run's own status), a failed one is
/// <c>failed</c>, and one terminated, cancelled or timed out outside the engine <c>incomplete</c> (F9: never
/// <c>completed</c>). A rebuilt record is dated when its workflow closed, or for an open run when its current workflow
/// run started, so any record the run writes itself is newer and wins (LOG 290): a run that ends during the pass is
/// never set back to <c>running</c>. Waits, timelines and the rest are not rebuilt: the run index is what lists and links runs.
/// </summary>
public static class RunIndex
{
    public const string Detail = "rebuilt from Temporal by reconcile";

    public static async Task<ReconcileResult> ReconcileAsync(ITemporalClient client, string taskQueue, IRecordStoreProvider records, CancellationToken ct)
    {
        var ids = new SortedSet<string>(StringComparer.Ordinal);
        await foreach (var execution in client.ListWorkflowsAsync($"WorkflowType = '{RunWorkflowNames.Workflow}' AND TaskQueue = '{taskQueue}'").WithCancellation(ct))
            ids.Add(execution.Id);

        var rebuilt = new List<RunRecord>();
        foreach (var id in ids)
        {
            var handle = client.GetWorkflowHandle(id);
            var description = await handle.DescribeAsync();
            var open = description.Status == WorkflowExecutionStatus.Running;
            var existing = (await records.QueryAsync<RunRecord>(Queries.Run, new Dictionary<string, string> { ["run_id"] = id }, ct)).Unwrap().FirstOrDefault();
            if (existing is not null && (open || existing.Status is not ("starting" or "running")))
                continue;

            var start = await StartOfAsync(client, handle, ct);
            var status = description.Status switch
            {
                WorkflowExecutionStatus.Running => "running",
                WorkflowExecutionStatus.Completed => await handle.GetResultAsync<string>(),
                WorkflowExecutionStatus.Failed => "failed",
                _ => "incomplete",
            };
            rebuilt.Add(new RunRecord(start.RunId, start.ParentWorkflowId, start.Template, status, start.Requester, start.Repositories, start.DryRun,
                new DateTimeOffset(description.CloseTime ?? description.StartTime, TimeSpan.Zero), Detail));
        }

        if (rebuilt.Count > 0)
            (await records.AppendAsync(new RecordBatch(rebuilt), ct)).Unwrap();
        return new ReconcileResult(ids.Count, rebuilt);
    }

    /// <summary>The input the workflow's current run started with, decoded with the client's converter (and so its payload codec).</summary>
    static async Task<RunStart> StartOfAsync(ITemporalClient client, WorkflowHandle handle, CancellationToken ct)
    {
        await foreach (var e in handle.FetchHistoryEventsAsync().WithCancellation(ct))
            return await client.Options.DataConverter.ToSingleValueAsync<RunStart>(e.WorkflowExecutionStartedEventAttributes.Input.Payloads_);
        throw new InvalidOperationException($"workflow {handle.Id} has no history");
    }
}
