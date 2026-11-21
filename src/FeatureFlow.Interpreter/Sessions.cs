using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Interpreter;

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
