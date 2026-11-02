// The OrchestrationWorker host: composes the local providers through FeatureFlow.Hosting (docs/11 section 2.3).
using FeatureFlow.Hosting;
using FeatureFlow.Providers.Local;

return await HostArguments.RunAsync(args, LocalProviders.Factories, orchestration: true, agent: false, apiHost: false);
