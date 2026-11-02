using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Hosting.Tests;

/// <summary>Composition (docs/06 section 4): providers are created from the factories a host registers, by type and kind.</summary>
public class CompositionTests
{
    sealed class Probe(string type, string kind) : IProvider
    {
        public ProviderDescription Describe() => new(type, kind, "0", CapabilityRecords.All(type));

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    static readonly ProviderFactory[] Factories =
    [
        new("code_host", "fake", [], _ => new Probe("code_host", "fake")),
        new("agent_runtime", "scripted", ["script"], _ => new Probe("agent_runtime", "scripted")),
        new("agent_runtime", "recording", ["folder"], _ => new Probe("agent_runtime", "recording")),
    ];

    static InstallationConfig Read(string providers)
    {
        var d = new DiagnosticBag();
        var config = ConfigurationReader.ReadInstallation($"api_version: feature-flow/v1\nkind: installation\ndevelopment: true\nproviders:\n{providers}", "/tmp/installation.yaml", d);
        Assert.False(d.HasErrors, string.Join("\n", d));
        return config!;
    }

    [Fact]
    public void A_dry_run_uses_the_dry_run_instance_and_capabilities_come_from_the_providers()
    {
        var set = ProviderComposer.Compose(Read("""
              agent_runtime: { default: scripted, dry_run: recording, instances: { scripted: { script: s.yaml }, recording: { folder: r } } }
              code_host: { instances: { fake: {} } }
            """), Factories);
        Assert.Equal("scripted", set.ForRun("agent_runtime", false)!.Describe().Name);
        Assert.Equal("recording", set.ForRun("agent_runtime", true)!.Describe().Name);
        Assert.Contains("code_host.pull_request", set.Offered(false));
        Assert.DoesNotContain("ci", set.Offered(false));
    }

    [Theory]
    [InlineData("  ci: { instances: { github_actions: {} } }", "no ci provider of kind 'github_actions'")]
    [InlineData("  agent_runtime: { instances: { scripted: { script: s.yaml, model: haiku } } }", "unknown setting 'model'")]
    public void An_instance_no_factory_can_create_is_refused(string providers, string message)
    {
        var e = Assert.Throws<InvalidOperationException>(() => ProviderComposer.Compose(Read(providers), Factories));
        Assert.Contains(message, e.Message);
    }
}
