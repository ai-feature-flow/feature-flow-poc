using System.Net;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// Findings for people (A6): whether each needs a person and which person decided it, by the engine's own rule (LOG 512,
/// <see cref="FindingsLedger.NeedsPerson"/>); who raised it; whether it may be marked fixed; and its history.
/// </summary>
public sealed class FindingsViewTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 24, 9, 0, 0, TimeSpan.Zero);

    static FindingRecord Finding(string runId, string id, string state, bool blocking = true, string source = "review", string? raised = "h1", string? stateAt = "h1") =>
        new(runId, id, "fp-" + id, new ReportedFinding("rule", "Title " + id, "d", "high", blocking, FindingBases.Reasoned, new FindingLocation("acme/api", "A.cs")),
            source, "a-model", state, raised, stateAt, 1, 2, 0, false, T0);

    static int sequence;

    static FindingEventRecord Event(string runId, string id, string kind, string state, string actor, string? reason = null) =>
        new(runId, id, $"k{Interlocked.Increment(ref sequence)}", kind, state, actor, reason, "verify.fix.apply", "h1", null, T0.AddMinutes(sequence));

    [Fact]
    public async Task A_finding_needs_a_person_until_a_person_decides_it_and_an_agent_restating_it_does_not_undo_that()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1");
        const string agent = "verify.fix.triage", person = "alice";
        await world.AppendAsync(new RecordBatch(
            Findings:
            [
                Finding("r1", "F-1", FindingStates.Escalated),
                Finding("r1", "F-2", FindingStates.Declined),
                Finding("r1", "F-3", FindingStates.Declined),
                Finding("r1", "F-4", FindingStates.Declined),
                Finding("r1", "F-5", FindingStates.Declined, blocking: false),
            ],
            FindingEvents:
            [
                Event("r1", "F-1", FindingEventKinds.Raised, FindingStates.Open, "review"),
                Event("r1", "F-1", FindingEventKinds.Triaged, FindingStates.Escalated, agent),
                // A person's decline.
                Event("r1", "F-2", FindingEventKinds.Triaged, FindingStates.Declined, agent),
                Event("r1", "F-2", FindingEventKinds.Triaged, FindingStates.Declined, person, "accepted"),
                // A person's decline that the next pass's re-raise and the triager's restatement leave alone.
                Event("r1", "F-3", FindingEventKinds.Triaged, FindingStates.Declined, person),
                Event("r1", "F-3", FindingEventKinds.Reraised, FindingStates.Declined, "review"),
                Event("r1", "F-3", FindingEventKinds.Triaged, FindingStates.Declined, agent),
                // A person's decline, reopened by a re-raise and declined again by the triager alone.
                Event("r1", "F-4", FindingEventKinds.Triaged, FindingStates.Declined, person),
                Event("r1", "F-4", FindingEventKinds.Reopened, FindingStates.Open, "review"),
                Event("r1", "F-4", FindingEventKinds.Triaged, FindingStates.Declined, agent),
                // A decline an agent may make alone.
                Event("r1", "F-5", FindingEventKinds.Triaged, FindingStates.Declined, agent),
            ],
            Answers: [DecisionWorld.Answered("r1", new AnswerArgs("w1", person, Outcome: "triaged"), T0)]));

        var findings = (await world.GetAsync<FindingsResponse>("runs/r1/findings")).Findings.ToDictionary(f => f.LedgerId);

        Assert.Equal((true, null), (findings["F-1"].NeedsPerson, findings["F-1"].DecidedByPerson));
        Assert.Equal((false, person), (findings["F-2"].NeedsPerson, findings["F-2"].DecidedByPerson));
        Assert.Equal((false, person), (findings["F-3"].NeedsPerson, findings["F-3"].DecidedByPerson));
        Assert.Equal((true, null), (findings["F-4"].NeedsPerson, findings["F-4"].DecidedByPerson));
        Assert.Equal((false, null), (findings["F-5"].NeedsPerson, findings["F-5"].DecidedByPerson));
    }

    [Fact]
    public async Task A_findings_history_is_its_events_oldest_first_and_an_unknown_finding_is_404()
    {
        await using var world = new DecisionWorld();
        await world.RunAsync("r1");
        await world.AppendAsync(new RecordBatch(
            Findings: [Finding("r1", "F-1", FindingStates.Declined)],
            FindingEvents:
            [
                Event("r1", "F-1", FindingEventKinds.Raised, FindingStates.Open, "review"),
                Event("r1", "F-1", FindingEventKinds.Triaged, FindingStates.Declined, "alice", "a known limitation"),
            ]));

        var detail = await world.GetAsync<FindingDetail>("runs/r1/findings/F-1");

        Assert.Equal(("F-1", "r1"), (detail.Finding.LedgerId, detail.Finding.RunId));
        Assert.Equal([(FindingEventKinds.Raised, FindingStates.Open, "review", null), (FindingEventKinds.Triaged, FindingStates.Declined, "alice", "a known limitation")],
            detail.History.Select(e => (e.Kind, e.State, e.Actor, e.Reason)));
        using var missing = await world.Http.GetAsync("runs/r1/findings/F-9", DecisionWorld.Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task A_finding_names_the_step_and_angle_that_raised_it_and_whether_it_may_be_marked_fixed()
    {
        await using var world = new DecisionWorld();
        var run = await world.StartAsync();
        var id = run.RunId;
        await world.AppendAsync(new RecordBatch(Findings:
        [
            Finding(id, "F-1", FindingStates.Open, source: "review_1.assess", raised: "h1", stateAt: "h2"),
            Finding(id, "F-2", FindingStates.Open, source: "coderabbit", raised: "h1", stateAt: "h1"),
            Finding(id, "F-3", FindingStates.Open, raised: null, stateAt: null),
            Finding(id, "F-4", FindingStates.Fixed, raised: "h1", stateAt: "h2"),
        ]));

        var findings = (await world.GetAsync<FindingsResponse>($"runs/{id}/findings")).Findings.ToDictionary(f => f.LedgerId);

        Assert.Equal(new FindingRaisedView("review_1.assess", "security", "person", "a-model"), findings["F-1"].RaisedBy);
        Assert.Equal(new FindingRaisedView(null, null, "external", "a-model"), findings["F-2"].RaisedBy);
        Assert.Equal((true, null), (findings["F-1"].CanMarkFixed, findings["F-1"].MarkFixedRefusal));
        Assert.Equal((false, "nothing has been committed since it was raised, as far as the run recorded"), (findings["F-2"].CanMarkFixed, findings["F-2"].MarkFixedRefusal));
        Assert.False(findings["F-3"].CanMarkFixed);
        Assert.Contains("has not prepared", findings["F-3"].MarkFixedRefusal);
        Assert.False(findings["F-4"].CanMarkFixed);
        Assert.Equal((id, 1, 2, T0), (findings["F-1"].RunId, findings["F-1"].Reraises, findings["F-1"].Fixes, findings["F-1"].UpdatedAt));
    }
}
