using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

/// <summary>
/// The one path every patch takes (docs/03 section 10): checks 25 to 30 of docs/09 section 2, with checks 3
/// and 17 on the plan as it will be after the patch. The patched plan is the template loaded again with every
/// applied patch, so it gets defaults, routing and the load checks exactly as a written plan does.
/// </summary>
public sealed partial class RunCore
{
    ApplyResult ProposeOutside(PatchProposed input)
    {
        var patch = input.Patch with { ProposedBy = input.By };
        var proposer = input.By.StepId is { } id ? StepById(id) : null;
        var result = Propose(patch, proposer, mayTouchProtected: input.MayTouchProtected);
        if (result.Status == PatchStatus.Applied)
            Resume(patch, proposer?.Id ?? "");
        return new ApplyResult(true, [], result);
    }

    ApplyResult PatchDecision(PatchAnswered answer)
    {
        var wait = State.Waits.FirstOrDefault(w => w.Id == answer.WaitId && w.Kind == WaitKind.PatchApproval);
        if (wait is { LibraryItem: { } item })
        {
            // An agent's library override (docs/05 section 6): approved, the shell carries it out with the approver as the actor.
            State.Waits.Remove(wait);
            Trace(answer.Approve ? "library_override_approved" : "library_override_rejected", wait.StepId, $"{item} by {answer.Actor}");
            if (answer.Approve)
                outbox.Add(new OverrideLibrary(wait.Id, item, wait.LibraryContent ?? "", answer.Actor, $"approved proposal of {wait.StepId}"));
            return ApplyResult.Ok;
        }

        if (wait?.Patch is not { } patch)
            return ApplyResult.Refused($"no patch approval is open as {answer.WaitId}");
        State.Waits.Remove(wait);
        if (!answer.Approve)
        {
            Trace("patch_rejected", wait.StepId, "a person rejected it");
            // A child's patch leaves its run.child step running; a step's own patch lets the step route on.
            if (!wait.FromChild && StepById(wait.StepId) is { } step)
                Route(step, State.Steps[step.Id].Current?.Outcome ?? "");
            return new ApplyResult(true, [], new PatchResult(PatchStatus.Rejected, []));
        }

        var result = Propose(patch, wait.FromChild ? null : StepById(wait.StepId), approved: true, fromChild: wait.FromChild);
        if (wait.FromChild)
        {
            if (result.Status == PatchStatus.Applied && patch.ResumeAt is { } at && FindIn(at) is { } resume)
                State.Pending.Add(new Entry(resume, false));
        }
        else if (result.Status == PatchStatus.Applied)
            Resume(patch, wait.StepId);
        else if (StepById(wait.StepId) is { } proposer)
            Route(proposer, State.Steps[proposer.Id].Current?.Outcome ?? "");
        return new ApplyResult(true, [], result);
    }

    /// <summary>
    /// A patch a child run sent (docs/03 section 9.4). The child checked its step's authority over the parent's
    /// plan (<c>planning.targets</c>); here it takes the path of any agent patch, may omit <c>resume_at</c> (the
    /// parent continues where it was, check 26), and a rejection is recorded. The run.child step keeps running:
    /// the child's branch counts towards a join only when the child ends.
    /// </summary>
    ApplyResult ProposeFromChild(ChildPatchProposed input)
    {
        if (StepById(input.ChildStepId) is not { Action: "run.child" })
            return ApplyResult.Refused($"{input.ChildStepId} is not a run.child step of plan revision {State.Revision}");
        var patch = input.Patch with { ProposedBy = new Proposer("agent", input.ProposerStepId) };
        var result = Propose(patch, null, fromChild: true, childStepId: input.ChildStepId);
        if (result.Status == PatchStatus.Applied && patch.ResumeAt is { } at && FindIn(at) is { } resume)
            State.Pending.Add(new Entry(resume, false));
        return new ApplyResult(true, [], result);
    }

    void Resume(PatchDocument patch, string proposerId)
    {
        if (patch.ResumeAt is { } at && FindIn(at) is { } step)
            State.Pending.Add(new Entry(step, false));
        else if (proposerId.Length > 0 && StepById(proposerId) is { } proposer)
            Route(proposer, State.Steps[proposer.Id].Current?.Outcome ?? "");
    }

    /// <summary>A full step id or a fragment use id in the current plan, as the step a run continues at.</summary>
    string? FindIn(string id) => FindIn(Plan, byId, id);

    static string? FindIn(Plan plan, IReadOnlyDictionary<string, Step> steps, string id) =>
        steps.ContainsKey(id) ? id : plan.Instances.FirstOrDefault(i => i.Id == id)?.FirstStepId;

    /// <summary>
    /// The one path every patch takes. A person's patch (<c>amend</c>) is not bound by F3 but touches protected content only
    /// when <paramref name="mayTouchProtected"/>, may omit <c>resume_at</c> (the run continues where it was), and changes a
    /// role only as <c>change_role</c> (<paramref name="roleChange"/>).
    /// </summary>
    PatchResult Propose(PatchDocument patch, Step? proposer, bool approved = false, bool fromChild = false, string? childStepId = null,
        bool mayTouchProtected = true, bool roleChange = false)
    {
        var human = patch.ProposedBy.Type == "human";
        var d = new DiagnosticBag();
        if (libraries is null || template is null)
        {
            d.Add(Checks.PatchLoadChecks, "this run has no template to load the patched plan from");
            return Reject(patch, d);
        }

        if (patch.BaseRevision != State.Revision)
            d.Add(Checks.PatchBaseRevision, $"base_revision {patch.BaseRevision} is not the current revision {State.Revision}; re-propose against it", patch.Location);
        if (!fromChild)
            CheckProposer(patch, proposer, d, roleChange);
        else
            CheckRoleUnchanged(patch, d);
        CheckRunning(patch, proposer, d);
        if (d.HasErrors)
            return Reject(patch, d);

        var loaded = new DiagnosticBag();
        var next = Load(libraries, template, Run, State.Patches.Append(patch).ToList(), loaded);
        foreach (var x in loaded.Where(x => x.Blocks))
        {
            switch (x.Check)
            {
                case Checks.DuplicateId or Checks.PlanSize or Checks.PatchLoadChecks:
                    d.AddRange([x]);
                    break;
                case Checks.EvidenceType or Checks.WriteAccess:
                    d.Add(Checks.PatchFloor, $"relaxes a safety floor (check {x.Check}, {Checks.Title(x.Check)}): {x.Message}", x.Location, x.StepId);
                    break;
                default:
                    d.Add(Checks.PatchLoadChecks, $"the patched plan fails check {x.Check} ({Checks.Title(x.Check)}): {x.Message}", x.Location, x.StepId);
                    break;
            }
        }

        if (next is not null)
        {
            var nextIds = next.AllSteps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var id in nextIds.Where(id => !byId.ContainsKey(id) && State.IdHistory.Contains(id)).Order(StringComparer.Ordinal))
                d.Add(Checks.DuplicateId, $"step id '{id}' was used earlier in this run for a different step", patch.Location, id);

            var nextById = next.AllSteps.GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First());
            if (patch.ResumeAt is null)
            {
                if (!fromChild && !human)
                    d.Add(Checks.PatchResumeAt, "resume_at is required", patch.Location);
            }
            else if (FindIn(next, nextById, patch.ResumeAt) is null)
                d.Add(Checks.PatchResumeAt, $"resume_at '{patch.ResumeAt}' is not a step after the patch", patch.Location);
            CheckCeilings(next, d);
        }

        if (human && !mayTouchProtected && next is not null && ProtectedReasons(patch, next, null).ToList() is { Count: > 0 } guarded)
            foreach (var reason in guarded)
                d.Add(Checks.PatchProtected, $"{reason}: a person's patch to protected content needs {Model.Overrides.AmendProtected}", patch.Location);

        if (d.HasErrors)
            return Reject(patch, d);

        if (!approved && patch.ProposedBy.Type == "agent" && PendingReasons(patch, proposer, next!).ToList() is { Count: > 0 } reasons)
        {
            foreach (var reason in reasons)
                d.Add(Checks.PatchProtected, reason, patch.Location, proposer?.Id);
            var waitStep = fromChild ? childStepId ?? "" : proposer?.Id ?? "";
            var wait = new OpenWait(NewWaitId("patch_approval"), WaitKind.PatchApproval, waitStep, null, string.Join("; ", reasons), patch, fromChild);
            State.Waits.Add(wait);
            Trace("patch_pending", waitStep, string.Join("; ", reasons));
            outbox.Add(new RequestPatchApproval(wait.Id, patch));
            return new PatchResult(PatchStatus.Pending, d);
        }

        State.Patches.Add(patch);
        SetPlan(next!);
        foreach (var step in Plan.AllSteps)
            State.IdHistory.Add(step.Id);
        Trace("patch_applied", proposer?.Id ?? "", $"revision {State.Revision}, by {patch.ProposedBy.Type}");
        return new PatchResult(PatchStatus.Applied, d);
    }

    PatchResult Reject(PatchDocument patch, DiagnosticBag d)
    {
        Trace("patch_rejected", patch.ProposedBy.StepId ?? "", string.Join("; ", d.Where(x => x.Blocks).Select(x => $"check {x.Check}: {x.Message}")));
        return new PatchResult(PatchStatus.Rejected, d);
    }

    /// <summary>Check 27: the proposer may use each operation; an agent never changes a role, and a person only by <c>change_role</c>.</summary>
    void CheckProposer(PatchDocument patch, Step? proposer, DiagnosticBag d, bool roleChange = false)
    {
        switch (patch.ProposedBy.Type)
        {
            case "human" when !roleChange:
                foreach (var op in patch.Changes.Where(o => o.Op == "replace"))
                    if (StepById(op.Target)?.Agent?.Role?.Text is { } held && RoleOf(op.Step) != held)
                        d.Add(Checks.PatchProposer, $"replace of {op.Target} changes its role from {held} to {RoleOf(op.Step) ?? "none"}; a person changes a role with change_role (docs/05 section 5)", op.Location);
                break;
            case "agent":
                if (proposer?.Agent?.Planning is not { } planning || !planning.Targets.Contains("self"))
                {
                    d.Add(Checks.PatchProposer, $"{patch.ProposedBy.StepId ?? "the proposer"} has no planning authority over this run's plan (docs/04 section 6.1)", patch.Location);
                    return;
                }

                foreach (var op in patch.Changes.Where(o => o.Op == "replace"))
                    if (StepById(op.Target)?.Agent?.Role?.Text is { } role && RoleOf(op.Step) != role)
                        d.Add(Checks.PatchProposer, $"replace of {op.Target} changes its role from {role} to {RoleOf(op.Step) ?? "none"}; an agent never changes a role (P8)", op.Location);
                break;
            case "engine":
                foreach (var op in patch.Changes.Where(o => o.Op is not ("add_branch" or "chain" or "insert")))
                    d.Add(Checks.PatchProposer, $"the engine only expands lists (add_branch, chain, insert), not {op.Op}", op.Location);
                break;
        }
    }

    /// <summary>Check 27 for a patch whose authority was checked elsewhere (a child's): an agent never changes a role.</summary>
    void CheckRoleUnchanged(PatchDocument patch, DiagnosticBag d)
    {
        foreach (var op in patch.Changes.Where(o => o.Op == "replace"))
            if (StepById(op.Target)?.Agent?.Role?.Text is { } role && RoleOf(op.Step) != role)
                d.Add(Checks.PatchProposer, $"replace of {op.Target} changes its role from {role} to {RoleOf(op.Step) ?? "none"}; an agent never changes a role (P8)", op.Location);
    }

    static string? RoleOf(YMap? step) => (step?["agent"] as YMap)?["role"].ScalarText();

    /// <summary>Run-time rules on a patch: branches change only on a parallel that has not started; a running step stays.</summary>
    void CheckRunning(PatchDocument patch, Step? proposer, DiagnosticBag d)
    {
        foreach (var op in patch.Changes)
        {
            if (op.Op is "add_branch" or "remove_branch" && State.Containers.TryGetValue(op.Target, out var container) && container.Active)
                d.Add(Checks.PatchLoadChecks, $"{op.Op}: {op.Target} has started; branches change only before it starts (docs/03 section 10.2)", op.Location, op.Target);
            // The proposing step is ending as its patch is applied, so it does not count as running.
            if (op.Op is "remove" or "replace" && Busy(op.Target, proposer?.Id) is { } busy)
                d.Add(Checks.PatchLoadChecks, $"{op.Op}: {busy} is running or waiting{(busy == op.Target ? "" : $" inside {op.Target}")}", op.Location, op.Target);
        }
    }

    /// <summary>A step at or inside the target that is running or waiting for a person, or an entered container.</summary>
    string? Busy(string target, string? except)
    {
        bool At(string id) => id != except && (id == target || InContainer(id, target));
        return State.InFlight.FirstOrDefault(At)
               ?? State.Waits.Select(w => w.StepId).FirstOrDefault(At)
               ?? State.Pending.Select(e => e.StepId).FirstOrDefault(At)
               ?? (State.Containers.TryGetValue(target, out var c) && c.Active ? target : null);
    }

    /// <summary>Check 22: no allowance above the organisation's per-step cap, for steps the patch adds or changes (LOG 78).</summary>
    void CheckCeilings(Plan next, DiagnosticBag d)
    {
        if (Options.StepCeiling is not { } ceiling)
            return;
        foreach (var step in next.AllSteps.Where(s => s.ConsumesAttempts))
        {
            var before = StepById(step.Id);
            var attempts = Allowance(step);
            var races = RaceAllowance(step);
            if (attempts > ceiling && (before is null || Allowance(before) != attempts))
                d.Add(Checks.LockedCap, $"sets {step.Id}'s attempts to {attempts}, above the cap of {ceiling} (F4)", step.Location, step.Id);
            if (step.Action == "scm.merge" && races > ceiling && (before is null || RaceAllowance(before) != races))
                d.Add(Checks.LockedCap, $"sets {step.Id}'s races to {races}, above the cap of {ceiling} (F4)", step.Location, step.Id);
        }
    }

    /// <summary>
    /// Check 29: an agent's patch that touches protected content, adds or changes a step to use an unlisted role (F10), or
    /// must be reviewed: the step's <c>planning.review: required</c>, or the organisation's <c>planning.require_review</c>.
    /// </summary>
    IEnumerable<string> PendingReasons(PatchDocument patch, Step? proposer, Plan next)
    {
        foreach (var reason in ProtectedReasons(patch, next, proposer?.Id))
            yield return reason;
        foreach (var op in patch.Changes)
            if (RoleOf(op.Step) is { } role && Options.AllowedRoles is { } allowed
                && !allowed.Contains(LibraryReference.TryParse(role)?.Name ?? role))
                yield return $"{op.Op} uses role {role}, which is not in planning.allowed_roles";

        if (proposer?.Agent?.Planning?.Review == "required")
            yield return $"{proposer.Id} has planning.review: required";
        else if (Options.RequireReview)
            yield return "the organisation requires review of agent patches for this template (planning.require_review)";
    }

    /// <summary>
    /// How a patch touches protected content (F3; docs/03 section 10.3): it removes or changes a protected step or a route out
    /// of it, or it leaves a protected step unrun that the run would otherwise run (LOG 210): one the run is at, or one on
    /// every path from where the run is to <c>end: completed</c>, every guarded route counted as possible. After the patch the
    /// run continues from where it is, from <c>resume_at</c>, and from the proposer's own routes.
    /// </summary>
    IEnumerable<string> ProtectedReasons(PatchDocument patch, Plan next, string? proposerId)
    {
        foreach (var op in patch.Changes)
            if (StepById(op.Target) is { Protected: true } target && (op.Op != "insert" || op.Position == "after"))
                yield return $"{op.Op} touches protected step {target.Id} (F3)";

        var before = Positions().Concat(proposerId is null ? [] : [proposerId]).ToList();
        // The proposer stays a way on: its routes are how the run leaves it whenever it runs again.
        var after = Positions().Where(id => id != proposerId).Concat(proposerId is null ? [] : [proposerId]).ToList();
        if (patch.ResumeAt is { } at && FindIn(next, next.AllSteps.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First()), at) is { } resume)
            after.Add(resume);
        foreach (var left in StillToRun(Plan, before).Except(StillToRun(next, after)))
            yield return $"leaves protected step {left} unrun (F3)";
    }

    /// <summary>Where the run is: steps entered and not started, steps running, steps waiting for a person, and parallels that have started.</summary>
    List<string> Positions() =>
        State.Pending.Select(e => e.StepId).Concat(State.InFlight)
            .Concat(State.Waits.Where(w => w.Kind != WaitKind.PatchApproval).Select(w => w.StepId))
            .Concat(State.Containers.Where(c => c.Value.Active).Select(c => c.Key)).Distinct().ToList();

    /// <summary>The protected steps a plan still runs from <paramref name="starts"/>: one it is at, or one on every path to <c>end: completed</c>. A step a person skipped does not count.</summary>
    List<string> StillToRun(Plan plan, IReadOnlyCollection<string> starts) =>
        plan.AllSteps.Where(s => s.Protected && !State.Skips.ContainsKey(s.Id) && (starts.Contains(s.Id) || !Completes(plan, starts, s.Id)))
            .Select(s => s.Id).ToList();

    /// <summary>
    /// Whether the run can reach <c>end: completed</c> from <paramref name="starts"/> without running <paramref name="avoid"/>.
    /// A parallel leads to each of its branches; a branch's end leads on to its parallel's routes. Escalations lead nowhere: a
    /// person decides what happens next.
    /// </summary>
    static bool Completes(Plan plan, IEnumerable<string> starts, string avoid)
    {
        const string After = "\u0000after:";
        var steps = plan.AllSteps.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First());
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>(starts.Where(steps.ContainsKey));
        while (queue.TryDequeue(out var id))
        {
            if (id == avoid || !seen.Add(id))
                continue;
            IEnumerable<Resolved> next;
            if (id.StartsWith(After, StringComparison.Ordinal))
                next = steps[id[After.Length..]].ResolvedRoutes.Select(r => r.Target);
            else if (steps[id].Container is { } container && container.Branches.Any(b => b.Steps.Count > 0))
            {
                foreach (var branch in container.Branches)
                    queue.Enqueue(branch.Steps.Count > 0 ? branch.Steps[0].Id : After + id);
                continue;
            }
            else
                next = steps[id].ResolvedRoutes.Select(r => r.Target);

            foreach (var target in next.SelectMany(Leaves))
                switch (target)
                {
                    case ResolvedEnd { Status: "completed" }:
                        return true;
                    case ResolvedStep to:
                        queue.Enqueue(to.StepId);
                        break;
                    case ResolvedBranchEnd branchEnd:
                        queue.Enqueue(After + branchEnd.ContainerId);
                        break;
                }
        }

        return false;
    }

    static IEnumerable<Resolved> Leaves(Resolved target) => target is ResolvedGuarded g ? g.Entries.SelectMany(e => Leaves(e.Target)) : [target];
}

/// <summary>
/// <c>plan.expand</c> as a deterministic patch (docs/04 section 6.2): each item of a list becomes a step,
/// added as a branch of the target <c>parallel</c> or chained after the target. No agent is involved, so the
/// same list always gives the same patch.
/// </summary>
public static class PlanExpansion
{
    public const int MaxItems = 500;

    /// <summary>Builds the patch for a <c>plan.expand</c> step from its list's items, against the core's current revision.</summary>
    public static (PatchDocument? Patch, string? Error) Build(RunCore core, string stepId, IReadOnlyList<YNode> items)
    {
        if (core.StepById(stepId) is not { Action: "plan.expand", With: { } with } step)
            return (null, $"{stepId} is not a plan.expand step with inputs");
        if (items.Count > MaxItems)
            return (null, $"the list has {items.Count} items; plan.expand takes at most {MaxItems}");
        var shape = with["shape"].ScalarText() ?? "parallel";
        var targetText = with["target"].ScalarText();
        var stepTemplate = with["step"] as YMap;
        if (targetText is null || stepTemplate is null)
            return (null, $"{stepId}: plan.expand needs target and step");
        if (core.FindStep(targetText, step.Scope) is not { } target)
            return (null, $"{stepId}: target '{targetText}' is not a step");
        var onOther = with["on_other"].ScalarText();
        if (shape == "chain" && onOther is null)
            return (null, $"{stepId}: a chain needs on_other");

        var changes = new List<PatchOp>();
        var previous = target.Id;
        string? first = null;
        for (var i = 0; i < items.Count; i++)
        {
            var (map, error) = Substitute(stepTemplate, items[i]);
            if (error is not null)
                return (null, $"{stepId}: item {i}: {error}");
            var localId = $"{target.LocalId}_{i}";
            var entries = new List<KeyValuePair<YScalar, YNode>> { new(new YScalar("id", true, map!.Location), new YScalar(localId, true, map.Location)) };
            entries.AddRange(((YMap)map).Entries.Where(e => e.Key.Text != "id"));
            var stepMap = new YMap(entries, map.Location);
            var fullId = target.Scope.Prefix + localId;
            first ??= fullId;
            changes.Add(shape == "chain"
                ? new PatchOp("chain", previous, step.Location, Step: stepMap, OnOther: onOther)
                : new PatchOp("add_branch", target.Id, step.Location, Step: stepMap));
            previous = fullId;
        }

        var expanded = step.ResolvedRoutes.FirstOrDefault(r => r.Outcome == "expanded")?.Target as ResolvedStep;
        var resumeAt = shape == "chain" && target.Id == step.Id
            ? first ?? expanded?.StepId
            : shape == "parallel" ? target.Id : expanded?.StepId ?? target.Id;
        var patch = new PatchDocument(core.Run.Id, new Proposer("engine", step.Id), $"plan.expand of {items.Count} item(s)",
            core.State.Revision, resumeAt, changes, step.Location);
        return (patch, null);
    }

    /// <summary>
    /// Replaces each string <c>$.field</c> or <c>$.a.b</c> in the step with the item's value at that path. A path ending in
    /// <c>?</c> is optional: a map entry whose optional path the item lacks is left out (LOG 185).
    /// </summary>
    static (YNode? Node, string? Error) Substitute(YNode node, YNode item)
    {
        switch (node)
        {
            case YScalar s when !s.IsNull && s.Text.StartsWith("$.", StringComparison.Ordinal):
                YNode? value = item;
                foreach (var part in s.Text[2..].TrimEnd('?').Split('.'))
                    value = (value as YMap)?[part];
                return value is null ? (null, $"the item has no field {s.Text}") : (value, null);
            case YMap m:
                var entries = new List<KeyValuePair<YScalar, YNode>>();
                foreach (var (key, value2) in m.Entries)
                {
                    if (value2 is YScalar { IsNull: false } optional && optional.Text.StartsWith("$.", StringComparison.Ordinal) && optional.Text.EndsWith('?')
                        && Substitute(optional, item).Error is not null)
                        continue;
                    var (v, e) = Substitute(value2, item);
                    if (e is not null)
                        return (null, e);
                    entries.Add(new(key, v!));
                }

                return (new YMap(entries, m.Location), null);
            case YSeq q:
                var items = new List<YNode>();
                foreach (var x in q.Items)
                {
                    var (v, e) = Substitute(x, item);
                    if (e is not null)
                        return (null, e);
                    items.Add(v!);
                }

                return (new YSeq(items, q.Location), null);
            default:
                return (node, null);
        }
    }
}
