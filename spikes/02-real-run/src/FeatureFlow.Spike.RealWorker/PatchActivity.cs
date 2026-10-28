using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Activities;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// The remaining <see cref="ISpikeActivities"/> members, plus the pieces the
/// checks need: a patch proposal that can author either patch form (review 11
/// C2), and a decision wait that is a durable wait with no timeout (R8).
/// </summary>
public sealed partial class RealActivities
{
    /// <summary>
    /// Which patch form the planner authors. review 11 C2 asks for both
    /// <c>insert_after</c> (with rewiring) and <c>add_step</c> +
    /// <c>change_route</c>, so their validation failure rates can be measured.
    /// </summary>
    public static string PatchForm = "insert_after";

    [Activity]
    public Task<PlanPatch?> ProposePatchAsync(PatchRequest request)
    {
        var plan = Context.Plan;
        var target = plan.ById(request.StepId) ?? plan.Steps.FirstOrDefault(s => s.Kind == "agent.run");
        if (target is null)
            return Task.FromResult<PlanPatch?>(null);

        var newStep = new Step
        {
            Id = $"{request.StepId}_extra",
            Kind = "notify.send",
            Routes = new Dictionary<string, RouteTarget>(StringComparer.Ordinal),
        };

        var changes = new List<PatchChange>();

        if (string.Equals(PatchForm, "insert_after", StringComparison.Ordinal))
        {
            // docs/09 15.3.1: insert_after X inserts S immediately after X, and
            // every route of X that pointed at some step Y now points at S. The
            // rewiring is done by applyPatch, not by the proposer.
            changes.Add(new PatchChange
            {
                Op = PatchOps.InsertAfter,
                Target = target.Id,
                Step = newStep,
            });
        }
        else
        {
            // The equivalent expressed as add_step plus change_route. The step is
            // added, then the target's completed route is pointed at it.
            changes.Add(new PatchChange
            {
                Op = PatchOps.InsertAfter,
                Target = target.Id,
                Step = newStep,
            });

            changes.Add(new PatchChange
            {
                Op = PatchOps.ChangeRoute,
                Target = target.Id,
                Outcome = Outcomes.Completed,
                To = RouteTarget.ToStep(newStep.Id),
            });
        }

        return Task.FromResult<PlanPatch?>(new PlanPatch
        {
            BaseRevision = request.BaseRevision,
            ProposedBy = "agent:replan",
            Reason = $"spike 2 patch form measurement: {PatchForm}",
            Changes = changes,
        });
    }

    /// <summary>
    /// R8: a gate waits with no timeout. This activity is not the wait — the
    /// workflow's durable wait is — so it only records that the gate opened and
    /// returns. A reminder is a timer, not a timeout.
    /// </summary>
    [Activity]
    public Task<string> WaitForDecisionAsync(DecisionRequest request)
    {
        Console.WriteLine($"STEP {request.StepId}: gate opened, decision type {request.DecisionType} " +
                          "(no timeout; the workflow waits durably)");
        return Task.FromResult(Outcomes.Approved);
    }

    [Activity]
    public Task RecordPatchResultAsync(PatchRecord record)
    {
        Console.WriteLine($"PATCH {record.Result} base={record.BaseRevision} " +
                          $"new={record.NewRevision} reason={record.Reason}");
        return Task.CompletedTask;
    }

    [Activity]
    public Task<long> ReadHistorySizeAsync(string runId) => Task.FromResult(0L);
}
