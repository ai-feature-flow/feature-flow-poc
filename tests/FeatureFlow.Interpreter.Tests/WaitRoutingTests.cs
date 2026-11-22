using System.Text.Json;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model.Plans;

namespace FeatureFlow.Interpreter.Tests;

/// <summary>
/// One routing for every human wait (docs/04 section 4; F11, L2): agents' questions, gates (a decision step's and the
/// <c>merge_action</c> gate an action raises), interventions, breakpoints and patch approvals each open, are answered and
/// close through <see cref="RunCore.Route"/> and <see cref="RunCore.Answer"/>, driven as the shell and the re-drive runner
/// drive them. Every answer is given twice over: <see cref="Agree"/> asserts the update validator's path (Route alone) and
/// the handler's (Answer) decide the same.
/// </summary>
public class WaitRoutingTests
{
    const string Waits = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: waits, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: ask
            agent:
              role: std:roles/planner
              task: std:tasks/repair_failures
              planning: { targets: [self] }
            outcomes: { done: success, stuck: blocked }
          - id: approve
            decision: { type: delivery.partial_recovery, prompt: std:prompts/partial_delivery }
            outcomes: { go: success, stop: failure }
            routes: { go: merge, stop: { end: rejected } }
          - id: merge
            action: scm.merge
            with: { engine_can_merge: false }
            routes:
              merged: { end: completed }
              declined: { end: rejected }
              base_moved: merge
              checks_failed: { end: failed }
        """;

    static readonly Probe Lib = new("wait-routing", ("waits", Waits));

    static readonly DateTimeOffset T0 = new(2026, 11, 21, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The run started: <c>ask</c>, an agent step, is running as execution 1.</summary>
    static RunCore Started(RunOptions? options = null)
    {
        var core = Lib.Start("waits", options);
        Assert.Equal([new StartStep("ask", 1)], core.Advance());
        return core;
    }

    /// <summary>
    /// Answers as the shell does: the validator's <see cref="RunCore.Route"/>, then the handler's <see cref="RunCore.Answer"/>.
    /// The two agree: a refusal is the same refusal, a duplicate changes nothing, an accepted route is applied.
    /// </summary>
    static ApplyResult Agree(RunCore core, AnswerArgs answer)
    {
        var route = core.Route(answer);
        var waits = core.State.Waits.ToList();
        var result = core.Answer(answer);
        switch (route)
        {
            case Refused refused:
                Assert.False(result.Accepted, $"the validator refused ({refused.Error}) but the handler applied it");
                Assert.Equal(refused.Error, Assert.Single(result.Errors));
                Assert.Equal(waits, core.State.Waits);
                break;
            case Duplicate:
                Assert.True(result.Accepted);
                Assert.Equal(waits, core.State.Waits);
                break;
            case Applies:
                Assert.True(result.Accepted, $"the validator accepted but the handler refused: {string.Join("; ", result.Errors)}");
                Assert.DoesNotContain(core.State.Waits, w => w.Id == answer.WaitId);
                Assert.Contains(core.State.Closed, c => c.Id == answer.WaitId && c is { How: WaitEnds.Answered } && c.Key == RunCore.Key(answer) && c.Actor == answer.Actor);
                break;
            default:
                Assert.Fail($"unexpected route {route}");
                break;
        }

        return result;
    }

    static string Refusal(RunCore core, AnswerArgs answer)
    {
        var result = Agree(core, answer);
        Assert.False(result.Accepted);
        return result.Errors[0];
    }

    static void Accepted(RunCore core, AnswerArgs answer) => Assert.True(Agree(core, answer).Accepted);

    static QuestionOpened Question(string id = "q-1", string stepKey = "run-1/ask/1") =>
        new(id, "ask", stepKey, "Which table?", ["orders", "items"], TimeSpan.FromMinutes(30), T0);

    // ---- Questions -------------------------------------------------------------------------------

    [Fact]
    public void A_question_opens_on_its_running_step_and_is_answered_once()
    {
        var core = Started();
        Assert.True(core.Apply(Question()).Accepted);
        var wait = Assert.Single(core.State.Waits);
        Assert.Equal((WaitKind.Question, "ask", "run-1/ask/1"), (wait.Kind, wait.StepId, wait.StepKey));
        Assert.Equal([new ArmTimer("q-1", T0.AddMinutes(30))], core.Advance());
        // Idempotent by the question's id (LOG 59): the endpoint's retry opens nothing more and arms no second timer.
        Assert.True(core.Apply(Question()).Accepted);
        Assert.Single(core.State.Waits);
        Assert.Empty(core.Advance());
        Assert.Equal("question ticket.question [orders, items | ]: Which table?", Shown(core, wait));

        Assert.Equal("q-1 is a question: answer with text", Refusal(core, new AnswerArgs("q-1", "alice", "  ")));
        Accepted(core, new AnswerArgs("q-1", "alice", "orders"));
        Assert.Contains(core.State.Trace, t => t is { Kind: "question_answered", StepId: "ask", Detail: "q-1 by alice" });
        // The step is still running: a question never routes the plan.
        Assert.Equal(["ask"], core.State.InFlight);

        Assert.IsType<Duplicate>(core.Route(new AnswerArgs("q-1", "bob", "orders")));
        Agree(core, new AnswerArgs("q-1", "bob", "orders"));
        Assert.Equal("conflict: q-1 was already answered (orders)", Refusal(core, new AnswerArgs("q-1", "bob", "items")));
    }

    [Fact]
    public void A_question_is_held_only_for_the_execution_that_asked_it()
    {
        var core = Started();
        Assert.False(core.Apply(Question(stepKey: "run-1/ask/2")).Accepted);
        Assert.False(core.Apply(Question() with { StepId = "approve", StepKey = "run-1/approve/1" }).Accepted);
        Assert.Empty(core.State.Waits);
    }

    [Fact]
    public void A_question_times_out_at_its_deadline_and_then_refuses_an_answer()
    {
        var core = Started();
        core.Apply(Question());
        core.Advance();

        Assert.True(core.Apply(new WaitTimerFired("q-1", T0.AddMinutes(29))).Accepted);
        Assert.Single(core.State.Waits);
        Assert.True(core.Apply(new WaitTimerFired("q-1", T0.AddMinutes(30))).Accepted);
        Assert.Empty(core.State.Waits);
        Assert.Equal(new ClosedWait("q-1", WaitKind.Question, "ask", WaitEnds.TimedOut), Assert.Single(core.State.Closed));
        Assert.Contains(core.State.Trace, t => t is { Kind: "question_timed_out", StepId: "ask", Detail: "q-1" });

        Assert.Equal("timed out: the agent has carried on without an answer to q-1", Refusal(core, new AnswerArgs("q-1", "alice", "orders")));
        // Closed is closed: neither a late timer nor the endpoint's retry opens it again.
        Assert.True(core.Apply(new WaitTimerFired("q-1", T0.AddHours(1))).Accepted);
        Assert.True(core.Apply(Question()).Accepted);
        Assert.Empty(core.State.Waits);
    }

    [Theory]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cannot_answer")]
    [InlineData("interrupted")]
    [InlineData("aborted")]
    public void A_question_closes_whenever_its_step_stops_running(string how)
    {
        var core = Started();
        core.Apply(Question());
        Assert.True(core.Apply(how switch
        {
            "completed" => new StepCompleted("ask", "done"),
            "failed" => new StepFailed("ask", ErrorClass.Work, "broke"),
            "cannot_answer" => new StepCannotAnswer("ask", "no inputs"),
            "interrupted" => new StepInterrupted("ask", "hand-over"),
            _ => new RunAborted("stop"),
        }).Accepted);

        Assert.DoesNotContain(core.State.Waits, w => w.Kind == WaitKind.Question);
        Assert.Equal(new ClosedWait("q-1", WaitKind.Question, "ask", WaitEnds.StepEnded), core.State.Closed.First());
        Assert.Contains(core.State.WaitChanges, c => c.Wait.Id == "q-1" && !c.Open);
        Assert.Equal("step ended: ask stopped running before q-1 was answered, so no agent is left to read it", Refusal(core, new AnswerArgs("q-1", "alice", "orders")));
    }

    [Fact]
    public void An_open_question_and_its_timer_are_carried_over_a_hand_over()
    {
        var core = Started();
        core.Apply(Question());
        core.Advance();
        var resumed = Resume(core);
        Assert.Equal([new ArmTimer("q-1", T0.AddMinutes(30))], resumed.Timers());
        Assert.Equal("run-1/ask/1", Assert.Single(resumed.State.Waits).StepKey);
        Accepted(resumed, new AnswerArgs("q-1", "alice", "orders"));
        Assert.Empty(resumed.Timers());
    }

    // ---- Gates -----------------------------------------------------------------------------------

    /// <summary><c>ask</c> done: the run waits at <c>approve</c>'s gate.</summary>
    static (RunCore Core, string Gate) AtGate()
    {
        var core = Started();
        core.Apply(new StepCompleted("ask", "done"));
        var gate = Assert.IsType<OpenGate>(Assert.Single(core.Advance()));
        Assert.Equal(("approve", "gate-1"), (gate.StepId, gate.WaitId));
        return (core, gate.WaitId);
    }

    [Fact]
    public void A_gate_is_answered_with_a_declared_outcome_and_a_repeat_is_a_duplicate_or_a_conflict()
    {
        var (core, gate) = AtGate();
        var wait = Assert.Single(core.State.Waits);
        Assert.StartsWith("gate delivery.partial_recovery [go, stop | stop]: ", Shown(core, wait));

        Assert.Equal("gate-1 is a gate: answer with an outcome", Refusal(core, new AnswerArgs(gate, "alice", Action: "retry")));
        Assert.Equal("approve: 'maybe' is not a declared outcome (go, stop)", Refusal(core, new AnswerArgs(gate, "alice", "maybe")));
        Assert.Equal("gate-1 is not an open session of run run-1: nothing to submit", Refusal(core, new AnswerArgs(gate, "alice", "go", Revision: "abc")));
        Assert.Equal("no wait is open as gate-9", Refusal(core, new AnswerArgs("gate-9", "alice", "go")));

        Accepted(core, new AnswerArgs(gate, "alice", "go"));
        Assert.Contains(core.State.Trace, t => t is { Kind: "answered", StepId: "gate-1", Detail: "go by alice" });
        Assert.Equal([new StartStep("merge", 1)], core.Advance());
        Agree(core, new AnswerArgs(gate, "bob", "go"));
        Assert.Equal("conflict: gate-1 was already answered (go)", Refusal(core, new AnswerArgs(gate, "bob", "stop")));

        // The answer is carried: after a hand-over the repeat is still a duplicate, and the other answer still a conflict.
        var resumed = Resume(core);
        Assert.IsType<Duplicate>(resumed.Route(new AnswerArgs(gate, "bob", "go")));
        Assert.IsType<Refused>(resumed.Route(new AnswerArgs(gate, "bob", "stop")));
    }

    [Fact]
    public void A_session_gate_takes_its_files_only_by_submission()
    {
        var (core, gate) = AtGate();
        Assert.True(core.Apply(new GateSessionOpened(gate)).Accepted);
        Assert.True(Assert.Single(core.State.Waits).Session);
        // approve declares no session in the plan, so SessionStep finds none: the session rules apply only to a plan's session.
        Assert.Null(core.SessionStep(gate));
        Accepted(core, new AnswerArgs(gate, "alice", "stop"));
    }

    // ---- Interventions and breakpoints -----------------------------------------------------------

    /// <summary><c>ask</c> stuck: it escalates.</summary>
    static (RunCore Core, string Wait) AtIntervention()
    {
        var core = Started();
        core.Apply(new StepCompleted("ask", "stuck"));
        var raised = Assert.IsType<RaiseIntervention>(Assert.Single(core.Advance()));
        return (core, raised.WaitId);
    }

    /// <summary>A blocked agent's own reason is in the intervention's text, its start only, so the person can act on it.</summary>
    [Fact]
    public void A_blocked_agent_s_reason_is_in_the_intervention_s_text()
    {
        var core = Started();
        core.Apply(new StepCompleted("ask", "stuck", Summary: "  Shell writes to /ff/out were denied.\n"));
        var raised = Assert.IsType<RaiseIntervention>(Assert.Single(core.Advance()));
        Assert.Equal("stuck escalates: Shell writes to /ff/out were denied.", raised.Reason);
        Assert.Equal(raised.Reason, core.State.Waits[0].Reason);

        var longer = Started();
        longer.Apply(new StepCompleted("ask", "stuck", Summary: new string('x', 2000)));
        var cut = Assert.IsType<RaiseIntervention>(Assert.Single(longer.Advance())).Reason;
        Assert.StartsWith("stuck escalates: xxx", cut, StringComparison.Ordinal);
        Assert.EndsWith(" [...]", cut, StringComparison.Ordinal);
        Assert.Equal("stuck escalates: ".Length + 500, cut.Length);
    }

    [Theory]
    [InlineData("go_to", "approve", 0, null, "approve")]
    [InlineData("replan", "ask", 0, null, "ask")]
    [InlineData("grant_attempts", null, 2, null, "ask")]
    [InlineData("end", null, 0, "failed", null)]
    public void An_intervention_is_answered_with_each_recovery(string action, string? step, long attempts, string? status, string? next)
    {
        var (core, wait) = AtIntervention();
        Assert.Equal($"intervention ticket.escalation [retry, go_to, replan, grant_attempts, end | end]: {core.State.Waits[0].Reason}", Shown(core, core.State.Waits[0]));
        Assert.Equal("intervention-1 is an intervention: answer retry, go_to, replan, grant_attempts or end",
            Refusal(core, new AnswerArgs(wait, "root", Action: "continue")));

        Accepted(core, new AnswerArgs(wait, "root", Action: action, StepId: step, Attempts: attempts, Status: status));
        var commands = core.Advance();
        if (next is null)
            Assert.Equal(RunStatus.Failed, core.State.Status);
        else
            Assert.Contains(commands, c => c is StartStep s && s.StepId == next || c is OpenGate g && g.StepId == next);
        Agree(core, new AnswerArgs(wait, "root", Action: action, StepId: step, Attempts: attempts, Status: status));
    }

    [Fact]
    public void An_intervention_refuses_a_recovery_the_core_would_refuse()
    {
        var (core, wait) = AtIntervention();
        Assert.Contains("go to: 'nowhere' is not a step", Refusal(core, new AnswerArgs(wait, "root", Action: "go_to", StepId: "nowhere")));
        Assert.Contains("end: the status must be one of", Refusal(core, new AnswerArgs(wait, "root", Action: "end", Status: "completed")));
        Assert.Single(core.State.Waits);
    }

    [Theory]
    [InlineData("continue", true)]
    [InlineData("continue", false)]
    [InlineData("retry", true)]
    [InlineData("go_to", true)]
    [InlineData("grant_attempts", true)]
    [InlineData("end", true)]
    public void A_breakpoint_is_answered_with_each_recovery_and_continue_may_come_as_its_outcome(string action, bool asAction)
    {
        var core = Started();
        Assert.True(core.Apply(new BreakpointSet("approve", true, "ann")).Accepted);
        core.Apply(new StepCompleted("ask", "done"));
        core.Advance();
        var wait = Assert.Single(core.State.Waits);
        Assert.Equal($"breakpoint ticket.breakpoint [continue, retry, go_to, replan, grant_attempts, end | end]: {wait.Reason}", Shown(core, wait));

        var answer = asAction
            ? new AnswerArgs(wait.Id, "ann", Action: action, StepId: action == "go_to" ? "ask" : null, Attempts: action == "grant_attempts" ? 1 : 0,
                Status: action == "end" ? "aborted" : null)
            : new AnswerArgs(wait.Id, "ann", action);
        Accepted(core, answer);
        core.Advance();
        Assert.Equal(action == "end" ? RunStatus.Aborted : RunStatus.Running, core.State.Status);
    }

    // ---- Patch approvals -------------------------------------------------------------------------

    [Theory]
    [InlineData("approve", PatchStatus.Applied)]
    [InlineData("reject", PatchStatus.Rejected)]
    public void A_patch_approval_is_approved_or_rejected(string action, PatchStatus status)
    {
        var core = Started(new RunOptions { RequireReview = true });
        var d = new Model.DiagnosticBag();
        var read = PatchReader.Read(Model.Yaml.YamlText.Parse(
            "{ api_version: feature-flow/v1, kind: patch, base_revision: 1, resume_at: approve, changes: [{ op: set, target: merge, field: limits, value: { attempts: 2 } }] }", "patch", d)!, d)!;
        var patch = read with { ProposedBy = new Proposer("agent", "ask") };
        var proposed = core.Apply(new StepCompleted("ask", "done", Patch: patch));
        Assert.True(proposed.Patch!.Status == PatchStatus.Pending, string.Join("; ", proposed.Errors.Concat(proposed.Patch.Diagnostics.Select(x => x.ToString()))));
        var request = Assert.Single(core.Advance().OfType<RequestPatchApproval>());
        var description = core.Describe(Assert.Single(core.State.Waits));
        Assert.Equal(["approve", "reject"], description.Choices);
        Assert.Null(description.ReasonRequired);
        // One name per thing (LOG 360): the kind as recorded is the wait id's prefix and the decision type's suffix.
        Assert.Equal(("patch_approval", "ticket.patch_approval"), (description.Kind, description.Type));
        Assert.StartsWith(description.Kind + "-", request.WaitId, StringComparison.Ordinal);

        Assert.Equal($"{request.WaitId} is a patch approval: answer approve or reject", Refusal(core, new AnswerArgs(request.WaitId, "ann", "go")));
        var result = Agree(core, new AnswerArgs(request.WaitId, "ann", Action: action));
        Assert.Equal(status, result.Patch?.Status ?? PatchStatus.Applied);
    }

    // ---- The merge_action gate an action raises (docs/04 section 4.5) ---------------------------

    static (RunCore Core, string Gate) AtMergeGate()
    {
        var (core, gate) = AtGate();
        Accepted(core, new AnswerArgs(gate, "alice", "go"));
        Assert.Equal([new StartStep("merge", 1)], core.Advance());
        Assert.True(core.Apply(new ActionDecisionRequested("merge", "may acme/api#1 be merged into main?")).Accepted);
        var wait = Assert.Single(core.State.Waits);
        Assert.Equal("gate ticket.merge_action [approve, reject | reject]: may acme/api#1 be merged into main?", Shown(core, wait));
        return (core, wait.Id);
    }

    [Fact]
    public void The_merge_action_gate_is_answered_like_any_gate()
    {
        var (core, gate) = AtMergeGate();
        Assert.Equal("merge: 'merged' is not an answer to its decision (approve, reject)", Refusal(core, new AnswerArgs(gate, "alice", "merged")));
        Accepted(core, new AnswerArgs(gate, "alice", ActionDecisions.Approve));
        Assert.Equal("approve", core.ActionDecisionState("merge"));
        Agree(core, new AnswerArgs(gate, "bob", ActionDecisions.Approve));
    }

    [Fact]
    public void The_merge_action_gate_closes_as_answered_on_the_code_host_when_the_step_ends_first()
    {
        var (core, gate) = AtMergeGate();
        core.Advance();
        Assert.True(core.Apply(new StepCompleted("merge", "merged")).Accepted);
        Assert.Equal(new ClosedWait(gate, WaitKind.Gate, "merge", WaitEnds.AnsweredOnCodeHost), core.State.Closed.Last(c => c.Id == gate));
        core.Advance();
        Assert.Equal(RunStatus.Completed, core.State.Status);
        Assert.Equal("the run has ended (completed)", Refusal(core, new AnswerArgs(gate, "alice", ActionDecisions.Approve)));
    }

    // ---- The re-drive runner and the shell route the same way ---------------------------------------

    [Fact]
    public void Each_kind_opens_and_closes_as_a_wait_change_the_shell_records()
    {
        var (core, gate) = AtGate();
        Accepted(core, new AnswerArgs(gate, "alice", "stop"));
        Assert.Equal([(gate, true), (gate, false)], core.State.WaitChanges.Select(c => (c.Wait.Id, c.Open)));
    }

    static RunCore Resume(RunCore core, RunOptions? options = null) =>
        RunCore.Resume(Lib.Libraries, Probe.Ref("waits"), JsonSerializer.Deserialize<CarriedCore>(JsonSerializer.Serialize(core.Snapshot()))!, options);

    // ---- Reminders (F21; docs/04 section 4) ----------------------------------------------------------

    static readonly RunOptions Reminding = new() { ReminderInterval = TimeSpan.FromHours(4) };

    static RunCore AtRemindedGate()
    {
        var core = Started(Reminding);
        core.Apply(new StepCompleted("ask", "done"));
        Assert.Equal([new ArmReminder("gate-1", 1, TimeSpan.FromHours(4)), new OpenGate("approve", 1, "gate-1", false)], core.Advance());
        return core;
    }

    static IReadOnlyList<string> Reminded(RunCore core) =>
        core.State.Trace.Where(t => t.Kind == "reminded").Select(t => $"{t.StepId} {t.Detail}").ToList();

    [Fact]
    public void An_open_gate_is_reminded_once_per_number_until_it_is_answered()
    {
        var core = AtRemindedGate();

        Assert.True(core.Apply(new ReminderDue("gate-1", 1)).Accepted);
        Assert.Equal(["gate-1 1"], Reminded(core));
        Assert.Equal([new ArmReminder("gate-1", 2, TimeSpan.FromHours(4))], core.Advance());
        // A repeated or late reminder does nothing: no second trace, no second timer.
        Assert.True(core.Apply(new ReminderDue("gate-1", 1)).Accepted);
        Assert.Equal(["gate-1 1"], Reminded(core));
        Assert.Empty(core.Advance());

        Accepted(core, new AnswerArgs("gate-1", "alice", "go", Reason: "fine"));
        core.Advance();
        Assert.True(core.Apply(new ReminderDue("gate-1", 2)).Accepted);
        Assert.Equal(["gate-1 1"], Reminded(core));
        Assert.Empty(core.Advance());
    }

    [Fact]
    public void Without_an_interval_no_wait_arms_a_reminder()
    {
        var core = Started();
        core.Apply(new StepCompleted("ask", "done"));
        Assert.Equal([new OpenGate("approve", 1, "gate-1", false)], core.Advance());
        Assert.True(core.Apply(new ReminderDue("gate-1", 1)).Accepted);
        Assert.Empty(Reminded(core));
        Assert.Empty(Resume(core).Timers());
    }

    [Fact]
    public void After_a_hand_over_each_open_wait_arms_its_next_reminder()
    {
        var core = AtRemindedGate();
        core.Apply(new ReminderDue("gate-1", 1));
        core.Advance();

        var resumed = Resume(core, Reminding);
        Assert.Equal([new ArmReminder("gate-1", 2, TimeSpan.FromHours(4))], resumed.Timers());
        Assert.True(resumed.Apply(new ReminderDue("gate-1", 2)).Accepted);
        Assert.Equal(["gate-1 1", "gate-1 2"], Reminded(resumed));
    }

    /// <summary>
    /// A wait as its record shows it (<see cref="RunCore.Describe"/>): kind, type, choices, those that need a person's
    /// reason after <c>|</c> (a choice routing the step as a failure or ending the run; LOG 502), and text.
    /// </summary>
    static string Shown(RunCore core, OpenWait wait)
    {
        var d = core.Describe(wait);
        return $"{d.Kind} {d.Type} [{string.Join(", ", d.Choices)} | {string.Join(", ", d.ReasonRequired ?? [])}]: {d.Prompt}";
    }
}
