using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.ClaudeCode;
using FeatureFlow.Providers.CopilotCli;
using FeatureFlow.Providers.Docker;
using FeatureFlow.Providers.Git;
using FeatureFlow.Providers.GitHub;
using FeatureFlow.Providers.Jira;
using FeatureFlow.Providers.Kubernetes;
using FeatureFlow.Providers.Local;
using FeatureFlow.Providers.Postgres;
using FeatureFlow.Providers.S3;
using FeatureFlow.Providers.Slack;
using FeatureFlow.Providers.StdValidators;

namespace FeatureFlow.Providers.Standard;

/// <summary>
/// Every standard provider package's factories in one list (docs/06 section 4). The engine's hosts compose these with the
/// test doubles (<c>TestingProviders</c>); a distribution composes these with its own, so adding a provider needs no fork.
/// </summary>
public static class StandardProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        .. LocalProviders.Factories,
        .. DockerProviders.Factories,
        .. ClaudeCodeProviders.Factories,
        .. GitProviders.Factories,
        .. GitHubProviders.Factories,
        .. StdValidatorProviders.Factories,
        .. SlackProviders.Factories,
        .. PostgresProviders.Factories,
        .. S3Providers.Factories,
        .. KubernetesProviders.Factories,
        .. CopilotCliProviders.Factories,
        .. JiraProviders.Factories,
    ];
}
