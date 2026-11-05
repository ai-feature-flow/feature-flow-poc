using System.Net;
using System.Text;
using FeatureFlow.Providers.Abstractions;
using k8s;
using k8s.Autorest;
using k8s.Models;

namespace FeatureFlow.Providers.Kubernetes;

/// <summary>
/// The Kubernetes pod sandbox (docs/06 section 3.1; the team profile). One pod per sandbox, named from the step's key, so a
/// new worker finds it again (docs/02 section 4.3); commands and copies are <see cref="ShellSandbox"/>'s, over the API's exec.
/// <para>
/// The pod has CPU and memory limits, no service account token and no service environment variables, so it holds no
/// credential to the cluster and no list of its services (F6). Its <c>/ff</c> folder is an <c>emptyDir</c>, so a restarted
/// container keeps the step's work and its commands read as lost, as a restarted Docker container's do. Its network is the
/// <see cref="NetworkPolicyName"/> policy's, which selects every sandbox pod by <see cref="AppLabel"/>: the agent worker and
/// the internet, nothing else in the cluster or on its nodes (M4 follow-up 12). <see cref="CheckAsync"/> refuses to start
/// without that policy, so a sandbox is never created with an open network.
/// </para>
/// </summary>
public sealed class KubernetesSandbox(IKubernetes client, string @namespace, string image, string user, string cpu, string memory,
    Func<IArtifactStoreProvider> store) : ShellSandbox(user, store)
{
    public const string AppLabel = "feature-flow-sandbox";
    public const string NetworkPolicyName = "feature-flow-sandbox";
    public const string KeyAnnotation = "feature-flow.sandbox/key";
    const string Container = "sandbox";

    public override ProviderDescription Describe() => new("sandbox", "kubernetes", "0.1.0", new SandboxCapabilities(["linux"], false, true, true, true));

    public override async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        var policy = await Call(() => client.NetworkingV1.ReadNamespacedNetworkPolicyAsync(NetworkPolicyName, @namespace, cancellationToken: cancellationToken));
        if (policy.Error?.Reason == "not_found")
            return ProviderResult<Unit>.Failure("not_found",
                $"the network policy {NetworkPolicyName} is not in namespace {@namespace}: apply sandbox-network-policy.yaml before any sandbox is created");
        return policy.Ok ? ProviderResult<Unit>.Success(default) : new ProviderResult<Unit>(default, policy.Error);
    }

    public override async Task<ProviderResult<SandboxHandle>> CreateAsync(SandboxSpec spec, CancellationToken cancellationToken)
    {
        var name = NameOf(spec.Key);
        var existing = await AttachAsync(spec.Key, cancellationToken);
        if (existing.Ok || existing.Error!.Reason != "not_found")
            return existing;
        var created = await Call(() => client.CoreV1.CreateNamespacedPodAsync(Pod(name, spec), @namespace, cancellationToken: cancellationToken));
        if (!created.Ok && created.Error!.Reason != "conflict")
            return new ProviderResult<SandboxHandle>(null, created.Error);
        var handle = new SandboxHandle(name, "kubernetes");
        if (await RunningAsync(name, cancellationToken) is { } notRunning)
            return notRunning.Convert<SandboxHandle>();
        if (await PrepareAsync(handle, spec, cancellationToken) is { } failed)
            return failed.Convert<SandboxHandle>();
        return ProviderResult<SandboxHandle>.Success(handle);
    }

    V1Pod Pod(string name, SandboxSpec spec) => new()
    {
        Metadata = new V1ObjectMeta
        {
            Name = name,
            Labels = new Dictionary<string, string> { ["app.kubernetes.io/name"] = AppLabel },
            Annotations = new Dictionary<string, string> { [KeyAnnotation] = spec.Key },
        },
        Spec = new V1PodSpec
        {
            AutomountServiceAccountToken = false,
            EnableServiceLinks = false,
            RestartPolicy = "Always",
            TerminationGracePeriodSeconds = 5,
            Containers =
            [
                new V1Container
                {
                    Name = Container,
                    Image = image,
                    ImagePullPolicy = "IfNotPresent",
                    Command = ["tini", "--", "sleep", "infinity"],
                    Env = spec.Environment.Select(kv => new V1EnvVar { Name = kv.Key, Value = kv.Value }).Prepend(new V1EnvVar { Name = "HOME", Value = spec.Home }).ToList(),
                    Resources = new V1ResourceRequirements
                    {
                        Limits = new Dictionary<string, ResourceQuantity> { ["cpu"] = new(cpu), ["memory"] = new(memory) },
                        Requests = new Dictionary<string, ResourceQuantity> { ["cpu"] = new("100m"), ["memory"] = new(memory) },
                    },
                    VolumeMounts = [new V1VolumeMount { Name = "ff", MountPath = "/ff" }],
                },
            ],
            Volumes = [new V1Volume { Name = "ff", EmptyDir = new V1EmptyDirVolumeSource() }],
        },
    };

    /// <summary>Waits for the pod's container to run; a pod that cannot (an image that does not pull) is a server error, after the create timeout.</summary>
    async Task<ProviderResult<Unit>?> RunningAsync(string name, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(5);
        while (true)
        {
            var read = await Call(() => client.CoreV1.ReadNamespacedPodAsync(name, @namespace, cancellationToken: cancellationToken));
            if (!read.Ok)
                return new ProviderResult<Unit>(default, read.Error);
            var pod = read.Value!;
            if (pod.Status?.Phase == "Running" && pod.Status.ContainerStatuses?.FirstOrDefault()?.Ready == true)
                return null;
            if (pod.Status?.Phase is "Failed" or "Succeeded")
                return ProviderResult<Unit>.Failure("server_error", $"the sandbox pod {name} ended: {pod.Status.Reason} {pod.Status.Message}");
            if (DateTimeOffset.UtcNow > deadline)
            {
                var waiting = pod.Status?.ContainerStatuses?.FirstOrDefault()?.State?.Waiting;
                return ProviderResult<Unit>.Failure("timeout", $"the sandbox pod {name} is not running: {waiting?.Reason} {waiting?.Message}");
            }

            await Task.Delay(500, cancellationToken);
        }
    }

    public override async Task<ProviderResult<SandboxHandle>> AttachAsync(string key, CancellationToken cancellationToken)
    {
        var name = NameOf(key);
        var read = await Call(() => client.CoreV1.ReadNamespacedPodAsync(name, @namespace, cancellationToken: cancellationToken));
        if (read.Error?.Reason == "not_found" || read.Value?.Metadata.DeletionTimestamp is not null)
            return ProviderResult<SandboxHandle>.Failure("not_found", $"no sandbox for {key}");
        if (!read.Ok)
            return new ProviderResult<SandboxHandle>(null, read.Error);
        if (await RunningAsync(name, cancellationToken) is { } notRunning)
            return notRunning.Convert<SandboxHandle>();
        return ProviderResult<SandboxHandle>.Success(new SandboxHandle(name, "kubernetes"));
    }

    public override async Task<ProviderResult<Unit>> DisposeAsync(SandboxHandle sandbox, CancellationToken cancellationToken)
    {
        var deleted = await Call(() => client.CoreV1.DeleteNamespacedPodAsync(sandbox.Id, @namespace, gracePeriodSeconds: 0, cancellationToken: cancellationToken));
        return deleted.Ok || deleted.Error!.Reason == "not_found" ? ProviderResult<Unit>.Success(default) : new ProviderResult<Unit>(default, deleted.Error);
    }

    protected override async Task<ProviderResult<ShellRun>> ExecAsync(SandboxHandle sandbox, string script, Stream? input, CancellationToken cancellationToken)
    {
        var output = new MemoryStream();
        var error = new MemoryStream();
        var exit = await Call(() => client.NamespacedPodExecAsync(sandbox.Id, @namespace, Container, ["sh", "-c", script], false, async (stdIn, stdOut, stdErr) =>
        {
            if (input is not null)
            {
                await input.CopyToAsync(stdIn, cancellationToken);
                await stdIn.FlushAsync(cancellationToken);
            }

            await Task.WhenAll(stdOut.CopyToAsync(output, cancellationToken), stdErr.CopyToAsync(error, cancellationToken));
        }, cancellationToken));
        return exit.Ok
            ? ProviderResult<ShellRun>.Success(new ShellRun(exit.Value, output.ToArray(), Encoding.UTF8.GetString(error.ToArray())))
            : new ProviderResult<ShellRun>(null, exit.Error);
    }

    /// <summary>Calls the API, classifying its failures (docs/06 section 1.1).</summary>
    static async Task<ProviderResult<T>> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return ProviderResult<T>.Success(await call());
        }
        catch (HttpOperationException e)
        {
            var reason = e.Response.StatusCode switch
            {
                HttpStatusCode.NotFound => "not_found",
                HttpStatusCode.Conflict => "conflict",
                HttpStatusCode.Unauthorized => "unauthenticated",
                HttpStatusCode.Forbidden => "forbidden",
                HttpStatusCode.TooManyRequests => "rate_limited",
                HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest => "validation_failed",
                _ => (int)e.Response.StatusCode >= 500 ? "server_error" : "unexpected_response",
            };
            return ProviderResult<T>.Failure(reason, $"the Kubernetes API answered {(int)e.Response.StatusCode}: {e.Response.Content}");
        }
        catch (HttpRequestException e)
        {
            return ProviderResult<T>.Failure("unreachable", "the Kubernetes API cannot be reached: " + e.Message);
        }
        catch (System.Net.WebSockets.WebSocketException e)
        {
            return ProviderResult<T>.Failure("unreachable", "the sandbox cannot be reached: " + e.Message);
        }
        catch (KubernetesException e)
        {
            return ProviderResult<T>.Failure("server_error", "the sandbox command could not run: " + e.Message);
        }
    }
}

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class KubernetesProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("sandbox", "kubernetes", ["image", "user", "namespace", "cpu", "memory", "context"], c => new KubernetesSandbox(
            new k8s.Kubernetes(c.Setting("context") is null && KubernetesClientConfiguration.IsInCluster()
                ? KubernetesClientConfiguration.InClusterConfig()
                : KubernetesClientConfiguration.BuildConfigFromConfigFile(currentContext: c.Setting("context"))),
            c.Setting("namespace") ?? "default",
            c.RequiredSetting("image"),
            c.Setting("user") ?? "node",
            c.Setting("cpu") ?? "2",
            c.Setting("memory") ?? "4Gi",
            () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("the Kubernetes sandbox needs an artifact store"))),
    ];
}
