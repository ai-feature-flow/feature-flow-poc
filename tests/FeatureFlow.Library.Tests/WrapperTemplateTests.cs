using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Validation;

namespace FeatureFlow.Library.Tests;

/// <summary>
/// A wrapper template: a document-level <c>use</c> of one fragment in place of steps, whose steps keep their ids
/// (docs/03 section 11.1). std's templates are wrappers, so an organisation library changes one std task in every
/// std template by wrapping them (docs/08 section 4.1; R1 done-when).
/// </summary>
public class WrapperTemplateTests
{
    static readonly string StdRoot = Path.Combine(Repository.Root, "library", "std");
    static readonly string AcmeRoot = Path.Combine(Repository.Root, "tests", "FeatureFlow.Library.Tests", "Fixtures", "acme_wraps_review");

    [Fact]
    public void A_library_that_wraps_one_std_task_changes_it_in_every_std_template()
    {
        var result = LibraryValidator.ValidateLibrary(AcmeRoot, [StdRoot]);
        Assert.False(result.Diagnostics.Count > 0, string.Join("\n", result.Diagnostics));
        Plan Acme(string template) => result.Plans[$"acme:templates/{template}"];

        foreach (var template in new[] { "ticket_delivery", "epic_delivery" })
        {
            var agents = Acme(template).AllSteps.Where(s => s.Agent is not null).ToList();
            var reviews = agents.Where(s => s.Agent!.Task!.Text == "acme:tasks/review_change").ToList();
            Assert.Equal(5, reviews.Count);   // one per angle of standard_review
            Assert.All(reviews, s => Assert.Equal("std:roles/reviewer", s.Agent!.Role!.Text));
            Assert.DoesNotContain(agents, s => s.Agent!.Task!.Text == "std:tasks/review_change");
            // Only the task bound changes: the verifying reviewer keeps std's task.
            Assert.Equal("std:tasks/verify_fix", agents.Single(s => s.Id == "verify.fix.verify_fix").Agent!.Task!.Text);
        }

        // Each level's children are acme's, so its tickets' reviewers are acme's too.
        Assert.Equal("acme:templates/ticket_delivery", ChildTemplate(Acme("epic_delivery"), "tickets.expand"));
        Assert.Equal("acme:templates/epic_delivery", ChildTemplate(Acme("project_delivery"), "epics.expand"));

        // Wrapped, not copied: acme holds only its task and three wrappers, none with steps of its own.
        var files = Directory.GetFiles(AcmeRoot, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(AcmeRoot, f)).Order(StringComparer.Ordinal);
        Assert.Equal(["library.yaml", "tasks/review_change.md", "templates/epic_delivery.yaml", "templates/project_delivery.yaml", "templates/ticket_delivery.yaml"], files);
        foreach (var template in Directory.GetFiles(Path.Combine(AcmeRoot, "templates")))
            Assert.False(((YMap)YamlText.Parse(File.ReadAllText(template), template, new DiagnosticBag())!).Has("steps"), template);
    }

    static string? ChildTemplate(Plan plan, string expand) =>
        (plan.AllSteps.Single(s => s.Id == expand).With!["step"] as YMap)?["with"] is YMap with ? with["template"].ScalarText() : null;

    [Theory]
    [InlineData("ticket_delivery", "ticket_flow")]
    [InlineData("epic_delivery", "epic_flow")]
    [InlineData("project_delivery", "project_flow")]
    public void A_wrapper_template_keeps_its_fragments_step_ids(string template, string fragment)
    {
        var d = new DiagnosticBag();
        var std = new LibrarySet(Library.Open(StdRoot, d)!);

        var wrapped = PlanLoader.LoadTemplate(std, new LibraryReference("std", "templates", template), d)!;
        var body = PlanLoader.LoadFragment(std, new LibraryReference("std", "fragments", fragment), d)!;

        Assert.Empty(d);
        Assert.Equal(body.AllSteps.Select(s => s.Id), wrapped.AllSteps.Select(s => s.Id));
    }

    const string Fragment = """
        api_version: feature-flow/v1
        kind: fragment
        metadata: { name: body }
        parameters:
          who: { type: reference, required: true }
          ticket_id: { type: string, required: true }
        exits: { done: success, stopped: failure }
        steps:
          - id: work
            agent: { role: std:roles/implementer, task: { param: who } }
            with: { ticket_id: { expr: "params.ticket_id" } }
            outcomes: { done: success, blocked: blocked }
            routes: { done: { exit: done }, blocked: { exit: stopped } }
        """;

    static (Plan? Plan, DiagnosticBag Diagnostics) LoadProbe(string template)
    {
        var d = new DiagnosticBag();
        var files = new LibraryFiles(new Dictionary<string, string>
        {
            ["library.yaml"] = "name: probe\nversion: 0.1.0\ndependencies: [{ name: std }]\n",
            ["fragments/body.yaml"] = Fragment,
            ["templates/probe.yaml"] = template,
        });
        var libraries = new LibrarySet(Library.Open(StdRoot, d)!, Library.FromFiles(files, d)!);
        return (PlanLoader.LoadTemplate(libraries, new LibraryReference("probe", "templates", "probe"), d), d);
    }

    static string Wrapper(string with, string extra = "") => $$"""
        api_version: feature-flow/v1
        kind: template
        metadata: { name: probe, level: ticket }
        parameters:
          ticket_id: { type: string, required: true }
        use: probe:fragments/body
        with: {{with}}
        routes: { stopped: { end: failed } }
        {{extra}}
        """;

    [Fact]
    public void A_wrapper_binds_literals_and_routes_its_fragments_exits_to_ends()
    {
        var (plan, d) = LoadProbe(Wrapper("{ who: std:tasks/implement_ticket }"));

        Assert.Empty(d);
        var work = Assert.Single(plan!.AllSteps);
        Assert.Equal("work", work.Id);
        Assert.Equal("std:tasks/implement_ticket", work.Agent!.Task!.Text);
        Assert.Equal(new ResolvedEnd("failed"), work.ResolvedRoutes.Single(r => r.Outcome == "blocked").Target);
        // An unrouted success exit ends the run as completed, as a template's last step does.
        Assert.Equal(new ResolvedEnd("completed"), work.ResolvedRoutes.Single(r => r.Outcome == "done").Target);
    }

    [Fact]
    public void A_template_with_both_steps_and_use_is_refused()
    {
        var (_, d) = LoadProbe(Wrapper("{ who: std:tasks/implement_ticket }", "steps: []"));

        Assert.Contains(d, x => x.Blocks && x.Check == Checks.Schema && x.Message.Contains("steps or a document-level use of a fragment, not both", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrapper_that_leaves_a_required_parameter_unbound_is_refused()
    {
        var (_, d) = LoadProbe(Wrapper("{}"));

        Assert.Contains(d, x => x.Blocks && x.Check == Checks.Fragment
            && x.Message.Contains("required parameter 'who' of fragment probe:fragments/body is neither bound nor a parameter of the template", StringComparison.Ordinal));
    }

    [Fact]
    public void A_wrapper_that_binds_an_expression_is_refused()
    {
        var (_, d) = LoadProbe(Wrapper("{ who: { expr: \"params.ticket_id\" } }"));

        Assert.Contains(d, x => x.Blocks && x.Check == Checks.Fragment
            && x.Message.Contains("binds 'who' to an expression; a wrapper template binds literals only", StringComparison.Ordinal));
    }
}
