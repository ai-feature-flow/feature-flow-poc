using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Testing;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// F3 (LOG A1-3a): an action no provider can serve, neither run against the installation's providers nor by an action
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
}
