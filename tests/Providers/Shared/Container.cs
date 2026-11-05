using System.Diagnostics;
using System.Globalization;

namespace FeatureFlow.Providers.Tests;

/// <summary>
/// A throwaway container started with the Docker CLI for a provider's integration tests (docs/11 section 4.1), so CI
/// needs no services: <c>docker run -d --rm</c> with one port published on a free loopback port, removed on dispose.
/// </summary>
public sealed class Container : IAsyncDisposable
{
    readonly string id;

    Container(string id, string address)
    {
        this.id = id;
        Address = address;
    }

    /// <summary><c>127.0.0.1:&lt;port&gt;</c>, where the container's port is published.</summary>
    public string Address { get; }

    public int Port => int.Parse(Address[(Address.LastIndexOf(':') + 1)..], CultureInfo.InvariantCulture);

    public static async Task<Container> StartAsync(string image, int port, string[] options, params string[] command)
    {
        var id = (await DockerAsync(["run", "-d", "--rm", "-p", $"127.0.0.1::{port}", .. options, image, .. command])).Trim();
        var address = (await DockerAsync(["port", id, port.ToString(CultureInfo.InvariantCulture)])).Split('\n')[0].Trim();
        return new Container(id, address);
    }

    public async ValueTask DisposeAsync() => await RunAsync(["rm", "-f", id]);

    /// <summary>A command's standard output. A pull's progress goes to standard error, so it never mixes with a container id.</summary>
    static async Task<string> DockerAsync(string[] args)
    {
        var (exit, output, error) = await RunAsync(args);
        return exit == 0 ? output : throw new InvalidOperationException($"docker {args[0]} failed: {error.Trim()}");
    }

    static async Task<(int Exit, string Output, string Error)> RunAsync(string[] args)
    {
        var start = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output, await error);
    }
}
