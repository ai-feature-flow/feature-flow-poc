using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Library.Tests;

public class PlanLoaderTests
{
    static readonly string StdRoot = FindStd();

    static string FindStd()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return Path.Combine(dir.FullName, "library", "std");
        throw new InvalidOperationException("FeatureFlow.slnx not found");
    }

    static (Plan Plan, DiagnosticBag Diagnostics) Load(string template, LoadOptions? options = null)
    {
        var d = new DiagnosticBag();
        var std = Library.Open(StdRoot, d)!;
        var plan = PlanLoader.LoadTemplate(new LibrarySet(std), new LibraryReference("std", null, "templates", template), d, options)!;
        return (plan, d);
    }

    [Fact]
    public void The_manifest_and_items_are_indexed()
    {
        var d = new DiagnosticBag();
        var std = Library.Open(StdRoot, d)!;

        Assert.Empty(d);
        Assert.Equal("std", std.Name);
        Assert.Equal(["epic_delivery", "project_delivery", "ticket_delivery"], std.Names("templates"));
        Assert.Equal(9, std.Names("fragments").Count());
        Assert.Equal(3, std.Names("skills").Count());
        Assert.Contains(std.DecisionTypes, t => t.Name == "*.escalation");
        Assert.Equal(["in_progress", "code_review", "done"], std.Statuses.Select(s => s.Name));
    }

    [Fact]
    public void A_list_known_only_at_binding_expands_to_one_representative_branch()
    {
        var (plan, d) = Load("project_delivery");

        Assert.Empty(d);
        var chain = plan.AllSteps.Single(s => s.Id == "deliver.deliver");
        Assert.Equal(StepForm.Chain, chain.Form);
        Assert.False(chain.Container!.ItemsKnown);
        Assert.Single(chain.Container.Branches);
    }

    [Fact]
    public void A_list_reached_through_literal_defaults_is_expanded_in_full()
    {
        // verify_and_review passes its angles default to standard_review, whose for_each reads params.angles.
        var (plan, _) = Load("ticket_delivery");

        var review = plan.AllSteps.Single(s => s.Id == "verify.review.review");
        Assert.True(review.Container!.ItemsKnown);
        Assert.Equal(5, review.Container.Branches.Count);
        Assert.Equal("security", ((YScalar)review.Container.Branches[3].Scope.Item!).Text);
    }

    [Fact]
    public void Fragment_parameters_are_substituted_as_literals()
    {
        var (plan, _) = Load("epic_delivery");

        var planner = plan.AllSteps.Single(s => s.Id == "tickets.plan");
        Assert.Equal("std:tasks/plan_epic_tickets", planner.Agent!.Task!.Text);
        var produced = planner.Produces.Single(p => p.Name == "child_requirements");
        Assert.Equal("std:artifact_types/delivery_plan", produced.Type!.Text);
        Assert.Equal("epic.ticket_plan_approval", plan.AllSteps.Single(s => s.Id == "tickets.approve").Decision!.Type);
    }

    [Fact]
    public void Protection_on_a_use_covers_every_step_of_the_fragment()
    {
        var (plan, _) = Load("ticket_delivery");

        Assert.All(plan.AllSteps.Where(s => s.Id.StartsWith("verify.", StringComparison.Ordinal)), s => Assert.True(s.Protected, s.Id));
        Assert.False(plan.AllSteps.Single(s => s.Id == "implement").Protected);
    }
}
