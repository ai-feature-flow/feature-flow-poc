using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

/// <summary>
/// A person's run-time overrides (docs/05 section 5) as the core applies them: breakpoints (docs/04 section 4.5), skips,
/// attempt grants, role changes and mid-run library overrides (docs/08 section 5.2), and an agent's proposed library
/// override (docs/05 section 6). The API authorised the key and named the actor (F8); the core refuses what it alone can
/// judge: protected content without the variant, a grant beyond the hard ceiling, and anything that relaxes a floor.
/// </summary>
public sealed partial class RunCore
{
    /// <summary>The library content types a mid-run override may replace: what steps resolve, never the plan's structure or a role (F10).</summary>
    public static readonly IReadOnlyList<string> OverridableTypes = ["tasks", "prompts", "skills", "artifact_types"];

    /// <summary>Why a steering input would be refused now, or null: the check <see cref="RouteOverride"/> makes of an override's input.</summary>
    public string? SteeringCheck(RunInput input) =>
        State.Status != RunStatus.Running ? $"the run has ended ({State.Status.Name()})" : SteeringError(input);

    // ---- The override update (docs/05 section 5) ------------------------------------------------------

    /// <summary>The keys the <c>override</c> update carries out: answers keep the <c>answer</c> update (LOG 203).</summary>
    static readonly IReadOnlyList<string> UpdateKeys =
        [Overrides.Breakpoint, Overrides.Skip, Overrides.Amend, Overrides.ChangeRole, Overrides.GrantAttempts, Overrides.OverrideLibrary, Overrides.Abort];

    /// <summary>
    /// Where an override goes, without changing anything: a duplicate of the one applied with its id (with its first result),
    /// refused with the reason, the input to apply, or a library override whose content the shell stores first. The update
    /// validator calls this alone; <see cref="Override"/> routes the same way, so the two cannot disagree.
    /// </summary>
    public AnswerRoute RouteOverride(OverrideArgs o)
    {
        if (State.Overrides.FirstOrDefault(a => a.Id == o.OverrideId) is { } previous)
            return previous.Key == o.Key ? new Duplicate(previous.Result) : new Refused($"conflict: override {o.OverrideId} was already applied as {previous.Key}");
        if (OverrideError(o) is { } error)
            return new Refused(error);
        return o.Key switch
        {
            Overrides.OverrideLibrary => new StoresLibrary(o.Change["item"], o.Change["with"]),
            Overrides.Abort => new Applies(new RunAborted($"{o.Reason} (by {o.Actor})")),
            _ => new Applies(SteeringInput(o)!),
        };
    }

    /// <summary>
    /// Routes an override and applies it, then records it by its id with its result and notes it on the timeline. A library
    /// override (<see cref="StoresLibrary"/>) was applied by <see cref="LibraryOverridden"/> once the shell stored its content;
    /// its result is given as <paramref name="stored"/> and recorded without routing again. A duplicate changes nothing.
    /// </summary>
    public OverrideOutcome Override(OverrideArgs o, string? stored = null)
    {
        string result;
        var close = false;
        if (stored is not null)
            result = stored;
        else
            switch (RouteOverride(o))
            {
                case Duplicate d:
                    return new(null, "duplicate: " + d.Result);
                case Refused r:
                    return new(r.Error);
                case StoresLibrary:
                    return new($"{o.OverrideId}: a library override's content is stored by the run before it is applied");
                case Applies { Input: RunAborted aborted }:
                    close = Bool(o.Change, "close_pull_requests") == true;
                    Apply(aborted);
                    result = close ? "aborted; its open pull requests will be closed" : "aborted";
                    break;
                case Applies p:
                    var applied = Apply(p.Input);
                    if (!applied.Accepted || applied.Patch is { Status: not PatchStatus.Applied })
                        return new(string.Join("; ", applied.Errors.Concat(applied.Patch?.Diagnostics.Where(d => d.Blocks).Select(d => $"check {d.Check}: {d.Message}") ?? [])));
                    result = o.Key switch
                    {
                        Overrides.Amend or Overrides.ChangeRole => $"applied: plan revision {State.Revision}",
                        Overrides.Skip when State.Skips.ContainsKey(o.StepId!) => "applied: skipped when the run reaches it",
                        Overrides.Skip => "applied: skipped",
                        _ => "applied",
                    };
                    break;
                default:
                    throw new InvalidOperationException("unknown route");
            }

        if (ProtectedTouched(o) is { Count: > 0 } guarded)
            result += $" (protected: {string.Join(", ", guarded)})";
        State.Overrides.Add(new AppliedOverride(o.OverrideId, o.Key, result));
        Trace("override", o.StepId ?? "", $"{o.Key} {By(o.Actor, o.Reason)} -> {result}");
        return new(null, result, close);
    }

    /// <summary>Why an override would be refused now, or null: its key and arguments, then the core's own check.</summary>
    string? OverrideError(OverrideArgs o)
    {
        if (!UpdateKeys.Contains(o.Key))
            return Overrides.IsKey(o.Key)
                ? $"{o.Key} is not delivered as an override: answers, recoveries and answers to agents go through the answer update"
                : $"'{o.Key}' is not a run-time override key";
        if (State.Status != RunStatus.Running)
            return $"the run has ended ({State.Status.Name()})";
        string? Missing(params string[] names) => names.FirstOrDefault(n => !o.Change.TryGetValue(n, out var v) || string.IsNullOrWhiteSpace(v)) is { } name
            ? $"{o.Key} needs change.{name}"
            : null;
        switch (o.Key)
        {
            case Overrides.Breakpoint or Overrides.Skip or Overrides.ChangeRole when o.StepId is null:
                return $"{o.Key} needs a step";
            case Overrides.Skip when Missing("outcome") is { } missing:
                return missing;
            case Overrides.ChangeRole when Missing("role") is { } missing:
                return missing;
            case Overrides.Amend when Missing("patch") is { } missing:
                return missing;
            case Overrides.GrantAttempts when !long.TryParse(o.Change.GetValueOrDefault("attempts"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _):
                return "grant_attempts needs change.attempts, a whole number";
            case Overrides.Breakpoint when o.Change.ContainsKey("set") && Bool(o.Change, "set") is null:
                return "breakpoint's change.set is true or false";
            case Overrides.OverrideLibrary:
                if (Missing("item", "with") is { } absent)
                    return absent;
                if (LibraryItemError(o.Change["item"]) is { } itemError)
                    return itemError;
                return ProtectedTouched(o) is { Count: > 0 } guarded && !Holds(o, Overrides.OverrideLibraryProtected)
                    ? $"{string.Join(", ", guarded)} may resolve {o.Change["item"]}: overriding it needs {Overrides.OverrideLibraryProtected}"
                    : null;
            case Overrides.Abort:
                return null;
        }

        if (o.Key == Overrides.Amend)
            return ReadPatch(o.Change["patch"], out var d) is not { } patch ? "the patch does not read: " + string.Join("; ", d.Where(x => x.Blocks).Select(x => x.Message))
                : patch.ResumeAt is not null ? "an amend changes the plan, not where the run is: omit resume_at, then answer a breakpoint or intervention with go_to (LOG 209)"
                : null;
        return SteeringCheck(SteeringInput(o)!);
    }

    /// <summary>The core input an override stands for; the actor is the authenticated one the API named (F8).</summary>
    static RunInput? SteeringInput(OverrideArgs o) => o.Key switch
    {
        Overrides.Breakpoint => new BreakpointSet(o.StepId!, Bool(o.Change, "set") ?? true, o.Actor),
        Overrides.Skip => new StepSkipped(o.StepId!, o.Change["outcome"], o.Actor, o.Reason, Holds(o, Overrides.SkipProtected)),
        Overrides.GrantAttempts => new AttemptsGranted(o.StepId, long.Parse(o.Change["attempts"], System.Globalization.CultureInfo.InvariantCulture),
            Holds(o, Overrides.GrantAttemptsBeyond), o.Actor),
        Overrides.ChangeRole => new RoleChanged(o.StepId!, o.Change["role"], o.Actor, o.Reason),
        Overrides.Amend when ReadPatch(o.Change["patch"], out _) is { } patch =>
            new PatchProposed(patch with { Reason = $"amend {By(o.Actor, o.Reason)}" }, new Proposer("human", null), Holds(o, Overrides.AmendProtected)),
        _ => null,
    };

    static bool Holds(OverrideArgs o, string variant) => o.Variants?.Contains(variant) == true;

    static bool? Bool(IReadOnlyDictionary<string, string> change, string name) => change.GetValueOrDefault(name) switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    static PatchDocument? ReadPatch(string text, out DiagnosticBag d)
    {
        d = new DiagnosticBag();
        var node = YamlText.Parse(text, "patch", d);
        var patch = node is null || d.HasErrors ? null : PatchReader.Read(node, d);
        return d.HasErrors ? null : patch;
    }

    /// <summary>Why a steering input would be refused now, or null; the update validator's check as well as the handler's.</summary>
    string? SteeringError(RunInput input) => input switch
    {
        BreakpointSet b => BreakpointError(b),
        StepSkipped s => SkipError(s),
        AttemptsGranted g => GrantError(g.StepId, g.Attempts, g.BeyondHardCeiling),
        RoleChanged r => RoleError(r),
        LibraryOverridden o => LibraryOverrideError(o.Entry.Item, o.Libraries),
        LibraryOverrideProposed p => StepById(p.StepId) is not { } proposer ? $"{p.StepId} is not a step of plan revision {State.Revision}"
            : proposer.Agent?.Planning is null ? $"{p.StepId} has no planning authority, so it may not propose a library override"
            : p.Content is null ? "a library override proposal needs with, the new content"
            : LibraryItemError(p.Item),
        _ => null,
    };

    ApplyResult Steer(RunInput input)
    {
        if (SteeringError(input) is { } error)
            return ApplyResult.Refused(error);
        switch (input)
        {
            case BreakpointSet b:
                if (b.Set ? State.Breakpoints.Add(b.StepId) : State.Breakpoints.Remove(b.StepId))
                    Trace(b.Set ? "breakpoint_set" : "breakpoint_cleared", b.StepId, $"by {b.Actor}");
                return ApplyResult.Ok;
            case StepSkipped s:
                Skip(s);
                return ApplyResult.Ok;
            case AttemptsGranted g:
                Grant(g.StepId, g.Attempts);
                Trace("attempts_granted", g.StepId ?? "", $"{g.Attempts} attempt(s){(g.StepId is null ? " to the run ceiling" : "")} by {g.Actor}");
                return ApplyResult.Ok;
            case RoleChanged r:
                return ChangeRole(r);
            case LibraryOverridden o:
                libraries = o.Libraries;
                State.LibraryOverrides.Add(o.Entry);
                Trace("library_overridden", "", $"{o.Entry.Item} ({o.Entry.Path}) {o.Entry.OldHash} -> {o.Entry.NewHash} {By(o.Entry.Actor, o.Entry.Reason)}"
                                              + (o.Entry.InFlight.Count > 0 ? $"; in flight, keeping theirs: {string.Join(", ", o.Entry.InFlight)}" : "")
                                              + (o.Entry.ProtectedSteps.Count > 0 ? $"; protected steps affected: {string.Join(", ", o.Entry.ProtectedSteps)}" : ""));
                return ApplyResult.Ok;
            case LibraryOverrideProposed p:
                var wait = AddWait(new OpenWait(NewWaitId("patch_approval"), WaitKind.PatchApproval, p.StepId, null,
                    $"{p.StepId} proposes a library override of {p.Item}; a person decides (docs/05 section 6)"
                    + (ProtectedResolving(p.Item) is { Count: > 0 } guarded ? $"; protected steps resolve it: {string.Join(", ", guarded)}" : ""))
                {
                    LibraryItem = p.Item,
                    LibraryContent = p.Content,
                });
                Trace("library_override_proposed", p.StepId, $"{p.Item}, {wait.Id}");
                return ApplyResult.Ok;
            default:
                return ApplyResult.Refused($"unknown input {input.GetType().Name}");
        }
    }

    // ---- Breakpoints ------------------------------------------------------------------------------

    string? BreakpointError(BreakpointSet b)
    {
        if (StepById(b.StepId) is null)
            return $"{b.StepId} is not a step of plan revision {State.Revision}";
        if (b.Set && (State.InFlight.Contains(b.StepId) || State.Pending.Any(e => e.StepId == b.StepId && e.Resumes)
                      || State.Waits.Any(w => w.StepId == b.StepId && w.Kind != WaitKind.PatchApproval)
                      || State.Containers.TryGetValue(b.StepId, out var container) && container.Active))
            return $"{b.StepId} is running or waiting; a breakpoint is set on a step before it starts";
        return null;
    }

    /// <summary>
    /// Pauses before a step with a breakpoint (docs/02 section 2.1: after the allowance check, before the step starts), unless
    /// a person's answer let this entry through. The breakpoint decision keeps the entry's <c>clean_up</c>.
    /// </summary>
    bool Paused(Entry entry, Step step)
    {
        if (entry.PastBreakpoint || !State.Breakpoints.Contains(step.Id))
            return false;
        var wait = AddWait(new OpenWait(NewWaitId("breakpoint"), WaitKind.Breakpoint, step.Id, null, $"a person set a breakpoint before {step.Id}") { CleanUp = entry.CleanUp });
        Trace("paused", step.Id, wait.Id);
        Progressed();
        return true;
    }

    // ---- Skips ------------------------------------------------------------------------------------

    string? SkipError(StepSkipped s)
    {
        if (StepById(s.StepId) is not { } step)
            return $"{s.StepId} is not a step of plan revision {State.Revision}";
        var outcomes = step.Outcomes().Where(o => o.Name != Actions.AttemptsExhausted).Select(o => o.Name).ToList();
        if (!outcomes.Contains(s.Outcome))
            return $"{s.StepId}: '{s.Outcome}' is not an outcome of the step ({string.Join(", ", outcomes)})";
        if (step.Protected && !s.MayTouchProtected)
            return $"{s.StepId} is protected: skipping it needs {Model.Overrides.SkipProtected}";
        if (State.InFlight.Contains(s.StepId) || State.Pending.Any(e => e.StepId == s.StepId && e.Resumes))
            return $"{s.StepId} is running; a running step cannot be skipped";
        if (State.Containers.TryGetValue(s.StepId, out var container) && container.Active)
            return $"{s.StepId} has started its branches; skip the branches, or wait for its join";
        if (State.Waits.Any(w => w.StepId == s.StepId && w.Kind == WaitKind.PatchApproval && !w.FromChild && w.LibraryItem is null))
            return $"{s.StepId} has a patch awaiting approval; answer it first";
        return null;
    }

    /// <summary>
    /// A step at a decision, breakpoint or intervention is skipped at once; otherwise it is skipped when the run next
    /// reaches it (<see cref="Enter"/>).
    /// </summary>
    void Skip(StepSkipped s)
    {
        Trace("skip_set", s.StepId, $"{s.Outcome} {By(s.Actor, s.Reason)}");
        State.Skips[s.StepId] = s.Outcome;
        var waits = State.Waits.Where(w => w.StepId == s.StepId && w.Kind is WaitKind.Gate or WaitKind.Breakpoint or WaitKind.Intervention).ToList();
        if (waits.Count == 0)
            return;
        foreach (var wait in waits)
            CloseWaitFor(wait, WaitEnds.Withdrawn);
        State.Pending.Add(new Entry(s.StepId, false));
    }

    /// <summary>Ends a step with the outcome a person chose for it, without running it or using an attempt.</summary>
    void EndSkipped(Step step, string outcome)
    {
        var record = Record(step.Id);
        record.Previous = record.Current;
        record.Executions++;
        record.Current = new Execution { Number = record.Executions, Skipped = true };
        Trace("skipped", step.Id, outcome);
        Progressed();
        Finish(step, record, outcome);
    }

    // ---- Attempt grants: the override and the recovery action share one mechanism ------------------

    string? GrantError(string? stepId, long attempts, bool beyond)
    {
        if (attempts <= 0)
            return "grant attempts needs a positive number of attempts";
        Step? step = null;
        if (stepId is not null && (step = StepById(stepId)) is null)
            return $"{stepId} is not a step of plan revision {State.Revision}";
        if (step is { ConsumesAttempts: false })
            return $"{stepId} uses no attempts";
        if (beyond)
            return null;
        if (step is not null)
        {
            var allowance = Allowance(step) + (State.Steps.GetValueOrDefault(step.Id)?.AttemptsGranted ?? 0) + attempts;
            if (Options.HardStepCeiling is { } hardStep && allowance > hardStep)
                return $"granting {attempts} would give {step.Id} {allowance} attempts, above the hard ceiling of {hardStep}; an administrator may grant beyond it ({Model.Overrides.GrantAttemptsBeyond})";
        }

        var ceiling = Options.RunCeiling + State.RunCeilingGranted + attempts;
        if (Options.HardRunCeiling is { } hardRun && ceiling > hardRun)
            return $"granting {attempts} would raise the run ceiling to {ceiling}, above the hard ceiling of {hardRun}; an administrator may grant beyond it ({Model.Overrides.GrantAttemptsBeyond})";
        return null;
    }

    void Grant(string? stepId, long attempts)
    {
        if (stepId is not null)
            Record(stepId).AttemptsGranted += attempts;
        State.RunCeilingGranted += attempts;
    }

    // ---- Role changes: a person's patch ------------------------------------------------------------

    string? RoleError(RoleChanged r)
    {
        if (StepById(r.StepId) is not { } step)
            return $"{r.StepId} is not a step of plan revision {State.Revision}";
        if (step.Agent is null || step.Source is null)
            return $"{r.StepId} is not an agent step; only an agent step has a role";
        if (LibraryReference.TryParse(r.Role) is not { Type: "roles" })
            return $"'{r.Role}' is not a role reference, such as std:roles/implementer";
        return step.Agent.Role?.Text == r.Role ? $"{r.StepId} already has role {r.Role}" : null;
    }

    ApplyResult ChangeRole(RoleChanged r)
    {
        var step = StepById(r.StepId)!;
        var source = step.Source!;
        var agent = (YMap)source["agent"]!;
        var changed = With(source, "agent", With(agent, "role", new YScalar(r.Role, true, agent.Location)));
        var patch = new PatchDocument(Run.Id, new Proposer("human", null), $"change_role {By(r.Actor, r.Reason)}", State.Revision, null,
            [new PatchOp("replace", step.Id, source.Location, Step: changed)], source.Location);
        var result = Propose(patch, null, roleChange: true);
        if (result.Status != PatchStatus.Applied)
            return new ApplyResult(false, result.Diagnostics.Where(d => d.Blocks).Select(d => $"check {d.Check}: {d.Message}").ToList(), result);
        Trace("role_changed", step.Id, $"{step.Agent!.Role?.Text ?? "none"} -> {r.Role} {By(r.Actor, r.Reason)}");
        return new ApplyResult(true, [], result);
    }

    static YMap With(YMap map, string key, YNode value)
    {
        var entries = map.Entries.Select(e => e.Key.Text == key ? new KeyValuePair<YScalar, YNode>(e.Key, value) : e).ToList();
        if (entries.All(e => e.Key.Text != key))
            entries.Add(new(new YScalar(key, true, map.Location), value));
        return new YMap(entries, map.Location);
    }

    // ---- Library overrides ------------------------------------------------------------------------

    /// <summary>Whether <paramref name="item"/> is content a mid-run override may replace, and the run's libraries hold it.</summary>
    public string? LibraryItemError(string item)
    {
        if (LibraryReference.TryParse(item) is not { } reference)
            return LibraryReference.ConstraintRefusal(item) ?? $"'{item}' is not a library reference, such as std:tasks/implement_ticket";
        if (!OverridableTypes.Contains(reference.Type))
            return $"{item}: a library override replaces {string.Join(", ", OverridableTypes)}, not {reference.Type}; the plan changes by amend, a role by change_role";
        if (libraries?.Resolve(reference) is not { } library || library.PathOf(reference.Type, reference.Name) is null)
            return $"{item} is not in the run's libraries";
        if (reference.Type == "artifact_types" && library.ArtifactType(reference.Name)?.Evidence == true)
            return $"{item} is an evidence type, which only engine actions produce (F7); it is not overridden mid-run";
        return null;
    }

    string? LibraryOverrideError(string item, Library.LibrarySet next)
    {
        if (LibraryItemError(item) is { } error)
            return error;
        var reference = LibraryReference.TryParse(item)!;
        return reference.Type == "artifact_types" && next.Resolve(reference)?.ArtifactType(reference.Name)?.Evidence == true
            ? $"{item}: the override would make it an evidence type, which only engine actions produce (F7)"
            : null;
    }

    /// <summary>The library file an item is in, for the shell's lock entry.</summary>
    public (string Library, string Path)? LibraryFile(string item) =>
        LibraryReference.TryParse(item) is { } reference && libraries?.Resolve(reference) is { } library && library.PathOf(reference.Type, reference.Name) is { } path
            ? (library.Name, path)
            : null;

    /// <summary>
    /// The protected steps that resolve <paramref name="item"/> (docs/08 section 5.2): an agent step's task or skill, an
    /// input naming it (a prompt), or an artifact type it produces or consumes. Overriding it needs
    /// <c>override_library_protected</c>.
    /// </summary>
    public IReadOnlyList<string> ProtectedResolving(string item)
    {
        if (LibraryReference.TryParse(item) is not { } reference)
            return [];
        bool Is(ItemRef? r) => r?.Reference == reference;
        bool Names(YNode? node) => node switch
        {
            YScalar s => LibraryReference.TryParse(s.Text) == reference,
            YMap m => m.Entries.Any(e => Names(e.Value)),
            YSeq q => q.Items.Any(Names),
            _ => false,
        };
        return Plan.AllSteps
            .Where(s => s.Protected && (Is(s.Agent?.Task) || s.Agent?.Skills.Any(Is) == true || Names(s.With)
                                        || s.Produces.Any(p => Is(p.Type)) || s.Consumes.Any(c => Is(c.Type))))
            .Select(s => s.Id).Distinct().ToList();
    }

    /// <summary>
    /// The protected content an override touches, for its reply and record, so a reader sees the <c>_protected</c> variant was
    /// needed: a protected step skipped or given another role, or the protected steps that resolve an overridden library item.
    /// A run that starts children, or may once <c>plan.expand</c> adds them, passes a library override down, where protected
    /// steps it cannot see may resolve it (LOG 212).
    /// </summary>
    IReadOnlyList<string> ProtectedTouched(OverrideArgs o) => o.Key switch
    {
        Overrides.Skip or Overrides.ChangeRole when o.StepId is not null && StepById(o.StepId) is { Protected: true } => [o.StepId],
        Overrides.OverrideLibrary when o.Change.GetValueOrDefault("item") is { } item => ProtectedResolving(item)
            .Concat(Plan.AllSteps.Any(x => x.Descriptor?.Handling is ActionHandling.ChildRun or ActionHandling.Expand) ? ["its child runs"] : []).ToList(),
        _ => [],
    };

    // ---- Re-running a branch of a decided join -----------------------------------------------------

    /// <summary>
    /// A step inside a <c>parallel</c> or chain whose join has decided is entered from outside (a recovery's go to, a
    /// skip): its branch runs again and the join decides again when it ends, so the run continues from the join's routes
    /// rather than ending incomplete (m6-results follow-up 3). A chain's later items, cancelled when it failed, wait again.
    /// </summary>
    void Reopen(Step step)
    {
        for (var lane = step.Lane; lane.Length > 0;)
        {
            var owner = lane[..lane.LastIndexOf('#')];
            if (StepById(owner) is { Container: not null } container && State.Containers.TryGetValue(owner, out var state) && !state.Active
                && BranchOf(step, owner) is { } index and >= 0)
            {
                state.Active = true;
                state.Branches[index] = BranchStatus.Running;
                if (container.Form == StepForm.Chain)
                    for (var i = index + 1; i < state.Branches.Count; i++)
                        if (state.Branches[i] == BranchStatus.Cancelled)
                            state.Branches[i] = BranchStatus.Waiting;
                Trace("reopened", owner, $"branch {index} runs again from {step.Id}; the join decides again");
            }

            lane = StepById(owner)?.Lane ?? "";
        }
    }
}
