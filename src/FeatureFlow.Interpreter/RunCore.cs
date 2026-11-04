using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Validation;

namespace FeatureFlow.Interpreter;

/// <summary>
/// The interpreter's pure core (docs/02 section 2.1; LOG 66): which steps are ready, starting them, applying
/// what happened, routing, attempt allowances and ceilings, joins, and patches. It does no I/O and reads no
/// clock: its behaviour is a function of the plan, the run's options and the inputs it is given. A shell (the
/// Temporal workflow, or the re-drive runner) calls <see cref="Advance"/>, carries out the commands, and gives
/// back what happened through <see cref="Apply"/>.
/// </summary>
public sealed partial class RunCore
{
    LibrarySet? libraries;
    readonly LibraryReference? template;
    readonly List<Command> outbox = [];
    readonly Evaluator evaluator;
    Dictionary<string, Step> byId = [];

    RunCore(Plan plan, RunInfo run, RunOptions options, LibrarySet? libraries, LibraryReference? template, IFindingsSource? findings)
    {
        this.libraries = libraries;
        this.template = template;
        Run = run;
        Options = options;
        Findings = findings;
        evaluator = new Evaluator(this);
        SetPlan(plan);
        foreach (var step in plan.AllSteps)
            State.IdHistory.Add(step.Id);
    }

    public Plan Plan { get; private set; } = null!;
    public RunInfo Run { get; }
    public RunOptions Options { get; }
    public RunState State { get; } = new();

    /// <summary>Where route guards read the <c>findings</c> namespace; null reads <see cref="RunState.Findings"/>.</summary>
    public IFindingsSource? Findings { get; }

    /// <summary>
    /// Loads and validates the template with the run's parameters and repositories (plan revision 1), and
    /// enters its first step. Returns the load diagnostics; the core is null when any blocks.
    /// </summary>
    public static (RunCore? Core, IReadOnlyList<Diagnostic> Diagnostics) Start(
        LibrarySet libraries, LibraryReference template, RunInfo run, RunOptions? options = null, IFindingsSource? findings = null)
    {
        var diagnostics = new DiagnosticBag();
        var plan = Load(libraries, template, run, [], diagnostics);
        if (plan is not null)
            CheckParameters(plan, run, diagnostics);
        if (plan is null || diagnostics.HasErrors)
            return (null, diagnostics);
        var core = new RunCore(plan, run, options ?? new RunOptions(), libraries, template, findings);
        core.Begin();
        return (core, diagnostics);
    }

    /// <summary>Runs an already loaded plan, valid or not, without patches. For tests of the run-time rules.</summary>
    public static RunCore FromPlan(Plan plan, RunInfo run, RunOptions? options = null, IFindingsSource? findings = null)
    {
        var core = new RunCore(plan, run, options ?? new RunOptions(), null, null, findings);
        core.Begin();
        return core;
    }

    /// <summary>
    /// Check 18 at run start: every required parameter is bound, and every literal value has its declared type,
    /// a <c>number</c> being a whole number (LOG 76). A value given by an expression is checked when it is read.
    /// </summary>
    static void CheckParameters(Plan plan, RunInfo run, DiagnosticBag d)
    {
        foreach (var decl in plan.Document.Parameters)
        {
            var node = run.Parameters.GetValueOrDefault(decl.Name) ?? decl.Default;
            if (node is null)
            {
                if (decl.Required)
                    d.Add(Checks.RequiredParameters, $"required parameter '{decl.Name}' is not bound", decl.Location);
                continue;
            }

            if (node.IsSingleKeyMap("expr", out _))
                continue;
            try
            {
                Values.FromNode(node, decl.Type);
            }
            catch (EvaluationException e)
            {
                d.Add(Checks.RequiredParameters, $"parameter '{decl.Name}' ({decl.Type}): {e.Message}", node.Location);
            }
        }
    }

    internal static Plan? Load(LibrarySet libraries, LibraryReference template, RunInfo run, IReadOnlyList<PatchDocument> patches, DiagnosticBag diagnostics)
    {
        var plan = PlanLoader.LoadTemplate(libraries, template, diagnostics, new LoadOptions(run.Parameters, run.Repositories, patches));
        if (plan is not null)
            PlanValidator.Validate(plan, libraries, diagnostics);
        return plan;
    }

    void SetPlan(Plan plan)
    {
        Plan = plan;
        byId = [];
        foreach (var step in plan.AllSteps)
            byId.TryAdd(step.Id, step);
    }

    void Begin()
    {
        if (Plan.Steps.Count == 0)
            End(RunStatus.Incomplete, "", "the plan has no steps");
        else
            State.Pending.Add(new Entry(Plan.Steps[0].Id, false));
    }

    public Step? StepById(string id) => byId.GetValueOrDefault(id);

    /// <summary>
    /// Starts every ready step and returns what the shell must do. When nothing is ready, in flight or waiting
    /// for a person and no end route was reached, the run is <c>incomplete</c> (F9).
    /// </summary>
    public IReadOnlyList<Command> Advance()
    {
        enteredWithoutProgress.Clear();
        while (State.Status == RunStatus.Running)
        {
            var index = State.Pending.FindIndex(e => !State.InFlight.Contains(e.StepId) && !WaitingOn(e.StepId));
            if (index < 0)
                break;
            var entry = State.Pending[index];
            State.Pending.RemoveAt(index);
            Enter(entry);
        }

        if (State.Status == RunStatus.Running && State.Pending.Count == 0 && State.InFlight.Count == 0 && State.Waits.Count == 0)
            End(RunStatus.Incomplete, "", "nothing is ready, running or waiting, and no end route was reached");

        var commands = outbox.ToList();
        outbox.Clear();
        return commands;
    }

    /// <summary>
    /// Steps entered since the core last started a step or opened a wait. Entering one twice means routing has
    /// gone round a cycle without running anything, for example an exhausted step routed back into itself: the
    /// core escalates instead, so one <see cref="Advance"/> always returns.
    /// </summary>
    readonly HashSet<string> enteredWithoutProgress = new(StringComparer.Ordinal);

    void Progressed() => enteredWithoutProgress.Clear();

    bool WaitingOn(string stepId) => State.Waits.Any(w => w.StepId == stepId && w.Kind is WaitKind.Decision or WaitKind.Breakpoint);

    StepRecord Record(string stepId)
    {
        if (!State.Steps.TryGetValue(stepId, out var record))
            State.Steps[stepId] = record = new StepRecord();
        return record;
    }

    void Trace(string kind, string stepId, string? detail = null) => State.Trace.Add(new TraceEntry(kind, stepId, detail));

    // ---- Entering a step --------------------------------------------------------------------------

    void Enter(Entry entry)
    {
        if (StepById(entry.StepId) is not { } step)
        {
            Intervene(entry.StepId, null, $"step {entry.StepId} is not in plan revision {State.Revision}");
            return;
        }

        if (entry.Resumes)
        {
            ResumeExecution(step);
            return;
        }

        if (!enteredWithoutProgress.Add(step.Id))
        {
            Intervene(step.Id, null, $"routing returned to {step.Id} without running any step, for example an exhausted step routed back into a cycle");
            return;
        }

        if (Options.Steering)
        {
            Reopen(step);
            if (State.Skips.Remove(step.Id, out var skipped))
            {
                EndSkipped(step, skipped);
                return;
            }
        }

        if (step.Container is not null)
        {
            if (Paused(entry, step))
                return;
            // A clean_up aimed at a container is the clean_up of the first step it starts: the step that touches the
            // repository and records where it started (re-check R3).
            EnterContainer(step, entry.CleanUp);
            return;
        }

        var record = Record(step.Id);
        if (step.ConsumesAttempts)
        {
            // F4: the allowance, the race allowance and the run ceiling are checked before every execution,
            // whatever route led here (docs/01 section 4).
            if (Exhausted(step, record) is { } reason)
            {
                record.Previous = record.Current;
                record.Current = new Execution { Number = record.Executions, Outcome = Actions.AttemptsExhausted };
                Trace("exhausted", step.Id, reason);
                Route(step, Actions.AttemptsExhausted);
                return;
            }

            if (Paused(entry, step))
                return;

            // Re-entry clears the current outcome, outputs and artifacts; the earlier execution stays readable (DR12).
            CleanUpFor(entry, step);
            record.Previous = record.Current;
            record.Executions++;
            record.Current = new Execution { Number = record.Executions };
            if (!IsMerge(step))
                record.AttemptsUsed++;
            State.RunAttempts++;
            State.InFlight.Add(step.Id);
            Trace("started", step.Id, $"execution {record.Executions}");
            outbox.Add(new StartStep(step.Id, record.Executions));
            Progressed();
            return;
        }

        // A decision: no attempts (docs/01 section 3.2). Re-entry clears it like any step.
        if (Paused(entry, step))
            return;
        CleanUpFor(entry, step);
        record.Previous = record.Current;
        record.Executions++;
        record.Current = new Execution { Number = record.Executions };
        Trace("started", step.Id, $"execution {record.Executions}");
        if (step.Decision is { Auto: { } auto, Type: { } type } && Options.AutoResolve(type))
        {
            // The actor of an automatic answer is policy, never a person (docs/04 section 4.2); M7's runs name it.
            Trace("auto_resolved", step.Id, Options.Steering ? $"{auto} by {PeopleRoles.PolicyActor}" : auto);
            Finish(step, record, auto);
            return;
        }

        State.Waits.Add(new OpenWait(NewWaitId("decision"), WaitKind.Decision, step.Id, null, "decision"));
        outbox.Add(new OpenDecision(step.Id, record.Executions));
        Progressed();
    }

    /// <summary>
    /// Starts an interrupted execution again (LOG 139). It is the same execution, not a new one, so the allowance is not
    /// checked and no attempt is counted (F4 counts executions); the shell gives it the same identity and records.
    /// </summary>
    void ResumeExecution(Step step)
    {
        var record = Record(step.Id);
        State.InFlight.Add(step.Id);
        Trace("resumed", step.Id, $"execution {record.Executions}");
        outbox.Add(new StartStep(step.Id, record.Executions));
        Progressed();
    }

    /// <summary>A <c>clean_up</c> route resets the branch only when the step will run, after the allowance check.</summary>
    void CleanUpFor(Entry entry, Step step)
    {
        if (!entry.CleanUp)
            return;
        Trace("clean_up", step.Id);
        outbox.Add(new CleanUp(step.Id));
    }

    static bool IsMerge(Step step) => step.Form == StepForm.Action && step.Action == "scm.merge";

    /// <summary>The attempt allowance: the step's limit, else its own document's <c>defaults.limits</c>, else the engine default (LOG 77).</summary>
    public long Allowance(Step step) =>
        step.Limits?.Attempts ?? step.Scope.DocumentScope.Document.Defaults.Limits?.Attempts ?? Options.DefaultAttempts;

    public long RaceAllowance(Step step) =>
        step.Limits?.Races ?? step.Scope.DocumentScope.Document.Defaults.Limits?.Races ?? Options.DefaultRaces;

    string? Exhausted(Step step, StepRecord record)
    {
        var allowance = Allowance(step) + record.AttemptsGranted;
        if (record.AttemptsUsed >= allowance)
            return $"{record.AttemptsUsed} of {allowance} attempts used";
        var races = RaceAllowance(step) + record.AttemptsGranted;
        if (IsMerge(step) && record.RacesUsed >= races)
            return $"{record.RacesUsed} of {races} races lost (limits.races)";
        var ceiling = Options.RunCeiling + State.RunCeilingGranted;
        if (State.RunAttempts >= ceiling)
            return $"the run ceiling of {ceiling} attempts is reached";
        return null;
    }

    void EnterContainer(Step step, bool cleanUp = false)
    {
        var container = step.Container!;
        var record = Record(step.Id);
        record.Previous = record.Current;
        record.Executions++;
        record.Current = new Execution { Number = record.Executions };
        var state = new ContainerRecord
        {
            BranchIndexes = container.Branches.Select(b => b.Index).ToList(),
            Branches = container.Branches.Select(_ => BranchStatus.Waiting).ToList(),
        };
        State.Containers[step.Id] = state;
        Trace("entered", step.Id, $"{container.Branches.Count} branch(es), {(step.Form == StepForm.Chain ? "chain" : $"join {container.Join}")}");
        if (container.Branches.Count == 0)
        {
            // A parallel with no branches completes at once (docs/03 section 10.4).
            Join(step, state, "completed");
            return;
        }

        StartWaitingBranches(step, state, cleanUp);
    }

    void StartWaitingBranches(Step step, ContainerRecord state, bool cleanUpFirst = false)
    {
        var container = step.Container!;
        var limit = step.Form == StepForm.Chain ? 1 : container.MaxConcurrency ?? long.MaxValue;
        for (var i = 0; i < state.Branches.Count; i++)
        {
            if (state.Branches.Count(b => b == BranchStatus.Running) >= limit)
                return;
            if (state.Branches[i] != BranchStatus.Waiting)
                continue;
            if (step.Form == StepForm.Chain && state.Branches.Take(i).Any(b => b != BranchStatus.Succeeded))
                return;
            state.Branches[i] = BranchStatus.Running;
            var branch = container.Branches[i];
            if (branch.Steps.Count == 0)
            {
                BranchEnded(step, i, OutcomeClass.Success);
                return;
            }

            State.Pending.Add(new Entry(branch.Steps[0].Id, cleanUpFirst));
            cleanUpFirst = false;
        }
    }

    // ---- Inputs -----------------------------------------------------------------------------------

    public ApplyResult Apply(RunInput input)
    {
        if (State.Status != RunStatus.Running)
            return ApplyResult.Refused($"the run has ended ({State.Status.Name()})");
        return input switch
        {
            StepCompleted c => Completed(c),
            StepFailed f => Failed(f),
            StepInterrupted i => Interrupted(i),
            DecisionAnswered a => Answered(a),
            PatchProposed p => ProposeOutside(p),
            PatchAnswered p => PatchDecision(p),
            FindingsChanged f => SetFindings(f),
            InterventionAnswered i => Recover(i),
            RunAborted a => Abort(a),
            StepCannotAnswer s => CannotAnswer(s),
            ChildPatchProposed p => ProposeFromChild(p),
            BreakpointSet or StepSkipped or AttemptsGranted or RoleChanged or LibraryOverridden or LibraryOverrideProposed => Steer(input),
            _ => ApplyResult.Refused($"unknown input {input.GetType().Name}"),
        };
    }

    /// <summary>
    /// The commands produced since the last call, without entering any ready step. A shell that is draining for
    /// continue-as-new calls this instead of <see cref="Advance"/>, so it starts nothing new (docs/02 section 3).
    /// </summary>
    public IReadOnlyList<Command> Flush()
    {
        var commands = outbox.ToList();
        outbox.Clear();
        return commands;
    }

    ApplyResult SetFindings(FindingsChanged f)
    {
        if (f.Version < State.FindingsVersion)
            return ApplyResult.Ok;
        State.FindingsVersion = f.Version;
        foreach (var (name, count) in f.Counts)
            State.Findings[name] = count;
        return ApplyResult.Ok;
    }

    ApplyResult Completed(StepCompleted c)
    {
        if (StepById(c.StepId) is not { } step)
            return ApplyResult.Refused($"{c.StepId} is not a step of plan revision {State.Revision}");
        if (!State.InFlight.Contains(step.Id))
            return ApplyResult.Refused($"{step.Id} is not running");
        var errors = new List<string>();
        var outputs = CheckOutputs(step, c, errors);
        if (errors.Count > 0)
            return new ApplyResult(false, errors);

        if (c.PatchToParent)
        {
            // docs/03 section 9.4: the patch goes to the parent, and this step ends as reported.
            if (c.Patch is null)
                return ApplyResult.Refused($"{step.Id}: a patch to the parent needs a patch");
            if (step.Agent?.Planning?.Targets.Contains("parent") != true)
                return ApplyResult.Refused($"{step.Id}: the step has no planning authority over its parent's plan (planning.targets)");
            if (Run.ParentId is null)
                return ApplyResult.Refused($"{step.Id}: this run has no parent to send a patch to");
            var toParent = c.Patch with { ProposedBy = new Proposer("agent", step.Id) };
            c = c with { Patch = null, PatchToParent = false };
            var recordParent = Record(step.Id);
            State.InFlight.Remove(step.Id);
            foreach (var (name, value) in outputs)
                recordParent.Current!.Outputs[name] = value;
            foreach (var (name, reference) in c.Artifacts ?? new Dictionary<string, string>())
                recordParent.Current!.Artifacts[name] = reference;
            Trace("patch_to_parent", step.Id, toParent.Reason);
            outbox.Add(new SendPatchToParent(step.Id, toParent));
            Finish(step, recordParent, c.Outcome);
            return ApplyResult.Ok;
        }

        // The proposer is the reporting step's form, never the document's claim (B2 of the M2 review).
        var patch = c.Patch is null ? null : c.Patch with { ProposedBy = new Proposer(ProposerOf(step), step.Id) };
        PatchResult? result = null;
        if (patch is not null)
        {
            result = Propose(patch, step);
            if (result.Status == PatchStatus.Rejected && patch.ProposedBy.Type == "agent")
            {
                // An invalid patch is an output contract error: the agent corrects it in correction turns
                // (docs/03 section 10.3; M4). Nothing is recorded and no attempt is used.
                return new ApplyResult(false,
                    result.Diagnostics.Where(d => d.Blocks).Select(d => $"{step.Id}: patch check {d.Check}: {d.Message}").ToList(), result);
            }
        }

        var record = Record(step.Id);
        State.InFlight.Remove(step.Id);
        foreach (var (name, value) in outputs)
            record.Current!.Outputs[name] = value;
        foreach (var (name, reference) in c.Artifacts ?? new Dictionary<string, string>())
            record.Current!.Artifacts[name] = reference;
        if (IsMerge(step))
        {
            // LOG 61: an execution that lost a race counts against limits.races, any other against attempts.
            if (c.Outcome == "base_moved")
                record.RacesUsed++;
            else
                record.AttemptsUsed++;
        }

        if (patch is null)
        {
            Finish(step, record, c.Outcome);
            return ApplyResult.Ok;
        }

        record.Current!.Outcome = c.Outcome;
        Trace("completed", step.Id, c.Outcome);
        switch (result!.Status)
        {
            case PatchStatus.Applied:
                Resume(patch, step.Id);
                break;
            case PatchStatus.Pending:
                break;
            case PatchStatus.Rejected:
                Intervene(step.Id, c.Outcome, "its patch was rejected: " + string.Join("; ", result.Diagnostics.Where(d => d.Blocks).Select(d => d.Message)));
                break;
        }

        return new ApplyResult(true, [], result);
    }

    /// <summary>Who proposes a patch a step reports: an agent step is an agent, an action the engine, a decision a person.</summary>
    static string ProposerOf(Step step) => step.Form switch
    {
        StepForm.Agent => "agent",
        StepForm.Decision => "human",
        _ => "engine",
    };

    /// <summary>Checks an outcome, outputs and artifacts against the step's contract (docs/09 section 3, output contracts).</summary>
    Dictionary<string, object?> CheckOutputs(Step step, StepCompleted c, List<string> errors)
    {
        var outcome = step.Outcomes().FirstOrDefault(o => o.Name == c.Outcome && o.Name != Actions.AttemptsExhausted);
        if (outcome is null)
            errors.Add($"{step.Id}: '{c.Outcome}' is not an outcome of the step ({string.Join(", ", step.Outcomes().Where(o => o.Name != Actions.AttemptsExhausted).Select(o => o.Name))})");

        var outputs = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (name, value) in c.Outputs ?? new Dictionary<string, object?>())
        {
            var type = OutputType(step, name);
            if (type is null)
            {
                errors.Add($"{step.Id}: output '{name}' is not declared");
                continue;
            }

            var (converted, error) = Values.Convert(value, type == "dyn" ? null : type);
            if (error is not null)
                errors.Add($"{step.Id}: output '{name}': {error}");
            else
                outputs[name] = converted;
        }

        if (outcome is { Class: OutcomeClass.Success })
            foreach (var declared in step.Outputs.Where(o => !outputs.ContainsKey(o.Name) && c.Outputs?.ContainsKey(o.Name) != true))
                errors.Add($"{step.Id}: declared output '{declared.Name}' was not reported (LOG 79)");

        if (outcome is not null)
        {
            var promised = step.Produces.Where(p => p.Outcome == c.Outcome || (p.Outcome is null && outcome.Class == OutcomeClass.Success)).Select(p => p.Name).ToHashSet();
            var given = (c.Artifacts ?? new Dictionary<string, string>()).Keys.ToHashSet();
            // A run.child step holds its child's artifacts, which its own contract does not list (docs/03 section 9.3).
            foreach (var name in given.Except(promised).Where(_ => step.Action != "run.child").Order(StringComparer.Ordinal))
                errors.Add($"{step.Id}: artifact '{name}' is not declared for outcome '{c.Outcome}'");
            foreach (var name in promised.Except(given).Order(StringComparer.Ordinal))
                errors.Add($"{step.Id}: outcome '{c.Outcome}' promises artifact '{name}', which was not produced");
        }

        return outputs;
    }

    /// <summary>
    /// The declared type of an output: the step's declaration, an engine output of agent steps (docs/04 section
    /// 3.6), or an action's published output. "dyn" for an action whose outputs docs/04 does not list; null when
    /// the step cannot publish it.
    /// </summary>
    public static string? OutputType(Step step, string name)
    {
        if (step.Outputs.FirstOrDefault(o => o.Name == name) is { } declared)
            return declared.Type;
        switch (step.Form)
        {
            case StepForm.Agent when Actions.AgentEngineOutputs.Contains(name):
                return Actions.EngineOutputTypes[name];
            case StepForm.Action when step.Action is { } a && Actions.All.GetValueOrDefault(a) is { } contract:
                if (contract.Outputs is null)
                    return "dyn";
                return contract.Outputs.Contains(name) ? Actions.EngineOutputTypes.GetValueOrDefault(name, "dyn") : null;
            default:
                return null;
        }
    }

    ApplyResult Failed(StepFailed f)
    {
        if (StepById(f.StepId) is not { } step || !State.InFlight.Contains(f.StepId))
            return ApplyResult.Refused($"{f.StepId} is not running");
        var record = Record(step.Id);
        if (f.Class == ErrorClass.Infrastructure)
        {
            // Free: the same execution is tried again and nothing is counted (docs/01 section 4).
            Trace("retried", step.Id, f.Reason);
            outbox.Add(new StartStep(step.Id, record.Executions));
            return ApplyResult.Ok;
        }

        State.InFlight.Remove(step.Id);
        if (IsMerge(step))
            record.AttemptsUsed++;
        Trace("attempt_failed", step.Id, f.Reason);
        State.Pending.Add(new Entry(step.Id, false));
        return ApplyResult.Ok;
    }

    ApplyResult Interrupted(StepInterrupted i)
    {
        if (StepById(i.StepId) is null || !State.InFlight.Contains(i.StepId))
            return ApplyResult.Refused($"{i.StepId} is not running");
        State.InFlight.Remove(i.StepId);
        State.Pending.Insert(0, new Entry(i.StepId, false, Resumes: true));
        Trace("interrupted", i.StepId, i.Reason);
        return ApplyResult.Ok;
    }

    ApplyResult Answered(DecisionAnswered a)
    {
        var wait = State.Waits.FirstOrDefault(w => w.Kind == WaitKind.Decision && w.StepId == a.StepId);
        if (wait is null || StepById(a.StepId) is not { } step)
            return ApplyResult.Refused($"no decision is open at {a.StepId}");
        if (!step.DeclaredOutcomes.Any(o => o.Name == a.Outcome))
            return ApplyResult.Refused($"{a.StepId}: '{a.Outcome}' is not a declared outcome ({string.Join(", ", step.DeclaredOutcomes.Select(o => o.Name))})");
        if (SessionArtifactsError(step, a) is { } error)
            return ApplyResult.Refused(error);
        State.Waits.Remove(wait);
        var record = Record(step.Id);
        record.Current ??= new Execution { Number = record.Executions };
        foreach (var (name, reference) in a.Artifacts ?? new Dictionary<string, string>())
            record.Current.Artifacts[name] = reference;
        Finish(step, record, a.Outcome);
        return ApplyResult.Ok;
    }

    /// <summary>Why a decision answer's artifacts are not exactly those its outcome promises, or null; an answer with none is not checked.</summary>
    static string? SessionArtifactsError(Step step, DecisionAnswered a)
    {
        if (a.Artifacts is null)
            return null;
        var promised = step.Produces.Where(p => p.Outcome == a.Outcome || (p.Outcome is null && step.Outcomes().Any(o => o.Name == a.Outcome && o.Class == OutcomeClass.Success)))
            .Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        var given = a.Artifacts.Keys.ToHashSet(StringComparer.Ordinal);
        var errors = given.Except(promised).Order(StringComparer.Ordinal).Select(n => $"artifact '{n}' is not declared for outcome '{a.Outcome}'")
            .Concat(promised.Except(given).Order(StringComparer.Ordinal).Select(n => $"outcome '{a.Outcome}' promises artifact '{n}', which was not submitted")).ToList();
        return errors.Count == 0 ? null : $"{a.StepId}: {string.Join("; ", errors)}";
    }

    void Finish(Step step, StepRecord record, string outcome)
    {
        record.Current ??= new Execution { Number = record.Executions };
        record.Current.Outcome = outcome;
        Trace("completed", step.Id, outcome);
        Route(step, outcome);
    }

    // ---- Routing ----------------------------------------------------------------------------------

    void Route(Step step, string outcome)
    {
        var route = step.ResolvedRoutes.FirstOrDefault(r => r.Outcome == outcome);
        if (route is null)
        {
            // Nothing is entered. If nothing else is running, the run ends incomplete (F9).
            Trace("unrouted", step.Id, outcome);
            return;
        }

        var cache = new Dictionary<string, long>(StringComparer.Ordinal);
        long ReadFindings(string name)
        {
            if (!cache.TryGetValue(name, out var value))
                cache[name] = value = Findings?.Read(name) ?? State.Findings.GetValueOrDefault(name);
            return value;
        }

        try
        {
            Follow(route.Target, step, outcome, route.Class, ReadFindings);
        }
        catch (EvaluationException e)
        {
            // A step that cannot produce a trustworthy route raises an intervention (docs/04 section 1).
            Trace("evaluation_failed", step.Id, e.Message);
            Intervene(step.Id, outcome, "a route guard could not be evaluated: " + e.Message);
        }
    }

    void Follow(Resolved target, Step from, string outcome, OutcomeClass cls, Func<string, long> findings)
    {
        switch (target)
        {
            case ResolvedGuarded guarded:
                foreach (var (when, next) in guarded.Entries)
                    if (when is null || evaluator.Guard(when, findings))
                    {
                        Follow(next, from, outcome, cls, findings);
                        return;
                    }

                throw new EvaluationException("no guard held and the list has no fallback");
            case ResolvedStep to:
                State.Pending.Add(new Entry(to.StepId, to.CleanUp));
                break;
            case ResolvedEnd end:
                End(RunStatuses.Parse(end.Status) ?? RunStatus.Failed, from.Id, $"{outcome} -> end {end.Status}");
                break;
            case ResolvedEscalate:
                Intervene(from.Id, outcome, outcome == Actions.AttemptsExhausted ? "attempts exhausted" : $"{outcome} escalates");
                break;
            case ResolvedBranchEnd branchEnd:
                if (StepById(branchEnd.ContainerId) is { } container && BranchOf(from, branchEnd.ContainerId) is { } index)
                    BranchEnded(container, index, branchEnd.Class ?? cls);
                break;
            case ResolvedExit exit:
                throw new EvaluationException($"exit '{exit.Exit}' leaves a fragment run on its own; only templates run");
        }
    }

    /// <summary>The position, in the container's branch list, of the branch a step belongs to.</summary>
    int? BranchOf(Step step, string containerId)
    {
        for (var lane = step.Lane; lane.Length > 0;)
        {
            var hash = lane.LastIndexOf('#');
            var owner = lane[..hash];
            if (owner == containerId)
            {
                var number = int.Parse(lane[(hash + 1)..], System.Globalization.CultureInfo.InvariantCulture);
                return StepById(containerId)!.Container!.Branches.FindIndex(b => b.Index == number);
            }

            lane = StepById(owner)?.Lane ?? "";
        }

        return null;
    }

    bool InContainer(string stepId, string containerId) => StepById(stepId) is { } step && BranchOf(step, containerId) is not null;

    void BranchEnded(Step container, int index, OutcomeClass cls)
    {
        if (!State.Containers.TryGetValue(container.Id, out var state) || !state.Active || index < 0)
            return;
        state.Branches[index] = cls == OutcomeClass.Success ? BranchStatus.Succeeded : BranchStatus.Failed;
        Trace("branch_ended", container.Id, $"branch {index}: {cls.Name()}");
        var join = container.Container!.Join;
        var succeeded = state.Branches.Count(b => b == BranchStatus.Succeeded);
        var open = state.Branches.Count(b => b is BranchStatus.Waiting or BranchStatus.Running);
        if (container.Form == StepForm.Chain)
        {
            // A chain: success goes on to the next item; anything else ends it (docs/03 section 8.2).
            if (cls != OutcomeClass.Success)
                Join(container, state, "any_failed");
            else if (open == 0)
                Join(container, state, "completed");
            else
                StartWaitingBranches(container, state);
            return;
        }

        var needed = join switch
        {
            "all" => state.Branches.Count,
            "any" => 1,
            _ => int.Parse(join, System.Globalization.CultureInfo.InvariantCulture),
        };
        if (join == "all")
        {
            if (open == 0)
                Join(container, state, succeeded == state.Branches.Count ? "completed" : "any_failed");
            else
                StartWaitingBranches(container, state);
        }
        else if (succeeded >= needed)
            Join(container, state, "completed");
        else if (succeeded + open < needed)
            Join(container, state, "any_failed");
        else
            StartWaitingBranches(container, state);
    }

    void Join(Step container, ContainerRecord state, string outcome)
    {
        state.Active = false;
        for (var i = 0; i < state.Branches.Count; i++)
            if (state.Branches[i] is BranchStatus.Waiting or BranchStatus.Running)
                state.Branches[i] = BranchStatus.Cancelled;
        CancelWithin(container.Id);
        Finish(container, Record(container.Id), outcome);
    }

    /// <summary>Cancels what is still running or queued in a container's branches, once its join has decided.</summary>
    void CancelWithin(string containerId)
    {
        foreach (var id in State.InFlight.Where(id => InContainer(id, containerId)).ToList())
        {
            State.InFlight.Remove(id);
            Trace("cancelled", id);
            outbox.Add(new CancelStep(id));
        }

        State.Pending.RemoveAll(e => InContainer(e.StepId, containerId));
        foreach (var wait in State.Waits.Where(w => InContainer(w.StepId, containerId)).ToList())
            CloseWaitFor(wait);
    }

    void CloseWaitFor(OpenWait wait)
    {
        State.Waits.Remove(wait);
        Trace("wait_closed", wait.StepId, wait.Id);
        outbox.Add(new CloseWait(wait.Id, wait.StepId));
    }

    void Intervene(string stepId, string? outcome, string reason)
    {
        var wait = new OpenWait(NewWaitId("intervention"), WaitKind.Intervention, stepId, outcome, reason);
        State.Waits.Add(wait);
        Trace("escalated", stepId, reason);
        outbox.Add(new RaiseIntervention(wait.Id, stepId, outcome, reason));
        Progressed();
    }

    string NewWaitId(string kind) => $"{kind}-{State.NextWaitNumber++}";

    void End(RunStatus status, string stepId, string detail)
    {
        State.Status = status;
        foreach (var id in State.InFlight)
        {
            Trace("cancelled", id);
            outbox.Add(new CancelStep(id));
        }

        State.InFlight.Clear();
        State.Pending.Clear();
        foreach (var wait in State.Waits.ToList())
            CloseWaitFor(wait);
        Trace("ended", stepId, $"{status.Name()} ({detail})");
        outbox.Add(new EndRun(status));
    }

    /// <summary>A step named in an expression, as the load check finds it: in a branch first, then outwards.</summary>
    public Step? FindStep(string local, Scope scope)
    {
        for (var s = scope; ; s = s.Parent!)
        {
            if (byId.TryGetValue(s.Prefix + local, out var step))
                return step;
            if (s.Kind != ScopeKind.Branch)
                return null;
        }
    }
}
