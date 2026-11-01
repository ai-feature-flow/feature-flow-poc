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
        // A branch step's unrouted outcome is reported to the join, not escalated (docs/03 section 8.1).
        Assert.Equal(new Model.Plans.ResolvedBranchEnd("verify.review.review"), Route("verify.review.review_0.assess", "blocked"));
        Assert.Equal(new Model.Plans.ResolvedBranchEnd("verify.review.review"), Route("verify.review.review_0.record", "unavailable"));
    }

    [Fact]
    public void A_fragment_step_leaves_by_its_exit_not_by_the_callers_defaults()
    {
        // The ticket routes verify_and_review's escalated exit; fix_loop's blocked triager must reach that
        // route through fix_loop's and verify_and_review's escalated exits (LOG 72).
        var result = Std.Mutate(new Std.Edit("templates/ticket_delivery.yaml",
            "    routes:\n      ready: deliver\n", "    routes:\n      ready: deliver\n      escalated: { end: failed }\n"));
        Assert.False(result.HasErrors, Std.Describe(result));
        var plan = result.Plans["std:templates/ticket_delivery"];

        Model.Plans.Resolved Route(string step, string outcome) =>
            plan.AllSteps.Single(s => s.Id == step).ResolvedRoutes.Single(r => r.Outcome == outcome).Target;

        Assert.Equal(new Model.Plans.ResolvedEnd("failed"), Route("verify.fix.triage", "blocked"));
        Assert.Equal(new Model.Plans.ResolvedEnd("failed"), Route("verify.repair", "blocked"));
        // A step that is not in a fragment still takes the template's default.
        Assert.Equal(new Model.Plans.ResolvedEscalate(null, null), Route("implement", "blocked"));
    }

    [Fact]
    public void A_branch_with_no_defaults_reports_every_outcome_to_the_join()
    {
        var root = Path.Combine(Path.GetTempPath(), "ff-branch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        Directory.CreateDirectory(Path.Combine(root, "decision_types"));
        try
        {
            File.WriteAllText(Path.Combine(root, "library.yaml"), "name: probe\nversion: 0.1.0\n");
            File.WriteAllText(Path.Combine(root, "decision_types", "engine.yaml"),
                "api_version: feature-flow/v1\nkind: decision_types\ndecision_types:\n  - name: \"*.escalation\"\n");
            File.WriteAllText(Path.Combine(root, "templates", "t.yaml"), """
                api_version: feature-flow/v1
                kind: template
                metadata: { name: t, level: ticket }
                steps:
                  - id: tests
                    parallel:
                      branches:
                        - { id: t1, action: test.run }
                    routes: { completed: { end: completed }, any_failed: { end: failed } }
                """);

            var result = LibraryValidator.ValidateLibrary(root);

            Assert.True(result.Diagnostics.Count == 0, Std.Describe(result));
            var t1 = result.Plans["probe:templates/t"].AllSteps.Single(s => s.Id == "t1");
            Assert.Equal(new Model.Plans.ResolvedBranchEnd("tests"), t1.ResolvedRoutes.Single(r => r.Outcome == "failed").Target);
            Assert.Equal(new Model.Plans.ResolvedBranchEnd("tests"), t1.ResolvedRoutes.Single(r => r.Outcome == "baseline_failed").Target);
            Assert.Equal(new Model.Plans.ResolvedBranchEnd("tests"), t1.ResolvedRoutes.Single(r => r.Outcome == "passed").Target);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
