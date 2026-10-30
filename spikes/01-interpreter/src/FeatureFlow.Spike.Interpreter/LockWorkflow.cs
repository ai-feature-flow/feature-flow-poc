using Temporalio.Activities;
using Temporalio.Workflows;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// Spike 3 N2: the merge lock for one target branch, as a Temporal-native lock
/// workflow (docs/06 6.5, review 06 S3, docs/17 4.3). FIFO. Requested by
/// signal-with-start, granted by signalling the holder, released by the holder's
/// signal, or by this workflow when it finds the holder closed: a terminated
/// workflow cannot release its own lock (J11).
/// </summary>
[Workflow]
public class LockWorkflow
{
    private readonly List<string> waiters = new();
    private readonly List<LockAuditEntry> audit = new();
    private string? holder;
    private bool dirty;
    private bool stop;

    [WorkflowRun]
    public async Task RunAsync(LockInput input)
    {
        var check = TimeSpan.FromSeconds(input.HolderCheckSeconds <= 0 ? 30 : input.HolderCheckSeconds);
        var nextCheck = Workflow.UtcNow + check;

        while (!stop)
        {
            if (holder is null && waiters.Count > 0)
            {
                var next = waiters[0];
                waiters.RemoveAt(0);
                holder = next;
                Audit("granted", next, null);
                try
                {
                    await Workflow.GetExternalWorkflowHandle<InterpreterWorkflow>(next)
                        .SignalAsync(w => w.LockGrantedAsync(input.LockId));
                }
                catch (Temporalio.Exceptions.TemporalException ex)
                {
                    Audit("released", next, $"grant undeliverable: {ex.GetType().Name}");
                    holder = null;
                }
                nextCheck = Workflow.UtcNow + check;
                continue;
            }

            var wait = nextCheck - Workflow.UtcNow;
            if (wait < TimeSpan.FromMilliseconds(1))
                wait = TimeSpan.FromMilliseconds(1);
            var signalled = await Workflow.WaitConditionAsync(() => dirty || stop, wait);
            dirty = false;

            if (!signalled || Workflow.UtcNow >= nextCheck)
            {
                nextCheck = Workflow.UtcNow + check;
                if (holder is not null)
                {
                    var open = await Workflow.ExecuteActivityAsync(
                        (ILockActivities a) => a.IsWorkflowOpenAsync(holder),
                        new ActivityOptions { StartToCloseTimeout = TimeSpan.FromSeconds(30) });
                    if (!open)
                    {
                        Audit("released", holder, "holder closed (liveness check)");
                        holder = null;
                    }
                }
            }
        }
    }

    [WorkflowSignal]
    public Task RequestAsync(string holderId)
    {
        if (holder == holderId || waiters.Contains(holderId))
        {
            Audit("request-duplicate", holderId, null);
        }
        else
        {
            waiters.Add(holderId);
            Audit("requested", holderId, holder is null ? null : $"held by {holder}");
        }
        dirty = true;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task ReleaseAsync(LockRelease release)
    {
        if (holder == release.HolderId)
        {
            holder = null;
            Audit("released", release.HolderId, release.Reason);
        }
        else
        {
            Audit("release-ignored", release.HolderId, $"holder is {holder ?? "(none)"}; {release.Reason}");
        }
        dirty = true;
        return Task.CompletedTask;
    }

    [WorkflowSignal]
    public Task StopAsync()
    {
        stop = true;
        return Task.CompletedTask;
    }

    [WorkflowQuery]
    public LockStateView State() => new(holder, waiters.ToList(), audit.ToList());

    private void Audit(string kind, string holderId, string? detail) =>
        audit.Add(new LockAuditEntry(kind, holderId, Workflow.UtcNow, detail));
}

public sealed record LockInput(string LockId, int HolderCheckSeconds = 30);

public sealed record LockRequest(string LockId, string HolderId, string LockQueue);

public sealed record LockRelease(string HolderId, string Reason);

public sealed record LockAuditEntry(string Kind, string HolderId, DateTimeOffset At, string? Detail);

public sealed record LockStateView(string? Holder, List<string> Waiters, List<LockAuditEntry> Audit);

/// <summary>The lock's outside-world calls. Implemented by the Spike 3 workers.</summary>
public interface ILockActivities
{
    /// <summary>Signal-with-start the lock workflow with a request from the holder.</summary>
    [Activity]
    Task RequestLockAsync(LockRequest request);

    /// <summary>Whether a workflow is still running (the holder-liveness check).</summary>
    [Activity]
    Task<bool> IsWorkflowOpenAsync(string workflowId);
}
