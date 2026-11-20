using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// F3 (LOG 352): an action no provider can serve, neither run against the installation's providers nor by an action
/// stand-in, cannot answer, so the workflow raises an intervention (<c>StepCannotAnswer</c>) instead of Temporal retrying
/// the activity for ever.
/// </summary>
public sealed class UnservableActionTests
{
    static RunActivities Activities()
    {
        var providers = new ProviderSet(new Dictionary<string, IReadOnlyDictionary<string, IProvider>>(), new Dictionary<string, string>(), new Dictionary<string, string>());
        var installation = new InstallationConfig("localhost:7233", "default", "q", 5120, new Dictionary<string, ProviderTypeConfig>(),
            new ApiAuthConfig("local_token", null, "tester", []), 3, 10, "installation.yaml");
        var organisation = new OrganisationConfig("acme", [], new PolicyConfig([], null, null, null, null), "organisation.yaml");
        return new RunActivities(new EngineServices(providers, installation, organisation), null);
    }

    static StepCall Call(string action, bool dryRun) =>
        new(new StepIdentity("run-1", "t", "step", 1, "run-1/step/1"), "action", action, new Dictionary<string, string>(),
            new StepContract(Actions.All[action].Outcomes.Select(o => new OutcomeSpec(o.Name, o.Class.Name())).ToList(), new Dictionary<string, string>(), [],
                new Dictionary<string, string>()), dryRun, null);

    [Theory]
    [InlineData("tracker.update", false)]
    [InlineData("artifact.compose", true)]
    public async Task An_action_no_provider_can_serve_cannot_answer(string action, bool dryRun)
    {
        var outcome = await new ActivityEnvironment().RunAsync(() => Activities().RunActionAsync(Call(action, dryRun)));

        Assert.Equal(StepOutcome.CannotAnswer, outcome.Kind);
        Assert.Contains($"no provider can serve {action}", outcome.Reason, StringComparison.Ordinal);
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
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repository.FullName, "FeatureFlow.slnx")))
            repository = repository.Parent!;
        var std = Path.Combine(repository.FullName, "library", "std");
        var libraries = new LibrarySet(Library.Library.Open(std, d)!, Library.Library.Open(root, d)!);
        var (core, loaded) = RunCore.Start(libraries, new LibraryReference("probe", null, "templates", "commenting"),
            new RunInfo("run-1", ["acme/api"], new Dictionary<string, YNode>()));
        Assert.True(core is not null, string.Join("\n", loaded.Concat(d)));
        Assert.Equal([new StartStep("comment", 1)], core.Advance());

        var outcome = await new ActivityEnvironment().RunAsync(() => Activities().RunActionAsync(Call("tracker.update", false)));
        Assert.True(core.Apply(RunWorkflow.Ending("comment", outcome)!).Accepted);

        var wait = Assert.Single(core.State.Waits);
        Assert.Equal((WaitKind.Intervention, "comment"), (wait.Kind, wait.StepId));
        Assert.Contains("no provider can serve tracker.update", wait.Reason, StringComparison.Ordinal);
        Assert.Equal(RunStatus.Running, core.State.Status);
        Assert.Contains(core.Advance(), c => c is RaiseIntervention { StepId: "comment" });
    }
}
