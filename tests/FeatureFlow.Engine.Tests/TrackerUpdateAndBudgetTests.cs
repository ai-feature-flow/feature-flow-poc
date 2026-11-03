using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using Temporalio.Testing;

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

    static RunActivities Activities(IRecordStoreProvider records)
    {
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>> { ["record_store"] = new Dictionary<string, IProvider> { ["one"] = records } };
        var providers = new ProviderSet(instances, new Dictionary<string, string> { ["record_store"] = "one" }, new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "tester", []), 3, 10, "installation.yaml");
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "organisation.yaml");
        return new RunActivities(new EngineServices(providers, installation, organisation), null);
    }

    static RunRecord Run(string id, string? parent) => new(id, parent, "t", "running", "tester", [], false, DateTimeOffset.UnixEpoch, null);

    [Fact]
    public async Task A_retried_reservation_does_not_count_its_own_child_twice()
    {
        var records = new InMemoryRecordStore();
        (await records.AppendAsync(new RecordBatch([Run("root", null), Run("root.a.1", "root"), Run("root.a.1.b.1", "root.a.1")]), TestContext.Current.CancellationToken)).Unwrap();
        var activities = Activities(records);
        var request = new WorkItemRequest(new WorkItemBudget("root", 3), 1, Run("root.c.1", "root"));

        var first = await new ActivityEnvironment().RunAsync(() => activities.ReserveWorkItemsAsync(request));
        var retried = await new ActivityEnvironment().RunAsync(() => activities.ReserveWorkItemsAsync(request));
        var another = await new ActivityEnvironment().RunAsync(() => activities.ReserveWorkItemsAsync(request with { Child = Run("root.d.1", "root") }));

        Assert.Null(first);
        Assert.Null(retried);
        Assert.Contains("it has 3", another, StringComparison.Ordinal);
    }

    [Fact]
    public void Std_epics_children_get_a_work_list_items_tracker_issue_when_it_names_one()
    {
        var core = Start("std:templates/epic_delivery", ("epic_name", "billing")).Core!;
        YNode Item(string name, string? issue) => YamlWriter.Read(
            $"{{ name: {name}, title: T, repositories: [acme/api], boundary: b, owns: [R1]{(issue is null ? "" : $", tracker_issue: \"{issue}\"")} }}");

        var (patch, error) = PlanExpansion.Build(core, "tickets.expand", [Item("with_issue", "acme/api#7"), Item("without", null)]);

        Assert.Null(error);
        var withs = patch!.Changes.Select(c => (YMap)c.Step!["with"]!).ToList();
        Assert.Equal("acme/api#7", withs[0]["tracker_issue"]?.ScalarText());
        Assert.Null(withs[1]["tracker_issue"]);
        Assert.Equal(2, WorkItems.ChildSteps(patch));
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
