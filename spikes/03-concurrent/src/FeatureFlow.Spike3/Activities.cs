using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Activities;
using Temporalio.Api.Enums.V1;
using Temporalio.Client;
using Temporalio.Exceptions;

namespace FeatureFlow.Spike3;

/// <summary>The lock workflow's outside-world calls (N2).</summary>
public sealed class LockActivities : ILockActivities
{
    private readonly ITemporalClient client;
    private readonly int holderCheckSeconds;

    public LockActivities(ITemporalClient client, int holderCheckSeconds = 30)
    {
        this.client = client;
        this.holderCheckSeconds = holderCheckSeconds;
    }

    [Activity]
    public async Task RequestLockAsync(LockRequest request)
    {
        var options = new WorkflowOptions(request.LockId, request.LockQueue);
        options.SignalWithStart((LockWorkflow l) => l.RequestAsync(request.HolderId));
        await client.StartWorkflowAsync(
            (LockWorkflow l) => l.RunAsync(new LockInput(request.LockId, holderCheckSeconds)), options);
    }

    [Activity]
    public async Task<bool> IsWorkflowOpenAsync(string workflowId)
    {
        try
        {
            var description = await client.GetWorkflowHandle(workflowId).DescribeAsync();
            return description.Status == WorkflowExecutionStatus.Running;
        }
        catch (RpcException ex) when (ex.Code == RpcException.StatusCode.NotFound)
        {
            return false;
        }
    }
}

/// <summary>
/// No agents, no GitHub: steps that wait and report, for the method sanity
/// checks (docs/17 5 and 5.1 I3, I4). A step's duration is its
/// <c>SimulatedDuration</c>; <c>noop.fail</c> reports failed. Every
/// <c>scm.merge_base_forward</c> records the interval it ran in, which is what
/// the lock must keep apart.
/// </summary>
public sealed class FakeActivities : ISpikeActivities
{
    private readonly string intervalsPath;

    public FakeActivities(string intervalsPath) => this.intervalsPath = intervalsPath;

    [Activity]
    public Task<AgentRunResult> RunAgentAsync(AgentRunRequest request) =>
        Task.FromResult(new AgentRunResult(Outcomes.Completed));

    [Activity]
    public async Task<AgentRunResult> RunGenericStepAsync(GenericStepRequest request)
    {
        var info = ActivityExecutionContext.Current.Info;
        var start = DateTimeOffset.UtcNow;
        var end = start + TimeSpan.FromSeconds(request.DurationSeconds);
        while (DateTimeOffset.UtcNow < end)
        {
            ActivityExecutionContext.Current.Heartbeat();
            await Task.Delay(TimeSpan.FromSeconds(1), ActivityExecutionContext.Current.CancellationToken);
        }

        var stop = DateTimeOffset.UtcNow;
        lock (intervalsPath)
        {
            File.AppendAllText(intervalsPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                workflowId = info.WorkflowId, step = request.StepId, kind = request.Kind, start, end = stop,
            }) + Environment.NewLine);
        }

        return new AgentRunResult(request.Kind == "noop.fail" ? Outcomes.Failed : Outcomes.Completed);
    }

    [Activity]
    public Task<PlanPatch?> ProposePatchAsync(PatchRequest request) => Task.FromResult<PlanPatch?>(null);

    [Activity]
    public Task<string> WaitForDecisionAsync(DecisionRequest request) => Task.FromResult(Outcomes.Approved);

    [Activity]
    public Task RecordPatchResultAsync(PatchRecord record) => Task.CompletedTask;

    [Activity]
    public Task<long> ReadHistorySizeAsync(string runId) => Task.FromResult(0L);
}
