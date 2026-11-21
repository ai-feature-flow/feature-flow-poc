using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

/// <summary>What a session opens with (<see cref="RunCore.SessionFacts"/>).</summary>
public sealed record SessionFacts(
    string Repository,
    string Folder,
    IReadOnlyList<SessionDocumentFile> Files,
    string Prompt,
    IReadOnlyDictionary<string, string> Context,
    IReadOnlyList<string> ChecksFrom,
    string SubmitOutcome,
    string? AbandonOutcome);

/// <summary>
/// What a session's import reads (<see cref="RunCore.SessionImport"/>): the action that imports and the outcome it reports, the
/// session's workspace and files, and the step's execution.
/// </summary>
public sealed record SessionImport(string ImportAction, string Outcome, string Repository, string Folder, IReadOnlyList<SessionDocumentFile> Files, int Execution);

/// <summary>
/// Interactive sessions on a decision step's gate (docs/04 section 4.4): the shell opens the session and says so
/// (<see cref="GateSessionOpened"/>); answers to that gate then follow the session's rules (<see cref="Route"/>).
/// </summary>
public sealed partial class RunCore
{
    /// <summary>
    /// A value a decision's <c>session</c> names (docs/03 section 2.3: its repository or folder), each <c>{ expr: }</c>
    /// evaluated now at the step. Throws <see cref="EvaluationException"/> when it cannot be evaluated.
    /// </summary>
    public object? ResolveSessionValue(string stepId, YNode node, string where)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        return Resolve(node, step, where, name => Findings?.Read(name) ?? State.Findings.GetValueOrDefault(name));
    }

    /// <summary>
    /// The decision step whose open gate <paramref name="waitId"/> has a session, or null. Only these are sessions: a gate whose
    /// plan declares <c>session</c> but that opened none is answered as any gate is.
    /// </summary>
    public Step? SessionStep(string waitId) =>
        State.Waits.FirstOrDefault(w => w.Id == waitId && w is { Kind: WaitKind.Gate, Session: true }) is { } wait
        && StepById(wait.StepId) is { Decision.Session: not null } step
            ? step
            : null;

    /// <summary>The artifacts an outcome of a step promises: those declared for it, and those declared for every success.</summary>
    public static IReadOnlyList<string> Promised(Step step, string outcome) => step.Produces
        .Where(p => p.Outcome == outcome || (p.Outcome is null && step.Outcomes().Any(o => o.Name == outcome && o.Class == Model.OutcomeClass.Success)))
        .Select(p => p.Name).Distinct().ToList();

    /// <summary>The outcome a submission takes: the one that promises the session's files (std: <c>submitted</c>), else the first success.</summary>
    public static string SubmitOutcome(Step step)
    {
        var files = step.Decision!.Session!.Files.Select(f => f.Name).ToList();
        return step.DeclaredOutcomes.FirstOrDefault(o => Promised(step, o.Name) is { Count: > 0 } promised && files.All(promised.Contains))?.Name
               ?? step.DeclaredOutcomes.FirstOrDefault(o => o.Class == Model.OutcomeClass.Success)?.Name
               ?? step.DeclaredOutcomes.First().Name;
    }

    /// <summary>The outcome abandoning takes: the first declared one that is not a success (std: <c>abandoned</c>).</summary>
    public static string? AbandonOutcome(Step step) => step.DeclaredOutcomes.FirstOrDefault(o => o.Class != Model.OutcomeClass.Success)?.Name;

    /// <summary>A session's repository (its own, else the run's first) and folder, evaluated now. Throws <see cref="EvaluationException"/>.</summary>
    public (string Repository, string Folder) SessionWorkspace(string stepId)
    {
        var step = StepById(stepId) ?? throw new EvaluationException($"{stepId} is not a step of plan revision {State.Revision}");
        var spec = step.Decision?.Session ?? throw new EvaluationException($"{stepId} declares no session");
        var repository = (spec.Repository is { } r ? ResolveSessionValue(stepId, r, "decision.session.repository") as string : null)
                         ?? Run.Repositories.FirstOrDefault()
                         ?? throw new EvaluationException($"{stepId}: the session names no repository and the run has none");
        var folder = spec.Folder is { } f ? ResolveSessionValue(stepId, f, "decision.session.folder") as string ?? "" : "";
        return (repository, folder);
    }

    /// <summary>
    /// What a session opens with (docs/04 section 4.4): its workspace, its files, each seeded from the artifact of its name the
    /// decision consumes, else the last submission (a re-entry); the rendered prompt; what the decision consumes, by key; the
    /// steps its checks stand for; and the outcomes a submission and an abandonment take. Throws <see cref="EvaluationException"/>.
    /// </summary>
    public SessionFacts SessionFacts(string stepId)
    {
        var (repository, folder) = SessionWorkspace(stepId);
        var step = StepById(stepId)!;
        var previous = State.Steps.GetValueOrDefault(stepId)?.Previous;
        var consumed = Consumed(stepId).Where(c => !c.LedgerView).ToList();
        string? Seed(string name) => consumed.FirstOrDefault(c => c.Name == name) is { Reference: { } reference } ? reference : previous?.Artifacts.GetValueOrDefault(name);
        var context = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in consumed.Where(c => c.Reference is not null))
            context[c.Key] = c.Reference!;
        var spec = step.Decision!.Session!;
        var prompt = step.Decision.Prompt is { Unbound: false } p ? RenderPrompt(stepId, p.Text) ?? "" : "";
        return new SessionFacts(repository, folder, spec.Files.Select(f => new SessionDocumentFile(f.Path, f.Name, f.Type?.Text, Seed(f.Name))).ToList(),
            prompt, context, spec.ChecksFrom, SubmitOutcome(step), AbandonOutcome(step));
    }

    /// <summary>
    /// The files a session's import reads (<c>artifact.import</c>): by path, the artifact name each becomes and its type, with the
    /// session step's execution. Throws <see cref="EvaluationException"/> when its workspace cannot be evaluated.
    /// </summary>
    public SessionImport SessionImport(string stepId)
    {
        var (repository, folder) = SessionWorkspace(stepId);
        var files = StepById(stepId)!.Decision!.Session!.Files;
        return new SessionImport("artifact.import", "imported", repository, folder,
            files.Select(f => new SessionDocumentFile(f.Path, f.Name, f.Type?.Text, null)).ToList(), State.Steps.GetValueOrDefault(stepId)?.Executions ?? 0);
    }

    /// <summary>
    /// The later step a pre-submit check stands for and the session's decision step, or why the check is refused: the run has
    /// ended or is handing over, the session is not open or is being submitted, the revision is not a commit, or the step is not
    /// one of the session's <c>checks_from</c> or not an <see cref="SessionNames.CheckAction"/> step (LOG 238).
    /// </summary>
    public (string? Target, string? Session, string? Error) CheckTarget(SessionCheckArgs check, bool draining, Func<string, bool> submitting)
    {
        if (State.Status != RunStatus.Running)
            return (null, null, $"the run has ended ({State.Status.Name()})");
        if (draining)
            return (null, null, SessionNames.HandingOver);
        if (SessionNames.WaitOf(check.SessionId, Run.Id) is not { } waitId || SessionStep(waitId) is not { } session)
            return (null, null, $"session {check.SessionId} is not open in run {Run.Id}");
        if (submitting(waitId))
            return (null, null, $"session {check.SessionId} is being submitted");
        if (!SessionNames.IsCommit(check.Revision))
            return (null, null, $"check a full commit id, not '{check.Revision}': a branch can move");
        var checksFrom = session.Decision!.Session!.ChecksFrom;
        if (!checksFrom.Contains(check.StepId))
            return (null, null, $"{check.StepId} is not a check of session {check.SessionId} (checks_from: {string.Join(", ", checksFrom)})");
        if (Plan.FindStep(check.StepId, session.Scope) is not { } target)
            return (null, null, $"{check.StepId} names no step of the plan");
        if (target.Action != SessionNames.CheckAction)
            return (null, null, $"{check.StepId} is {(target.Form == StepForm.Action ? target.Action : $"a{(target.Form == StepForm.Agent ? "n agent" : " decision")} step")}: " +
                                $"only an {SessionNames.CheckAction} step can be run as a pre-submit check (LOG 238)");
        return (target.Id, session.Id, null);
    }

    /// <summary>
    /// A step that a session's <c>checks_from</c> names records the digest of what its activity received, as its checks recorded
    /// theirs, so the two can be compared (docs/09 section 4.2).
    /// </summary>
    public void NoteCheckInputs(string stepId, string? digest)
    {
        if (digest is not null && Plan.AllSteps.Any(s => s.Decision?.Session is { } spec && spec.ChecksFrom.Any(c => Plan.FindStep(c, s.Scope)?.Id == stepId)))
            Trace("check_inputs", stepId, digest);
    }

    ApplyResult OpenSession(GateSessionOpened s)
    {
        var at = State.Waits.FindIndex(w => w.Id == s.WaitId && w.Kind == WaitKind.Gate);
        if (at < 0)
            return ApplyResult.Refused($"no gate is open as {s.WaitId}");
        State.Waits[at] = State.Waits[at] with { Session = true };
        return ApplyResult.Ok;
    }

    /// <summary>
    /// Why an answer to a session's gate is refused, or null. Only a submission (an answer with the exact commit) takes the
    /// outcome that promises the session's files, and its artifacts come from the run's own import, never the request.
    /// </summary>
    string? SessionAnswerError(AnswerArgs answer, Step step, bool submitting)
    {
        var session = SessionNames.Id(Run.Id, answer.WaitId);
        if (submitting)
            return $"session {session} is being submitted";
        if (answer.Revision is null)
            return answer.Outcome is { } outcome && Promised(step, outcome).Count > 0
                ? $"{answer.WaitId} is session {session}: '{outcome}' takes the session's files, so submit it (ff session submit {session})"
                : null;
        if (answer.Artifacts is not null)
            return $"session {session}: a submission's artifacts are imported by the run from the commit, not given";
        return answer.Outcome == SubmitOutcome(step) ? null : $"session {session}: a submission takes '{SubmitOutcome(step)}', not '{answer.Outcome}'";
    }
}
