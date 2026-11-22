using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Validation;

namespace FeatureFlow.Interpreter;

/// <summary>
/// The plan as an agent with planning authority sees it, and what the core would do with its patch now (design note s1, s3):
/// the answers to the workflow's <c>agent_plan</c>, <c>patch_preview</c> and <c>child_patch_preview</c> queries. Each is pure: a
/// preview runs the patch path's checks (<see cref="CheckPatch"/>) and none of its effects, so nothing enters the run's history.
/// </summary>
public sealed partial class RunCore
{
    /// <summary>
    /// The plan as the agent step <see cref="AgentPlanQuery.StepKey"/> may see it. <c>self</c>: this run's plan, to a running agent
    /// step with <c>planning</c>. <c>parent</c>: <c>forward</c> to the parent's workflow when the step's <c>targets</c> has
    /// <c>parent</c>. <c>child</c> (asked of a parent, the key a running child's workflow id): this run's plan, without a step of
    /// its own and with only the organisation's part of the authority, which the asking child completes.
    /// </summary>
    public AgentPlanView AgentPlan(AgentPlanQuery query)
    {
        if (State.Status != RunStatus.Running)
            return PlanUnavailable(Run.Id, $"the run has ended ({State.Status.Name()})");
        var allowed = Options.AllowedRoles?.Order(StringComparer.Ordinal).ToList();
        if (query.Target == "child")
            return ChildStep(query.StepKey) is null
                ? PlanUnavailable(Run.Id, $"{query.StepKey} is not a running child of this run")
                : View(null, new AgentPlanAuthority([], "", Options.RequireReview, allowed));
        if (AgentStepOf(query.StepKey) is not ({ } step, var execution))
            return PlanUnavailable(Run.Id, $"{query.StepKey} is not a running agent step of this run");
        if (step.Agent?.Planning is not { } planning)
            return PlanUnavailable(Run.Id, $"{step.Id} has no planning authority (docs/04 section 6.1)");
        return query.Target switch
        {
            "self" => View(new AgentPlanSelf(step.Id, execution), new AgentPlanAuthority(planning.Targets, planning.Review, Options.RequireReview, allowed)),
            "parent" when ParentRefusal(step) is { } refusal => PlanUnavailable(Run.Id, refusal),
            "parent" => PlanUnavailable(Run.Id, null) with { Status = AgentPlanView.Forward, ForwardTo = Run.ParentId },
            _ => PlanUnavailable(Run.Id, $"target must be self or parent, not '{query.Target}'"),
        };
    }

    /// <summary>
    /// What the core would do now with an agent step's patch (YAML), as <see cref="Completed"/> would when the step reports it.
    /// To the parent, only the authority is checked here and the answer is <c>forward</c>: the parent's core decides.
    /// </summary>
    public PatchPreview PreviewPatch(PatchPreviewQuery query)
    {
        if (State.Status != RunStatus.Running)
            return PreviewUnavailable(State.Revision, $"the run has ended ({State.Status.Name()})");
        if (AgentStepOf(query.StepKey) is not ({ } step, _))
            return PreviewUnavailable(State.Revision, $"{query.StepKey} is not a running agent step of this run");
        var reviewRequired = step.Agent?.Planning?.Review == "required";
        if (query.ToParent)
            return ParentRefusal(step) is { } refusal
                ? new PatchPreview(PatchPreview.Refused, [], [refusal], State.Revision, null, reviewRequired)
                : new PatchPreview(PatchPreview.Forward, [], [], State.Revision, Run.ParentId, reviewRequired);
        if (ReadPatch(query.Patch, out var unread) is not { } patch)
            return new PatchPreview(PatchPreview.Refused, [], Errors(unread), State.Revision, null, reviewRequired);
        var (d, _, pending) = CheckPatch(patch with { ProposedBy = new Proposer(ProposerOf(step), step.Id) }, step);
        return Preview(d, pending, reviewRequired);
    }

    /// <summary>What this run's core would do now with a patch from a running child's agent step, as <see cref="ProposeFromChild"/> would.</summary>
    public PatchPreview PreviewChildPatch(ChildPatchPreviewQuery query)
    {
        if (State.Status != RunStatus.Running)
            return PreviewUnavailable(State.Revision, $"the run has ended ({State.Status.Name()})");
        if (ChildStep(query.ChildWorkflowId) is not { } childStep || StepById(childStep) is not { Descriptor.Handling: ActionHandling.ChildRun })
            return PreviewUnavailable(State.Revision, $"{query.ChildWorkflowId} is not a running child of this run");
        if (ReadPatch(query.Patch, out var unread) is not { } patch)
            return new PatchPreview(PatchPreview.Refused, [], Errors(unread), State.Revision, null, query.ReviewRequired);
        var (d, _, pending) = CheckPatch(patch with { ProposedBy = new Proposer("agent", query.ProposerStepId) }, null, fromChild: true,
            reviewRequired: query.ReviewRequired);
        return Preview(d, pending, query.ReviewRequired);
    }

    public static AgentPlanView PlanUnavailable(string runId, string? message) =>
        new(AgentPlanView.Unavailable, message, null, runId, "", 0, null, null, "", [], []);

    public static PatchPreview PreviewUnavailable(int revision, string message) =>
        new(PatchPreview.Unavailable, [], [message], revision, null, false);

    PatchPreview Preview(DiagnosticBag d, IReadOnlyList<string> pending, bool reviewRequired) =>
        d.HasErrors
            ? new PatchPreview(PatchPreview.Refused, [], Errors(d), State.Revision, null, reviewRequired)
            : new PatchPreview(pending.Count > 0 ? PatchPreview.NeedsApproval : PatchPreview.Applies, pending, [], State.Revision, null, reviewRequired);

    /// <summary>Why a step may not propose to its parent's plan, as <see cref="Completed"/> refuses it, or null.</summary>
    string? ParentRefusal(Step step) =>
        step.Agent?.Planning?.Targets.Contains("parent") != true ? $"{step.Id}: the step has no planning authority over its parent's plan (planning.targets)"
        : Run.ParentId is null ? $"{step.Id}: this run has no parent to send a patch to"
        : null;

    /// <summary>The running agent step whose current execution has the key <c>&lt;run&gt;/&lt;step&gt;/&lt;execution&gt;</c>, or null.</summary>
    (Step Step, int Execution)? AgentStepOf(string stepKey)
    {
        foreach (var id in State.InFlight)
            if (StepById(id) is { Form: StepForm.Agent } step && StepKey(id, Executions(id)) == stepKey)
                return (step, Executions(id));
        return null;
    }

    static IReadOnlyList<string> Errors(DiagnosticBag d) => d.Where(x => x.Blocks).Select(x => $"patch check {x.Check}: {x.Message}").ToList();

    AgentPlanView View(AgentPlanSelf? self, AgentPlanAuthority authority)
    {
        var statuses = StepStatuses();
        var steps = Plan.AllSteps.Select(s => new AgentPlanStep(s.Id, s.Scope.ContainerId, ReadableForm.Form(s), ReadableForm.RoleOrAction(s),
            s.Outcomes().Select(o => new AgentPlanOutcome(o.Name, o.Class.Name(),
                s.ResolvedRoutes.FirstOrDefault(r => r.Outcome == o.Name) is { } route ? ReadableForm.Show(route.Target) : null)).ToList(),
            s.Protected, statuses.GetValueOrDefault(s.Id) ?? "pending", s.Source is { } written ? string.Join('\n', Lines(written)) : null)).ToList();
        return new AgentPlanView(AgentPlanView.Ok, null, null, Run.Id, Plan.Document.Level ?? "run", State.Revision, self, authority,
            ReadableForm.Render(Plan, statuses), Positions(), steps);
    }

    /// <summary>
    /// Each step's status as the API's plan view names it (<c>running</c>, <c>done(&lt;outcome&gt;)</c>, <c>waiting(&lt;kind&gt;)</c>),
    /// from the core's state; a step not yet run has none. A wait shows only its kind.
    /// </summary>
    Dictionary<string, string> StepStatuses()
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (id, record) in State.Steps)
            if (record.Current?.Outcome is { } outcome)
                statuses[id] = $"done({outcome})";
        foreach (var id in State.InFlight)
            statuses[id] = "running";
        foreach (var wait in State.Waits.Where(w => w.StepId.Length > 0))
            statuses[wait.StepId] = $"waiting({wait.Kind.Name()})";
        return statuses;
    }
}
