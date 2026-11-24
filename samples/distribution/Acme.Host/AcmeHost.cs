using Acme.Providers;
using FeatureFlow.Hosting;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Standard;
using FeatureFlow.Providers.Testing;

namespace Acme.Host;

/// <summary>
/// Acme's host (docs/06 section 4): one process running the orchestration worker, the agent worker and the API, composing
/// the standard providers, the test doubles (refused outside <c>development: true</c>) and Acme's own. Run it with
/// <c>--installation &lt;file&gt; --organisation &lt;file&gt;</c>, as the engine's hosts.
/// </summary>
public static class AcmeHost
{
    public static IEnumerable<ProviderFactory> Factories =>
        StandardProviders.Factories.Concat(TestingProviders.Factories).Concat(AcmeProviders.Factories);

    public static Task<int> Main(string[] args) =>
        HostArguments.RunAsync(args, Factories, orchestration: true, agent: true, apiHost: true);
}
