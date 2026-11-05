using System.Diagnostics;
using System.Text;
using IOPath = System.IO.Path;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// The three hosts as the processes an image runs (M6 follow-up 7): each starts against the Temporal dev server and, on
/// SIGTERM, stops its workers and endpoints and exits 0, as a pod's container must when Kubernetes stops it.
/// </summary>
[Trait("Category", "Integration")]
public class HostProcessTests
{
    [Theory]
    [InlineData("Api")]
    [InlineData("OrchestrationWorker")]
    [InlineData("AgentWorker")]
    public async Task A_host_stops_cleanly_on_SIGTERM(string host)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGTERM is a POSIX signal");
        var folder = IOPath.Combine(IOPath.GetTempPath(), $"ff-it-host-{Guid.NewGuid():N}"[..32]);
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(IOPath.Combine(folder, "secrets.json"), """{ "api_token": "host-test", "step_tokens": "host-test-key" }""", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(IOPath.Combine(folder, "installation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: installation
            temporal: { address: "{{Harness.TemporalAddress}}", namespace: default, task_queue: ff-host-{{Guid.NewGuid():N}} }
            providers:
              artifact_store: { default: filesystem, instances: { filesystem: { root: artifacts } } }
              record_store: { default: sqlite, instances: { sqlite: { path: records.db } } }
              secrets: { default: file, instances: { file: { path: secrets.json } } }
            agent_worker: { listen: "http://127.0.0.1:0", urls: ["http://127.0.0.1:1"], token_key_ref: secrets/step_tokens }
            api:
              auth: { mode: local_token, token_ref: secrets/api_token, actor: tester, roles: [admin] }
            """, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(IOPath.Combine(folder, "organisation.yaml"), $$"""
            api_version: feature-flow/v1
            kind: organisation
            name: host-test
            libraries:
              - { name: std, source: directory, path: "{{IOPath.Combine(Harness.RepositoryRoot, "library", "std")}}" }
            """, TestContext.Current.CancellationToken);
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = folder };
        start.ArgumentList.Add(HostDll(host));
        start.ArgumentList.Add("--installation");
        start.ArgumentList.Add("installation.yaml");
        start.ArgumentList.Add("--organisation");
        start.ArgumentList.Add("organisation.yaml");
        start.Environment["FF_API_LISTEN"] = "http://127.0.0.1:0";
        using var process = Process.Start(start)!;
        var output = new StringBuilder();
        var started = new TaskCompletionSource();
        process.OutputDataReceived += (_, e) =>
        {
            lock (output)
                output.AppendLine(e.Data);
            if (e.Data == "Started")
                started.TrySetResult();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            lock (output)
                output.AppendLine(e.Data);
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            var (sent, _) = await AgentWorld.RunAsync("kill", ["-TERM", process.Id.ToString()]);
            Assert.Equal(0, sent);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(0, process.ExitCode);
            lock (output)
                Assert.Contains("Stopped", output.ToString());
        }
        catch (Exception e) when (e is OperationCanceledException or TimeoutException)
        {
            lock (output)
                Assert.Fail($"the {host} host did not start, or did not stop on SIGTERM:\n{output}");
        }
        finally
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The host's built assembly, beside this test assembly's configuration (Debug or Release).</summary>
    static string HostDll(string host)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = IOPath.Combine(Harness.RepositoryRoot, "hosts", $"FeatureFlow.Host.{host}", "bin", configuration, "net10.0", $"FeatureFlow.Host.{host}.dll");
        Assert.True(File.Exists(dll), $"{dll} is not built");
        return dll;
    }
}
