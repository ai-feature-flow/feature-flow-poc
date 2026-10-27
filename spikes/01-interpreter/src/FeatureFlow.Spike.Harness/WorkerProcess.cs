using System.Diagnostics;
using System.Text;

namespace FeatureFlow.Spike.Harness;

/// <summary>
/// Starts and kills the spike worker as a separate OS process. Killing the
/// worker is the point of exit criterion 1, so the harness owns its lifecycle
/// rather than leaving it to a shell script.
/// </summary>
public sealed class WorkerProcess : IAsyncDisposable
{
    private readonly string projectDir;
    private readonly string queue;
    private readonly string sandboxUrl;
    private readonly string address;
    private readonly int maxConcurrentActivities;
    private readonly string logPath;
    private Process? process;

    public WorkerProcess(
        string projectDir,
        string queue,
        string sandboxUrl,
        string address,
        int maxConcurrentActivities,
        string logPath)
    {
        this.projectDir = projectDir;
        this.queue = queue;
        this.sandboxUrl = sandboxUrl;
        this.address = address;
        this.maxConcurrentActivities = maxConcurrentActivities;
        this.logPath = logPath;
    }

    public int? Pid => process?.Id;

    /// <summary>Starts the worker and waits until it prints "WORKER ready".</summary>
    public async Task<int> StartAsync()
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = projectDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--queue");
        startInfo.ArgumentList.Add(queue);
        startInfo.ArgumentList.Add("--sandbox");
        startInfo.ArgumentList.Add(sandboxUrl);
        startInfo.ArgumentList.Add("--address");
        startInfo.ArgumentList.Add(address);
        startInfo.ArgumentList.Add("--max-concurrent-activities");
        startInfo.ArgumentList.Add(maxConcurrentActivities.ToString());

        var log = new StringBuilder();
        var ready = new TaskCompletionSource();

        process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (log)
            {
                log.AppendLine(e.Data);
                File.AppendAllText(logPath, e.Data + Environment.NewLine);
            }
            if (e.Data.Contains("WORKER ready", StringComparison.Ordinal))
                ready.TrySetResult();
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
                return;
            lock (log)
            {
                log.AppendLine(e.Data);
                File.AppendAllText(logPath, e.Data + Environment.NewLine);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(60)));
        if (!ready.Task.IsCompleted)
            throw new InvalidOperationException($"worker did not become ready:\n{log}");

        return process.Id;
    }

    /// <summary>
    /// Kills the worker outright. No graceful shutdown: a crash is the
    /// injection, so the process must die without flushing anything.
    /// </summary>
    public void Kill()
    {
        if (process is null || process.HasExited)
            return;
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HARNESS kill failed: {ex.Message}");
        }
        process.WaitForExit(TimeSpan.FromSeconds(10));
        process = null;
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await Task.CompletedTask;
    }
}
