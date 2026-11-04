using FeatureFlow.Model;

namespace FeatureFlow.Engine.Tests;

/// <summary>The organisation's <c>planning.require_review</c> for a run's template, fixed in its policy at run start (docs/05 sections 3.1 and 8).</summary>
public class RequireReviewTests
{
    static PolicyConfig Policy(params (string Match, bool Value)[] rules) =>
        new([], null, null, null, null) { RequireReview = rules.Select(r => new ReviewRule(r.Match, new Cap<bool>(r.Value, false))).ToList() };

    [Fact]
    public void The_first_rule_matching_the_template_reference_or_name_applies()
    {
        var policy = Policy(("std:templates/project_*", true), ("epic_delivery", true), ("*", false));
        Assert.True(RunActivities.RequiresReview(policy, "std:templates/project_delivery"));
        Assert.True(RunActivities.RequiresReview(policy, "acme:templates/epic_delivery"));
        Assert.False(RunActivities.RequiresReview(policy, "std:templates/ticket_delivery"));
        Assert.Null(RunActivities.RequiresReview(Policy(("epic_*", true)), "std:templates/ticket_delivery"));
        Assert.Null(RunActivities.RequiresReview(Policy(), "std:templates/ticket_delivery"));
    }
}
