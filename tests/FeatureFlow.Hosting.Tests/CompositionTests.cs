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
        Assert.Contains("code_host.pull_request", set.Offered("code_host", null, false));
        Assert.Empty(set.Offered("ci", null, false));
        // A type with no dry-run instance is absent from a dry run, never served by its default (LOG 362).
        Assert.Null(set.ForRun("code_host", true));
        Assert.Empty(set.Offered("code_host", null, true));
    }

    /// <summary>
    /// F16: the names offered for a type are those of the instance the run uses for it: the chosen one (an agent role's
    /// runtime), else the default; in a dry run the dry_run instance, whatever was chosen.
    /// </summary>
    [Fact]
    public void Capabilities_are_those_of_the_instance_the_run_picks()
    {
        var factories = Factories.Append(new("agent_runtime", "bare", [], _ => new Bare())).ToArray();
        var set = ProviderComposer.Compose(Read("""
              agent_runtime: { default: scripted, dry_run: recording, instances: { scripted: { script: s.yaml }, recording: { folder: r }, bare: {} } }
            """), factories);
        Assert.Contains("agent_runtime.mcp", set.Offered("agent_runtime", null, false));
        Assert.Equal(["agent_runtime"], set.Offered("agent_runtime", "bare", false));
        Assert.Contains("agent_runtime.mcp", set.Offered("agent_runtime", "bare", true));
    }

    sealed class Bare : IProvider
    {
        public ProviderDescription Describe() => new("agent_runtime", "bare", "0", new AgentRuntimeCapabilities(false, false));

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));
    }

    [Theory]
    [InlineData("  ci: { instances: { github_actions: {} } }", "no ci provider of kind 'github_actions'")]
    [InlineData("  agent_runtime: { instances: { scripted: { script: s.yaml, model: haiku } } }", "unknown setting 'model'")]
    public void An_instance_no_factory_can_create_is_refused(string providers, string message)
    {
        var e = Assert.Throws<StartupRefusal>(() => ProviderComposer.Compose(Read(providers), Factories));
        Assert.Contains(message, e.Message);
    }

    /// <summary>L5 (LOG 448): <c>engine_tools</c> is removed, and naming it on either built-in runtime is refused at load.</summary>
    [Theory]
    [InlineData("claude_code", "true")]
    [InlineData("copilot_cli", "false")]
    public void Engine_tools_is_refused_on_either_built_in_runtime(string kind, string value)
    {
        var installation = Read($"  agent_runtime: {{ instances: {{ a: {{ provider: {kind}, engine_tools: {value}, model_access: {{ mode: proxy }} }} }} }}");
        var e = Assert.Throws<StartupRefusal>(() => ProviderComposer.Compose(installation,
            [.. Providers.ClaudeCode.ClaudeCodeProviders.Factories, .. Providers.CopilotCli.CopilotCliProviders.Factories]));
        Assert.Contains("providers.agent_runtime.instances.a: unknown setting 'engine_tools'", e.Message);
    }

    /// <summary>
    /// LOG 496: an unknown <c>model_access.auth</c> is refused at composition, and so is <c>auth: github</c> without an
    /// https upstream, whose default would send the GitHub token to the Anthropic API.
    /// </summary>
    [Theory]
    [InlineData("auth: bearer, upstream: \"https://api.example\"", "model_access.auth 'bearer' is not one of api_key, oauth, github")]
    [InlineData("auth: github", "model_access.auth github needs an https upstream, the account's Copilot API")]
    [InlineData("auth: github, upstream: \"http://api.example\"", "model_access.auth github needs an https upstream, the account's Copilot API")]
    [InlineData("auth: github, upstream: \"https://api.business.githubcopilot.com\"", null)]
    public void An_unknown_auth_or_github_without_an_https_upstream_is_refused(string access, string? refusal)
    {
        var installation = Read($"  agent_runtime: {{ instances: {{ a: {{ provider: copilot_cli, model_access: {{ mode: proxy, {access} }} }} }} }}");
        var compose = () => ProviderComposer.Compose(installation, Providers.CopilotCli.CopilotCliProviders.Factories);
        if (refusal is null)
            compose();
        else
            Assert.Contains($"providers.agent_runtime.instances.a: {refusal}", Assert.Throws<StartupRefusal>(compose).Message);
    }

    /// <summary>LOG 496: <c>auth: github</c> is Copilot CLI's; a <c>claude_code</c> instance naming it is refused at load.</summary>
    [Fact]
    public void Auth_github_is_refused_on_claude_code()
    {
        var installation = Read("  agent_runtime: { instances: { a: { provider: claude_code, model_access: { mode: proxy, auth: github, upstream: \"https://api.business.githubcopilot.com\" } } } }");
        var e = Assert.Throws<InvalidOperationException>(() => ProviderComposer.Compose(installation, Providers.ClaudeCode.ClaudeCodeProviders.Factories));
        Assert.Contains("providers.agent_runtime.instances.a: model_access.auth 'github' is not for claude_code, which takes api_key or oauth", e.Message);
    }

    /// <summary>
    /// LOG 446: each runtime instance's own <c>model_access</c> configures its model proxy, the default's and every other's;
    /// an instance with none has no proxy.
    /// </summary>
    [Fact]
    public void Each_agent_runtime_instance_has_the_model_proxy_of_its_own_model_access()
    {
        var installation = Read("""
              agent_runtime:
                default: claude
                instances:
                  claude: { provider: claude_code, model_access: { mode: proxy, key_ref: secrets/anthropic } }
                  copilot: { provider: copilot_cli, model_access: { mode: proxy, key_ref: secrets/router, upstream: "https://router.example/anthropic" } }
                  scripted: { script: s.yaml }
            """);

        var proxies = FeatureFlowHost.ModelProxies(installation, null);

        Assert.Equal(["claude", "copilot"], proxies.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new Uri("https://api.anthropic.com/"), proxies["claude"].Upstream);
        Assert.Equal(new Uri("https://router.example/anthropic/"), proxies["copilot"].Upstream);
    }
}
