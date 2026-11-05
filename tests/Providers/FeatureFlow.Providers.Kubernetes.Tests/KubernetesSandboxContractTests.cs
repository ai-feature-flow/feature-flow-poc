using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace FeatureFlow.Providers.Kubernetes.Tests;

/// <summary>
/// The sandbox contract against the Kubernetes pod sandbox on a cluster that enforces network policies (a k3d cluster).
/// The cluster is the kubeconfig context <c>FF_K8S_CONTEXT</c> names, or the current context of the kubeconfig file
/// <c>FF_KUBECONFIG</c> names (the CI deployment's), with the sandbox image (<see cref="Image"/>) already imported. The test applies the repository's <c>sandbox-network-policy.yaml</c> in a namespace of its own, with a stand-in
/// store (Temporal, Postgres, MinIO) beside the sandboxes and a stand-in agent worker (labelled as one) in another namespace,
/// as a deployment has it, each listening on a port.
/// </summary>
[Trait("Category", "Cluster")]
public sealed class KubernetesSandboxContractTests : SandboxContract
{
    /// <summary>The sandbox image already in the cluster: <c>FF_SANDBOX_IMAGE</c>, else <c>feature-flow-sandbox:m8</c>.</summary>
    public static readonly string Image = Environment.GetEnvironmentVariable("FF_SANDBOX_IMAGE") is { Length: > 0 } image ? image : "feature-flow-sandbox:m8";
    public const string Namespace = "ff-sandbox-contract";

    /// <summary>The stand-in agent worker's namespace: in a deployment the worker and the sandboxes are in different ones.</summary>
    public const string WorkerNamespace = "ff-sandbox-contract-worker";

    readonly string folder = Directory.CreateTempSubdirectory("ff-k8s-contract-").FullName;
    readonly FileSystemArtifactStore artifacts;
    KubernetesSandbox? sandboxes;

    public KubernetesSandboxContractTests() => artifacts = new FileSystemArtifactStore(folder);

    protected override ISandboxProvider Sandboxes => sandboxes!;
    protected override IArtifactStoreProvider Store => artifacts;
    protected override Uri Reachable => new($"http://agent-worker.{WorkerNamespace}.svc.cluster.local:8471");

    protected override IReadOnlyList<Uri> Forbidden =>
    [
        new($"http://store.{Namespace}.svc.cluster.local:5432"),
        new("https://kubernetes.default.svc.cluster.local:443"),
        new($"https://{ClusterWorld.NodeAddress}:6443"),
        new($"http://host.k3d.internal:{ClusterWorld.HostPort}"),
    ];

    public override async ValueTask InitializeAsync()
    {
        var client = await ClusterWorld.ClientAsync();
        sandboxes = new KubernetesSandbox(client, Namespace, Image, "node", "1", "1Gi", () => artifacts);
        Ok(await sandboxes.CheckAsync(Ct));
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        if (sandboxes is not null)
            await base.DisposeAsync();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task A_pod_without_the_sandbox_label_reaches_the_forbidden_endpoints()
    {
        // The control for the forbidden endpoints: in the same namespace, only the policy stands between a pod and them.
        var client = await ClusterWorld.ClientAsync();
        var probe = new KubernetesSandbox(client, Namespace, Image, "node", "1", "1Gi", () => artifacts);
        var name = ShellSandbox.NameOf(Key + "/unlabelled");
        var pod = new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = new Dictionary<string, string> { ["app.kubernetes.io/name"] = "contract-probe" } },
            Spec = new V1PodSpec
            {
                AutomountServiceAccountToken = false,
                TerminationGracePeriodSeconds = 0,
                Containers = [new V1Container { Name = "sandbox", Image = Image, ImagePullPolicy = "IfNotPresent", Command = ["tini", "--", "sleep", "infinity"] }],
            },
        };
        await client.CoreV1.CreateNamespacedPodAsync(pod, Namespace, cancellationToken: Ct);
        try
        {
            var handle = Ok(await probe.AttachAsync(Key + "/unlabelled", Ct));
            foreach (var endpoint in Forbidden)
            {
                var (exit, _) = Ok(await probe.RunAsync(handle, new SandboxCommand(["bash", "-c", $"timeout 8 bash -c '</dev/tcp/{endpoint.IdnHost}/{endpoint.Port}'"], AsRoot: true),
                    $"probe/{endpoint.Port}", Ct));
                Assert.True(exit == 0, $"{endpoint} does not listen, so the forbidden check proves nothing");
            }
        }
        finally
        {
            await client.CoreV1.DeleteNamespacedPodAsync(name, Namespace, gracePeriodSeconds: 0, cancellationToken: CancellationToken.None);
        }
    }

    [Fact]
    public async Task The_provider_refuses_to_start_without_the_network_policy()
    {
        var client = await ClusterWorld.ClientAsync();
        var elsewhere = new KubernetesSandbox(client, "default", Image, "node", "1", "1Gi", () => artifacts);

        Refused(await elsewhere.CheckAsync(Ct), "not_found");
    }
}

/// <summary>The cluster, its client, and the namespace's network policy and stand-ins, set up once per test run.</summary>
public static class ClusterWorld
{
    static readonly Lazy<Task<IKubernetes>> Client = new(SetUpAsync);
    static readonly System.Net.Sockets.TcpListener Host = Listen();

    public static Task<IKubernetes> ClientAsync()
    {
        Assert.SkipWhen(Context is null && KubeConfig is null,
            "the Cluster tests need FF_K8S_CONTEXT (a kubeconfig context) or FF_KUBECONFIG (a kubeconfig file) for a cluster that enforces network policies");
        return Client.Value;
    }

    static string? Context => Environment.GetEnvironmentVariable("FF_K8S_CONTEXT") is { Length: > 0 } c ? c : null;

    static string? KubeConfig => Environment.GetEnvironmentVariable("FF_KUBECONFIG") is { Length: > 0 } k ? k : null;

    public static string NodeAddress { get; private set; } = "";

    /// <summary>A port listening on this machine, which a k3d cluster's pods reach as <c>host.k3d.internal</c>: the worker's host.</summary>
    public static int HostPort => ((System.Net.IPEndPoint)Host.LocalEndpoint).Port;

    static System.Net.Sockets.TcpListener Listen()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, 0);
        listener.Start();
        return listener;
    }

    static async Task<IKubernetes> SetUpAsync()
    {
        var client = new k8s.Kubernetes(KubernetesClientConfiguration.BuildConfigFromConfigFile(kubeconfigPath: KubeConfig, currentContext: Context));
        foreach (var ns in new[] { KubernetesSandboxContractTests.Namespace, KubernetesSandboxContractTests.WorkerNamespace })
            await Ignore409(() => client.CoreV1.CreateNamespaceAsync(new V1Namespace { Metadata = new V1ObjectMeta { Name = ns } }));
        var manifest = Path.Combine(AppContext.BaseDirectory, "sandbox-network-policy.yaml");
        var policy = KubernetesYaml.Deserialize<V1NetworkPolicy>(await File.ReadAllTextAsync(manifest));
        await client.NetworkingV1.DeleteNamespacedNetworkPolicyAsync(policy.Metadata.Name, KubernetesSandboxContractTests.Namespace).ContinueWith(_ => { });
        await client.NetworkingV1.CreateNamespacedNetworkPolicyAsync(policy, KubernetesSandboxContractTests.Namespace);
        await StandInAsync(client, KubernetesSandboxContractTests.WorkerNamespace, "agent-worker", "feature-flow-agent-worker", 8471);
        await StandInAsync(client, KubernetesSandboxContractTests.Namespace, "store", "store", 5432);
        var nodes = await client.CoreV1.ListNodeAsync();
        NodeAddress = nodes.Items[0].Status.Addresses.First(a => a.Type == "InternalIP").Address;
        await PolicyEnforcedAsync(client);
        return client;
    }

    /// <summary>
    /// A network policy takes effect a moment after it is applied (a deployment applies it before the agent worker starts).
    /// Waits, at most a minute, until a sandbox-labelled probe is refused by the stand-in store and reaches the stand-in
    /// worker, so the tests see the policy in force (review R2 re-check, F2).
    /// </summary>
    static async Task PolicyEnforcedAsync(IKubernetes client)
    {
        var probe = new KubernetesSandbox(client, KubernetesSandboxContractTests.Namespace, KubernetesSandboxContractTests.Image, "node", "1", "1Gi",
            () => throw new InvalidOperationException("the probe copies nothing"));
        var key = "contract/policy-probe-" + Guid.NewGuid().ToString("N");
        var handle = (await probe.CreateAsync(new SandboxSpec(key, "/ff/home", new Dictionary<string, string>(), []), CancellationToken.None)).Unwrap();
        try
        {
            async Task<bool> Connects(string host, int port) =>
                (await probe.RunAsync(handle, new SandboxCommand(["bash", "-c", $"timeout 3 bash -c '</dev/tcp/{host}/{port}'"], AsRoot: true),
                    $"{key}/{Guid.NewGuid():N}", CancellationToken.None)).Unwrap().Exit == 0;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (!await Connects($"store.{KubernetesSandboxContractTests.Namespace}.svc.cluster.local", 5432)
                    && await Connects($"agent-worker.{KubernetesSandboxContractTests.WorkerNamespace}.svc.cluster.local", 8471))
                    return;
                await Task.Delay(1000);
            }

            throw new InvalidOperationException("the sandbox network policy was not in force within a minute of being applied");
        }
        finally
        {
            await probe.DisposeAsync(handle, CancellationToken.None);
        }
    }

    /// <summary>A pod listening on a port, with a service of the same name, labelled <paramref name="app"/>.</summary>
    static async Task StandInAsync(IKubernetes client, string ns, string name, string app, int port)
    {
        var labels = new Dictionary<string, string> { ["app.kubernetes.io/name"] = app };
        await Ignore409(() => client.CoreV1.CreateNamespacedPodAsync(new V1Pod
        {
            Metadata = new V1ObjectMeta { Name = name, Labels = labels },
            Spec = new V1PodSpec
            {
                AutomountServiceAccountToken = false,
                TerminationGracePeriodSeconds = 0,
                Containers =
                [
                    new V1Container
                    {
                        Name = name, Image = KubernetesSandboxContractTests.Image, ImagePullPolicy = "IfNotPresent",
                        Command = ["node", "-e", $"require('net').createServer(s => s.end()).listen({port})"],
                        ReadinessProbe = new V1Probe { TcpSocket = new V1TCPSocketAction { Port = port } },
                    },
                ],
            },
        }, ns));
        await Ignore409(() => client.CoreV1.CreateNamespacedServiceAsync(new V1Service
        {
            Metadata = new V1ObjectMeta { Name = name },
            Spec = new V1ServiceSpec { Selector = labels, Ports = [new V1ServicePort { Port = port, TargetPort = port }] },
        }, ns));
        for (var i = 0; i < 240; i++)
        {
            var pod = await client.CoreV1.ReadNamespacedPodAsync(name, ns);
            if (pod.Status?.ContainerStatuses?.FirstOrDefault()?.Ready == true)
                return;
            await Task.Delay(500);
        }

        throw new InvalidOperationException($"the stand-in {name} did not become ready");
    }

    static async Task Ignore409<T>(Func<Task<T>> create)
    {
        try
        {
            await create();
        }
        catch (HttpOperationException e) when (e.Response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
        }
    }
}
