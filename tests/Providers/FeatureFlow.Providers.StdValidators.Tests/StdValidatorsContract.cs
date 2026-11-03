using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using static FeatureFlow.Providers.StdValidators.Tests.World;

namespace FeatureFlow.Providers.StdValidators.Tests;

/// <summary>
/// The validator contract over the standard validators, with a passing and a failing fixture for each of the seven
/// (M6's verified-by: "each validator has a failing and a passing fixture"). The failing fixtures are the mistakes a
/// planner, implementer or fixer is likely to make; each names the code its error must carry.
/// </summary>
public sealed class StdValidatorsContract : ValidatorContract, IDisposable
{
    readonly World w = new();

    protected override IValidatorProvider Validators => w.Validators;

    protected override IReadOnlyList<ValidatorFixture> Fixtures { get; }

    static readonly IReadOnlyDictionary<string, string> Coverage = new Dictionary<string, string> { ["require_coverage"] = "true" };
    static readonly IReadOnlyDictionary<string, string> Rewording = new Dictionary<string, string> { ["allow_rewording"] = "true" };

    public StdValidatorsContract()
    {
        var fixes = w.Commit("tests/WidgetTests.cs", "class WidgetTests { void Existing() { } void RefusesLongNames() { } }\n");
        // The fixed production file mentions the test, so only the rule that a test is not the finding's own file refuses it.
        var codeOnly = w.Commit("src/Widgets.cs", "class Widgets { const int Max = 100; } // see RefusesLongNames\n", "refs/heads/ff/ticket/T-2");
        var head = Frozen(fixes);
        ValidationSubject Requirements() => w.Subject("requirements", "requirements", TicketRequirements);
        ValidationSubject Plan(string text) => w.Subject("delivery_plan", "delivery_plan", text);

        const string Id = "WidgetTests.RefusesLongNames";
        var passed = w.TestEvidence(fixes, [Id]);
        var failed = w.TestEvidence(fixes, [], [Id]);
        var agents = w.TestEvidence(fixes, [Id], action: null, evidence: false);
        ValidationSubject Results(ArtifactRecord r) => new("test_results", r.Type, new ArtifactRef(r.Ref));

        // Satisfaction: the plan, this run's claims (indexed, so the run is known) and the engine's results.
        ValidationRequest Satisfaction(ArtifactRecord? results, string plan = "", string claims = "", ArtifactRecord[]? more = null, string runId = TicketRun)
        {
            var (claimsSubject, claimsRecord) = w.Indexed("implementation_claims", "evidence_claims", claims == "" ? Claims() : claims, runId);
            ValidationSubject[] subjects = results is null
                ? [Plan(plan == "" ? TicketPlan() : plan), claimsSubject]
                : [Plan(plan == "" ? TicketPlan() : plan), claimsSubject, Results(results)];
            return Request(Names.VerificationSatisfaction, subjects, head, [claimsRecord, .. results is null ? [] : new[] { results }, .. more ?? []]);
        }

        var traced = new[]
        {
            Record("requirements", "requirements", w.Put(TicketRequirements)),
            Record("delivery_plan", "delivery_plan", w.Put(TicketPlan())),
            Record("implementation_claims", "evidence_claims", w.Put(Claims())),
            passed,
        };
        ValidationRequest Trace(IReadOnlyList<ArtifactRecord> artifacts) =>
            Request(Names.RequirementTrace, [new ValidationSubject("requirements", artifacts[0].Type, new ArtifactRef(artifacts[0].Ref))], head, artifacts);

        // tests_added: this run's fix claims (indexed), the triage, the engine's results at the fixed head, the ledger.
        ValidationRequest TestsAdded(string claims, string revision, IReadOnlyList<string> passing, string? raised = "", string? testedAt = null,
            IReadOnlyList<string>? passingWhenRaised = null)
        {
            var (claimsSubject, claimsRecord) = w.Indexed("fix_claims", "evidence_claims", claims);
            ArtifactRecord[] evidence = passingWhenRaised is null
                ? [w.TestEvidence(testedAt ?? revision, passing)]
                : [w.TestEvidence(testedAt ?? revision, passing), w.TestEvidence(w.Main, passingWhenRaised)];
            return Request(Names.TestsAdded, [claimsSubject, w.Subject("triage", "triage", TriageText)], Frozen(revision),
                [claimsRecord, .. evidence], [Finding("F-1", raised == "" ? w.Main : raised)]);
        }

        var ownedPlan = TicketPlan(Criterion + "\n    owner: { kind: ticket, id: T-1 }");
        var twoOwners = EpicPlan + "\n" + EpicPlan[EpicPlan.IndexOf("  - id: AC-1", StringComparison.Ordinal)..].Replace("AC-1", "AC-2").Replace("T-1", "T-2");
        var childPlan = Record("delivery_plan", "delivery_plan", w.Put(InheritedPlan()), runId: TicketRun);
        const string NoClaims = "schema: 1\nclaims: []\n";

        Fixtures =
        [
            // ---- requirement_support ----
            new("requirement_support: the ticket's requirement supports the epic's, its criterion supports it",
                Request(Names.RequirementSupport, [Requirements(), Plan(TicketPlan()), w.Subject("requirements", "requirements", EpicRequirements, "parent")], options: Coverage), true),
            new("requirement_support: the only criterion is enabling work, so R-1 has no supporter",
                Request(Names.RequirementSupport, [Requirements(), Plan(TicketPlan(Criterion.Replace("supports: [{ artifact: requirements, requirement: R-1 }]", "supports_reason: Enabling work") + "\n" + Method)),
                    w.Subject("requirements", "requirements", EpicRequirements, "parent")], options: Coverage), false, "uncovered"),
            new("requirement_support: a link to a requirement the parent does not hold",
                Request(Names.RequirementSupport, [w.Subject("requirements", "requirements", TicketRequirements.Replace("E-1", "E-9")),
                    w.Subject("requirements", "requirements", EpicRequirements, "parent")]), false, "unresolved_link"),
            // LOG 191: the sandbox names the parent's requirements parent/requirements, so a planner links to that name.
            new("requirement_support: a link to the parent's requirements by the sandbox's name for them",
                Request(Names.RequirementSupport, [w.Subject("requirements", "requirements", TicketRequirements.Replace("artifact: requirements", "artifact: parent/requirements")),
                    w.Subject("requirements", "requirements", EpicRequirements, "parent")]), true),
            new("requirement_support: a parent/ link does not resolve to this run's own document of the name",
                Request(Names.RequirementSupport, [w.Subject("requirements", "requirements", TicketRequirements.Replace("artifact: requirements", "artifact: parent/requirements")),
                    w.Subject("requirements", "requirements", EpicRequirements.Replace("kind: epic", "kind: ticket"))]),
                false, "unresolved_link"),
            new("requirement_support: a requirement both supports one above and gives a reason",
                Request(Names.RequirementSupport, [w.Subject("requirements", "requirements", TicketRequirements + "\n    supports_reason: Also enabling"),
                    w.Subject("requirements", "requirements", EpicRequirements, "parent")]), false, "support_and_reason"),
            new("requirement_support: a requirement supports nothing and gives no reason",
                Request(Names.RequirementSupport, [w.Subject("requirements", "requirements", TicketRequirements.Replace("    supports: [{ artifact: requirements, requirement: E-1 }]", ""))]),
                false, "unsupported"),

            // ---- requirement_ownership ----
            new("requirement_ownership: the epic's plan gives AC-1 to ticket T-1",
                Request(Names.RequirementOwnership, [w.Subject("child_requirements", "delivery_plan", EpicPlan)]), true),
            new("requirement_ownership: a criterion of the epic's plan has no owner",
                Request(Names.RequirementOwnership, [w.Subject("child_requirements", "delivery_plan", EpicPlan.Replace("    owner: { kind: ticket, id: T-1 }", ""))]), false,
                "missing_owner"),
            new("requirement_ownership: an epic's plan gives a criterion to another epic",
                Request(Names.RequirementOwnership, [w.Subject("child_requirements", "delivery_plan", EpicPlan.Replace("{ kind: ticket, id: T-1 }", "{ kind: epic, id: F }"))]), false,
                "invalid_owner"),

            // ---- requirement_inheritance ----
            new("requirement_inheritance: the ticket holds AC-1 as the epic gave it, and adds its method",
                Request(Names.RequirementInheritance, [w.Subject("child_requirements", "delivery_plan", EpicPlan, "parent"), Plan(InheritedPlan())], options: Rewording), true),
            new("requirement_inheritance: a standalone ticket, with no parent plan (LOG 63)",
                Request(Names.RequirementInheritance, [Absent("child_requirements"), Plan(TicketPlan())], options: Rewording), true),
            new("requirement_inheritance: the ticket dropped the criterion it owns",
                Request(Names.RequirementInheritance, [w.Subject("child_requirements", "delivery_plan", EpicPlan, "parent"), Plan(InheritedPlan().Replace("AC-1", "AC-2"))],
                    options: Rewording), false, "missing_inherited"),
            new("requirement_inheritance: reworded where rewording is not allowed (an epic copies word for word)",
                Request(Names.RequirementInheritance, [w.Subject("child_requirements", "delivery_plan", EpicPlan, "parent"), Plan(InheritedPlan("Long names are rejected"))]),
                false, "reworded"),
            new("requirement_inheritance: the inherited criterion supports something else",
                Request(Names.RequirementInheritance, [w.Subject("child_requirements", "delivery_plan", EpicPlan, "parent"), Plan(InheritedPlan().Replace("requirement: E-1", "requirement: E-2"))],
                    options: Rewording), false, "links_changed"),
            // LOG 191: the copy names the epic's requirements as the ticket's sandbox does; seen from the epic, the same link.
            new("requirement_inheritance: the inherited criterion links to parent/requirements, the parent's requirements",
                Request(Names.RequirementInheritance, [w.Subject("child_requirements", "delivery_plan", EpicPlan, "parent"),
                    Plan(InheritedPlan().Replace("artifact: requirements", "artifact: parent/requirements"))], options: Rewording), true),
            new("requirement_inheritance: the inherited criterion links to root/requirements, not the parent's",
                Request(Names.RequirementInheritance, [w.Subject("child_requirements", "delivery_plan", EpicPlan, "parent"),
                    Plan(InheritedPlan().Replace("artifact: requirements", "artifact: root/requirements"))], options: Rewording), false, "links_changed"),
            new("requirement_inheritance: the ticket also holds a criterion the epic gave another ticket",
                Request(Names.RequirementInheritance, [w.Subject("child_requirements", "delivery_plan", twoOwners, "parent"),
                    Plan(InheritedPlan() + "\n" + InheritedPlan()[InheritedPlan().IndexOf("  - id: AC-1", StringComparison.Ordinal)..].Replace("AC-1", "AC-2"))], options: Rewording),
                false, "owned_by_another"),
            new("requirement_inheritance: a standalone ticket's planner marks its own criterion as owned (review F1)",
                Request(Names.RequirementInheritance, [Absent("child_requirements"), Plan(TicketPlan(Criterion + "\n    owner: { kind: ticket, id: T-1 }\n" + Method))]),
                false, "owner_in_child"),

            // ---- verification_method_support ----
            new("verification_method_support: AC-1 has a method", Request(Names.VerificationMethodSupport, [Plan(TicketPlan())]), true),
            new("verification_method_support: the epic gives AC-1 to a ticket, which adds the method",
                Request(Names.VerificationMethodSupport, [Plan(EpicPlan)]), true),
            new("verification_method_support: AC-1 has neither a method nor an owner",
                Request(Names.VerificationMethodSupport, [Plan(TicketPlan(Criterion))]), false, "no_method"),
            new("verification_method_support: a ticket's criterion owned by the ticket itself still needs a method (review F1)",
                Request(Names.VerificationMethodSupport, [Plan(ownedPlan)]), false, "no_method"),
            new("verification_method_support: a criterion given to a ticket has methods of its own",
                Request(Names.VerificationMethodSupport, [Plan(EpicPlan + "\n" + Method)]), false, "owned_with_methods"),
            new("verification_method_support: AC-1 twice",
                Request(Names.VerificationMethodSupport, [Plan(TicketPlan(Criterion + "\n" + Method + "\n" + Criterion + "\n" + Method))]), false, "duplicate_id"),
            new("verification_method_support: a plan with no scope",
                Request(Names.VerificationMethodSupport, [Plan(TicketPlan().Replace("scope: { kind: ticket, id: T-1 }\n", ""))]), false, "malformed"),
            new("verification_method_support: an empty plan (review F7)",
                Request(Names.VerificationMethodSupport, [Plan("schema: 1\nscope: { kind: ticket, id: T-1 }\nacceptance_criteria: []\n")]), false, "malformed"),

            // ---- verification_satisfaction ----
            new("verification_satisfaction: the claimed test passed in the engine's results at the frozen head", Satisfaction(passed), true),
            new("verification_satisfaction: the claimed test failed", Satisfaction(failed), false, "test_not_passed"),
            new("verification_satisfaction: test results an agent produced are not evidence (F7)", Satisfaction(agents), false, "not_evidence"),
            new("verification_satisfaction: a test reported both passed and failed has not passed",
                Satisfaction(w.TestEvidence(fixes, [Id], [Id])), false, "test_not_passed"),
            new("verification_satisfaction: a baseline run proves nothing about the change",
                Satisfaction(w.TestEvidence(fixes, [Id], baseline: true)), false, "not_tested"),
            new("verification_satisfaction: the test passed only in another tier than the method's",
                Satisfaction(w.TestEvidence(fixes, ["WidgetTests.Other"], extraTiers: [new TestResults(fixes, "integration", 1, 0, 0, [], [Id])])), false, "test_not_passed"),
            new("verification_satisfaction: the claim is of another tier than the method's",
                Satisfaction(passed, claims: Claims().Replace("tier: unit", "tier: integration")), false, "claim_mismatch"),
            new("verification_satisfaction: the claim's change is one the method does not allow",
                Satisfaction(passed, plan: TicketPlan(Criterion + "\n" + Method.Replace("semantic_review: required }", "semantic_review: required, allowed_test_changes: [new] }")),
                    claims: Claims(change: "reused")), false, "change_not_allowed"),
            new("verification_satisfaction: a claim names a method no criterion has",
                Satisfaction(passed, claims: Claims() + "\n" + Claims(criterion: "AC-9")[Claims().IndexOf("  - criterion", StringComparison.Ordinal)..]), false, "unknown_claim"),
            new("verification_satisfaction: a ticket's criterion owned by the ticket itself, with no method or claim, is unproved (review F1)",
                Satisfaction(null, plan: ownedPlan, claims: NoClaims), false, "unproved"),
            new("verification_satisfaction: an epic's criterion given to a ticket that never planned it is unproved",
                Satisfaction(null, plan: EpicPlan, claims: NoClaims, runId: "run-epic"), false, "unproved"),
            new("verification_satisfaction: an epic's criterion owned by another epic is the epic's own to prove, whatever that epic planned (review N4)",
                Satisfaction(null, plan: EpicPlan.Replace("{ kind: ticket, id: T-1 }", "{ kind: epic, id: F }"), claims: NoClaims, runId: "run-epic",
                    more: [Record("delivery_plan", "delivery_plan", w.Put(InheritedPlan().Replace("{ kind: ticket, id: T-1 }", "{ kind: epic, id: F }")), runId: "run-f")]),
                false, "unproved"),
            new("verification_satisfaction: the owner's plan is in this run, not a child's (review N4)",
                Satisfaction(null, plan: EpicPlan, claims: NoClaims, more: [childPlan with { RunId = "run-epic" }], runId: "run-epic"), false, "unproved"),
            new("verification_satisfaction: the owner's plan does not hold the criterion (review N4)",
                Satisfaction(null, plan: EpicPlan, claims: NoClaims, runId: "run-epic",
                    more: [Record("delivery_plan", "delivery_plan", w.Put(InheritedPlan().Replace("AC-1", "AC-2")), runId: TicketRun)]), false, "unproved"),
            new("verification_satisfaction: the subjects come from two runs (review N4)",
                Satisfaction(passed, claims: Claims(), runId: "run-other"), false, "own_run_unknown"),
            new("verification_satisfaction: an epic's criterion given to a ticket whose plan holds it is the ticket's to prove",
                Satisfaction(null, plan: EpicPlan, claims: NoClaims, more: [childPlan], runId: "run-epic"), true),

            // ---- requirement_trace ----
            new("requirement_trace: R-1 traces through AC-1 and its claim to a test that passed at the head", Trace(traced), true),
            new("requirement_trace: no claims, so AC-1's method proves nothing", Trace([traced[0], traced[1], traced[3]]), false, "trace_gap"),

            // ---- tests_added ----
            new("tests_added: the fix added a test that passed at the fixed head", TestsAdded(Claims(), fixes, [Id]), true),
            new("tests_added: the fix changed code and claims an existing test it did not change",
                TestsAdded(Claims("WidgetTests.Existing", "reused"), codeOnly, ["WidgetTests.Existing"]), false, "no_test_added"),
            new("tests_added: the claimed test did not pass in the engine's results (review F2)", TestsAdded(Claims(), fixes, []), false, "no_test_added"),
            new("tests_added: the claim names the production file the fix changed (review F2)",
                TestsAdded(Claims(file: "src/Widgets.cs"), codeOnly, [Id]), false, "no_test_added"),
            new("tests_added: the claim's own symbol names something else in the changed file than its test (review F2)",
                TestsAdded(Claims("WidgetTests.Ghost").Replace("source: { file: tests/WidgetTests.cs }", "source: { file: tests/WidgetTests.cs, symbol: Existing }"), fixes,
                    ["WidgetTests.Ghost"]), false, "no_test_added"),
            new("tests_added: the claimed test is in another repository than the finding",
                TestsAdded(Claims().Replace("repository: acme/api", "repository: acme/web"), fixes, [Id]), false, "no_test_added"),
            new("tests_added: the claimed test passed at an earlier revision of this run, not at the fixed head (review N4)",
                TestsAdded(Claims(change: "modified"), fixes, [Id], testedAt: w.Main), false, "no_test_added"),
            new("tests_added: a test claimed as new already passed where the finding was raised (review N3)",
                TestsAdded(Claims(), fixes, [Id], passingWhenRaised: [Id]), false, "no_test_added"),
            new("tests_added: the claim names the finding's own file as ./src/Widgets.cs (review N3)",
                TestsAdded(Claims(file: "./src/Widgets.cs"), codeOnly, [Id]), false, "no_test_added"),
            new("tests_added: the finding has no revision it was raised on", TestsAdded(Claims(), fixes, [Id], raised: null), false, "no_raised_revision"),
        ];
    }

    public void Dispose() => w.Dispose();
}
