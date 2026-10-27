using FeatureFlow.Spike.Contracts;

namespace FeatureFlow.Spike.Interpreter;

/// <summary>
/// Synthetic plan generator. docs/08 2.1: sized by history events, not steps.
/// Fake providers generate plans from about 50 to 400 steps cheaply, to find
/// where continue-as-new triggers.
/// </summary>
public static class PlanGenerator
{
    /// <summary>
    /// A linear chain of generic steps. One activity per step, so history events
    /// scale predictably with step count.
    /// </summary>
    public static Plan Linear(int stepCount, string prefix = "s")
    {
        var plan = new Plan { Entry = $"{prefix}0", Revision = 1 };
        for (var i = 0; i < stepCount; i++)
        {
            var isLast = i == stepCount - 1;
            plan.Steps.Add(new Step
            {
                Id = $"{prefix}{i}",
                Kind = "notify.send",
                SimulatedDuration = "0",
                Routes = new Dictionary<string, RouteTarget>(StringComparer.Ordinal)
                {
                    [Outcomes.Completed] = isLast
                        ? RouteTarget.ToEnd(Outcomes.Completed)
                        : RouteTarget.ToStep($"{prefix}{i + 1}"),
                    [Outcomes.Failed] = RouteTarget.ToEnd(Outcomes.Failed),
                    [Outcomes.AttemptsExhausted] = RouteTarget.ToEnd(Outcomes.Failed),
                },
            });
        }
        return plan;
    }

    /// <summary>
    /// The plan docs/08 2.1 asks for: several agent.run steps with a long
    /// start_to_close, a parallel with three branches and join: all, a protected
    /// step a re-plan will try to remove, and a human.gate mid-run.
    /// </summary>
    public static Plan SpecPlan()
    {
        var plan = new Plan { Entry = "plan_spec", Revision = 1 };

        plan.Steps.Add(Agent("plan_spec", "run_tests", 8));

        // A protected step. A re-plan will try to remove it (R7).
        plan.Steps.Add(new Step
        {
            Id = "implement",
            Kind = "agent.run",
            Protected = true,
            SimulatedDuration = "20",
            PollsInActivity = true,
            Inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["role"] = "feature-flow-default:roles/implementer",
                ["write_access"] = "branch",
            },
            Limits = new StepLimits { Attempts = 3, StartToClose = "PT4H", Heartbeat = "PT30S" },
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.Completed] = RouteTarget.ToStep("run_tests"),
                [Outcomes.Blocked] = RouteTarget.ToStep("replan"),
                [Outcomes.Failed] = RouteTarget.ToStep("replan"),
                [Outcomes.AttemptsExhausted] = RouteTarget.ToStep("escalate"),
                [Outcomes.ReplanRequested] = RouteTarget.ToStep("replan"),
            },
        });

        plan.Steps.Add(Agent("run_tests", "tickets", 6));

        // A parallel with three branches and join: all. docs/08 2.1.
        plan.Steps.Add(new Step
        {
            Id = "tickets",
            Kind = "parallel",
            Inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["branches"] = new List<object?> { "ticket_a", "ticket_b", "ticket_c" },
                ["join"] = "all",
                ["max_concurrency"] = 2,
            },
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.Completed] = RouteTarget.ToStep("gate_epic_review"),
                [Outcomes.AnyFailed] = RouteTarget.ToStep("escalate"),
            },
        });

        // docs/09 7.0: a branch reports only its terminal outcome to the join.
        // It routes nothing else, and it may not use a directive.
        foreach (var branch in new[] { "ticket_a", "ticket_b", "ticket_c" })
        {
            plan.Steps.Add(new Step
            {
                Id = branch,
                Kind = "notify.send",
                SimulatedDuration = "10",
                Routes = new(StringComparer.Ordinal),
            });
        }

        // A human.gate mid-run. docs/08 2.1.
        plan.Steps.Add(new Step
        {
            Id = "gate_epic_review",
            Kind = "human.gate",
            Gate = new StepGate { DecisionType = "epic.review" },
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.Approved] = RouteTarget.ToStep("deliver"),
                [Outcomes.Rejected] = RouteTarget.ToEnd(Outcomes.Rejected),
            },
        });

        plan.Steps.Add(Agent("deliver", "replan", 5));

        plan.Steps.Add(new Step
        {
            Id = "replan",
            Kind = "plan.replan",
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.ReplanRequested] = RouteTarget.ToResume(),
                [Outcomes.Failed] = RouteTarget.ToStep("escalate"),
                [Outcomes.AttemptsExhausted] = RouteTarget.ToStep("escalate"),
            },
        });

        plan.Steps.Add(new Step
        {
            Id = "escalate",
            Kind = "human.gate",
            Gate = new StepGate { DecisionType = "run.escalate" },
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.Approved] = RouteTarget.ToEnd(Outcomes.Completed),
                [Outcomes.Rejected] = RouteTarget.ToEnd(Outcomes.Rejected),
            },
        });

        return plan;
    }

    private static Step Agent(string id, string next, int seconds) => new()
    {
        Id = id,
        Kind = "agent.run",
        SimulatedDuration = seconds.ToString(),
        PollsInActivity = true,
        Limits = new StepLimits { Attempts = 3, StartToClose = "PT4H", Heartbeat = "PT30S" },
        Routes = new(StringComparer.Ordinal)
        {
            [Outcomes.Completed] = RouteTarget.ToStep(next),
            [Outcomes.Blocked] = RouteTarget.ToStep("replan"),
            [Outcomes.Failed] = RouteTarget.ToStep("replan"),
            [Outcomes.AttemptsExhausted] = RouteTarget.ToStep("escalate"),
            [Outcomes.ReplanRequested] = RouteTarget.ToStep("replan"),
        },
    };

    /// <summary>
    /// A patch that tries to remove a protected step. Exit criterion 2: rejected
    /// and recorded, not silently dropped (R7).
    /// </summary>
    public static PlanPatch RemoveProtected(int baseRevision) => new()
    {
        BaseRevision = baseRevision,
        ProposedBy = "agent:replan",
        Reason = "implementation discovered the step is unnecessary",
        Changes = { new PatchChange { Op = PatchOps.RemoveStep, Target = "implement" } },
    };

    /// <summary>
    /// A patch that bypasses a protected step without removing it. docs/09 15.2:
    /// bypassing is removal by another route.
    /// </summary>
    public static PlanPatch BypassProtected(int baseRevision) => new()
    {
        BaseRevision = baseRevision,
        ProposedBy = "agent:replan",
        Reason = "route around the slow step",
        Changes =
        {
            new PatchChange
            {
                Op = PatchOps.ChangeRoute,
                Target = "plan_spec",
                Outcome = Outcomes.Completed,
                To = RouteTarget.ToStep("run_tests"),
            },
        },
    };

    /// <summary>
    /// A legitimate patch: insert a step after run_tests. docs/09 15.3.1.
    /// </summary>
    public static PlanPatch InsertAfterRunTests(int baseRevision) => new()
    {
        BaseRevision = baseRevision,
        ProposedBy = "agent:replan",
        Reason = "add a lint pass after tests",
        ResumeAt = "lint",
        Changes =
        {
            new PatchChange
            {
                Op = PatchOps.InsertAfter,
                Target = "run_tests",
                Step = new Step
                {
                    Id = "lint",
                    Kind = "notify.send",
                    SimulatedDuration = "1",
                    Routes = new(StringComparer.Ordinal)
                    {
                        [Outcomes.Completed] = RouteTarget.ToStep("tickets"),
                        [Outcomes.Failed] = RouteTarget.ToStep("escalate"),
                        [Outcomes.AttemptsExhausted] = RouteTarget.ToStep("escalate"),
                    },
                },
            },
        },
    };
}
