using FeatureFlow.Spike.Contracts;
using Temporalio.Activities;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// What the workflow needs from the outside world. Every method here is an
/// activity: docs/01 3.2, everything that touches the outside world.
/// </summary>
/// <summary>
/// The attributes live on the interface, because the workflow calls through the
/// interface and Temporalio resolves the activity name from the method it sees
/// in the call expression.
/// </summary>
public interface ISpikeActivities
{
    [Activity]
    Task<AgentRunResult> RunAgentAsync(AgentRunRequest request);

    [Activity]
    Task<AgentRunResult> RunGenericStepAsync(GenericStepRequest request);

    [Activity]
    Task<PlanPatch?> ProposePatchAsync(PatchRequest request);

    [Activity]
    Task<string> WaitForDecisionAsync(DecisionRequest request);

    [Activity]
    Task RecordPatchResultAsync(PatchRecord record);

    [Activity]
    Task<long> ReadHistorySizeAsync(string runId);
}

public sealed record AgentRunRequest(
    string StepId,
    string IdempotencyKey,
    string? SessionRef,
    int DurationSeconds,
    bool ProduceCommits,
    int CommitEverySeconds,
    int AttemptNumber,
    bool Resumed);

public sealed record GenericStepRequest(string StepId, string Kind, int DurationSeconds);

public sealed record PatchRequest(string StepId, string Intent, int BaseRevision);

public sealed record DecisionRequest(string StepId, string DecisionType);
