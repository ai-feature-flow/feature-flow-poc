using FeatureFlow.Engine.Actions;
using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Testing;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// F3 (LOG 352): an action no handler or provider can serve cannot answer, so the workflow raises an intervention
/// (<c>StepCannotAnswer</c>) instead of Temporal retrying the activity for ever. A dry run takes no provider from the
/// default instance of a type with no <c>dry_run</c> instance (docs/07 section 5).
/// </summary>
public sealed class UnservableActionTests
{
    /// <summary>The activities with a <c>memory</c> tracker as the default instance, and no dry-run instance of it.</summary>
    static RunActivities Activities()
    {
        IReadOnlyDictionary<string, IProvider> One(IProvider provider) => new Dictionary<string, IProvider> { ["one"] = provider };
        var folder = Path.Combine(Path.GetTempPath(), "ff-unservable-" + Guid.NewGuid().ToString("N"));
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
        {
            ["tracker"] = One(new InMemoryTracker()),
            ["record_store"] = One(new SqliteRecordStore(Path.Combine(folder, "records", "records.db"))),
            ["artifact_store"] = One(new FileSystemArtifactStore(folder)),
        };
        var providers = new ProviderSet(instances, instances.Keys.ToDictionary(k => k, _ => "one"), new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "tester", []), 3, 10, "installation.yaml");
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "organisation.yaml");
        return new RunActivities(new EngineServices(providers, installation, organisation), null);
    }

    static readonly ActionScope Scope = new(["acme/api"], "acme/api", "ff/ticket/T-1", null, [], [], []);

    static StepCall Call(string action, bool dryRun, string inputs = "{}") =>
        new(new StepIdentity("run-1", "t", "step", 1, "run-1/step/1"), "action", action,
            ((YMap)YamlWriter.Read(inputs)).Entries.ToDictionary(e => e.Key.Text, e => YamlWriter.Write(e.Value)),
            new StepContract([new OutcomeSpec("done", "success")], new Dictionary<string, string>(), [], new Dictionary<string, string>()), dryRun, null);

    [Theory]
    [InlineData("repo.inspect", false, "{}", "provider, and none that can serve it is configured")]
    [InlineData("tracker.update", true, "{ op: comment, issue: T-1, body: started }", "needs a tracker provider")]
    [InlineData("no.such_action", false, "{}", "no.such_action")]
    public async Task An_action_no_handler_or_provider_can_serve_cannot_answer(string action, bool dryRun, string inputs, string reason)
    {
        var outcome = await new ActivityEnvironment().RunAsync(() => Activities().RunActionAsync(Call(action, dryRun, inputs), Scope));

        Assert.Equal(StepOutcome.CannotAnswer, outcome.Kind);
        Assert.Contains(reason, outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outside_a_dry_run_the_default_instance_serves_the_action()
    {
        var outcome = await new ActivityEnvironment().RunAsync(() => Activities().RunActionAsync(Call("tracker.update", false, "{ op: comment, issue: T-1, body: started }"), Scope));

        Assert.True((outcome.Kind, outcome.Report?.Outcome) == (StepOutcome.Reported, "not_found"), outcome.Reason);
    }

    const string Commenting = """
        api_version: feature-flow/v1
        kind: template
        metadata: { name: commenting, level: ticket }
        defaults:
          routes:
            by_class: { failure: { escalate: true }, blocked: { escalate: true } }
        steps:
          - id: comment
            action: tracker.update
            with: { op: comment, issue: T-1, body: started }
            routes:
              done: { end: completed }
        """;

    /// <summary>
    /// The whole chain on a run: the plan's step starts, its activity cannot answer, the workflow's mapping gives the core
    /// that outcome, and the run stops at an open intervention on the step rather than ending or retrying.
    /// </summary>
    [Fact]
    public async Task A_plan_using_an_action_no_provider_can_serve_stops_at_an_open_intervention()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "probe-unservable");
        Directory.CreateDirectory(Path.Combine(root, "templates"));
        File.WriteAllText(Path.Combine(root, "library.yaml"), "name: probe\nversion: 0.1.0\n");
        File.WriteAllText(Path.Combine(root, "templates", "commenting.yaml"), Commenting);
        var d = new DiagnosticBag();
        var std = Path.Combine(Repository.Root, "library", "std");
        var libraries = new LibrarySet(Library.Library.Open(std, d)!, Library.Library.Open(root, d)!);
        var (core, loaded) = RunCore.Start(libraries, new LibraryReference("probe", "templates", "commenting"),
            new RunInfo("run-1", ["acme/api"], new Dictionary<string, YNode>()));
        Assert.True(core is not null, string.Join("\n", loaded.Concat(d)));
        Assert.Equal([new StartStep("comment", 1)], core.Advance());

        var outcome = await new ActivityEnvironment().RunAsync(() => Activities().RunActionAsync(Call("tracker.update", true, "{ op: comment, issue: T-1, body: started }"), Scope));
        Assert.True(core.Apply(RunWorkflow.Ending("comment", outcome)!).Accepted);

        var wait = Assert.Single(core.State.Waits);
        Assert.Equal((WaitKind.Intervention, "comment"), (wait.Kind, wait.StepId));
        Assert.Contains("tracker.update needs a tracker provider", wait.Reason, StringComparison.Ordinal);
        Assert.Equal(RunStatus.Running, core.State.Status);
        Assert.Contains(core.Advance(), c => c is RaiseIntervention { StepId: "comment" });
    }
}
