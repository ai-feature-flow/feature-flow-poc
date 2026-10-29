using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>Executable regression checks for review 14 A1, A5 and A6.</summary>
public static class Review14Checks
{
    public static Task<int> RunAsync()
    {
        HarnessProgram.WriteHeader("Review 14: policy floors and run-start checks");
        var failures = 0;
        var organisation = OrganisationConfiguration.Load();
        var plan = PlanLoader.LoadTicketDelivery("TICKET-SPIKE2-1");

        var accepted = RunStartValidator.Validate(plan, organisation);
        Console.WriteLine("[A5] corrected plan against organisation configuration:");
        accepted.WriteTo(Console.Out);
        if (!accepted.Accepted)
            failures++;
        Console.WriteLine();

        var unknownPlan = plan.Clone();
        unknownPlan.ById("gate_merge")!.Gate = new StepGate
        {
            DecisionType = "ticket.unknown_approval",
        };
        var rejected = RunStartValidator.Validate(unknownPlan, organisation);
        Console.WriteLine("[A5] plan with unknown decision type:");
        rejected.WriteTo(Console.Out);
        if (rejected.Accepted ||
            !rejected.Problems.Any(problem => problem.Check == 21 &&
                                               problem.Message.Contains("ticket.unknown_approval", StringComparison.Ordinal)))
        {
            failures++;
        }
        Console.WriteLine();

        var harnessDecision = new DecisionRecord(
            "gate_merge",
            OrganisationConfiguration.TicketMergeDecision,
            "approved",
            new DecisionActor(DecisionActorKind.Harness, "real-harness", Array.Empty<string>()),
            DateTimeOffset.UtcNow);
        var mainAuthorization = MergeAuthorization.Evaluate(
            "main", "main", new[] { harnessDecision }, organisation);
        var mainMergeProviderCalled = false;
        if (mainAuthorization.Allowed)
            mainMergeProviderCalled = true;

        Console.WriteLine($"[A1] main PR, gate_merge approved by actor=harness: {mainAuthorization.Message}");
        Console.WriteLine($"[A1] merge provider called={mainMergeProviderCalled}");
        if (mainAuthorization.Allowed || mainMergeProviderCalled ||
            !mainAuthorization.Message.Contains("floor F1", StringComparison.Ordinal) ||
            !mainAuthorization.Message.Contains("missing human approval", StringComparison.Ordinal))
        {
            failures++;
        }
        Console.WriteLine();

        var humanDecision = new DecisionRecord(
            "gate_merge",
            OrganisationConfiguration.TicketMergeDecision,
            "approved",
            new DecisionActor(DecisionActorKind.Human, "product-owner", new[] { "approver" }),
            DateTimeOffset.UtcNow);
        var integrationAuthorization = MergeAuthorization.Evaluate(
            "ff/epic/spike-2", "main", new[] { humanDecision }, organisation);
        var integrationMergeProviderCalled = false;
        if (integrationAuthorization.Allowed)
            integrationMergeProviderCalled = true;

        Console.WriteLine($"[A1] integration PR, approval actor=human identity=product-owner: {integrationAuthorization.Message}");
        Console.WriteLine($"[A1] merge provider called={integrationMergeProviderCalled}; result=" +
                          $"{(integrationMergeProviderCalled ? "SUCCEEDED" : "REFUSED")}");
        if (!integrationAuthorization.Allowed || !integrationMergeProviderCalled)
            failures++;
        Console.WriteLine();

        var patch = new PlanPatch
        {
            BaseRevision = plan.Revision,
            ProposedBy = "agent:replan",
            Reason = "introduce an unapproved release-manager role",
            Changes =
            {
                new PatchChange
                {
                    Op = PatchOps.ReplaceInputs,
                    Target = "implement",
                    Inputs = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["role"] = "feature-flow-default:roles/release-manager",
                    },
                },
            },
        };
        var patchResult = ReplanPolicy.Apply(plan, patch, organisation);
        Console.WriteLine($"[A6] agent re-plan with disallowed role: {patchResult.Kind}");
        Console.WriteLine($"[A6] reason: {string.Join("; ", patchResult.Reasons)}");
        if (patchResult.Kind != PatchResultKind.PendingHumanDecision)
            failures++;

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? "REVIEW 14 checks: PASS"
            : $"REVIEW 14 checks: FAIL ({failures} failure(s))");
        return Task.FromResult(failures == 0 ? 0 : 1);
    }
}
