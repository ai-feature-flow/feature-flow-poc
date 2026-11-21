using FeatureFlow.Hosting;
using FeatureFlow.Integration.Tests;
using FeatureFlow.Model;
using FeatureFlow.Providers.ClaudeCode;
using FeatureFlow.Providers.CopilotCli;

namespace FeatureFlow.Live.Tests;

/// <summary>
/// The default category's check of the Live tests' configuration, which needs no credential, Temporal or Docker: each Live
/// test's <c>model_access</c> composes, so a slip a host would refuse at start-up is caught without a live run (review 4
/// finding 1: N1's <c>http</c> upstream, which <c>auth: github</c> refuses).
/// </summary>
public sealed class LiveConfigurationTests
{
    public static TheoryData<string> Shapes => new(Options.Keys);

    static readonly Dictionary<string, AgentHarnessOptions> Options = new()
    {
        ["claude_code"] = LiveAgentTests.Live,
        ["copilot_cli"] = LiveCopilotTests.Live,
        ["copilot_cli N1"] = LiveCopilotTests.Unreachable,
        ["copilot_cli N2"] = LiveCopilotTests.Refused,
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void Each_live_test_s_model_access_composes(string shape)
    {
        var options = Options[shape];
        var (_, access) = AgentHarness.LiveModelAccess(options, "https://api.individual.githubcopilot.com");
        var d = new DiagnosticBag();
        var installation = ConfigurationReader.ReadInstallation(
            $"api_version: feature-flow/v1\nkind: installation\ndevelopment: true\nproviders:\n  agent_runtime: {{ instances: {{ a: {{ provider: {options.Runtime}, model: m, model_access: {access} }} }} }}\n",
            "/tmp/installation.yaml", d);
        Assert.False(d.HasErrors, string.Join("\n", d));

        var composed = ProviderComposer.Compose(installation!, [.. ClaudeCodeProviders.Factories, .. CopilotCliProviders.Factories]);

        Assert.Equal(options.Runtime, composed.ForRun("agent_runtime", false)!.Describe().Name);
    }
}
