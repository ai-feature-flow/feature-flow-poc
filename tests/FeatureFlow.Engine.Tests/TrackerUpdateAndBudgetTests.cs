using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Engine.Tests;

/// <summary>Which declarative tracker updates a run's timeline makes due (docs/03 section 2.5), and the root's work-item budget (F11).</summary>
public sealed class TrackerUpdateAndBudgetTests
{
    static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("FeatureFlow.slnx not found");
    }

    static LibrarySet Libraries(DiagnosticBag d) =>
        new(Library.Library.Open(Path.Combine(Root(), "library", "std"), d)!,
            Library.Library.Open(Path.Combine(Root(), "tests", "FeatureFlow.Integration.Tests", "libraries", "delivery"), d)!);

    static (RunCore? Core, IReadOnlyList<Diagnostic> Diagnostics) Start(string template, params (string Name, string Value)[] parameters) =>
        RunCore.Start(Libraries(new DiagnosticBag()), LibraryReference.TryParse(template)!,
            new RunInfo("run-1", ["acme/api"], parameters.ToDictionary(p => p.Name, p => YamlWriter.Read(p.Value))), new RunOptions { BranchFromPattern = true });

    static Plan Load(string template, params (string Name, string Value)[] parameters)
    {
        var (core, loaded) = Start(template, parameters);
        Assert.DoesNotContain(loaded, x => x.Blocks);
        return core!.Plan;
    }

    static List<string> Due(Plan plan, params TraceEntry[] trace) => TrackerUpdates.Due(plan, trace).Select(u => $"{u.StepId}:{u.Status}").ToList();

    [Fact]
    public void A_step_and_a_fragment_use_move_the_issue_on_start_and_on_an_exit()
    {
        var plan = Load("delivery:templates/tracked");

        Assert.Equal(["begin:in_progress"], Due(plan, new TraceEntry("started", "begin", "execution 1")));
        Assert.Equal(["deliver:code_review"], Due(plan, new TraceEntry("started", "deliver.say", "execution 1")));
        Assert.Equal(["deliver:done"], Due(plan, new TraceEntry("completed", "deliver.say", "sent")));
        Assert.Empty(Due(plan, new TraceEntry("completed", "begin", "sent"), new TraceEntry("escalated", "deliver.say")));
    }

    [Fact]
    public void Std_epic_delivery_moves_its_issue_to_done_only_when_the_epic_merges()
    {
        var plan = Load("std:templates/epic_delivery", ("epic_name", "billing"));

        Assert.Equal(["design:in_progress"], Due(plan, new TraceEntry("started", "design", "execution 1")));
        Assert.Equal(["deliver:done"], Due(plan, new TraceEntry("completed", "deliver.merge", "merged")));
        Assert.Empty(Due(plan, new TraceEntry("completed", "deliver.merge", "declined"), new TraceEntry("completed", "deliver.checks", "passed")));
    }

    [Fact]
    public void A_for_each_over_a_list_known_only_at_run_time_does_not_start()
    {
        var (core, diagnostics) = Start("delivery:templates/items_at_run_time");

        Assert.Null(core);
        Assert.Contains(diagnostics, x => x.Blocks && x.Check == Checks.Expression && x.Message.Contains("plan.expand", StringComparison.Ordinal));

        // Loaded on its own, with no run, the same template only shows one representative branch.
        var d = new DiagnosticBag();
        var plan = PlanLoader.LoadTemplate(Libraries(d), LibraryReference.TryParse("delivery:templates/items_at_run_time")!, d);
        Assert.DoesNotContain(d, x => x.Blocks);
        Assert.False(plan!.AllSteps.Single(s => s.Id == "each").Container!.ItemsKnown);
    }

    [Fact]
    public void A_roots_budget_is_its_templates_within_the_ceiling_or_the_ceiling_alone()
    {
        var project = Load("std:templates/project_delivery", ("project_name", "p"));
        var ticket = Load("std:templates/ticket_delivery", ("ticket_id", "T-1"));
        RunPolicy Ceiling(long? c) => new(3, 10, 200, null, null, [], WorkItemCeiling: c);

        Assert.Equal(20, WorkItems.Root("r", project, Ceiling(null))!.Limit);
        Assert.Equal(20, WorkItems.Root("r", project, Ceiling(30))!.Limit);
        Assert.Equal(5, WorkItems.Root("r", project, Ceiling(5))!.Limit);
        Assert.Equal(30, WorkItems.Root("r", ticket, Ceiling(30))!.Limit);
        Assert.Null(WorkItems.Root("r", ticket, Ceiling(null)));
    }
}
