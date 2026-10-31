using Temporalio.Activities;
using Temporalio.Client;

namespace Sp1;

public sealed class Activities(ITemporalClient client)
{
    /// <summary>Stands in for a step's work: takes a fixed time and returns.</summary>
    [Activity]
    public Task WorkAsync(int millis) => Task.Delay(millis, ActivityExecutionContext.Current.CancellationToken);

    /// <summary>
    /// The parent's confirm read: the child's status and result as Temporal holds
    /// them. A signal is only a hint; this read decides.
    /// </summary>
    [Activity]
    public async Task<ChildRead> ReadChildAsync(string childId)
    {
        var handle = client.GetWorkflowHandle(childId);
        string? result = null;
        try
        {
            // The child signals just before it returns, so it may still be running.
            result = await handle.GetResultAsync<string>(
                rpcOptions: new RpcOptions { Timeout = TimeSpan.FromSeconds(30) });
        }
        catch (Exception)
        {
            // Not completed normally; the status below says how it ended.
        }
        var description = await handle.DescribeAsync();
        return new ChildRead(description.Status.ToString(), result);
    }
}
