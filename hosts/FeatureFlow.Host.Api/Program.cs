// The Api host: composes the standard providers and the test doubles through FeatureFlow.Hosting (docs/11 section 2.3,
// docs/06 section 4). Every host registers the same factories, because each composes every provider the installation
// configures; the doubles serve development installations only (composition refuses them otherwise).
using FeatureFlow.Hosting;
using FeatureFlow.Providers.Standard;
using FeatureFlow.Providers.Testing;

return await HostArguments.RunAsync(args, StandardProviders.Factories.Concat(TestingProviders.Factories), orchestration: false, agent: false, apiHost: true);
