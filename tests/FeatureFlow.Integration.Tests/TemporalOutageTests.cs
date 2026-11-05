using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IOPath = System.IO.Path;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// Failure injection, "Temporal unavailable" (docs/09 section 4.2): the engine's workers retry the connection, the API
/// reports a run from the record store while Temporal is down, and a run is never failed because the platform is down
/// (docs/02 section 7). The test runs its own dev server, on its own port and database file, so it can stop it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TemporalOutageTests
{
    static string Library => IOPath.Combine(Harness.TestLibraries, "m7s");

    [Fact]
    public async Task A_run_waiting_through_a_Temporal_outage_is_not_failed_and_completes_once_Temporal_is_back()
    {
        await using var temporal = await DevServer.StartAsync();
        await using var h = await Harness.StartAsync("outage", libraries: [Library], temporalAddress: temporal.Address);
        var id = h.Start("m7s:templates/decide");
        var wait = await h.WaitForWaitAsync(id, TimeSpan.FromSeconds(60));
        Assert.NotNull(wait);

        temporal.Stop();
        // Long enough for the workers' polls to fail and back off.
        await Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal("running", (await h.ShowAsync(id))["run"]!["status"]!.ToString());
        // Temporal is really down: an answer now cannot reach the run.
        Assert.NotEqual(0, h.Cli("answer", id, wait!, "approved").Exit);
        await temporal.RestartAsync();

        // The answer is an update the workflow worker must take: it is accepted only once that worker polls again.
        var (exit, _, error) = await AnswerAsync(h, id, wait!);
        Assert.True(exit == 0, error);
        var (waited, end, waitError) = h.Cli("wait", id, "--timeout", "120");
        Assert.True((waited, end.Trim()) == (0, $"run {id} completed"), end + waitError);
    }

    /// <summary>The CLI's answer, tried again while the restarted server is still coming up (its frontend refuses at first).</summary>
    static async Task<(int Exit, string Output, string Error)> AnswerAsync(Harness h, string id, string wait)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            var result = h.Cli("answer", id, wait, "approved");
            if (result.Exit == 0 || DateTime.UtcNow > deadline)
                return result;
            await Task.Delay(1000);
        }
    }

    /// <summary>A Temporal CLI dev server keeping its state in a file, so it can be stopped and started again with its runs.</summary>
    sealed class DevServer : IAsyncDisposable
    {
        readonly string folder = Directory.CreateTempSubdirectory("ff-it-temporal-").FullName;
        readonly int port = FreePort();
        Process? process;

        public string Address => $"127.0.0.1:{port}";

        public static async Task<DevServer> StartAsync()
        {
            var server = new DevServer();
            await server.RestartAsync();
            return server;
        }

        public async Task RestartAsync()
        {
            var start = new ProcessStartInfo(Executable())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "server", "start-dev", "--headless", "--ip", "127.0.0.1", "--port", port.ToString(), "--ui-port", FreePort().ToString(),
                         "--db-filename", IOPath.Combine(folder, "temporal.db"), "--log-level", "error" })
                start.ArgumentList.Add(a);
            process = Process.Start(start)!;
            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await Temporalio.Client.TemporalClient.ConnectAsync(new(Address));
                    return;
                }
                catch (Exception) when (!process.HasExited)
                {
                    await Task.Delay(500);
                }
            }

            throw new InvalidOperationException($"the Temporal dev server on {Address} did not start");
        }

        public void Stop()
        {
            process!.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        public ValueTask DisposeAsync()
        {
            if (process is { HasExited: false })
                Stop();
            Directory.Delete(folder, recursive: true);
            return ValueTask.CompletedTask;
        }

        /// <summary>The Temporal CLI on the path, else where its installer puts it.</summary>
        static string Executable()
        {
            var installed = IOPath.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".temporalio", "bin", "temporal");
            return File.Exists(installed) ? installed : "temporal";
        }

        static int FreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
