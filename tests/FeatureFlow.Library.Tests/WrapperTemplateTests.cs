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

    // ---- Wrappers of std's ticket_flow, validated as a library would be ------------------------

    const string ProbeFile = "templates/probe.yaml";

    static DiagnosticBag ValidateTicketWrapper(string parameters, string rest)
    {
        var d = new DiagnosticBag();
        var files = new LibraryFiles(new Dictionary<string, string>
        {
            ["library.yaml"] = "name: probe\nversion: 0.1.0\ndependencies: [{ name: std }]\n",
            [ProbeFile] = $"api_version: feature-flow/v1\nkind: template\nmetadata: {{ name: probe, level: ticket }}\nparameters:\n{parameters}\nuse: std:fragments/ticket_flow\n{rest}\n",
        });
        var libraries = new LibrarySet(Library.Open(StdRoot, d)!, Library.FromFiles(files, d)!);
        if (PlanLoader.LoadTemplate(libraries, new LibraryReference("probe", "templates", "probe"), d) is { } plan)
            PlanValidator.Validate(plan, libraries, d);
        return d;
    }

    const string TicketParameters = "  ticket_id: { type: string, required: true }\n  engine_can_merge: { type: boolean, default: false }";

    static Diagnostic OneErrorAtTheWrapper(DiagnosticBag d)
    {
        var error = Assert.Single(d);
        Assert.True(error.Blocks, error.ToString());
        Assert.EndsWith(ProbeFile, error.Location!.File, StringComparison.Ordinal);
        return error;
    }

    [Theory]
    // The template declares the parameter: with would be ignored, since the fragment reads params.engine_can_merge.
    [InlineData(TicketParameters, "with: { engine_can_merge: true }", "engine_can_merge")]
    // The template does not declare it: the fragment's params.ticket_id would read nothing.
    [InlineData("  engine_can_merge: { type: boolean, default: false }", "with: { ticket_id: ABC-1 }", "ticket_id")]
    public void A_wrapper_binding_of_a_parameter_its_fragment_reads_through_params_is_refused_once_at_the_wrapper(string parameters, string with, string name)
    {
        var error = OneErrorAtTheWrapper(ValidateTicketWrapper(parameters, with + "\nroutes: { declined: { end: rejected } }"));

        Assert.Equal(Checks.Fragment, error.Check);
        Assert.Contains($"binds '{name}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("give it as the template's parameter, with its default", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_fragment_parameter_read_through_params_that_the_wrapper_does_not_declare_is_refused_once_at_the_wrapper()
    {
        var error = OneErrorAtTheWrapper(ValidateTicketWrapper("  ticket_id: { type: string, required: true }", "routes: { declined: { end: rejected } }"));

        Assert.Equal(Checks.Fragment, error.Check);
        Assert.Contains("fragment std:fragments/ticket_flow reads params.engine_can_merge; declare 'engine_can_merge' as a parameter of the template", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrapper_parameter_whose_type_differs_from_the_fragments_is_refused_once_at_the_wrapper()
    {
        var error = OneErrorAtTheWrapper(ValidateTicketWrapper(
            "  ticket_id: { type: string, required: true }\n  engine_can_merge: { type: string, default: \"yes\" }", "routes: { declined: { end: rejected } }"));

        Assert.Equal(Checks.Fragment, error.Check);
        Assert.Contains("parameter 'engine_can_merge' is a string here and a boolean in fragment std:fragments/ticket_flow", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wrapper_exit_routed_other_than_to_an_end_gives_one_error()
    {
        var error = OneErrorAtTheWrapper(ValidateTicketWrapper(TicketParameters, "routes: { declined: { escalate: true } }"));

        Assert.Contains("a wrapper template routes exit 'declined' to { end: <status> }", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every role, task and child template a std flow names is a reference parameter passed through to its steps. Bound
    /// to distinct items, each must reach a step and none of std's defaults may remain: a dropped pass-through leaves
    /// std's plans unchanged, so only this shows it.
    /// </summary>
    [Theory]
    [InlineData("ticket_delivery")]
    [InlineData("epic_delivery")]
    [InlineData("project_delivery")]
    public void Every_reference_parameter_of_a_std_flow_reaches_its_steps(string template)
    {
        var templateText = File.ReadAllText(Path.Combine(StdRoot, "templates", template + ".yaml"));
        var fragment = ((YMap)YamlText.Parse(templateText, template, new DiagnosticBag())!)["use"].ScalarText()!.Split('/')[1];
        var raw = (YMap)YamlText.Parse(File.ReadAllText(Path.Combine(StdRoot, "fragments", fragment + ".yaml")), fragment, new DiagnosticBag())!;
        var references = ((YMap)raw["parameters"]!).Entries
            .Where(e => (e.Value as YMap)?["type"].ScalarText() == "reference")
            .Select(e => (Name: e.Key.Text, Default: ((YMap)e.Value)["default"].ScalarText()!))
            .ToList();
        Assert.NotEmpty(references);

        // Each bound to a copy of its default under a name of its own; a child template to another std template.
        var files = new Dictionary<string, string> { ["library.yaml"] = "name: probe\nversion: 0.1.0\ndependencies: [{ name: std }]\n" };
        var bound = new Dictionary<string, string>();
        foreach (var (name, dflt) in references)
        {
            var (type, item) = (dflt.Split(':')[1].Split('/')[0], dflt.Split('/')[1]);
            if (type == "templates")
            {
                bound[name] = item == "project_delivery" ? "std:templates/ticket_delivery" : "std:templates/project_delivery";
                continue;
            }

            var extension = type == "roles" ? ".yaml" : ".md";
            var text = File.ReadAllText(Path.Combine(StdRoot, type, item + extension));
            files[$"{type}/p_{name}{extension}"] = type == "roles" ? text.Replace($"name: {item}", $"name: p_{name}", StringComparison.Ordinal) : text;
            bound[name] = $"probe:{type}/p_{name}";
        }

        files[$"templates/{template}.yaml"] = templateText + "\nwith:\n" + string.Join("\n", bound.Select(b => $"  {b.Key}: {b.Value}")) + "\n";
        var d = new DiagnosticBag();
        var plan = PlanLoader.LoadTemplate(new LibrarySet(Library.Open(StdRoot, d)!, Library.FromFiles(new LibraryFiles(files), d)!),
            new LibraryReference("probe", "templates", template), d);

        Assert.Empty(d);
        var named = plan!.AllSteps.SelectMany(s => new[] { s.Agent?.Role?.Text, s.Agent?.Task?.Text, s.With is null ? null : YamlWriter.Write(s.With) })
            .OfType<string>().ToList();
        Assert.All(bound, b => Assert.True(named.Any(n => n.Contains(b.Value, StringComparison.Ordinal)), $"{b.Key} reaches no step"));
        Assert.All(references, r => Assert.False(named.Any(n => n.Contains(r.Default, StringComparison.Ordinal)), $"{r.Name}: a step keeps std's {r.Default}"));
        Assert.DoesNotContain(plan.AllSteps, s => s.Agent?.Role?.Text?.StartsWith("std:", StringComparison.Ordinal) == true || s.Agent?.Task?.Text?.StartsWith("std:", StringComparison.Ordinal) == true);
    }
}
