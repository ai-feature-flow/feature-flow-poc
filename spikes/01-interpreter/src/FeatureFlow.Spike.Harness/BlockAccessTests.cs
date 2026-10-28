using System.Net;
using System.Net.Sockets;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using Temporalio.Client;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Review 12 B1: the outage test killed the dev server, and the dev server's
/// SQLite file does not always survive that. This is the **block access**
/// variant docs/08 2.1 allows: drop traffic to the Temporal port for several
/// minutes. It is the enterprise case — a network partition — and it avoids the
/// dev server's durability problem entirely, because the server keeps running.
///
/// The harness runs a local TCP proxy on a spare port that forwards to the real
/// Temporal port, and the worker is pointed at the proxy. Closing the proxy's
/// listener simulates the partition without touching the server.
/// </summary>
public static class BlockAccessTests
{
    private const string RealAddress = "127.0.0.1:7233";

    public static async Task<int> RunAsync(ITemporalClient client, string queue, string[] args)
    {
        var outageSeconds = 0;
        foreach (var i in Enumerable.Range(0, args.Length - 1))
        {
            if (args[i] == "--seconds")
                int.TryParse(args[i + 1], out outageSeconds);
        }
        if (outageSeconds <= 0)
            outageSeconds = 120;

        Console.WriteLine($"BLOCK: Temporal unreachable for {outageSeconds}s " +
                          $"via a local proxy (server keeps running)");

        // Start the proxy on a spare port.
        var proxy = new PortProxy(RealAddress);
        var proxyPort = await proxy.StartAsync();
        Console.WriteLine($"BLOCK: proxy listening on 127.0.0.1:{proxyPort} " +
                          $"-> {RealAddress}");

        // Start a worker pointed at the proxy, on its own queue.
        var workerQueue = queue + "-blocked";
        var worker = await StartWorkerAsync(workerQueue, $"http://127.0.0.1:{proxyPort}");
        if (!worker)
        {
            Console.WriteLine("BLOCK: FAIL could not start a worker on the proxy");
            proxy.Stop();
            return 1;
        }

        var plan = LongAgentPlan();
        var id = $"block-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        var handle = await client.StartWorkflowAsync(
            (InterpreterWorkflow w) => w.RunAsync(new InterpreterInput { Plan = plan }),
            new(id, workerQueue));

        // Wait until the step is in flight.
        var inFlight = false;
        for (var i = 0; i < 60; i++)
        {
            await Task.Delay(500);
            try
            {
                var s = await handle.QueryAsync(w => w.State());
                if (s.Cursors.Contains("block_agent"))
                {
                    inFlight = true;
                    break;
                }
            }
            catch
            {
                // Not started yet.
            }
        }

        if (!inFlight)
        {
            Console.WriteLine("BLOCK: FAIL the step never went in flight");
            proxy.Stop();
            return 1;
        }

        Console.WriteLine("BLOCK: step in flight; blocking access to Temporal");
        var downAt = DateTimeOffset.UtcNow;
        proxy.Block();

        await Task.Delay(TimeSpan.FromSeconds(outageSeconds));

        Console.WriteLine("BLOCK: restoring access");
        proxy.Unblock();

        // Wait for the run to finish.
        string result;
        try
        {
            result = await handle.GetResultAsync<string>(
                followRuns: true,
                new RpcOptions { Timeout = TimeSpan.FromSeconds(300) });
        }
        catch (Exception ex)
        {
            Console.WriteLine($"BLOCK: FAIL run did not complete: {ex.GetType().Name}: {ex.Message}");
            proxy.Stop();
            return 1;
        }

        var state = await handle.QueryAsync(w => w.State());
        var outcome = state.StepOutcomes.TryGetValue("block_agent", out var r) ? r.Outcome : null;
        var detail = RecordStore.Read(id)
            .Where(e => e.Kind == "step_run")
            .Select(e => e.PayloadAs<StepRunDetail>()).Where(d => d is not null).Select(d => d!)
            .LastOrDefault(d => d.StepId == "block_agent");

        Console.WriteLine($"BLOCK: result={result} outcome={outcome} " +
                          $"attempts={detail?.Attempts} " +
                          $"infrastructureFailures={detail?.InfrastructureFailures} " +
                          $"downFor={(DateTimeOffset.UtcNow - downAt).TotalSeconds:F0}s");

        // The criterion: no step failed because the platform was unreachable.
        var noStepFailed = outcome == Outcomes.Completed;
        var outageWasSeen = (detail?.InfrastructureFailures ?? 0) > 0;
        var outageWasFree = (detail?.Attempts ?? 99) == 1;

        Console.WriteLine($"BLOCK: noStepFailed={noStepFailed} outageWasSeen={outageWasSeen} " +
                          $"outageWasFree={outageWasFree}");

        var ok = noStepFailed && outageWasSeen && outageWasFree;
        Console.WriteLine($"BLOCK: {(ok ? "PASS" : "FAIL")}");

        proxy.Stop();
        return ok ? 0 : 1;
    }

    private static Plan LongAgentPlan()
    {
        var plan = new Plan { Entry = "block_agent", Revision = 1 };
        plan.Steps.Add(new Step
        {
            Id = "block_agent",
            Kind = "agent.run",
            Protected = true,
            SimulatedDuration = "300",
            PollsInActivity = true,
            Limits = new StepLimits { Attempts = 3, StartToClose = "PT4H", Heartbeat = "PT30S" },
            Routes = new(StringComparer.Ordinal)
            {
                [Outcomes.Completed] = RouteTarget.ToEnd(Outcomes.Completed),
                [Outcomes.Blocked] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.Failed] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.AttemptsExhausted] = RouteTarget.ToEnd(Outcomes.Failed),
                [Outcomes.ReplanRequested] = RouteTarget.ToEnd(Outcomes.Failed),
            },
        });
        return plan;
    }

    private static async Task<bool> StartWorkerAsync(string queue, string address)
    {
        var root = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var workerDir = Path.Combine(root, "src", "FeatureFlow.Spike.Worker");
        if (!Directory.Exists(workerDir))
            return false;

        var psi = new System.Diagnostics.ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            WorkingDirectory = workerDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("run");
        psi.ArgumentList.Add("--no-build");
        psi.ArgumentList.Add("--");
        psi.ArgumentList.Add("--queue");
        psi.ArgumentList.Add(queue);
        psi.ArgumentList.Add("--sandbox");
        psi.ArgumentList.Add("http://127.0.0.1:5199");
        psi.ArgumentList.Add("--address");
        psi.ArgumentList.Add(address.Replace("http://", ""));

        System.Diagnostics.Process.Start(psi);
        await Task.Delay(TimeSpan.FromSeconds(8));
        return true;
    }
}

/// <summary>
/// A minimal TCP forwarder that can be blocked and unblocked on demand. This is
/// the network partition: the server stays up, the worker cannot reach it.
/// </summary>
public sealed class PortProxy
{
    private readonly string targetHost;
    private readonly int targetPort;
    private TcpListener? listener;
    private volatile bool blocked;
    private readonly CancellationTokenSource cts = new();
    private readonly List<TcpClient> openConnections = new();

    public PortProxy(string target)
    {
        var parts = target.Split(':');
        targetHost = parts[0];
        targetPort = int.Parse(parts[1]);
    }

    public async Task<int> StartAsync()
    {
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
        await Task.CompletedTask;
        return port;
    }

    public void Block() => blocked = true;

    public void Unblock() => blocked = false;

    public void Stop()
    {
        cts.Cancel();
        lock (openConnections)
        {
            foreach (var c in openConnections)
                c.Dispose();
            openConnections.Clear();
        }
        listener?.Stop();
    }

    private async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener!.AcceptTcpClientAsync(cts.Token);
            }
            catch
            {
                return;
            }

            lock (openConnections)
            {
                openConnections.Add(client);
            }

            _ = Task.Run(() => ForwardAsync(client));
        }
    }

    private async Task ForwardAsync(TcpClient client)
    {
        try
        {
            // While blocked, accept but never connect: the client sees a
            // connection that goes nowhere, which is a partition rather than a
            // refusal.
            while (blocked && !cts.IsCancellationRequested)
                await Task.Delay(200, cts.Token);

            using var upstream = new TcpClient();
            await upstream.ConnectAsync(targetHost, targetPort, cts.Token);

            using var downstream = client;
            var up = PumpAsync(downstream.GetStream(), upstream.GetStream());
            var down = PumpAsync(upstream.GetStream(), downstream.GetStream());
            await Task.WhenAll(up, down);
        }
        catch
        {
            // The partition or shutdown closes connections; that is expected.
        }
    }

    private async Task PumpAsync(NetworkStream from, NetworkStream to)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var read = await from.ReadAsync(buffer, cts.Token);
                if (read == 0)
                    return;
                if (blocked)
                {
                    await Task.Delay(200, cts.Token);
                    continue;
                }
                await to.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                await to.FlushAsync(cts.Token);
            }
        }
        catch
        {
            // Connection closed.
        }
    }
}
