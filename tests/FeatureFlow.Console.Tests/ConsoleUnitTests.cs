using System.ComponentModel;
using System.Diagnostics;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The console's own unit tests (<c>src/FeatureFlow.Console/tests/*.test.ts</c>, such as the Markdown reader's, LOG 542), run
/// by <c>npm test</c>, so the default suite guards them whether or not the console's build ran them.
/// </summary>
public sealed class ConsoleUnitTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_consoles_unit_tests_pass()
    {
        var console = Path.Combine(Repository.Root, "src", "FeatureFlow.Console");
        var (exit, output) = await RunAsync(OperatingSystem.IsWindows() ? "npm.cmd" : "npm", console, "test");
        Assert.True(exit == 0, $"npm test in {console} exited {exit}:\n{output}");
    }

    /// <summary>The exit code and output of <paramref name="file"/>; the test is skipped when Node and npm are not installed.</summary>
    static async Task<(int Exit, string Output)> RunAsync(string file, string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo(file) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);
        Process process;
        try
        {
            process = Process.Start(start)!;
        }
        catch (Win32Exception)
        {
            Assert.Skip($"{file} is not on PATH: the console's unit tests need Node 22.12 or later and npm, as its build does");
            throw new UnreachableException();
        }

        using (process)
        {
            var output = process.StandardOutput.ReadToEndAsync(Ct);
            var errors = process.StandardError.ReadToEndAsync(Ct);
            await process.WaitForExitAsync(Ct);
            return (process.ExitCode, await output + await errors);
        }
    }
}
