using System.Diagnostics;
using Temporalio.Client;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// Shows that the integration environment is present: a Temporal dev server and Docker.
/// Without these, an integration run with no other tests would pass whatever the environment.
/// </summary>
[Trait("Category", "Integration")]
public class EnvironmentTests
{
    static string TemporalAddress =>
        Environment.GetEnvironmentVariable("FF_TEMPORAL_ADDRESS") is { Length: > 0 } address ? address : "localhost:7233";

    [Fact]
    public async Task Temporal_dev_server_answers()
    {
        var client = await TemporalClient.ConnectAsync(new(TemporalAddress));

        var info = await client.Connection.WorkflowService.GetSystemInfoAsync(new());

        Assert.False(string.IsNullOrEmpty(info.ServerVersion));
    }

    [Fact]
    public async Task Docker_daemon_answers()
    {
        using var docker = Process.Start(new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var output = await docker.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await docker.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(docker.ExitCode == 0, await docker.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken));
        Assert.False(string.IsNullOrWhiteSpace(output));
    }
}
