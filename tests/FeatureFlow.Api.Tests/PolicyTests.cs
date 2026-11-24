using System.Net;
using System.Text.Json;
using FeatureFlow.Api.Contracts;
using FeatureFlow.Model;

namespace FeatureFlow.Api.Tests;

/// <summary>
/// <c>GET /runs/{id}/policy</c> (plan s2.4): who answers each decision type of the run's plan and where the rule comes from,
/// who may use each override and whether the caller may, and the attempt limits configuration sets.
/// </summary>
public sealed class PolicyTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Each_decision_type_of_a_tickets_plan_shows_its_steps_and_the_rule_that_decides_it()
    {
        await using var world = new ReadsWorld();
        var parameters = new Dictionary<string, string> { ["ticket_id"] = "\"T-1\"" };
        var run = await world.StartAsync("std:templates/ticket_delivery", "ticket", parameters, "acme/api");
        await world.IndexAsync(run, "std:templates/ticket_delivery", ["acme/api"], parameters);

        var policy = await world.GetAsync<RunPolicyView>($"runs/{run}/policy");

        var decisions = policy.Decisions.ToDictionary(d => d.DecisionType, d => (Steps: string.Join(",", d.StepIds), Roles: string.Join(",", d.Roles), d.MinCount, d.Automatic, d.Source));
        // The organisation's 'ticket.*' rule decides the spec approval over std's default_approval (which allows auto).
        Assert.Equal(("approve_spec", "approver", 1, false, "organisation"), decisions["ticket.spec_approval"]);
        // An action's gate, by the plan's level.
        Assert.Equal(("deliver.merge", "organisation"), (decisions["ticket.merge_action"].Steps, decisions["ticket.merge_action"].Source));
        // No organisation rule matches findings.triage: std's default_approval decides it.
        Assert.Equal(("verify.fix.person_triage", "approver", 1, false, "library"), decisions["findings.triage"]);
        Assert.Equal(new AttemptPolicyView(3, null, ConfigurationReader.DefaultHardPerStep, ConfigurationReader.DefaultHardPerRun), policy.Attempts);
    }

    [Fact]
    public async Task Every_override_key_lists_its_roles_and_whether_the_caller_holds_one_and_an_unseen_run_is_404()
    {
        await using var world = new ApiWorld();
        await world.RunAsync("r", "acme/api");

        async Task<IReadOnlyDictionary<string, OverridePolicyView>> AsAsync(string roles) =>
            JsonSerializer.Deserialize<RunPolicyView>(await world.As("p", roles).GetStringAsync("runs/r/policy", Ct), ApiJson.Options)!.Overrides.ToDictionary(o => o.Key);

        var steerer = await AsAsync("viewer,steerer");
        var viewer = await AsAsync("viewer");

        Assert.Equal(Overrides.DefaultRoles.Keys.Order(), steerer.Keys.Order());
        Assert.Equal(("steerer,admin", true), (string.Join(",", steerer[Overrides.GoTo].Roles), steerer[Overrides.GoTo].Yours));
        Assert.Equal(("admin", false), (string.Join(",", steerer[Overrides.InstructProtected].Roles), steerer[Overrides.InstructProtected].Yours));
        Assert.False(viewer[Overrides.GoTo].Yours);
        using var missing = await world.As("p", "viewer").GetAsync("runs/nope/policy", Ct);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var unplanned = await world.As("p", "viewer").GetAsync("runs/r/policy", Ct);
        Assert.Empty(JsonSerializer.Deserialize<RunPolicyView>(await unplanned.Content.ReadAsStringAsync(Ct), ApiJson.Options)!.Decisions);
    }
}
