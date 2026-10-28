using System.Diagnostics;
using System.Text;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Starts and kills the real worker as a separate OS process. Killing the worker
/// is how Spike 1 proved resume, so the harness owns the lifecycle rather than
/// leaving it to a shell script.
/// </summary>
public sealed class WorkerProcess : IAsyncDisposable
{
    private readonly string projectDir;
    private readonly string queue;
    private readonly string address;
    private readonly string repo;
    private readonly string branch;
    private readonly string baseBranch;
    private readonly string ticket;
    private readonly string runId;
    private readonly string records;
    private readonly string logPath;
    private Process? process;

    public WorkerProcess(
        string projectDir,
        string queue,
        string address,
        string repo,
        string branch,
        string baseBranch,
        string ticket,
        string runId,
        string records,
        string logPath)
    {
        this.projectDir = projectDir;
        this.queue = queue;
        this.address = address;
        this.repo = repo;
        this.branch = branch;
        this.baseBranch = baseBranch;
        this.ticket = ticket;
        this.runId = runId;
        this.records = records;
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
        startInfo.ArgumentList.Add("--address");
        startInfo.ArgumentList.Add(address);
        startInfo.ArgumentList.Add("--repo");
        startInfo.ArgumentList.Add(repo);
        startInfo.ArgumentList.Add("--branch");
        startInfo.ArgumentList.Add(branch);
        startInfo.ArgumentList.Add("--base");
        startInfo.ArgumentList.Add(baseBranch);
        startInfo.ArgumentList.Add("--ticket");
        startInfo.ArgumentList.Add(ticket);
        startInfo.ArgumentList.Add("--run-id");
        startInfo.ArgumentList.Add(runId);
        startInfo.ArgumentList.Add("--records");
        startInfo.ArgumentList.Add(records);

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

        await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromSeconds(120)));
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
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"HARNESS kill failed: {ex.Message}");
        }

        process = null;
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await Task.CompletedTask;
    }
}
