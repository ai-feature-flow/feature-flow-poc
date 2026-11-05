using FeatureFlow.Model;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Hosting.Tests;

/// <summary>M7 follow-up 4: startup resolves every secret reference the installation names, and a failure names it, never a value.</summary>
public sealed class SecretCheckTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Secrets(Dictionary<string, string> values) : ISecretsProvider
    {
        public ProviderDescription Describe() => new("secrets", "test", "0", new NoCapabilities());

        public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

        public Task<ProviderResult<string>> ResolveAsync(string reference, CancellationToken cancellationToken) =>
            Task.FromResult(values.TryGetValue(reference, out var value)
                ? ProviderResult<string>.Success(value)
                : ProviderResult<string>.Failure("not_found", $"no secret named {reference}"));
    }

    static InstallationConfig Installation()
    {
        var d = new DiagnosticBag();
        var installation = ConfigurationReader.ReadInstallation("""
            api_version: feature-flow/v1
            kind: installation
            development: true
            temporal: { address: "localhost:7233", payload_codec: { name: aes_gcm, key_ref: secrets/payload_key } }
            providers:
              agent_runtime: { instances: { claude: { provider: claude_code, model_access: { mode: proxy, key_ref: secrets/model } } } }
              notifications: { instances: { slack: { token_ref: secrets/slack_bot, signing_secret_ref: secrets/slack_signing } } }
              tracker: { instances: { jira: { base_url: "https://jira.example", secret_ref: secrets/jira, project: ACME } } }
            agent_worker: { listen: "http://0.0.0.0:8490", urls: ["http://h:8490"], token_key_ref: secrets/step_key }
            api:
              auth: { mode: local_token, token_ref: secrets/api_token }
            """, "/tmp/installation.yaml", d)!;
        Assert.False(d.HasErrors, string.Join("\n", d));
        return installation;
    }

    static ProviderSet With(ISecretsProvider? secrets) => new(
        secrets is null
            ? new Dictionary<string, IReadOnlyDictionary<string, IProvider>>()
            : new Dictionary<string, IReadOnlyDictionary<string, IProvider>> { ["secrets"] = new Dictionary<string, IProvider> { ["file"] = secrets } },
        secrets is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["secrets"] = "file" },
        new Dictionary<string, string>());

    static readonly Dictionary<string, string> All = new()
    {
        ["secrets/model"] = "sk-model-value",
        ["secrets/slack_bot"] = "xoxb-value",
        ["secrets/slack_signing"] = "signing-value",
        ["secrets/jira"] = "bot@example.com:jira-value",
        ["secrets/step_key"] = "step-value",
        ["secrets/api_token"] = "api-value",
        ["secrets/payload_key"] = "payload-value",
    };

    [Fact]
    public void Every_reference_is_found_with_where_it_is_named_however_deep()
    {
        Assert.Equal(
        [
            ("api.auth.token_ref", "secrets/api_token"),
            ("agent_worker.token_key_ref", "secrets/step_key"),
            ("temporal.payload_codec.key_ref", "secrets/payload_key"),
            ("providers.agent_runtime.instances.claude.model_access.key_ref", "secrets/model"),
            ("providers.notifications.instances.slack.token_ref", "secrets/slack_bot"),
            ("providers.notifications.instances.slack.signing_secret_ref", "secrets/slack_signing"),
            ("providers.tracker.instances.jira.secret_ref", "secrets/jira"),
        ], StartupChecks.SecretReferences(Installation()));
    }

    [Fact]
    public async Task Startup_goes_on_when_every_reference_resolves()
    {
        await StartupChecks.CheckSecretsAsync(Installation(), With(new Secrets(All)), Ct);
    }

    [Fact]
    public async Task A_reference_that_does_not_resolve_or_is_empty_stops_startup_naming_it_and_never_a_value()
    {
        var some = new Dictionary<string, string>(All);
        some.Remove("secrets/api_token");
        some["secrets/jira"] = "";

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => StartupChecks.CheckSecretsAsync(Installation(), With(new Secrets(some)), Ct));

        Assert.Contains("api.auth.token_ref: the secret 'secrets/api_token' does not resolve (not_found", e.Message);
        Assert.Contains("providers.tracker.instances.jira.secret_ref: the secret 'secrets/jira' is empty", e.Message);
        Assert.DoesNotContain("model_access", e.Message);
        Assert.All(All.Values, value => Assert.DoesNotContain(value, e.Message));
    }

    [Fact]
    public async Task A_reference_with_no_secrets_provider_stops_startup()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => StartupChecks.CheckSecretsAsync(Installation(), With(null), Ct));

        Assert.Contains("api.auth.token_ref names the secret 'secrets/api_token', and no secrets provider is configured", e.Message);
    }

    [Fact]
    public async Task A_host_does_not_start_with_a_secret_that_does_not_resolve()
    {
        var d = new DiagnosticBag();
        var installation = ConfigurationReader.ReadInstallation("""
            api_version: feature-flow/v1
            kind: installation
            development: true
            temporal: { address: "127.0.0.1:1" }
            providers:
              secrets: { instances: { test: {} } }
            api:
              auth: { mode: local_token, token_ref: secrets/api_token }
            """, "/tmp/installation.yaml", d)!;
        var organisation = new OrganisationConfig("t", [], new PolicyConfig([], null, null, null, null), "/tmp/organisation.yaml");
        ProviderFactory[] factories = [new("secrets", "test", [], _ => new Secrets([]))];

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FeatureFlowHost.StartAsync(new FeatureFlowConfiguration(installation, organisation), factories, false, false, "http://127.0.0.1:0"));

        Assert.Contains("api.auth.token_ref: the secret 'secrets/api_token' does not resolve", e.Message);
    }
}
