using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Validation.Tests;

/// <summary>
/// Run-start checks 19 to 22 (docs/09 section 2) on std and on mutations of it: each mutation triggers its one
/// check, and std with the configuration the M3 integration tests use triggers none.
/// </summary>
public class RunStartCheckTests
{
    static readonly LibraryReference Ticket = new("std", null, "templates", "ticket_delivery");

    /// <summary>Std's files with each edit applied once, as a library set, and ticket_delivery loaded from it.</summary>
    static (LibrarySet Libraries, Plan Plan) Load(params Std.Edit[] edits)
    {
        var files = LibraryFiles.Read(Std.Root).All.ToDictionary(kv => kv.Key, kv => kv.Value);
        foreach (var edit in edits)
        {
            Assert.Equal(1, files[edit.File].Split(edit.Old).Length - 1);
            files[edit.File] = files[edit.File].Replace(edit.Old, edit.New, StringComparison.Ordinal);
        }

        var d = new DiagnosticBag();
        var libraries = new LibrarySet(Library.Library.FromFiles(new LibraryFiles(files), d)!);
        var plan = PlanLoader.LoadTemplate(libraries, Ticket, d)!;
        Assert.False(d.HasErrors, string.Join("\n", d));
        return (libraries, plan);
    }

    static IReadOnlySet<string> Everything(Plan plan, LibrarySet libraries) => RunStartChecks.Needs(plan, libraries).Keys.ToHashSet();

    static List<int> Blocking(DiagnosticBag d) => d.Where(x => x.Blocks).Select(x => x.Check).Distinct().ToList();

    [Fact]
    public void Std_meets_its_own_requirements_on_this_engine()
    {
        var (libraries, _) = Load();
        var d = new DiagnosticBag();
        RunStartChecks.LibraryRequirements(libraries, "0.1.0+abc", d);
        Assert.Empty(d);
    }

    [Theory]
    [InlineData("  engine: \">=0.1.0 <2.0.0\"", "  engine: \">=2.0.0\"", "requires engine >=2.0.0")]
    [InlineData("  plan_schema: 1", "  plan_schema: 2", "requires plan schema 2")]
    public void Check_19_an_unmet_library_requirement_refuses_the_run(string old, string replacement, string message)
    {
        var (libraries, _) = Load(new Std.Edit("library.yaml", old, replacement));
        var d = new DiagnosticBag();
        RunStartChecks.LibraryRequirements(libraries, "0.1.0", d);
        Assert.Equal([Checks.ReferenceResolves], Blocking(d));
        Assert.Contains(message, d.Single().Message);
    }

    [Fact]
    public void Check_21_names_each_capability_no_configured_provider_offers()
    {
        var (libraries, plan) = Load();
        var needs = RunStartChecks.Needs(plan, libraries);
        Assert.Contains("code_host.pull_request", needs.Keys);
        Assert.Contains("agent_runtime", needs.Keys);
        Assert.Contains("tracker", needs.Keys);

        var all = new DiagnosticBag();
        RunStartChecks.Capabilities(plan, libraries, Everything(plan, libraries), all);
        Assert.Empty(all);

        var d = new DiagnosticBag();
        RunStartChecks.Capabilities(plan, libraries, Everything(plan, libraries).Where(n => n is not ("code_host.pull_request" or "ci")).ToHashSet(), d);
        Assert.Equal([Checks.Capabilities], Blocking(d));
        Assert.Equal(2, d.Count);
        Assert.Contains(d, x => x.Message.Contains("capability code_host.pull_request is not offered", StringComparison.Ordinal));
        Assert.Contains(d, x => x.Message.Contains("no ci provider is configured", StringComparison.Ordinal));
    }

    static YNode Section(string yaml) => YamlText.Parse(yaml, "feature-flow.yaml", new DiagnosticBag())!;

    [Theory]
    [InlineData("{ default_template: std:templates/ticket_delivery, adds: { skills: [{ name: acme_api, path: .feature-flow/skills/acme_api }] } }", null)]
    [InlineData("{ overrides: { std:tasks/implement_ticket: x } }", "library.overrides is not allowed")]
    [InlineData("{ adds: { templates: [{ name: mine, path: t.yaml }] } }", "library.adds.templates is not allowed")]
    [InlineData("{ adds: { skills: [{ name: review, path: .feature-flow/skills/review }] } }", "has the name of std:skills/review")]
    public void Check_20_a_repository_library_section_adds_only(string yaml, string? message)
    {
        var (libraries, _) = Load();
        var d = new DiagnosticBag();
        RunStartChecks.RepositoryLibrary("acme/api", Section(yaml), libraries, d);
        if (message is null)
            Assert.Empty(d);
        else
        {
            Assert.Equal([Checks.RepositoryLibraryAddsOnly], Blocking(d));
            Assert.Contains(message, d.Single().Message);
        }
    }

    static PolicyConfig Policy(long? perStep = null, ApprovalRule? rule = null, Cap<bool>? requireChecks = null) =>
        new(rule is null ? [] : [rule], perStep, null, null, requireChecks);

    static long Allowance(Step step) => step.Limits?.Attempts ?? step.Scope.DocumentScope.Document.Defaults.Limits?.Attempts ?? 3;

    [Fact]
    public void Check_22_holds_for_std_under_default_caps()
    {
        var (_, plan) = Load();
        var d = new DiagnosticBag();
        RunStartChecks.Caps(plan, Policy(perStep: 12, new ApprovalRule("*", ["approver"], 1, true, new Cap<bool>(true, false)), new Cap<bool>(true, true)), Allowance, d);
        Assert.Empty(d);
    }

    [Fact]
    public void Check_22_a_roots_budget_above_the_work_item_ceiling_is_refused_and_a_childs_is_not_read()
    {
        var (_, plan) = Load(new Std.Edit("templates/ticket_delivery.yaml", "\nparameters:\n", "\nbudget: { max_work_items: 50 }\n\nparameters:\n"));
        var policy = Policy() with { MaxWorkItems = 10 };
        var asRoot = new DiagnosticBag();
        var asChild = new DiagnosticBag();

        RunStartChecks.Caps(plan, policy, Allowance, asRoot);
        RunStartChecks.Caps(plan, policy, Allowance, asChild, root: false);

        Assert.Equal([Checks.LockedCap], Blocking(asRoot));
        Assert.Contains("max_work_items", asRoot.Single(x => x.Blocks).Message, StringComparison.Ordinal);
        Assert.Empty(Blocking(asChild));
    }

    [Fact]
    public void Check_22_an_allowance_above_the_per_step_ceiling_is_refused()
    {
        var (_, plan) = Load(new Std.Edit("templates/ticket_delivery.yaml",
            "    limits: { questions: { max: 3, timeout: 24h } }\n", "    limits: { attempts: 13, questions: { max: 3, timeout: 24h } }\n"));
        var d = new DiagnosticBag();
        RunStartChecks.Caps(plan, Policy(perStep: 12), Allowance, d);
        Assert.Equal([Checks.LockedCap], Blocking(d));
        Assert.Equal("implement", d.Single(x => x.Blocks).StepId);
    }

    [Fact]
    public void Check_22_auto_resolution_is_refused_only_where_a_rule_locks_it_off()
    {
        var (_, plan) = Load();
        var unlocked = new DiagnosticBag();
        RunStartChecks.Caps(plan, Policy(rule: new ApprovalRule("ticket.*", ["approver"], 1, false, new Cap<bool>(false, false))), Allowance, unlocked);
        Assert.Empty(Blocking(unlocked));
        // Not refused, but reported: the request has no effect (docs/05 section 3.1).
        Assert.Contains(unlocked, x => x is { Blocks: false, StepId: "approve_spec" } && x.Message.Contains("so a person answers", StringComparison.Ordinal));
        var allowed = new DiagnosticBag();
        RunStartChecks.Caps(plan, Policy(rule: new ApprovalRule("ticket.*", ["approver"], 1, false, new Cap<bool>(true, false))), Allowance, allowed);
        Assert.Empty(allowed);

        var locked = new DiagnosticBag();
        RunStartChecks.Caps(plan, Policy(rule: new ApprovalRule("ticket.*", ["approver"], 1, false, new Cap<bool>(false, true))), Allowance, locked);
        Assert.Equal([Checks.LockedCap], Blocking(locked));
        Assert.Equal("approve_spec", locked.Single(x => x.Blocks).StepId);
    }

    [Fact]
    public void Check_22_a_merge_without_checks_is_refused_where_checks_are_locked_on()
    {
        var (_, plan) = Load(new Std.Edit("fragments/merge_into_parent.yaml",
            "      engine_can_merge: { expr: \"params.engine_can_merge\" }\n", "      engine_can_merge: { expr: \"params.engine_can_merge\" }\n      require_checks: false\n"));
        var d = new DiagnosticBag();
        RunStartChecks.Caps(plan, Policy(requireChecks: new Cap<bool>(true, true)), Allowance, d);
        Assert.Equal([Checks.LockedCap], Blocking(d));
        Assert.Contains("require_checks", d.Single(x => x.Blocks).Message);
        var unlocked = new DiagnosticBag();
        RunStartChecks.Caps(plan, Policy(requireChecks: new Cap<bool>(true, false)), Allowance, unlocked);
        Assert.Empty(Blocking(unlocked));
    }

    [Theory]
    [InlineData(">=0.1.0 <2.0.0", "0.1.0", true)]
    [InlineData(">=0.1.0 <2.0.0", "2.0.0", false)]
    [InlineData(">=1.0.0", "0.9.9", false)]
    [InlineData("1.2.3", "1.2.3+sha", true)]
    public void Version_ranges(string range, string version, bool contains) =>
        Assert.Equal(contains, VersionRange.Parse(range)!.Contains(version));
}
