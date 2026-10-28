using System.Diagnostics;
using System.Text;

namespace FeatureFlow.Spike.AgentRuntime;

/// <summary>
/// How the provider talks to the runtime process. Split out so the provider can
/// be exercised without spawning a real CLI, and so a second runtime that is not
/// a local process can supply its own.
/// </summary>
public interface IAgentRuntimeProcess
{
    /// <summary>
    /// Starts the runtime and returns immediately. Never waits for the agent to
    /// finish: that is what keeps <c>StartAsync</c> bounded (docs/04 1.3).
    /// </summary>
    RuntimeLaunch Launch(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment);

    /// <summary>Whether a process is still alive. Non-blocking.</summary>
    bool IsRunning(int processId);

    /// <summary>
    /// The process exit code, or null when the process is still running or has
    /// already been reaped.
    /// </summary>
    int? ExitCode(int processId);

    /// <summary>Reads a file if it exists. Returns null when it does not.</summary>
    Task<string?> ReadFileAsync(string path, CancellationToken ct);

    /// <summary>Runs a short bounded command and returns its output.</summary>
    Task<CommandOutput> RunAsync(
        string workingDirectory, string fileName, IReadOnlyList<string> arguments, CancellationToken ct);
}

public sealed record RuntimeLaunch(int ProcessId);

public sealed record CommandOutput(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// The default <see cref="IAgentRuntimeProcess"/>: a real local process and real
/// file reads.
/// </summary>
public sealed class LocalAgentRuntimeProcess : IAgentRuntimeProcess
{
    /// <summary>
    /// Live process references, kept so an exit code is still readable after the
    /// process has gone away. Without this, <see cref="Process.GetProcessById"/>
    /// throws once the OS has reaped the process and the exit code is lost,
    /// which would make a completed run look like a failed one.
    /// </summary>
    private readonly Dictionary<int, Process> live = new();

    /// <summary>
    /// Output captured per process. The pipes must be drained: a redirected
    /// stream that nobody reads fills its buffer and the child blocks on write,
    /// which hangs a long agent run. Draining on a background task is what keeps
    /// <see cref="Launch"/> non-blocking.
    /// </summary>
    private readonly Dictionary<int, StringBuilder> captured = new();

    public RuntimeLaunch Launch(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string> environment)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var pair in environment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        var process = Process.Start(startInfo)
                      ?? throw new InvalidOperationException($"could not start '{fileName}'");

        var buffer = new StringBuilder();

        lock (live)
        {
            live[process.Id] = process;
            captured[process.Id] = buffer;
        }

        // Drain both pipes. A closed pipe is the process exiting, not an error.
        _ = DrainAsync(process.StandardOutput, buffer);
        _ = DrainAsync(process.StandardError, buffer);

        return new RuntimeLaunch(process.Id);
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder buffer)
    {
        try
        {
            var text = await reader.ReadToEndAsync().ConfigureAwait(false);
            lock (buffer)
            {
                buffer.Append(text);
            }
        }
        catch (Exception)
        {
            // The process went away. Nothing to recover.
        }
    }

    /// <summary>
    /// The output captured so far for a process. Used for diagnostics; the
    /// provider never classifies by matching its text (docs/04 section 1).
    /// </summary>
    public string Output(int processId)
    {
        StringBuilder buffer;

        lock (live)
        {
            if (!captured.TryGetValue(processId, out buffer!))
                return "";
        }

        lock (buffer)
        {
            return buffer.ToString();
        }
    }

    public bool IsRunning(int processId)
    {
        try
        {
            return !Process.GetProcessById(processId).HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public int? ExitCode(int processId)
    {
        // The held reference is authoritative: it survives the process being
        // reaped, which GetProcessById does not.
        lock (live)
        {
            if (live.TryGetValue(processId, out var held))
            {
                if (!held.HasExited)
                    return null;

                var code = held.ExitCode;
                live.Remove(processId);
                captured.Remove(processId);
                held.Dispose();
                return code;
            }
        }

        try
        {
            var process = Process.GetProcessById(processId);
            return process.HasExited ? process.ExitCode : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<string?> ReadFileAsync(string path, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        // Share read-write, because the runtime may still be appending.
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct).ConfigureAwait(false);
    }

    public async Task<CommandOutput> RunAsync(
        string workingDirectory, string fileName, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
                            ?? throw new InvalidOperationException($"could not start '{fileName}'");

        var stdout = process.StandardOutput.ReadToEndAsync(ct);
        var stderr = process.StandardError.ReadToEndAsync(ct);

        await process.WaitForExitAsync(ct).ConfigureAwait(false);

        return new CommandOutput(
            process.ExitCode,
            await stdout.ConfigureAwait(false),
            await stderr.ConfigureAwait(false));
    }
}
