using Temporalio.Workflows;

namespace Sp1;

[Workflow]
public class ChildWorkflow
{
    /// <summary>
    /// Waits, then signals the parent that it has ended. The signal is addressed by
    /// workflow id, so it reaches whichever run of the parent is current; with a run
    /// id (negative control N1) it reaches only that run.
    /// </summary>
    [WorkflowRun]
    public async Task<string> RunAsync(ChildInput input)
    {
        await Workflow.DelayAsync(TimeSpan.FromSeconds(input.Seconds));
        try
        {
            await Workflow.GetExternalWorkflowHandle<ParentWorkflow>(input.ParentId, input.ParentRunId)
                .SignalAsync(w => w.ChildEndedAsync(input.Id));
        }
        catch (Exception)
        {
            return input.Outcome + ";signal_failed";
        }
        return input.Outcome;
    }
}
