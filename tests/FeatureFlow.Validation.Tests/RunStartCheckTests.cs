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
    static readonly LibraryReference Ticket = new("std", "templates", "ticket_delivery");

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

    /// <summary>Check 21 with one set of names offered for every type and instance, and no role mapped.</summary>
    static void Check21(Plan plan, LibrarySet libraries, IReadOnlySet<string> offered, DiagnosticBag d) =>
        RunStartChecks.Capabilities(plan, libraries, (_, _) => offered, _ => null, d);

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
        Check21(plan, libraries, Everything(plan, libraries), all);
        Assert.Empty(all);

        var d = new DiagnosticBag();
        Check21(plan, libraries, Everything(plan, libraries).Where(n => n is not ("code_host.pull_request" or "ci")).ToHashSet(), d);
        Assert.Equal([Checks.Capabilities], Blocking(d));
        Assert.Equal(2, d.Count);
        Assert.Contains(d, x => x.Message.Contains("capability code_host.pull_request is not offered", StringComparison.Ordinal));
        Assert.Contains(d, x => x.Message.Contains("no ci provider is configured", StringComparison.Ordinal));
    }

    /// <summary>A template whose only step is <c>wait.condition</c> with <paramref name="condition"/>.</summary>
    static (LibrarySet Libraries, Plan Plan) Waiting(string condition)
    {
        var (libraries, plan, d) = Std.Probe($$"""
            api_version: feature-flow/v1
            kind: template
            metadata: { name: probe, level: ticket }
            steps:
              - id: wait
                action: wait.condition
                with: { {{condition}} }
                routes: { met: { end: completed }, unmet: { end: failed } }
            """);
        Assert.False(d.HasErrors, string.Join("\n", d));
        return (libraries, plan);
    }

    [Theory]
    [InlineData("pull_request: approved", "code_host", "tracker", "no code_host provider is configured")]
    [InlineData("checks: passed", "code_host.check_status", "tracker", "capability code_host.check_status is not offered")]
    [InlineData("tracker: { statuses: [done] }", "tracker", "code_host", "no tracker provider is configured")]
    public void Check_21_wait_condition_needs_the_provider_its_condition_reads_and_no_other(string condition, string need, string other, string message)
    {
        var (libraries, plan) = Waiting(condition);
        var needs = RunStartChecks.Needs(plan, libraries);
        Assert.Equal("step wait", needs.GetValueOrDefault(need));
        Assert.DoesNotContain(needs, kv => kv.Value == "step wait" && (kv.Key == other || kv.Key.StartsWith(other + ".", StringComparison.Ordinal)));

        // Accepted with the provider its condition needs besides what the run and std need: the other is not asked for.
        var rest = needs.Where(kv => kv.Value != "step wait").Select(kv => kv.Key).ToList();
        var accepted = new DiagnosticBag();
        Check21(plan, libraries, rest.Append(need).ToHashSet(), accepted);
        Assert.Empty(accepted);

        var refused = new DiagnosticBag();
        Check21(plan, libraries, rest.Append(other).ToHashSet(), refused);
        Assert.Equal([Checks.Capabilities], Blocking(refused));
        Assert.Contains(message, refused.Single().Message, StringComparison.Ordinal);
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

    [Theory]
    [InlineData("require_checks", "false")]
    [InlineData("require_checks", "{ expr: \"params.squash\" }")]
    [InlineData("require_tested", "false")]
    [InlineData("require_tested", "{ expr: \"params.squash\" }")]
    public void Check_22_a_locked_merge_floor_refuses_any_value_but_a_literal_true(string input, string value)
    {
        var (_, plan) = Load(MergeInput(input, value));
        var locked = Policy() with { RequireChecks = new Cap<bool>(true, true), RequireTested = new Cap<bool>(true, true) };
        var d = new DiagnosticBag();
        RunStartChecks.Caps(plan, locked, Allowance, d);
        Assert.Equal([Checks.LockedCap], Blocking(d));
        Assert.Contains($"policy.merge.{input} is locked true", d.Single(x => x.Blocks).Message, StringComparison.Ordinal);

        // The same request is the workflow's to make where the floor is a default, and a literal true meets a locked one.
        var unlocked = new DiagnosticBag();
        RunStartChecks.Caps(plan, Policy() with { RequireChecks = new Cap<bool>(true, false), RequireTested = new Cap<bool>(true, false) }, Allowance, unlocked);
        Assert.Empty(Blocking(unlocked));
        var (_, literal) = Load(MergeInput(input, "true"));
        var met = new DiagnosticBag();
        RunStartChecks.Caps(literal, locked, Allowance, met);
        Assert.Empty(Blocking(met));
    }

    static Std.Edit MergeInput(string input, string value) => new("fragments/merge_into_parent.yaml",
        "      squash: { expr: \"params.squash\" }\n", $"      squash: {{ expr: \"params.squash\" }}\n      {input}: {value}\n");

    [Fact]
    public void Check_22_races_above_the_per_step_ceiling_are_refused_and_a_merge_asking_none_is_held_to_it()
    {
        var (_, plan) = Load(new Std.Edit("fragments/merge_into_parent.yaml",
            "    produces: [{ name: merge_record", "    limits: { races: 13 }\n    produces: [{ name: merge_record"));
        var d = new DiagnosticBag();
        RunStartChecks.Caps(plan, Policy(perStep: 12), Allowance, d);
        Assert.Equal([Checks.LockedCap], Blocking(d));
        Assert.Contains("allows 13 races, above policy.attempts.ceiling.per_step 12", d.Single(x => x.Blocks).Message, StringComparison.Ordinal);

        // Std's merge asks for no races: it gets the engine default held to the ceiling, so a ceiling below the default refuses no races.
        var (_, std) = Load();
        var held = new DiagnosticBag();
        RunStartChecks.Caps(std, Policy(perStep: 3), Allowance, held);
        Assert.DoesNotContain(held, x => x.Message.Contains("races", StringComparison.Ordinal));
        Assert.Equal(3, StepCaps.RaceAllowance(std.AllSteps.First(s => s.Action == "scm.merge"), 10, 3));
        Assert.Equal(13, StepCaps.RaceAllowance(plan.AllSteps.First(s => s.Action == "scm.merge"), 10, 3));

        // A race allowance asked for in the document's defaults is a request too, and is refused the same way.
        var (_, byDefaults) = Load(new Std.Edit("fragments/merge_into_parent.yaml",
            "artifact_types:\n  - std:artifact_types/check_results", "defaults:\n  limits: { races: 13 }\n\nartifact_types:\n  - std:artifact_types/check_results"));
        var viaDefaults = new DiagnosticBag();
        RunStartChecks.Caps(byDefaults, Policy(perStep: 12), Allowance, viaDefaults);
        Assert.Contains(viaDefaults, x => x.Blocks && x.Message.Contains("allows 13 races, above policy.attempts.ceiling.per_step 12", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(">=0.1.0 <2.0.0", "2.0.0", false)]
    [InlineData(">=1.0.0", "0.9.9", false)]
    [InlineData("1.2.3", "1.2.3+sha", true)]
    public void Version_ranges(string range, string version, bool contains) =>
        Assert.Equal(contains, VersionRange.Parse(range)!.Contains(version));

    [Fact]
    public void A_tracker_whose_status_map_names_none_of_the_plans_statuses_is_refused_and_one_named_is_enough()
    {
        var (_, plan, d) = Std.Probe("""
            api_version: feature-flow/v1
            kind: template
            metadata: { name: probe, level: ticket }
            steps:
              - id: ship
                action: tracker.update
                with: { op: transition, status: shipped }
                tracker: { on_start: in_progress }
                routes: { done: note, not_found: { end: failed } }
              - id: note
                action: tracker.update
                with: { op: comment, body: Shipped }
                routes: { done: { end: completed }, not_found: { end: failed } }
            """);
        Assert.False(d.HasErrors, string.Join("\n", d));
        Assert.Equal(["in_progress", "shipped"], RunStartChecks.TrackerStatuses(plan).Order(StringComparer.Ordinal));

        var refused = new DiagnosticBag();
        RunStartChecks.TrackerStatusMap(plan, "jira", new HashSet<string> { "done" }, refused);
        Assert.Equal([Checks.Capabilities], Blocking(refused));
        Assert.Contains("the status_map of tracker instance 'jira' maps none of the statuses this plan moves issues to (in_progress, shipped)", refused.Single().Message,
            StringComparison.Ordinal);

        var accepted = new DiagnosticBag();
        RunStartChecks.TrackerStatusMap(plan, "jira", new HashSet<string> { "shipped" }, accepted);
        RunStartChecks.TrackerStatusMap(plan, "memory", null, accepted);
        Assert.Empty(accepted);
    }
}
