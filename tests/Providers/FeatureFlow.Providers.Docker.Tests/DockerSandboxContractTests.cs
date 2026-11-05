using System.Net;
using System.Net.Sockets;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Contracts;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.Docker.Tests;

/// <summary>
/// The sandbox contract against the Docker sandbox. The agent worker is a listener on this machine, reached as
/// <c>host.docker.internal</c>; the forbidden endpoints stand for what else runs beside it: another port of the same host
/// (a store, Temporal), a service bound to the host's loopback, and a neighbouring container on the Docker network.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DockerSandboxContractTests : SandboxContract
{
    public const string Image = "feature-flow-sandbox:m8";

    readonly TcpListener worker = Listen(IPAddress.Any);
    readonly TcpListener store = Listen(IPAddress.Any);
    readonly TcpListener loopback = Listen(IPAddress.Loopback);
    readonly string folder = Directory.CreateTempSubdirectory("ff-docker-contract-").FullName;
    readonly DockerSandbox sandboxes;
    readonly FileSystemArtifactStore artifacts;
    string neighbour = "";
    string neighbourAddress = "";

    public DockerSandboxContractTests()
    {
        artifacts = new FileSystemArtifactStore(folder);
        sandboxes = new DockerSandbox(Image, "node", () => artifacts, reach: [Reachable]);
    }

    static TcpListener Listen(IPAddress address)
    {
        var listener = new TcpListener(address, 0);
        listener.Start();
        return listener;
    }

    static int Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port;

    protected override ISandboxProvider Sandboxes => sandboxes;
    protected override IArtifactStoreProvider Store => artifacts;
    protected override Uri Reachable => new($"http://host.docker.internal:{Port(worker)}");

    protected override IReadOnlyList<Uri> Forbidden =>
    [
        new($"http://host.docker.internal:{Port(store)}"),
        new($"http://host.docker.internal:{Port(loopback)}"),
        new($"http://{neighbourAddress}:8080"),
    ];

    public override async ValueTask InitializeAsync()
    {
        await DockerImage.EnsureAsync(Image);
        neighbour = "ff-contract-neighbour-" + Guid.NewGuid().ToString("N")[..12];
        var cli = new DockerCli();
        var started = (await cli.RunAsync(["run", "-d", "--rm", "--name", neighbour, Image, "node", "-e", "require('net').createServer(s=>s.end()).listen(8080)"], Ct)).Unwrap();
        Assert.True(started.Exit == 0, started.Error);
        neighbourAddress = (await cli.RunAsync(["inspect", "--format", "{{range .NetworkSettings.Networks}}{{.IPAddress}}{{end}}", neighbour], Ct)).Unwrap().Text.Trim();
        await base.InitializeAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        await new DockerCli().RunAsync(["rm", "-f", neighbour], CancellationToken.None);
        worker.Stop();
        store.Stop();
        loopback.Stop();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public async Task The_neighbouring_container_is_reachable_without_the_rules()
    {
        // The control for the forbidden endpoints: a container on the same network with no rules of its own reaches them.
        var cli = new DockerCli();
        var probe = await cli.RunAsync(["run", "--rm", "--add-host", "host.docker.internal:host-gateway", Image, "bash", "-c",
            $"timeout 8 bash -c '</dev/tcp/{neighbourAddress}/8080' && timeout 8 bash -c '</dev/tcp/host.docker.internal/{Port(store)}'"], Ct);

        Assert.Equal(0, probe.Unwrap().Exit);
    }
}

/// <summary>The sandbox image, built from the repository's Dockerfile when it is not present.</summary>
public static class DockerImage
{
    static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task EnsureAsync(string image)
    {
        await Gate.WaitAsync();
        try
        {
            var cli = new DockerCli();
            if ((await cli.RunAsync(["image", "inspect", image], CancellationToken.None)).Unwrap().Exit == 0)
                return;
            var folder = Path.Combine(Root(), "src", "Providers", "FeatureFlow.Providers.Docker");
            var built = (await cli.RunAsync(["build", "-q", "-t", image, "-f", Path.Combine(folder, "sandbox.Dockerfile"), folder], CancellationToken.None,
                timeout: TimeSpan.FromMinutes(20))).Unwrap();
            Assert.True(built.Exit == 0, "the sandbox image did not build: " + built.Error);
        }
        finally
        {
            Gate.Release();
        }
    }

    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
    }
}
