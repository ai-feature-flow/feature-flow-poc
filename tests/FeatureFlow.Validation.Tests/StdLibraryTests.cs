using FeatureFlow.Model;

namespace FeatureFlow.Validation.Tests;

/// <summary>Every std template and fragment loads with no blocking error (docs/11, M1).</summary>
public class StdLibraryTests
{
    [Fact]
    public void Std_validates_with_no_diagnostics()
    {
        var result = LibraryValidator.ValidateLibrary(Std.Root);

        Assert.True(result.Diagnostics.Count == 0, Std.Describe(result));
        Assert.Equal(["std:templates/epic_delivery", "std:templates/project_delivery", "std:templates/ticket_delivery"],
            result.Plans.Keys.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("templates", "ticket_delivery")]
    [InlineData("templates", "epic_delivery")]
    [InlineData("templates", "project_delivery")]
    [InlineData("fragments", "baseline")]
    [InlineData("fragments", "plan_and_validate")]
    [InlineData("fragments", "standard_review")]
    [InlineData("fragments", "fix_loop")]
    [InlineData("fragments", "verify_and_review")]
    [InlineData("fragments", "merge_into_parent")]
    [InlineData("fragments", "plan_children")]
    [InlineData("fragments", "trace")]
    [InlineData("fragments", "deliver_in_order")]
    public void Each_template_and_fragment_loads_on_its_own(string type, string name)
    {
        var result = LibraryValidator.ValidateItem(Std.Root, new LibraryReference("std", null, type, name));

        Assert.False(result.HasErrors, Std.Describe(result));
        Assert.Single(result.Plans);
    }

    [Fact]
    public void Every_std_fragment_and_template_is_covered()
    {
        var files = Directory.GetFiles(Path.Combine(Std.Root, "fragments"), "*.yaml")
            .Concat(Directory.GetFiles(Path.Combine(Std.Root, "templates"), "*.yaml"));

        Assert.Equal(12, files.Count());
    }

    [Fact]
    public void Fragments_expand_with_prefixed_ids_and_for_each_branches()
    {
        var plan = LibraryValidator.ValidateLibrary(Std.Root).Plans["std:templates/ticket_delivery"];
        var ids = plan.AllSteps.Select(s => s.Id).ToList();

        Assert.Contains("plan.plan", ids);
        Assert.Contains("verify.fix.verify_fix", ids);
        Assert.Contains("deliver.merge", ids);
        // standard_review's for_each over its five default angles (docs/08 section 6.3)
        Assert.Equal(5, ids.Count(id => id.StartsWith("verify.review.review_", StringComparison.Ordinal) && id.EndsWith(".assess", StringComparison.Ordinal)));
    }

    [Fact]
    public void Route_defaults_follow_list_order_and_classes()
    {
        var plan = LibraryValidator.ValidateLibrary(Std.Root).Plans["std:templates/ticket_delivery"];
        Model.Plans.Resolved Route(string step, string outcome) =>
            plan.AllSteps.Single(s => s.Id == step).ResolvedRoutes.Single(r => r.Outcome == outcome).Target;

        // RW13: a success outcome with no route goes to the next step in list order.
        Assert.Equal(new Model.Plans.ResolvedStep("implement", false), Route("approve_spec", "approved"));
        // The template's class default: blocked escalates.
        Assert.Equal(new Model.Plans.ResolvedEscalate(null, null), Route("implement", "blocked"));
        // A fragment exit routed by the caller: merge_into_parent's reverify goes back to verification.
        Assert.Equal(new Model.Plans.ResolvedStep("verify.tests", false), Route("deliver.checks", "failed"));
        // The engine default for attempts_exhausted.
        Assert.Equal(new Model.Plans.ResolvedEscalate(null, null), Route("implement", "attempts_exhausted"));
        // A route to a fragment use enters its first step.
        Assert.Equal(new Model.Plans.ResolvedStep("plan.plan", true), Route("approve_spec", "rejected"));
    }
}
