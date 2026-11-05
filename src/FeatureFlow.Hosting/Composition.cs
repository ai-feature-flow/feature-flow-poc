using System.Security.Cryptography;
using FeatureFlow.Api;
using FeatureFlow.Engine;
using FeatureFlow.Engine.Agent;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Temporalio.Client;
using Temporalio.Worker;

namespace FeatureFlow.Hosting;

/// <summary>The installation and organisation files, read and checked (docs/07 section 7, startup).</summary>
public sealed record FeatureFlowConfiguration(InstallationConfig Installation, OrganisationConfig Organisation)
{
    public static FeatureFlowConfiguration Load(string installationPath, string organisationPath)
    {
        var d = new DiagnosticBag();
        var installation = ConfigurationReader.ReadInstallation(File.ReadAllText(installationPath), Path.GetFullPath(installationPath), d);
        var organisation = ConfigurationReader.ReadOrganisation(File.ReadAllText(organisationPath), Path.GetFullPath(organisationPath), d);
        if (installation is not null && organisation is not null)
            StartupChecks.Check(installation, organisation, d);
        if (installation is null || organisation is null || d.HasErrors)
            throw new InvalidOperationException("the configuration does not validate:\n" + string.Join("\n", d.Where(x => x.Blocks)));
        return new FeatureFlowConfiguration(installation, organisation);
    }
}

/// <summary>
/// Composition (docs/06 section 4): selects and creates each configured provider instance from the factories a
/// host registers, by type and kind. An instance whose kind no factory provides, or with a setting its factory
/// does not take, is a configuration error.
/// </summary>
public static class ProviderComposer
{
    public static ProviderSet Compose(InstallationConfig installation, IEnumerable<ProviderFactory> factories)
    {
        var byKind = factories.ToDictionary(f => (f.Type, f.Kind));
        var baseDirectory = Path.GetDirectoryName(installation.Location) ?? ".";
        var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>(StringComparer.Ordinal);
        var defaults = installation.Providers.Where(p => p.Value.Default is not null).ToDictionary(p => p.Key, p => p.Value.Default!);
        var dryRun = installation.Providers.Where(p => p.Value.DryRun is not null).ToDictionary(p => p.Key, p => p.Value.DryRun!);
        ProviderSet? set = null;
        var errors = new List<string>();
        var worker = installation.AgentWorker ?? AgentWorkerConfig.None;
        var reach = worker.Urls.Append(worker.ModelBaseUrl).OfType<string>().Select(u => new Uri(u)).ToList();
        foreach (var (type, config) in installation.Providers)
        {
            var named = new Dictionary<string, IProvider>(StringComparer.Ordinal);
            foreach (var (name, instance) in config.Instances)
            {
                if (!byKind.TryGetValue((type, instance.Kind), out var factory))
                {
                    errors.Add($"providers.{type}.instances.{name}: no {type} provider of kind '{instance.Kind}' is in this host");
                    continue;
                }

                foreach (var key in instance.Settings.Keys.Where(k => !factory.Settings.Contains(k)))
                    errors.Add($"providers.{type}.instances.{name}: unknown setting '{key}'");
                named[name] = factory.Create(new ProviderContext(instance, baseDirectory, t => set?.ForRun(t, false)) { SandboxReach = reach });
            }

            instances[type] = named;
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("the providers do not compose:\n" + string.Join("\n", errors));
        set = new ProviderSet(instances, defaults, dryRun);
        return set;
    }
}

/// <summary>A running engine in one process: any of the orchestration worker, the agent worker and the API, as a host chooses.</summary>
public sealed class FeatureFlowHost : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new();
    readonly List<Task> workers = [];
    WebApplication? api;
    WebApplication? agentEndpoint;

    FeatureFlowHost(FeatureFlowConfiguration configuration, ProviderSet providers, ITemporalClient client)
    {
        Configuration = configuration;
        Providers = providers;
        Client = client;
    }

    public FeatureFlowConfiguration Configuration { get; }
    public ProviderSet Providers { get; }
    public ITemporalClient Client { get; }

    /// <summary>The API's address once started, such as <c>http://127.0.0.1:53211</c>.</summary>
    public string? ApiAddress { get; private set; }

    public static async Task<FeatureFlowHost> StartAsync(FeatureFlowConfiguration configuration, IEnumerable<ProviderFactory> factories,
        bool orchestrationWorker, bool agentWorker, string? apiUrl)
    {
        var providers = ProviderComposer.Compose(configuration.Installation, factories);
        foreach (var type in providers.Types)
            if (providers.ForRun(type, false) is { } provider && await provider.CheckAsync(CancellationToken.None) is { Ok: false } failed)
                throw new InvalidOperationException($"the {type} provider is not usable: {failed.Error!.Message}");
        var client = await TemporalClient.ConnectAsync(new TemporalClientConnectOptions(configuration.Installation.TemporalAddress)
        {
            Namespace = configuration.Installation.TemporalNamespace,
        });
        var host = new FeatureFlowHost(configuration, providers, client);
        var services = new EngineServices(providers, configuration.Installation, configuration.Organisation);
        var activities = new RunActivities(services, client);
        var queue = configuration.Installation.TaskQueue;
        if (orchestrationWorker)
            host.Run(new TemporalWorker(client, new TemporalWorkerOptions(queue).AddWorkflow<RunWorkflow>().AddAllActivities(activities)));
        if (agentWorker)
            await host.StartAgentWorkerAsync(configuration, providers, client, queue);
        if (apiUrl is not null)
        {
            host.api = ApiApp.Build(new ApiServices(providers, configuration.Installation, configuration.Organisation, client)
            {
                Authenticators = ApiAuthentication.For(configuration.Installation.ApiAuth),
                PeopleRoles = new OrganisationRoles(configuration.Organisation),
            }, apiUrl);
            await host.api.StartAsync();
            host.ApiAddress = host.api.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        }

        return host;
    }

    /// <summary>
    /// The agent worker (docs/02 section 1): the agent step's activity on the agent queue and, when the installation
    /// sets <c>agent_worker.listen</c>, the endpoint sandboxes call: hooks, engine tools and the model proxy (LOG 68).
    /// </summary>
    async Task StartAgentWorkerAsync(FeatureFlowConfiguration configuration, ProviderSet providers, ITemporalClient client, string queue)
    {
        var settings = configuration.Installation.AgentWorker ?? AgentWorkerConfig.None;
        var secrets = providers.ForRun("secrets", false) as ISecretsProvider;
        var key = settings.TokenKeyRef is { } reference
            ? (await (secrets ?? throw new InvalidOperationException("agent_worker.token_key_ref needs a secrets provider")).ResolveAsync(reference, CancellationToken.None)).Unwrap()
            : Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var tokens = StepTokens.FromSecret(key);
        var options = new AgentWorkerOptions(settings.Urls, settings.ModelBaseUrl ?? (settings.Urls.FirstOrDefault()?.TrimEnd('/') + "/model"), settings.CommitInterval,
            settings.SandboxClient);
        Run(new TemporalWorker(client, new TemporalWorkerOptions(TaskQueues.Agent(queue)).AddAllActivities(new AgentActivities(providers, tokens, options))));
        if (settings.Listen is not { } listen)
            return;
        var services = new AgentEndpointServices(tokens, providers.Required<IRecordStoreProvider>("record_store"), new TemporalRunSignals(client),
            ModelProxyOf(configuration.Installation, secrets));
        agentEndpoint = AgentEndpoint.Build(services, listen);
        await agentEndpoint.StartAsync();
        AgentEndpointAddress = agentEndpoint.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    /// <summary>
    /// The model proxy from the default agent runtime's <c>model_access</c> (docs/07 section 2): <c>key_ref</c> names the
    /// credential, <c>upstream</c> the model's API (default the Anthropic API), <c>auth</c> how it is sent: <c>api_key</c>
    /// (the default) or <c>oauth</c> for a subscription token.
    /// </summary>
    static ModelProxy? ModelProxyOf(InstallationConfig installation, ISecretsProvider? secrets)
    {
        if (!installation.Providers.TryGetValue("agent_runtime", out var runtimes) || runtimes.Default is null
            || runtimes.Instances[runtimes.Default].Settings["model_access"] is not YMap access || access["mode"].ScalarText() != "proxy")
            return null;
        var keyRef = access["key_ref"].ScalarText();
        var upstream = new Uri((access["upstream"].ScalarText() ?? "https://api.anthropic.com").TrimEnd('/') + "/");
        return new ModelProxy(new ModelProxyOptions(upstream, access["auth"].ScalarText() ?? "api_key", async ct =>
            keyRef is null || secrets is null ? null : (await secrets.ResolveAsync(keyRef, ct)) is { Ok: true } found ? found.Value : null));
    }

    /// <summary>The agent endpoint's address once started, when the installation configures one.</summary>
    public string? AgentEndpointAddress { get; private set; }

    void Run(TemporalWorker worker) => workers.Add(Task.Run(async () =>
    {
        using (worker)
            await worker.ExecuteAsync(stop.Token);
    }));

    /// <summary>Runs until cancelled.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        if (api is not null)
            await api.DisposeAsync();
        if (agentEndpoint is not null)
            await agentEndpoint.DisposeAsync();
        try
        {
            await Task.WhenAll(workers);
        }
        catch (OperationCanceledException)
        {
        }
    }
}

/// <summary>Command-line arguments the three hosts share: the two configuration files.</summary>
public static class HostArguments
{
    public static (string Installation, string Organisation) Files(string[] args)
    {
        string? installation = Environment.GetEnvironmentVariable("FF_INSTALLATION"), organisation = Environment.GetEnvironmentVariable("FF_ORGANISATION");
        for (var i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] == "--installation")
                installation = args[++i];
            else if (args[i] == "--organisation")
                organisation = args[++i];
        }

        return (installation ?? throw new ArgumentException("--installation <file> or FF_INSTALLATION is required"),
            organisation ?? throw new ArgumentException("--organisation <file> or FF_ORGANISATION is required"));
    }

    /// <summary>Runs a host until Ctrl+C.</summary>
    public static async Task<int> RunAsync(string[] args, IEnumerable<ProviderFactory> factories, bool orchestration, bool agent, bool apiHost)
    {
        var (installation, organisation) = Files(args);
        var configuration = FeatureFlowConfiguration.Load(installation, organisation);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        var url = Environment.GetEnvironmentVariable("FF_API_LISTEN") ?? "http://127.0.0.1:8480";
        await using var host = await FeatureFlowHost.StartAsync(configuration, factories, orchestration, agent, apiHost ? url : null);
        if (host.ApiAddress is { } address)
            Console.WriteLine($"API listening on {address}");
        await host.WaitAsync(cancel.Token);
        return 0;
    }
}
