using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// The pure applyPatch function. docs/01 3.4 and docs/09 section 15.
/// Three results, not two: applied, pending human decision, rejected.
/// </summary>
public static class ApplyPatch
{
    public static PatchResult Apply(Plan plan, PlanPatch patch)
    {
        if (patch.BaseRevision != plan.Revision)
            return PatchResult.Rejected(
                $"stale patch: base_revision {patch.BaseRevision} != current revision {plan.Revision}");

        var working = plan.Clone();
        var held = new List<string>();

        foreach (var change in patch.Changes)
        {
            var outcome = ApplyOne(working, change);
            if (outcome.Held is not null)
                held.Add(outcome.Held);
            else if (outcome.Rejected is not null)
                return PatchResult.Rejected(outcome.Rejected);
        }

        // docs/09 15.2: bypassing a protected step is removal by another route.
        // Reachability is a validation check (docs/09 16.3).
        var unreachable = FindUnreachableProtected(working);
        if (unreachable.Count > 0)
            return PatchResult.Rejected(
                $"patch would make protected step(s) unreachable: {string.Join(", ", unreachable)}");

        if (held.Count > 0)
            return PatchResult.Held(string.Join("; ", held));

        var resumeAt = patch.ResumeAt;
        if (resumeAt is not null && working.ById(resumeAt) is null)
            return PatchResult.Rejected($"resume_at '{resumeAt}' does not exist after the patch");

        working.Revision = plan.Revision + 1;
        return PatchResult.Applied(working, working.Revision, resumeAt);
    }

    private static (string? Held, string? Rejected) ApplyOne(Plan plan, PatchChange change)
    {
        var target = plan.ById(change.Target);
        if (target is null)
            return (null, $"target step '{change.Target}' does not exist");

        // docs/09 15.2: every one of these is blocked on a protected step.
        if (target.Protected)
        {
            var blocked = change.Op switch
            {
                PatchOps.RemoveStep => "removed",
                PatchOps.ReplaceStep => "replaced",
                PatchOps.ReplaceInputs => "inputs changed",
                PatchOps.ReplaceLimits => "limits changed",
                PatchOps.ReplaceRole => "role changed",
                PatchOps.ChangeRoute => "route out of it changed",
                _ => null,
            };
            if (blocked is not null)
                return (null, $"protected step '{target.Id}' cannot be {blocked} (floor F3)");
        }

        switch (change.Op)
        {
            case PatchOps.InsertBefore:
                return Insert(plan, change, before: true);

            case PatchOps.InsertAfter:
                return Insert(plan, change, before: false);

            case PatchOps.ReplaceStep:
                if (change.Step is null)
                    return (null, "replace_step requires a step");
                plan.Steps[plan.Steps.FindIndex(s => s.Id == change.Target)] = change.Step;
                return (null, null);

            case PatchOps.RemoveStep:
                plan.Steps.RemoveAll(s => s.Id == change.Target);
                return (null, null);

            case PatchOps.ReplaceInputs:
                if (change.Inputs is null)
                    return (null, "replace_inputs requires inputs");
                target.Inputs = new Dictionary<string, object?>(change.Inputs, StringComparer.Ordinal);
                return (null, null);

            case PatchOps.ReplaceLimits:
                if (change.Limits is null)
                    return (null, "replace_limits requires limits");
                target.Limits = change.Limits;
                return (null, null);

            case PatchOps.ReplaceRole:
                if (change.Role is null)
                    return (null, "replace_role requires a role");
                target.Inputs["role"] = change.Role;
                return (null, null);

            case PatchOps.ChangeRoute:
                if (change.Outcome is null || change.To is null)
                    return (null, "change_route requires outcome and to");
                target.Routes[change.Outcome] = change.To;
                return (null, null);

            case PatchOps.AddBranch:
                if (change.Step is null)
                    return (null, "add_branch requires a step");
                if (target.Kind != "parallel")
                    return (null, $"add_branch target '{target.Id}' is not a parallel step");
                plan.Steps.Add(change.Step);
                AddToBranches(target, change.Step.Id);
                return (null, null);

            case PatchOps.RemoveBranch:
                if (target.Kind != "parallel")
                    return (null, $"remove_branch target '{target.Id}' is not a parallel step");
                var branch = plan.ById(change.Target);
                RemoveFromBranches(target, change.Step?.Id ?? change.Target);
                return (null, null);

            case PatchOps.AddChain:
                return AddChain(plan, change, target);

            default:
                return (null, $"unknown patch op '{change.Op}'");
        }
    }

    /// <summary>
    /// docs/09 15.3.1. insert_after X inserts S immediately after X: every route
    /// of X that pointed at some step Y now points at S. insert_before X is the
    /// mirror: every route from any step that pointed at X now points at S, and
    /// S routes to X.
    /// </summary>
    private static (string? Held, string? Rejected) Insert(Plan plan, PatchChange change, bool before)
    {
        if (change.Step is null)
            return (null, $"{change.Op} requires a step");
        if (plan.ById(change.Step.Id) is not null)
            return (null, $"step '{change.Step.Id}' already exists");

        plan.Steps.Add(change.Step);

        if (before)
        {
            foreach (var step in plan.Steps)
            {
                if (step.Id == change.Step.Id)
                    continue;
                foreach (var outcome in step.Routes.Keys.ToList())
                {
                    if (PointsAt(step.Routes[outcome], change.Target))
                        step.Routes[outcome] = RouteTarget.ToStep(change.Step.Id);
                }
            }
            if (plan.Entry == change.Target)
                plan.Entry = change.Step.Id;
            change.Step.Routes["completed"] = RouteTarget.ToStep(change.Target);
        }
        else
        {
            var target = plan.ById(change.Target)!;
            foreach (var outcome in target.Routes.Keys.ToList())
            {
                if (target.Routes[outcome].Step is not null)
                    target.Routes[outcome] = RouteTarget.ToStep(change.Step.Id);
            }
        }

        return (null, null);
    }

    /// <summary>
    /// docs/09 15.3.2. target's completed route goes to the new step; every
    /// other outcome goes to on_other.
    /// </summary>
    private static (string? Held, string? Rejected) AddChain(Plan plan, PatchChange change, Step target)
    {
        if (change.Step is null)
            return (null, "add_chain requires a step");
        if (change.OnOther is null)
            return (null, "add_chain requires on_other");
        if (plan.ById(change.OnOther) is null)
            return (null, $"add_chain on_other '{change.OnOther}' does not exist");

        plan.Steps.Add(change.Step);
        foreach (var outcome in target.Routes.Keys.ToList())
            target.Routes[outcome] = RouteTarget.ToStep(
                outcome == Outcomes.Completed ? change.Step.Id : change.OnOther);
        return (null, null);
    }

    private static bool PointsAt(RouteTarget target, string stepId)
    {
        if (target.Step == stepId)
            return true;
        return target.Guarded?.Any(g => g.To is not null && PointsAt(g.To, stepId)) ?? false;
    }

    private static void AddToBranches(Step parallel, string branchId)
    {
        if (!parallel.Inputs.TryGetValue("branches", out var existing) ||
            existing is not List<object?> list)
        {
            parallel.Inputs["branches"] = new List<object?> { branchId };
            return;
        }
        list.Add(branchId);
    }

    private static void RemoveFromBranches(Step parallel, string branchId)
    {
        if (parallel.Inputs.TryGetValue("branches", out var existing) &&
            existing is List<object?> list)
        {
            list.RemoveAll(x => x?.ToString() == branchId);
        }
    }

    /// <summary>
    /// docs/09 16.3. A protected step that is no longer reachable from the entry
    /// point has been bypassed, which is removal by another route.
    /// </summary>
    public static List<string> FindUnreachableProtected(Plan plan)
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        if (plan.ById(plan.Entry) is not null)
            queue.Enqueue(plan.Entry);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            if (!reachable.Add(id))
                continue;
            var step = plan.ById(id);
            if (step is null)
                continue;
            foreach (var target in step.Routes.Values)
            {
                foreach (var next in TargetsOf(target))
                {
                    if (plan.ById(next) is not null)
                        queue.Enqueue(next);
                }
            }
            if (step.Kind == "parallel")
            {
                foreach (var branch in ParallelInputs.FromInputs(step.Inputs).Branches)
                {
                    if (plan.ById(branch) is not null)
                        queue.Enqueue(branch);
                }
            }
        }

        return plan.Steps.Where(s => s.Protected && !reachable.Contains(s.Id))
            .Select(s => s.Id)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();
    }

    private static IEnumerable<string> TargetsOf(RouteTarget target)
    {
        if (target.Step is not null)
            yield return target.Step;
        if (target.Guarded is null)
            yield break;
        foreach (var g in target.Guarded)
        {
            if (g.To?.Step is not null)
                yield return g.To.Step;
        }
    }
}
