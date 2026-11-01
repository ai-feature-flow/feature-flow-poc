using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Library;

/// <summary>
/// Applies patches to the expanded plan before routes are resolved (docs/03 section 10.2), so a patched plan
/// gets defaults, next-step routing and every load check exactly as a written one does. A step a patch
/// supplies is written in the scope of its target: its id and its route targets are local to the template or
/// fragment that holds the target. Errors that stop an operation are check 28.
/// </summary>
public sealed partial class PlanLoader
{
    /// <summary>Replaced step ids to their replacements: a route to the old id reaches the new step.</summary>
    readonly Dictionary<string, string> aliases = [];

    /// <summary>After <c>insert after X</c>: X's routes to the step that followed it now go to the new step.</summary>
    readonly List<(string From, string OldTarget, string NewTarget)> redirectOwn = [];

    /// <summary>After <c>insert before X</c>: every route to X, except the new step's own, goes to the new step.</summary>
    readonly List<(string OldTarget, string NewTarget)> redirectAll = [];

    /// <summary>After <c>chain</c>: the target's success routes go to the new step, every other outcome to <c>on_other</c>.</summary>
    readonly List<(Step Target, string NewStep, string OnOther, SourceLocation Location)> chains = [];

    void ApplyPatches()
    {
        foreach (var patch in options.Patches ?? [])
            foreach (var op in patch.Changes)
                ApplyOp(op);
    }

    void ApplyOp(PatchOp op)
    {
        if (!stepsById.TryGetValue(op.Target, out var target))
        {
            Fail(op, $"target '{op.Target}' is not a step of the plan");
            return;
        }

        var lane = lanes[target.Lane];
        var index = lane.IndexOf(target);
        switch (op.Op)
        {
            case "insert":
            {
                if (ReadPatchStep(op, target.Scope, target.Lane) is not { } step)
                    return;
                if (op.Position == "after")
                {
                    var oldNext = index + 1 < lane.Count ? lane[index + 1] : null;
                    Splice(step, target.Lane, index + 1);
                    if (oldNext is not null)
                        redirectOwn.Add((target.Id, oldNext.Id, step.Id));
                }
                else
                {
                    Splice(step, target.Lane, index);
                    redirectAll.Add((target.Id, step.Id));
                }

                break;
            }
            case "replace":
            {
                if (target.Container is not null)
                {
                    Fail(op, $"replace applies to an agent step, decision or action; {target.Id} is a parallel or for_each");
                    return;
                }

                if (ReadPatchStep(op, target.Scope, target.Lane) is not { } step)
                    return;
                RemoveAt(target.Lane, index);
                Splice(step, target.Lane, index);
                step.Protected |= target.Protected;
                aliases[target.Id] = step.Id;
                break;
            }
            case "remove":
                RemoveAt(target.Lane, index);
                break;
            case "set":
                Set(op, target, index);
                break;
            case "add_branch":
                AddBranch(op, target);
                break;
            case "remove_branch":
                RemoveBranch(op, target);
                break;
            case "chain":
            {
                if (ReadPatchStep(op, target.Scope, target.Lane) is not { } step)
                    return;
                Splice(step, target.Lane, index + 1);
                chains.Add((target, step.Id, op.OnOther!, op.Location));
                break;
            }
        }
    }

    void Fail(PatchOp op, string message) => d.Add(Checks.PatchLoadChecks, $"{op.Op}: {message}", op.Location, op.Target);

    /// <summary>Reads the agent step, decision or action a patch supplies, without adding it to a lane.</summary>
    Step? ReadPatchStep(PatchOp op, Scope scope, string laneId)
    {
        if (op.Step is null)
        {
            Fail(op, "step is required");
            return null;
        }

        if (!op.Step.Has("agent") && !op.Step.Has("decision") && !op.Step.Has("action"))
        {
            Fail(op, "a patch inserts an agent step, decision or action; add parallel branches with add_branch");
            return null;
        }

        var temporary = new List<Step>();
        ExpandEntry(op.Step, scope, temporary, laneId, idOverride: null);
        if (temporary.Count != 1)
            return null;
        var step = temporary[0];
        step.Protected |= InProtectedFragment(scope);
        return step;
    }

    bool InProtectedFragment(Scope scope)
    {
        for (var s = scope; s is not null; s = s.Parent)
            if (s.Kind == ScopeKind.Fragment && plan.Instances.FirstOrDefault(i => i.Id == s.UseId) is { Protected: true })
                return true;
        return false;
    }

    /// <summary>Inserts a step into a lane, keeping lane positions and fragment spans in step.</summary>
    void Splice(Step step, string laneId, int index)
    {
        var lane = lanes[laneId];
        lane.Insert(index, step);
        step.Lane = laneId;
        Renumber(lane);
        stepsById[step.Id] = stepsById.GetValueOrDefault(step.Id) ?? step;
        foreach (var (key, span) in instanceSpans.ToList())
        {
            if (span.Lane != laneId)
                continue;
            if (Within(step.Scope, key))
                instanceSpans[key] = (span.Lane, Math.Min(span.First, index), span.Last + 1);
            else if (span.First >= index)
                instanceSpans[key] = (span.Lane, span.First + 1, span.Last + 1);
        }
    }

    void RemoveAt(string laneId, int index)
    {
        var lane = lanes[laneId];
        var removed = lane[index];
        lane.RemoveAt(index);
        Renumber(lane);
        foreach (var step in Walk([removed]))
            stepsById.Remove(step.Id);
        foreach (var (key, span) in instanceSpans.ToList())
        {
            if (span.Lane != laneId)
                continue;
            if (span.First > index)
                instanceSpans[key] = (span.Lane, span.First - 1, span.Last - 1);
            else if (span.Last >= index)
                instanceSpans[key] = (span.Lane, span.First, span.Last - 1);
        }
    }

    static void Renumber(List<Step> lane)
    {
        for (var i = 0; i < lane.Count; i++)
            lane[i].LaneIndex = i;
    }

    static bool Within(Scope scope, string useId)
    {
        for (var s = scope; s is not null; s = s.Parent)
            if (s.Kind == ScopeKind.Fragment && s.UseId == useId)
                return true;
        return false;
    }

    void Set(PatchOp op, Step target, int index)
    {
        var field = op.Field!;
        if (field.StartsWith("routes.", StringComparison.Ordinal))
        {
            var outcome = field["routes.".Length..];
            if (StepReader.ReadTarget(op.Value!, target.Scope, d, target.Id, $"route {outcome}") is not { } route)
                return;
            target.Routes.RemoveAll(r => r.Outcome == outcome);
            target.Routes.Add(new RouteSpec(outcome, route, target.Scope, op.Location));
            target.Expressions.AddRange(StepReader.Guards(route));
            return;
        }

        if (target.Source is null)
        {
            Fail(op, $"set {field} applies to an agent step, decision or action; {target.Id} is a parallel or for_each");
            return;
        }

        var entries = target.Source.Entries.Where(e => e.Key.Text != field).ToList();
        entries.Add(new(new YScalar(field, true, op.Location), op.Value!));
        var temporary = new List<Step>();
        ExpandEntry(new YMap(entries, target.Source.Location), target.Scope, temporary, target.Lane, idOverride: null);
        if (temporary.Count != 1)
            return;
        var step = temporary[0];
        step.Protected = target.Protected;
        lanes[target.Lane][index] = step;
        step.LaneIndex = index;
        stepsById[step.Id] = step;
    }

    void AddBranch(PatchOp op, Step target)
    {
        if (target.Container is not { Mode: "parallel", FromForEach: false } container)
        {
            Fail(op, $"add_branch applies to a parallel; {target.Id} is not one");
            return;
        }

        if (op.Step is null)
        {
            Fail(op, "step is required");
            return;
        }

        var index = container.Branches.Count == 0 ? 0 : container.Branches.Max(b => b.Index) + 1;
        var branchScope = new Scope
        {
            Id = $"{target.Id}#{index}",
            Prefix = target.Scope.Prefix,
            Kind = ScopeKind.Branch,
            Parent = target.Scope,
            Document = target.Scope.Document,
            BranchIndex = index,
            ContainerId = target.Id,
        };
        var branch = new Branch { Index = index, Scope = branchScope };
        container.Branches.Add(branch);
        var laneId = $"{target.Id}#{index}";
        lanes[laneId] = branch.Steps;
        var entry = op.Step;
        if (entry.Has("routes"))
        {
            d.Add(Checks.ParallelRoutes, "a branch has no routes: its outcome's class is reported to the join (docs/03 section 8.1)", entry["routes"]!.Location, target.Id);
            entry = new YMap(entry.Entries.Where(e => e.Key.Text != "routes").ToList(), entry.Location);
        }

        ExpandEntry(entry, branchScope, branch.Steps, laneId, idOverride: null);
        if (target.Protected)
            foreach (var step in Walk(branch.Steps))
                step.Protected = true;
    }

    void RemoveBranch(PatchOp op, Step target)
    {
        var branch = target.Container?.Branches.FirstOrDefault(b => b.Steps.Count > 0 && b.Steps[0].Id == op.Branch);
        if (branch is null)
        {
            Fail(op, $"{target.Id} has no branch starting with step '{op.Branch}'");
            return;
        }

        target.Container!.Branches.Remove(branch);
        foreach (var step in Walk(branch.Steps))
            stepsById.Remove(step.Id);
    }

    /// <summary>The route changes <c>insert</c> and <c>chain</c> make, applied to the resolved routes.</summary>
    void RewritePatchedRoutes()
    {
        foreach (var (from, oldTarget, newTarget) in redirectOwn)
            if (stepsById.TryGetValue(from, out var step))
                Redirect(step, oldTarget, newTarget);

        foreach (var (oldTarget, newTarget) in redirectAll)
            foreach (var step in plan.AllSteps.Where(s => s.Id != newTarget))
                Redirect(step, oldTarget, newTarget);

        foreach (var (target, newStep, onOther, location) in chains)
        {
            var other = FindStep(onOther, target.Scope);
            if (other is null)
            {
                d.Add(Checks.RouteTarget, $"chain on_other '{onOther}' is not a step", location, target.Id);
                continue;
            }

            for (var i = 0; i < target.ResolvedRoutes.Count; i++)
            {
                var route = target.ResolvedRoutes[i];
                target.ResolvedRoutes[i] = route with
                {
                    Target = new ResolvedStep(route.Class == OutcomeClass.Success ? newStep : other, false),
                    Source = RouteSource.Step,
                };
            }
        }
    }

    static void Redirect(Step step, string oldTarget, string newTarget)
    {
        for (var i = 0; i < step.ResolvedRoutes.Count; i++)
            step.ResolvedRoutes[i] = step.ResolvedRoutes[i] with { Target = Redirect(step.ResolvedRoutes[i].Target, oldTarget, newTarget) };
    }

    static Resolved Redirect(Resolved target, string oldTarget, string newTarget) => target switch
    {
        ResolvedStep s when s.StepId == oldTarget => s with { StepId = newTarget },
        ResolvedGuarded g => new ResolvedGuarded(g.Entries.Select(e => (e.When, Redirect(e.Target, oldTarget, newTarget))).ToList()),
        _ => target,
    };
}
