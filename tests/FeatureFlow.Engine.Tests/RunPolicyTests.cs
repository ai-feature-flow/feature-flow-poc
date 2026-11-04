using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// The policy a run fixes at its start (docs/05 section 8): which decision types it may auto-resolve, by the same rule the
/// API uses for who may answer: the organisation's rule, then the library's <c>default_approval</c>. A team's rule sets only min_count.
/// </summary>
public sealed class RunPolicyTests
{
    static readonly SourceLocation Nowhere = new("", 0, 0);

    static readonly DecisionTypeItem[] Declared =
    [
        new("ticket.spec_approval", null, Nowhere) { DefaultApproval = new ApprovalRule("ticket.spec_approval", ["approver"], 1, false, new Cap<bool>(true, false)) },
        new("epic.ticket_plan_approval", null, Nowhere) { DefaultApproval = new ApprovalRule("epic.ticket_plan_approval", ["approver"], 1, false, new Cap<bool>(false, false)) },
    ];

    static EngineServices Services(OrganisationConfig organisation) => new(
        new ProviderSet(new Dictionary<string, IReadOnlyDictionary<string, IProvider>>(), new Dictionary<string, string>(), new Dictionary<string, string>()),
        new InstallationConfig("t", "d", "q", 5120, new Dictionary<string, ProviderTypeConfig>(), new ApiAuthConfig("local_token", null, "x", []), 3, 10, "i.yaml"),
        organisation);

    [Fact]
    public void A_runs_auto_resolution_follows_the_organisation_then_the_library_default()
    {
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([new ApprovalRule("epic.*", ["approver"], 1, false, new Cap<bool>(true, false))], null, null, null, null), "o.yaml")
        {
            Teams = new Dictionary<string, TeamConfig>
            {
                ["payments"] = new("payments", ["acme/payments-*"], [new ApprovalRule("ticket.*", ["approver"], 2, false, new Cap<bool>(false, false))],
                    new Dictionary<string, PeopleRoleConfig>()),
            },
        };
        var services = Services(organisation);

        var outside = services.Policy(Declared).ToOptions().AutoResolve;

        // No organisation rule matches: the library's default allows it (a team's rule sets only min_count, so it does not take part).
        Assert.True(outside("ticket.spec_approval"));
        // The organisation's rule wins over the library's default.
        Assert.True(outside("epic.ticket_plan_approval"));
        // Nothing matches: never automatic.
        Assert.False(outside("project.requirements_approval"));
    }
}
