using System.Globalization;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;

namespace FeatureFlow.Interpreter;

/// <summary>Where a person's answer (<see cref="RunCore.Route"/>) or override (<see cref="RunCore.RouteOverride"/>) goes.</summary>
public abstract record AnswerRoute;

/// <summary>
/// The same answer as the one the wait was answered with, or the same override as the one applied with its id: succeeds
/// without a second effect (LOG 58). An override's keeps the result it was first applied with.
/// </summary>
public sealed record Duplicate(string? Result = null) : AnswerRoute;

/// <summary>Refused: a conflicting answer, a question that timed out, an answer of the wrong kind, or one the run would not apply.</summary>
public sealed record Refused(string Error) : AnswerRoute;

/// <summary>Accepted: the core input the answer stands for, which <see cref="RunCore.Answer"/> applies.</summary>
public sealed record Applies(RunInput Input) : AnswerRoute;

/// <summary>A session's submission (docs/04 section 4.4): the shell imports the session's files first, then gives them to <see cref="RunCore.Answer"/>.</summary>
public sealed record Submits(string StepId) : AnswerRoute;

/// <summary>
/// A library override (docs/08 section 5.2): the shell stores <see cref="Content"/> for <see cref="Item"/> (an activity), the
/// core applies it as <see cref="LibraryOverridden"/>, and <see cref="RunCore.Override"/> records it with the stored result.
/// </summary>
public sealed record StoresLibrary(string Item, string Content) : AnswerRoute;

/// <summary>What <see cref="RunCore.Override"/> did: refused with <see cref="Error"/>, or applied (or a duplicate) with the reply's <see cref="Result"/>, and whether an abort asked for the run's open pull requests to be closed.</summary>
public sealed record OverrideOutcome(string? Error, string Result = "", bool ClosePullRequests = false);

/// <summary>
/// One routing for every human wait (docs/04 section 4): gates, agents' questions, interventions, patch approvals and
/// breakpoints open here, are answered through <see cref="Route"/> and <see cref="Answer"/>, and close here, each close kept
/// in <see cref="RunState.Closed"/> and each change in <see cref="RunState.WaitChanges"/> for the shell's records. The update
/// validator calls <see cref="Route"/> alone; the handler calls <see cref="Answer"/>, which routes the same way, so the two
/// cannot disagree.
/// </summary>
public sealed partial class RunCore
{
    /// <summary>The answers to a patch approval.</summary>
    public static readonly IReadOnlyList<string> PatchChoices = ["approve", "reject"];

    /// <summary>
    /// Where an answer goes, without changing anything: a duplicate, refused with the reason, the input to apply, or a session
    /// submission to import first. <paramref name="submitting"/>: the shell is importing a submission to this wait now.
    /// </summary>
    public AnswerRoute Route(AnswerArgs a, bool submitting = false)
    {
        if (State.Closed.LastOrDefault(c => c.Id == a.WaitId) is { } closed)
        {
            if (closed.How == WaitEnds.Answered)
                return closed.Key == Key(a) ? new Duplicate() : new Refused($"conflict: {a.WaitId} was already answered ({closed.Key})");
            if (closed.Kind == WaitKind.Question)
                return new Refused(closed.How == WaitEnds.TimedOut
                    ? $"timed out: the agent has carried on without an answer to {a.WaitId}"
                    : $"step ended: {closed.StepId} stopped running before {a.WaitId} was answered, so no agent is left to read it");
        }

        if (State.Status != RunStatus.Running)
            return new Refused($"the run has ended ({State.Status.Name()})");
        if (State.Waits.FirstOrDefault(w => w.Id == a.WaitId) is not { } wait)
            return new Refused($"no wait is open as {a.WaitId}");

        RunInput input;
        switch (wait.Kind)
        {
            case WaitKind.Question:
                if (string.IsNullOrWhiteSpace(a.Outcome))
                    return new Refused($"{a.WaitId} is a question: answer with text");
                return new Applies(new QuestionAnswered(a.WaitId, a.Outcome));
            case WaitKind.Gate:
                var step = StepById(wait.StepId);
                if (wait.Session && step is not null)
                {
                    if (SessionAnswerError(a, step, submitting) is { } refused)
                        return new Refused(refused);
                    if (a.Revision is not null)
                        return new Submits(step.Id);
                }
                else if (a.Revision is not null)
                    return new Refused($"{a.WaitId} is not an open session of run {Run.Id}: nothing to submit");

                if (a.Outcome is null)
                    return new Refused($"{a.WaitId} is a gate: answer with an outcome");
                if (step is { Decision.Session: null } && ArtifactsMissing(a, step) is { } missing)
                    return new Refused(missing);
                // An answer's artifacts (a person's triage, LOG 202) become the decision step's, as a completed step's do.
                input = new DecisionAnswered(wait.StepId, a.Outcome, a.Artifacts);
                break;
            case WaitKind.PatchApproval:
                if (a.Action is not ("approve" or "reject"))
                    return new Refused($"{a.WaitId} is a patch approval: answer {Or(PatchChoices)}");
                input = new PatchAnswered(a.WaitId, a.Action == "approve", a.Actor);
                break;
            default:
                // A breakpoint's continue may come as its outcome (ff run answer <run> <wait> continue).
                var choices = Recoveries.For(wait.Kind);
                var name = wait.Kind == WaitKind.Breakpoint ? a.Action ?? a.Outcome : a.Action;
                if (!choices.Contains(name) || Recoveries.Parse(name) is not { } kind)
                    return new Refused($"{a.WaitId} is a{(wait.Kind == WaitKind.Breakpoint ? " breakpoint" : "n intervention")}: answer {Or(choices)}");
                input = new InterventionAnswered(a.WaitId,
                    new Recovery(kind, a.StepId, a.CleanUp, a.Attempts, a.Status is null ? null : RunStatuses.Parse(a.Status), a.BeyondHardCeiling));
                break;
        }

        return InputError(input) is { } error ? new Refused(error) : new Applies(input);
    }

    /// <summary>
    /// Routes an answer and applies it: the wait closes as answered, with the answer's key and actor, and the timeline says
    /// who answered. A submission (<see cref="Submits"/>) needs the files the shell imported as <paramref name="imported"/>.
    /// A duplicate changes nothing.
    /// </summary>
    public ApplyResult Answer(AnswerArgs a, IReadOnlyDictionary<string, string>? imported = null)
    {
        RunInput input;
        switch (Route(a))
        {
            case Duplicate:
                return ApplyResult.Ok;
            case Refused r:
                return ApplyResult.Refused(r.Error);
            case Submits when imported is null:
                return ApplyResult.Refused($"{a.WaitId}: a submission's files are imported by the run before it is answered");
            case Submits s:
                input = new DecisionAnswered(s.StepId, a.Outcome!, imported);
                break;
            case Applies p:
                input = p.Input;
                break;
            default:
                throw new InvalidOperationException("unknown route");
        }

        var wait = State.Waits.First(w => w.Id == a.WaitId);
        var result = Apply(input);
        if (!result.Accepted)
            return result;
        var at = State.Closed.FindLastIndex(c => c.Id == a.WaitId);
        if (at >= 0)
            State.Closed[at] = State.Closed[at] with { Key = Key(a), Actor = a.Actor };
        if (wait.Kind == WaitKind.Question)
            Trace("question_answered", wait.StepId, $"{a.WaitId} by {a.Actor}");
        else
            Trace("answered", a.WaitId, $"{Key(a)} by {a.Actor}");
        return result;
    }

    /// <summary>What an answer is compared by: a decision's outcome or a question's text, else the action with its arguments.</summary>
    public static string Key(AnswerArgs a) =>
        a.Outcome ?? string.Join(" ", new[] { a.Action, a.StepId, a.CleanUp ? "clean_up" : null, a.Attempts > 0 ? a.Attempts.ToString(CultureInfo.InvariantCulture) : null, a.Status }
            .Where(x => x is not null));

    /// <summary>Why the core would refuse an answer's input now, or null.</summary>
    string? InputError(RunInput input) => input switch
    {
        DecisionAnswered d => GateAnswerError(d, out _),
        InterventionAnswered i => RecoveryError(i, out _),
        _ => null,
    };

    /// <summary>Why an answer to the gate at a step would be refused, or null.</summary>
    string? GateAnswerError(DecisionAnswered a, out OpenWait? wait)
    {
        wait = State.Waits.FirstOrDefault(w => w.Kind == WaitKind.Gate && w.StepId == a.StepId);
        if (wait is null || StepById(a.StepId) is not { } step)
            return $"no decision is open at {a.StepId}";
        if (IsActionDecision(wait))
            return ActionAnswerError(a);
        return step.DeclaredOutcomes.Any(o => o.Name == a.Outcome)
            ? SessionArtifactsError(step, a)
            : $"{a.StepId}: '{a.Outcome}' is not a declared outcome ({string.Join(", ", step.DeclaredOutcomes.Select(o => o.Name))})";
    }

    /// <summary>
    /// An answer to a gate with no session gives the artifacts its outcome promises (a person's triage, LOG 248); a session's
    /// gate is submitted from its commit instead.
    /// </summary>
    static string? ArtifactsMissing(AnswerArgs a, Step step)
    {
        var missing = Promised(step, a.Outcome!).Where(n => a.Artifacts?.ContainsKey(n) != true).ToList();
        return missing.Count == 0 ? null : $"{a.WaitId}: '{a.Outcome}' must give artifact {string.Join(", ", missing)}";
    }

    static string Or(IReadOnlyList<string> choices) =>
        choices.Count < 2 ? string.Join("", choices) : $"{string.Join(", ", choices.Take(choices.Count - 1))} or {choices[^1]}";

    // ---- Opening and closing ----------------------------------------------------------------------

    OpenWait AddWait(OpenWait wait)
    {
        State.Waits.Add(wait);
        State.WaitChanges.Add(new WaitChange(wait, true));
        return wait;
    }

    void RemoveWait(OpenWait wait, string how)
    {
        State.Waits.Remove(wait);
        State.Closed.Add(new ClosedWait(wait.Id, wait.Kind, wait.StepId, how));
        State.WaitChanges.Add(new WaitChange(wait, false));
    }

    /// <summary>Closes a wait no one answered, on the timeline and for the shell.</summary>
    void CloseWaitFor(OpenWait wait, string how)
    {
        RemoveWait(wait, how);
        Trace("wait_closed", wait.StepId, wait.Id);
        outbox.Add(new CloseWait(wait.Id, wait.StepId));
    }

    /// <summary>
    /// A step stops running, however it ended (completed, failed, cannot answer, interrupted, cancelled, or waiting at its
    /// action's gate): its questions close, since no agent is left to read the answer (LOG A2-0e). Returns whether it was running.
    /// </summary>
    bool LeaveInFlight(string stepId)
    {
        foreach (var question in State.Waits.Where(w => w.Kind == WaitKind.Question && w.StepId == stepId).ToList())
            RemoveWait(question, WaitEnds.StepEnded);
        return State.InFlight.Remove(stepId);
    }

    /// <summary>The identity of a step's execution, by which its activity is idempotent and its agent's questions are held.</summary>
    public string StepKey(string stepId, int execution) => $"{Run.Id}/{stepId}/{execution}";

    /// <summary>The timers the open waits need, for a shell starting again after a hand-over.</summary>
    public IReadOnlyList<ArmTimer> Timers() =>
        State.Waits.Where(w => w.Deadline is not null).Select(w => new ArmTimer(w.Id, w.Deadline!.Value)).ToList();

    // ---- Questions (docs/04 section 3.4; LOG 93 as amended by A2-0h) --------------------------------

    ApplyResult OpenQuestion(QuestionOpened q)
    {
        if (State.Waits.Any(w => w.Id == q.QuestionId) || State.Closed.Any(c => c.Id == q.QuestionId))
            return ApplyResult.Ok;
        if (StepById(q.StepId) is null || !State.InFlight.Contains(q.StepId) || q.StepKey != StepKey(q.StepId, Record(q.StepId).Executions))
            return ApplyResult.Refused($"{q.StepId} is not running as {q.StepKey}: question {q.QuestionId} is not held");
        var deadline = q.At + q.Timeout;
        AddWait(new OpenWait(q.QuestionId, WaitKind.Question, q.StepId, null, q.Text)
        {
            StepKey = q.StepKey,
            Options = q.Options.ToList(),
            Deadline = deadline,
        });
        outbox.Add(new ArmTimer(q.QuestionId, deadline));
        return ApplyResult.Ok;
    }

    ApplyResult AnswerQuestion(QuestionAnswered q)
    {
        if (State.Waits.FirstOrDefault(w => w.Id == q.WaitId && w.Kind == WaitKind.Question) is not { } wait)
            return ApplyResult.Refused($"no question is open as {q.WaitId}");
        RemoveWait(wait, WaitEnds.Answered);
        return ApplyResult.Ok;
    }

    /// <summary>On timeout the agent is told no answer came, and carries on (docs/04 section 3.4; LOG 18). A timer for a closed wait does nothing.</summary>
    ApplyResult TimerFired(WaitTimerFired t)
    {
        if (State.Waits.FirstOrDefault(w => w.Id == t.WaitId) is { Kind: WaitKind.Question, Deadline: { } deadline } question && t.At >= deadline)
        {
            RemoveWait(question, WaitEnds.TimedOut);
            Trace("question_timed_out", question.StepId, question.Id);
        }

        return ApplyResult.Ok;
    }

    // ---- Records ----------------------------------------------------------------------------------

    /// <summary>
    /// The waits opened and closed since the shell last recorded, drained: each to record, opened with its description or
    /// closed, leaving out one that opened and closed in between, since no one saw it; and each question that closed answered or
    /// timed out, whose record the agent endpoint long-polls for its answer (docs/04 section 3.4).
    /// </summary>
    public (IReadOnlyList<WaitShown> Waits, IReadOnlyList<QuestionClosed> Questions) TakeWaitChanges()
    {
        var changes = State.WaitChanges.ToList();
        State.WaitChanges.Clear();
        var waits = new List<WaitShown>();
        var questions = new List<QuestionClosed>();
        foreach (var (wait, opened) in changes)
        {
            if (!changes.Any(c => c.Wait.Id == wait.Id && c.Open != opened))
                waits.Add(new WaitShown(wait.Id, wait.StepId, opened ? Describe(wait) : null));
            if (!opened && wait.Kind == WaitKind.Question && State.Closed.LastOrDefault(c => c.Id == wait.Id) is { How: WaitEnds.Answered or WaitEnds.TimedOut } closed)
                questions.Add(new QuestionClosed(wait.Id, wait.StepKey!, wait.Reason, wait.Options ?? [], closed.How == WaitEnds.Answered, closed.Key));
        }

        return (waits, questions);
    }

    /// <summary>The open waits, for the workflow's query.</summary>
    public IReadOnlyList<WaitView> WaitViews() => State.Waits.Select(w => new WaitView(w.Id, w.Kind.ToString(), w.StepId, w.Reason)).ToList();
    /// <summary>
    /// A wait as the record store shows it: its kind, its decision type at the run's level, the choices that answer it, and
    /// the text to show (a gate's rendered prompt, a question's text, else the core's reason), so Slack, the console and the
    /// CLI show the same.
    /// </summary>
    public WaitDescription Describe(OpenWait wait)
    {
        var level = Plan.Document.Level ?? "run";
        var step = StepById(wait.StepId);
        var kind = wait.Kind.Name();
        return wait.Kind switch
        {
            WaitKind.Gate when step?.Form == StepForm.Action => new(kind, ActionDecisionTypeOf(step.Id) ?? $"{level}.decision", ActionDecisions.Choices, wait.Reason),
            WaitKind.Gate => new(kind, step?.Decision?.Type ?? $"{level}.decision", step?.DeclaredOutcomes.Select(o => o.Name).ToList() ?? [], GatePrompt(wait, step)),
            WaitKind.Question => new(kind, $"{level}.question", wait.Options ?? [], wait.Reason),
            WaitKind.PatchApproval => new(kind, $"{level}.patch_approval", PatchChoices, wait.Reason),
            WaitKind.Breakpoint => new(kind, $"{level}.breakpoint", Recoveries.For(wait.Kind), wait.Reason),
            _ => new(kind, $"{level}.escalation", Recoveries.For(wait.Kind), wait.Reason),
        };
    }

    /// <summary>A decision's rendered prompt, else the core's reason.</summary>
    string GatePrompt(OpenWait wait, Step? step)
    {
        if (step?.Decision?.Prompt is not { Unbound: false } prompt)
            return wait.Reason;
        try
        {
            return RenderPrompt(step.Id, prompt.Text) is { Length: > 0 } text ? text : wait.Reason;
        }
        catch (EvaluationException)
        {
            return wait.Reason;
        }
    }
}
