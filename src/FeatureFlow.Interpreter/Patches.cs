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
            RemoveWait(wait, WaitEnds.Answered);
            Trace(answer.Approve ? "library_override_approved" : "library_override_rejected", wait.StepId, $"{item} by {answer.Actor}");
            if (answer.Approve)
                outbox.Add(new OverrideLibrary(wait.Id, item, wait.LibraryContent ?? "", answer.Actor, $"approved proposal of {wait.StepId}"));
            return ApplyResult.Ok;
        }

        if (wait?.Patch is not { } patch)
            return ApplyResult.Refused($"no patch approval is open as {answer.WaitId}");
        RemoveWait(wait, WaitEnds.Answered);
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
        if (StepById(input.ChildStepId) is not { Descriptor.Handling: ActionHandling.ChildRun })
            return ApplyResult.Refused($"{input.ChildStepId} is not a run.child step of plan revision {State.Revision}");
        var patch = input.Patch with { ProposedBy = new Proposer("agent", input.ProposerStepId) };
        var result = Propose(patch, null, fromChild: true, childStepId: input.ChildStepId, reviewRequired: input.ReviewRequired);
        if (result.Status == PatchStatus.Applied && patch.ResumeAt is { } at && FindIn(at) is { } resume)
            State.Pending.Add(new Entry(resume, false));
        return new ApplyResult(true, [], result);
    }

    void Resume(PatchDocument patch, string proposerId)
    {
        if (patch.ResumeAt is { } at && FindIn(at) is { } step)
        {
            if (ReExpanded(patch, step) is { } container)
                ResumeExpanded(container);
            else
                State.Pending.Add(new Entry(step, false));
        }
        else if (proposerId.Length > 0 && StepById(proposerId) is { } proposer)
            Route(proposer, State.Steps[proposer.Id].Current?.Outcome ?? "");
    }

    /// <summary>
    /// The parallel a <c>plan.expand</c> patch expanded again, when an earlier expansion filled it and it has run (docs/03
    /// section 10.4), else null: a first expansion resumes at its target as any patch does.
    /// </summary>
    Step? ReExpanded(PatchDocument patch, string resumeAt)
    {
        if (patch.ProposedBy is not { Type: "engine", StepId: { } by } || StepById(by)?.Descriptor?.Handling != ActionHandling.Expand
            || StepById(resumeAt) is not { Container: not null, Form: not StepForm.Chain } container || !State.Containers.ContainsKey(container.Id))
            return null;
        var earlier = State.Patches.Take(State.Patches.Count - 1).Where(p => p.ProposedBy.Type == "engine").SelectMany(p => p.Changes);
        return earlier.Any(op => op.Op == "add_branch" && op.Target == container.Id) ? container : null;
    }

    /// <summary>
    /// Resumes a parallel an expansion changed as a go to its branches does (<see cref="Reopen"/>): only the branches that have
    /// not ended with class <c>success</c> run, new ones included; a succeeded branch keeps its outcome for the join, which
    /// decides again. A parallel still running goes on: its join decides as it would have.
    /// </summary>
    void ResumeExpanded(Step container)
    {
        var before = State.Containers[container.Id];
        if (before.Active)
            return;
        var succeeded = new HashSet<int>(before.BranchIndexes.Where((_, i) => before.Branches[i] == BranchStatus.Succeeded));
        var branches = container.Container!.Branches;
        var state = new ContainerRecord
        {
            BranchIndexes = branches.Select(b => b.Index).ToList(),
            Branches = branches.Select(b => succeeded.Contains(b.Index) ? BranchStatus.Succeeded : BranchStatus.Waiting).ToList(),
        };
        State.Containers[container.Id] = state;
        Trace("reopened", container.Id,
            $"expanded again: {state.Branches.Count(b => b == BranchStatus.Waiting)} branch(es) run, {state.Branches.Count(b => b == BranchStatus.Succeeded)} succeeded kept; the join decides again");
        DecideJoin(container, state);
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
        bool mayTouchProtected = true, bool roleChange = false, bool reviewRequired = false)
    {
        var (d, next, reasons) = CheckPatch(patch, proposer, approved, fromChild, mayTouchProtected, roleChange, reviewRequired);
        if (d.HasErrors)
            return Reject(patch, d);

        if (reasons.Count > 0)
        {
            foreach (var reason in reasons)
                d.Add(Checks.PatchProtected, reason, patch.Location, proposer?.Id);
            var waitStep = fromChild ? childStepId ?? "" : proposer?.Id ?? "";
            var wait = AddWait(new OpenWait(NewWaitId("patch_approval"), WaitKind.PatchApproval, waitStep, null, string.Join("; ", reasons), patch, fromChild));
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

    /// <summary>
    /// The checks of <see cref="Propose"/> without its effects: the diagnostics (any error refuses the patch), the plan after
    /// it, and why a person must approve it (check 29; empty when it applies). Changes no state, so a preview calls it alone.
    /// <paramref name="reviewRequired"/> is a child's proposing step's <c>planning.review: required</c>, which the parent applies.
    /// </summary>
    (DiagnosticBag Diagnostics, Plan? Next, IReadOnlyList<string> Pending) CheckPatch(PatchDocument patch, Step? proposer, bool approved = false,
        bool fromChild = false, bool mayTouchProtected = true, bool roleChange = false, bool reviewRequired = false)
    {
        var human = patch.ProposedBy.Type == "human";
        var d = new DiagnosticBag();
        if (libraries is null || template is null)
        {
            d.Add(Checks.PatchLoadChecks, "this run has no template to load the patched plan from");
            return (d, null, []);
        }

        if (patch.BaseRevision != State.Revision)
            d.Add(Checks.PatchBaseRevision, $"base_revision {patch.BaseRevision} is not the current revision {State.Revision}; re-propose against it", patch.Location);
        if (!fromChild)
            CheckProposer(patch, proposer, d, roleChange);
        else
            CheckRoleUnchanged(patch, d);
        CheckRunning(patch, proposer, d);
        if (d.HasErrors)
            return (d, null, []);

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
            return (d, next, []);

        var reasons = !approved && patch.ProposedBy.Type == "agent" ? PendingReasons(patch, proposer, next!, fromChild, reviewRequired).ToList() : [];
        return (d, next, reasons);
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
                foreach (var op in patch.Changes.Where(o => o.Op is not ("add_branch" or "remove_branch" or "chain" or "insert")))
                    d.Add(Checks.PatchProposer, $"the engine only expands lists (add_branch, remove_branch, chain, insert), not {op.Op}", op.Location);
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

    /// <summary>Whether a step, or one inside it, is running, waiting for a person or about to start.</summary>
    public bool IsBusy(string stepId) => Busy(stepId, null) is not null;

    /// <summary>A step at or inside the target that is running or waiting for a person, or an entered container.</summary>
    string? Busy(string target, string? except)
    {
        bool At(string id) => id != except && (id == target || InContainer(id, target));
        return State.InFlight.FirstOrDefault(At)
               ?? State.Waits.Select(w => w.StepId).FirstOrDefault(At)
               ?? State.Pending.Select(e => e.StepId).FirstOrDefault(At)
               ?? (State.Containers.TryGetValue(target, out var c) && c.Active ? target : null);
    }

    /// <summary>
    /// Check 22 on a patch: the steps it adds or changes are held to the organisation's caps by the same function as at run
    /// start (<see cref="StepCaps"/>; LOG 78, F1). A breach a step already had is not the patch's: run start refused it.
    /// </summary>
    void CheckCeilings(Plan next, DiagnosticBag d)
    {
        var caps = Options.StepCaps;
        foreach (var step in next.AllSteps)
        {
            var before = StepById(step.Id);
            var had = before is null ? [] : caps.Breaches(before, Allowance(before)).ToHashSet(StringComparer.Ordinal);
            foreach (var breach in caps.Breaches(step, Allowance(step)).Where(b => !had.Contains(b)))
                d.Add(Checks.LockedCap, breach, step.Location, step.Id);
        }
    }

    /// <summary>
    /// Check 29: an agent's patch that touches protected content, adds or changes a step to use an unlisted role (F10) or a
    /// <c>run.child</c> to start a template not in the proposer's <c>planning.templates</c>, or must be reviewed: the proposing
    /// step's <c>planning.review: required</c> (a child's, carried as <paramref name="reviewRequired"/>), or the organisation's
    /// <c>planning.require_review</c>.
    /// </summary>
    IEnumerable<string> PendingReasons(PatchDocument patch, Step? proposer, Plan next, bool fromChild = false, bool reviewRequired = false)
    {
        foreach (var reason in ProtectedReasons(patch, next, proposer?.Id))
            yield return reason;
        foreach (var op in patch.Changes)
            if (RoleOf(op.Step) is { } role && Options.AllowedRoles is { } allowed
                && !allowed.Contains(LibraryReference.TryParse(role)?.Name ?? role))
                yield return $"{op.Op} uses role {role}, which is not in planning.allowed_roles";
        if (!fromChild)
            foreach (var reason in TemplateReasons(next, proposer?.Agent?.Planning?.Templates ?? []))
                yield return reason;

        var by = patch.ProposedBy.StepId ?? "the proposer";
        if (fromChild ? reviewRequired : proposer?.Agent?.Planning?.Review == "required")
            yield return fromChild ? $"{by} (in the child run) has planning.review: required" : $"{proposer!.Id} has planning.review: required";
        else if (Options.RequireReview)
            yield return "the organisation requires review of agent patches for this template (planning.require_review)";
    }

    /// <summary>
    /// The <c>run.child</c> steps a patch adds, or whose <c>template</c> it changes, that start a template not in
    /// <paramref name="granted"/> (the proposer's <c>planning.templates</c>): a person decides, as for an unlisted role (F10). A
    /// template that is not a literal reference is unlisted. A child's patch is not held to it: the parent does not hold the
    /// child step's grant, and consented to the child by starting its template (design note s4).
    /// </summary>
    IEnumerable<string> TemplateReasons(Plan next, IReadOnlyList<ItemRef> granted)
    {
        static YNode? TemplateOf(Step s) => s.Descriptor?.Handling == ActionHandling.ChildRun ? s.With?["template"] : null;
        foreach (var step in next.AllSteps)
        {
            if (TemplateOf(step) is not { } node)
                continue;
            var text = YamlWriter.Write(node);
            if (StepById(step.Id) is { } before && TemplateOf(before) is { } was && YamlWriter.Write(was) == text)
                continue;
            var reference = node.ScalarText() is { } scalar ? LibraryReference.TryParse(scalar) : null;
            if (reference is null || !granted.Any(g => g.Reference == reference))
                yield return $"{step.Id} starts template {node.ScalarText() ?? text.Trim()}, which is not in planning.templates";
        }
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
        // A step added inside protected content (a protected fragment, parallel or chain) changes it, whatever the operation.
        foreach (var added in next.AllSteps.Where(s => StepById(s.Id) is null && InsideProtected(next, s)))
            yield return $"adds {added.Id} inside protected content (F3)";

        var before = Positions().Concat(proposerId is null ? [] : [proposerId]).ToList();
        // The proposer stays a way on: its routes are how the run leaves it whenever it runs again.
        var after = Positions().Where(id => id != proposerId).Concat(proposerId is null ? [] : [proposerId]).ToList();
        if (patch.ResumeAt is { } at && FindIn(next, next.AllSteps.GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First()), at) is { } resume)
            after.Add(resume);
        foreach (var left in StillToRun(Plan, before).Except(StillToRun(next, after)))
            yield return $"leaves protected step {left} unrun (F3)";
    }

    /// <summary>
    /// Whether a step is protected content: protected itself (the loader marks a step in a protected fragment so), or inside a
    /// protected parallel or chain.
    /// </summary>
    static bool InsideProtected(Plan plan, Step step)
    {
        if (step.Protected)
            return true;
        for (var scope = step.Scope; scope is not null; scope = scope.Parent)
            if (scope.ContainerId is { } id && plan.AllSteps.FirstOrDefault(s => s.Id == id) is { Protected: true })
                return true;
        return false;
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

    /// <summary>
    /// Builds the patch for a <c>plan.expand</c> step from its list's items, against the core's current revision, with the
    /// <paramref name="inputs"/> check 31 passed at step start (<see cref="RunCore.StepSpec"/>), so what was checked is what is
    /// used (LOG 488). <c>step</c> is taken as written, since its expressions and <c>$.</c> paths belong to the steps it makes.
    /// </summary>
    public static (PatchDocument? Patch, string? Error) Build(RunCore core, string stepId, IReadOnlyDictionary<string, object?> inputs, IReadOnlyList<YNode> items)
    {
        if (core.StepById(stepId) is not { Descriptor.Handling: ActionHandling.Expand, With: { } with } step)
            return (null, $"{stepId} is not a plan.expand step with inputs");
        if (items.Count > MaxItems)
            return (null, $"the list has {items.Count} items; plan.expand takes at most {MaxItems}");

        var shape = inputs.GetValueOrDefault("shape") as string ?? "parallel";
        var targetText = inputs.GetValueOrDefault("target") as string;
        var stepTemplate = with["step"] as YMap;
        if (targetText is null || stepTemplate is null)
            return (null, $"{stepId}: plan.expand needs target and step");
        if (core.Plan.FindStep(targetText, step.Scope) is not { } target)
            return (null, $"{stepId}: target '{targetText}' is not a step");
        var onOther = inputs.GetValueOrDefault("on_other") as string;
        if (shape == "chain" && onOther is null)
            return (null, $"{stepId}: a chain needs on_other");

        var mapped = new List<YMap>();
        for (var i = 0; i < items.Count; i++)
        {
            var (map, error) = Substitute(stepTemplate, items[i]);
            if (error is not null)
                return (null, $"{stepId}: item {i}: {error}");
            mapped.Add(new YMap(((YMap)map!).Entries.Where(e => e.Key.Text != "id").ToList(), map.Location));
        }

        // A parallel that earlier expansions filled is expanded again (docs/03 section 10.4): an item already expanded is kept,
        // a new or changed one added at the next free index, and a branch no item matches removed.
        var expandedBefore = shape == "parallel" ? Expanded(core, target) : [];
        var kept = new List<string>();
        var removed = new List<string>();
        var adding = mapped;
        if (expandedBefore.Count > 0)
        {
            var unmatched = new List<YMap>();
            var open = expandedBefore.ToList();
            foreach (var map in mapped)
            {
                var text = YamlWriter.Write(map);
                var at = open.FindIndex(b => b.Text == text);
                if (at < 0)
                {
                    unmatched.Add(map);
                    continue;
                }

                kept.Add(open[at].StepId);
                open.RemoveAt(at);
            }

            adding = unmatched;
            // A branch that is running or waiting is left for a person (docs/03 section 10.4).
            removed.AddRange(open.Select(b => b.StepId).Where(id => !core.IsBusy(id)));
        }

        var changes = new List<PatchOp>();
        var previous = target.Id;
        string? first = null;
        var next = shape == "parallel" ? NextFreeIndex(core, target) : 0;
        foreach (var map in adding)
        {
            var localId = $"{target.LocalId}_{next++}";
            var entries = new List<KeyValuePair<YScalar, YNode>> { new(new YScalar("id", true, map.Location), new YScalar(localId, true, map.Location)) };
            entries.AddRange(map.Entries);
            var stepMap = new YMap(entries, map.Location);
            var fullId = target.Scope.Prefix + localId;
            first ??= fullId;
            changes.Add(shape == "chain"
                ? new PatchOp("chain", previous, step.Location, Step: stepMap, OnOther: onOther)
                : new PatchOp("add_branch", target.Id, step.Location, Step: stepMap));
            previous = fullId;
        }

        // Added before removed, so a new branch never takes the place of one removed (Branch.Index).
        changes.AddRange(removed.Select(id => new PatchOp("remove_branch", target.Id, step.Location, Branch: id)));

        var expanded = step.ResolvedRoutes.FirstOrDefault(r => r.Outcome == "expanded")?.Target as ResolvedStep;
        var resumeAt = shape == "chain" && target.Id == step.Id
            ? first ?? expanded?.StepId
            : shape == "parallel" ? target.Id : expanded?.StepId ?? target.Id;
        var reason = $"plan.expand of {items.Count} item(s)";
        if (expandedBefore.Count > 0)
            reason += $": kept {Names(kept)}; added {Names(changes.Where(c => c.Op == "add_branch").Select(c => target.Scope.Prefix + c.Step!["id"].ScalarText()))}; removed {Names(removed)}";
        var patch = new PatchDocument(core.Run.Id, new Proposer("engine", step.Id), reason, core.State.Revision, resumeAt, changes, step.Location);
        return (patch, null);
    }

    static string Names(IEnumerable<string> ids) => ids.ToList() is { Count: > 0 } list ? string.Join(", ", list) : "none";

    /// <summary>
    /// The branches of a parallel that an engine's <c>add_branch</c> made (an earlier <c>plan.expand</c>), each with its step as
    /// the expansion wrote it, less its id: what an item now must equal to be the same item. Branches written in the template
    /// are not an expansion's.
    /// </summary>
    static List<(string StepId, string Text)> Expanded(RunCore core, Step target)
    {
        var written = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var op in core.State.Patches.Where(p => p.ProposedBy.Type == "engine").SelectMany(p => p.Changes))
            if (op is { Op: "add_branch", Step: { } map } && op.Target == target.Id && map["id"].ScalarText() is { } id)
                written[target.Scope.Prefix + id] = YamlWriter.Write(new YMap(map.Entries.Where(e => e.Key.Text != "id").ToList(), map.Location));
        return target.Container!.Branches.Where(b => b.Steps.Count > 0 && written.ContainsKey(b.Steps[0].Id))
            .Select(b => (b.Steps[0].Id, written[b.Steps[0].Id])).ToList();
    }

    /// <summary>
    /// The first index whose step id <c>&lt;target&gt;_&lt;index&gt;</c> this run has never used: one more than the highest so far,
    /// so a removed branch's id is not used again for another step (check 3). 0 on a first expansion.
    /// </summary>
    static int NextFreeIndex(RunCore core, Step target)
    {
        var prefix = $"{target.Scope.Prefix}{target.LocalId}_";
        var used = core.State.IdHistory.Where(id => id.StartsWith(prefix, StringComparison.Ordinal))
            .Select(id => int.TryParse(id[prefix.Length..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1)
            .DefaultIfEmpty(-1).Max();
        return used + 1;
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
