// The OrchestrationWorker host: composes the solo profile's providers through FeatureFlow.Hosting (docs/11 section 2.3). Every
// host registers the same factories, because each composes every provider the installation configures.
using FeatureFlow.Hosting;
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

var factories = LocalProviders.Factories.Concat(DockerProviders.Factories).Concat(ClaudeCodeProviders.Factories)
    .Concat(GitProviders.Factories).Concat(GitHubProviders.Factories).Concat(StdValidatorProviders.Factories).Concat(SlackProviders.Factories)
    .Concat(PostgresProviders.Factories).Concat(S3Providers.Factories).Concat(KubernetesProviders.Factories).Concat(CopilotCliProviders.Factories)
    .Concat(JiraProviders.Factories);
return await HostArguments.RunAsync(args, factories, orchestration: true, agent: false, apiHost: false);
