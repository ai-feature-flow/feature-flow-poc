using System.Diagnostics;

namespace FeatureFlow.Hosting.Tests;

/// <summary>
/// LOG AFd-2: a host that refuses to start writes the refusal alone to standard error, with no stack trace, and exits 2,
/// whatever the refusal: arguments, a configuration file, a diagnostic, an address.
/// </summary>
public sealed class StartupRefusalTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-refusal-" + Guid.NewGuid().ToString("N"));

    public StartupRefusalTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    string Write(string name, string content)
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, content);
        return path;
    }

    string[] Arguments(string installation) =>
    [
        "--installation", Write("installation.yaml", installation),
        "--organisation", Write("organisation.yaml", "api_version: feature-flow/v1\nkind: organisation\nname: acme\n"),
    ];

    static async Task<(int Exit, string Error)> RunAsync(string[] args)
    {
        var error = new StringWriter();
        var exit = await HostArguments.RunAsync(args, [], false, false, false, error);
        return (exit, error.ToString());
    }

    [Fact]
    public async Task A_configuration_diagnostic_is_its_message_and_exit_2()
    {
        var (exit, error) = await RunAsync(Arguments("api_version: feature-flow/v1\nkind: installation\nno_such_key: 1\n"));

        Assert.Equal(2, exit);
        Assert.StartsWith("the configuration does not validate:\n", error, StringComparison.Ordinal);
        Assert.Contains("no_such_key", error, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_missing_argument_or_file_is_one_line_and_exit_2()
    {
        var missing = Path.Combine(folder, "none.yaml");

        Assert.Equal((2, "--organisation <file> or FF_ORGANISATION is required"), Trimmed(await RunAsync(["--installation", missing])));
        Assert.Equal((2, $"there is no configuration file {missing}"), Trimmed(await RunAsync(["--installation", missing, "--organisation", missing])));
    }

    static (int, string) Trimmed((int Exit, string Error) result) => (result.Exit, result.Error.TrimEnd());

    /// <summary>The built hosts themselves: what an operator sees, through the process's own exit code and standard error.</summary>
    [Theory]
    [InlineData("FeatureFlow.Host.Api")]
    [InlineData("FeatureFlow.Host.OrchestrationWorker")]
    [InlineData("FeatureFlow.Host.AgentWorker")]
    public async Task Each_host_process_prints_the_refusal_and_exits_2(string host)
    {
        var (exit, output, error) = await ProcessAsync(host, Arguments("api_version: feature-flow/v1\nkind: installation\nno_such_key: 1\n"));

        Assert.True(exit == 2, $"exit {exit}\n{output}\n{error}");
        Assert.StartsWith("the configuration does not validate:", error, StringComparison.Ordinal);
        Assert.Contains("no_such_key", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Unhandled exception", error, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_API_host_process_refuses_no_sign_in_off_loopback_in_one_line()
    {
        var args = Arguments("""
            api_version: feature-flow/v1
            kind: installation
            development: true
            temporal: { address: "127.0.0.1:1" }
            api:
              auth: { mode: none, actor: ana }
            """);

        var (exit, _, error) = await ProcessAsync("FeatureFlow.Host.Api", args, ("FF_API_LISTEN", "http://0.0.0.0:8480"));

        Assert.Equal(2, exit);
        var line = Assert.Single(error.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.StartsWith("api.auth.mode is none, so the API must listen only on loopback", line, StringComparison.Ordinal);
    }

    /// <summary>Runs a built host (referenced, unused, by this project so that it is built first) to its exit.</summary>
    static async Task<(int Exit, string Output, string Error)> ProcessAsync(string host, string[] args, params (string Name, string Value)[] environment)
    {
        // The test's own output folder ends bin/<configuration>/<framework>; the host's is built alike.
        var output = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var dll = Path.Combine(Repository.Root, "hosts", host, "bin", output.Parent!.Name, output.Name, host + ".dll");
        Assert.True(File.Exists(dll), $"{dll} is not built");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(dll);
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        start.Environment.Remove("FF_INSTALLATION");
        start.Environment.Remove("FF_ORGANISATION");
        foreach (var (name, value) in environment)
            start.Environment[name] = value;
        using var process = Process.Start(start)!;
        var (stdout, stderr) = (process.StandardOutput.ReadToEndAsync(Ct), process.StandardError.ReadToEndAsync(Ct));
        await process.WaitForExitAsync(Ct);
        return (process.ExitCode, await stdout, (await stderr).ReplaceLineEndings("\n"));
    }
}
