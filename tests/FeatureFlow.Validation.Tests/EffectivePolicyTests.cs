using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;

namespace FeatureFlow.Validation.Tests;

/// <summary>
/// The effective policy view (docs/05 section 7) on std's <c>ticket_delivery</c>: for its decision step and its merge step,
/// the request, the cap, the result and which setting decided it.
/// </summary>
public class EffectivePolicyTests
{
    static readonly SourceLocation Nowhere = new("", 0, 0);

    static Plan Ticket()
    {
        var d = new DiagnosticBag();
        var libraries = new LibrarySet(Library.Library.Open(Std.Root, d)!);
        var plan = PlanLoader.LoadTemplate(libraries, new LibraryReference("std", null, "templates", "ticket_delivery"), d)!;
        Assert.False(d.HasErrors, string.Join("\n", d));
        return plan;
    }

    static IReadOnlyList<DecisionTypeItem> Declared => Library.Library.Open(Std.Root, new DiagnosticBag())!.DecisionTypes;

    static PolicyConfig Policy(params ApprovalRule[] rules) => new(rules, null, null, null, null);

    static PolicyRow Row(IReadOnlyList<PolicyRow> rows, string step, string topic) => rows.Single(r => r.StepId == step && r.Topic == topic);

    [Fact]
    public void A_decision_shows_who_answers_and_whether_auto_resolution_applies_and_from_which_setting()
    {
        var plan = Ticket();

        // No organisation rule: std's default_approval for ticket.spec_approval allows auto.
        var policy = Policy();
        var rows = EffectivePolicy.For(plan, policy, t => Approval.For(t, policy, null, Declared));
        var auto = Row(rows, "approve_spec", "auto-resolution");
        Assert.Equal(("auto: approved", "auto allowed", "library default_approval of ticket.spec_approval"), (auto.Request, auto.Cap, auto.From));
        Assert.StartsWith("resolves 'approved' at once, actor policy", auto.Result, StringComparison.Ordinal);
        Assert.Equal("approver, 1 person, console or CLI only", Row(rows, "approve_spec", "who answers").Result);

        // An organisation rule wins over the library's default; unlocked, the request has no effect.
        policy = Policy(new ApprovalRule("ticket.*", ["approver"], 2, true, new Cap<bool>(false, false)));
        rows = EffectivePolicy.For(plan, policy, t => Approval.For(t, policy, null, Declared));
        Assert.Equal(("a person answers (auto not allowed)", "approval rule 'ticket.*'"), (Row(rows, "approve_spec", "auto-resolution").Result, Row(rows, "approve_spec", "auto-resolution").From));
        Assert.Equal("approver, 2 people, chat or console", Row(rows, "approve_spec", "who answers").Result);

        // Locked off: run start refuses it (check 22), and the view says so.
        policy = Policy(new ApprovalRule("ticket.*", ["approver"], 1, false, new Cap<bool>(false, true)));
        rows = EffectivePolicy.For(plan, policy, t => Approval.For(t, policy, null, Declared));
        Assert.Equal(("auto not allowed, locked", "refused at run start (check 22)"), (Row(rows, "approve_spec", "auto-resolution").Cap, Row(rows, "approve_spec", "auto-resolution").Result));
    }

    [Fact]
    public void A_team_rule_raises_min_count_for_a_run_in_the_teams_scope()
    {
        var policy = Policy(new ApprovalRule("*", ["approver"], 1, true, new Cap<bool>(true, false)));
        var team = new TeamConfig("payments", ["acme/payments-*"], [new ApprovalRule("ticket.*", [], 3, false, new Cap<bool>(false, false))],
            new Dictionary<string, PeopleRoleConfig>());
        var rows = EffectivePolicy.For(Ticket(), policy, t => Approval.For(t, policy, team, Declared));
        Assert.Equal(("approver, 3 people, chat or console", "approval rule '*', min_count from team payments's rule 'ticket.*'"),
            (Row(rows, "approve_spec", "who answers").Result, Row(rows, "approve_spec", "who answers").From));
    }

    [Fact]
    public void A_merge_shows_who_merges_per_repository_by_F1_and_whether_checks_are_required()
    {
        var plan = Ticket();
        var policy = Policy() with { EngineMayMerge = ["acme/poc-*"], RequireChecks = new Cap<bool>(true, true) };
        var rows = EffectivePolicy.For(plan, policy, t => Approval.For(t, policy, null, Declared), step =>
            (true, [new MergeTarget("acme/api", "main"), new MergeTarget("acme/poc-x", "main"), new MergeTarget("acme/web", null)]));

        var who = Row(rows, "deliver.merge", "who merges");
        Assert.Equal(("engine_can_merge = true", "engine_may_merge: acme/poc-*", "F1: policy.merge.default_branch.engine_may_merge"), (who.Request, who.Cap, who.From));
        Assert.Equal("a person merges into acme/api's default branch main (action request); the engine merges into acme/poc-x's default branch main; " +
                     "the engine merges into acme/web (not its default branch)", who.Result);
        var checks = Row(rows, "deliver.merge", "checks before merging");
        Assert.Equal(("require_checks true, locked", "checks required", "scm.merge's default"), (checks.Cap, checks.Result, checks.From));

        // ff show has no run: the request as the plan states it, traced to the template's parameter.
        var shown = Row(EffectivePolicy.For(plan, policy, t => Approval.For(t, policy, null, Declared)), "deliver.merge", "who merges");
        Assert.Equal("engine_can_merge = params.engine_can_merge (default false)", shown.Request);
    }

    [Fact]
    public void The_readable_form_carries_the_view_at_its_foot_and_is_unchanged_without_it()
    {
        var plan = Ticket();
        var policy = Policy();
        var rows = EffectivePolicy.For(plan, policy, t => Approval.For(t, policy, null, Declared));
        var with = ReadableForm.Render(plan, effective: rows);
        Assert.Contains("Effective policy:\n  approve_spec who answers: approver, 1 person", with, StringComparison.Ordinal);
        Assert.Equal(ReadableForm.Render(plan), with[..with.IndexOf("Effective policy:", StringComparison.Ordinal)]);
    }
}
