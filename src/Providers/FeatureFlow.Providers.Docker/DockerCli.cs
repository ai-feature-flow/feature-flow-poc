using System.Diagnostics;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Docker;

/// <summary>Runs the <c>docker</c> command line. A failure to reach the daemon is an infrastructure error (docs/06 section 1.1).</summary>
public sealed class DockerCli(string executable = "docker")
{
    public sealed record Run(int Exit, byte[] Output, string Error)
    {
        public string Text => System.Text.Encoding.UTF8.GetString(Output);
    }

    public async Task<ProviderResult<Run>> RunAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken, Stream? input = null, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = input is not null,
            UseShellExecute = false,
        };
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return ProviderResult<Run>.Failure("unreachable", $"docker cannot be run: {e.Message}");
        }

        using (process)
        {
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cancel.CancelAfter(timeout ?? TimeSpan.FromMinutes(5));
            var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output, cancel.Token);
            var error = process.StandardError.ReadToEndAsync(cancel.Token);
            if (input is not null)
            {
                await input.CopyToAsync(process.StandardInput.BaseStream, cancel.Token);
                process.StandardInput.Close();
            }

            try
            {
                await process.WaitForExitAsync(cancel.Token);
                await copy;
                var err = await error;
                if (process.ExitCode != 0 && (err.Contains("Cannot connect to the Docker daemon", StringComparison.Ordinal) || err.Contains("error during connect", StringComparison.Ordinal)))
                    return ProviderResult<Run>.Failure("unreachable", err.Trim());
                return ProviderResult<Run>.Success(new Run(process.ExitCode, output.ToArray(), err));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                return ProviderResult<Run>.Failure("timeout", $"docker {arguments.FirstOrDefault()} did not finish in time");
            }
        }
    }
}
