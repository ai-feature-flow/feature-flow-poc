using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class GitHubProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("credential_broker", "github_app", ["app_id", "installation_id", "key_ref", "key_path", "repositories", "api_base"], Broker),
        new("code_host", "github", ["api_base", "webhook_secret_ref"], c => new GitHubCodeHost(Api(c)) { WebhookSecret = WebhookSecret(c) }),
        new("ci", "github_actions", ["api_base"], c => new GitHubActionsCi(Api(c),
            () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("the GitHub Actions provider needs an artifact store"))),
        new("findings_source", "copilot_review", ["api_base", "blocking"], c => new CopilotReviewSource(Api(c),
            () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("the Copilot review source needs an artifact store"),
            c.Setting("blocking") == "true")),
        new("tracker", "github_issues", ["api_base", "repository", "status_map"], c => new GitHubIssuesTracker(Api(c), c.Setting("repository"), StatusMap(c))),
    ];

    /// <summary>
    /// <c>status_map</c> (docs/07 section 2): a library status to a label, or to <c>closed</c>. Unset, std's statuses map to
    /// <see cref="GitHubIssuesTracker.DefaultStatusMap"/>.
    /// </summary>
    static IReadOnlyDictionary<string, string>? StatusMap(ProviderContext c)
    {
        if (c.Instance.Settings["status_map"] is not { } node)
            return null;
        if (node is not YMap map || map.Entries.Any(e => e.Value.ScalarText() is not { Length: > 0 }))
            throw new InvalidOperationException($"providers.tracker.instances.{c.Instance.Name}.status_map maps each library status to a label or 'closed'");
        return map.Entries.ToDictionary(e => e.Key.Text, e => e.Value.ScalarText()!, StringComparer.Ordinal);
    }

    /// <summary>The webhook secret the inbound gateway verifies deliveries with (M7): a secret reference, never a value in configuration.</summary>
    static Func<CancellationToken, Task<ProviderResult<string>>>? WebhookSecret(ProviderContext c) =>
        c.Setting("webhook_secret_ref") is { } reference
            ? ct => (c.Provider("secrets") as ISecretsProvider ?? throw new InvalidOperationException("webhook_secret_ref needs a secrets provider")).ResolveAsync(reference, ct)
            : null;

    /// <summary>The code host and CI providers take their tokens from the default credential broker, never from configuration.</summary>
    static GitHubApi Api(ProviderContext c) =>
        new(() => c.Provider("credential_broker") as ICredentialBroker, apiBase: c.Setting("api_base") ?? "https://api.github.com");

    /// <summary>
    /// The App's key is a secret reference (<c>key_ref</c>, resolved by the secrets provider) or a key file outside
    /// the repository (<c>key_path</c>, the solo profile). Never a value in configuration.
    /// </summary>
    static AppCredentialBroker Broker(ProviderContext c)
    {
        var keyRef = c.Setting("key_ref");
        var keyPath = c.PathSetting("key_path");
        if ((keyRef is null) == (keyPath is null))
            throw new InvalidOperationException($"providers.credential_broker.instances.{c.Instance.Name} needs one of key_ref and key_path");
        var repositories = c.Instance.Settings["repositories"] is YSeq seq ? seq.Items.Select(i => i.ScalarText() ?? "").ToList() : [];
        Func<CancellationToken, Task<ProviderResult<string>>> key = keyRef is not null
            ? ct => (c.Provider("secrets") as ISecretsProvider ?? throw new InvalidOperationException("key_ref needs a secrets provider")).ResolveAsync(keyRef, ct)
            : async ct => File.Exists(keyPath)
                ? ProviderResult<string>.Success(await File.ReadAllTextAsync(keyPath!, ct))
                : ProviderResult<string>.Failure("unauthenticated", $"the GitHub App key file {keyPath} does not exist");
        return new AppCredentialBroker(c.RequiredSetting("app_id"), c.RequiredSetting("installation_id"), key, repositories,
            apiBase: c.Setting("api_base") ?? "https://api.github.com");
    }
}
