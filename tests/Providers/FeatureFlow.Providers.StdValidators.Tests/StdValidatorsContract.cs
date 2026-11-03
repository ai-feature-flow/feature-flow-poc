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
        var codeOnly = w.Commit("src/Widgets.cs", "class Widgets { const int Max = 100; }\n", "refs/heads/ff/ticket/T-2");
        var head = Frozen(fixes);
        ValidationSubject Requirements() => w.Subject("requirements", "requirements", TicketRequirements);
        ValidationSubject Plan(string text) => w.Subject("delivery_plan", "delivery_plan", text);

        var passed = w.TestEvidence(fixes, ["WidgetTests.RefusesLongNames"]);
        var failed = w.TestEvidence(fixes, [], ["WidgetTests.RefusesLongNames"]);
        var agents = w.TestEvidence(fixes, ["WidgetTests.RefusesLongNames"], action: null, evidence: false);
        ValidationSubject Results(ArtifactRecord r) => new("test_results", r.Type, new ArtifactRef(r.Ref));
        ValidationRequest Satisfaction(ArtifactRecord results) => Request(Names.VerificationSatisfaction,
            [Plan(TicketPlan()), w.Subject("implementation_claims", "evidence_claims", Claims()), Results(results)], head, [results]);

        var traced = new[]
        {
            Record("requirements", "requirements", w.Put(TicketRequirements)),
            Record("delivery_plan", "delivery_plan", w.Put(TicketPlan())),
            Record("implementation_claims", "evidence_claims", w.Put(Claims())),
            passed,
        };
        ValidationRequest Trace(IReadOnlyList<ArtifactRecord> artifacts) =>
            Request(Names.RequirementTrace, [new ValidationSubject("requirements", artifacts[0].Type, new ArtifactRef(artifacts[0].Ref))], head, artifacts);

        ValidationRequest TestsAdded(string claims, string revision) => Request(Names.TestsAdded,
            [w.Subject("fix_claims", "evidence_claims", claims), w.Subject("triage", "triage", TriageText)], Frozen(revision), [], [Finding("F-1", w.Main)]);

        Fixtures =
        [
            new("requirement_support: the ticket's requirement supports the epic's, its criterion supports it",
                Request(Names.RequirementSupport, [Requirements(), Plan(TicketPlan()), w.Subject("requirements", "requirements", EpicRequirements, "parent")], options: Coverage), true),
            new("requirement_support: the only criterion is enabling work, so R-1 has no supporter",
                Request(Names.RequirementSupport, [Requirements(), Plan(TicketPlan(Criterion.Replace("supports: [{ artifact: requirements, requirement: R-1 }]", "supports_reason: Enabling work") + "\n" + Method)),
                    w.Subject("requirements", "requirements", EpicRequirements, "parent")], options: Coverage), false, "uncovered"),
            new("requirement_support: a link to a requirement the parent does not hold",
                Request(Names.RequirementSupport, [w.Subject("requirements", "requirements", TicketRequirements.Replace("E-1", "E-9")),
                    w.Subject("requirements", "requirements", EpicRequirements, "parent")]), false, "unresolved_link"),

            new("requirement_ownership: the epic's plan gives AC-1 to ticket T-1",
                Request(Names.RequirementOwnership, [w.Subject("child_requirements", "delivery_plan", EpicPlan)]), true),
            new("requirement_ownership: a criterion of the epic's plan has no owner",
                Request(Names.RequirementOwnership, [w.Subject("child_requirements", "delivery_plan", EpicPlan.Replace("    owner: { kind: ticket, id: T-1 }", ""))]), false,
                "missing_owner"),

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

            new("verification_method_support: AC-1 has a method", Request(Names.VerificationMethodSupport, [Plan(TicketPlan())]), true),
            new("verification_method_support: AC-1 has neither a method nor an owner",
                Request(Names.VerificationMethodSupport, [Plan(TicketPlan(Criterion))]), false, "no_method"),

            new("verification_satisfaction: the claimed test passed in the engine's results at the frozen head", Satisfaction(passed), true),
            new("verification_satisfaction: the claimed test failed", Satisfaction(failed), false, "test_not_passed"),
            new("verification_satisfaction: test results an agent produced are not evidence (F7)", Satisfaction(agents), false, "not_evidence"),

            new("requirement_trace: R-1 traces through AC-1 and its claim to a test that passed at the head", Trace(traced), true),
            new("requirement_trace: no claims, so AC-1's method proves nothing", Trace([traced[0], traced[1], traced[3]]), false, "trace_gap"),

            new("tests_added: the fix changed the test file since the finding was raised", TestsAdded(Claims(), fixes), true),
            new("tests_added: the fix changed code and claims an existing test it did not change",
                TestsAdded(Claims("WidgetTests.Existing", "reused"), codeOnly), false, "no_test_added"),
        ];
    }

    public void Dispose() => w.Dispose();
}
