using System.Diagnostics;
using System.Text;

namespace FeatureFlow.Integration.Tests.Cluster;

/// <summary>
/// kubectl against the cluster deploy/k3d/up.sh brought up, through the kubeconfig file it wrote (FF_KUBECONFIG), so a
/// test never uses the default kubeconfig's context. Only a k3d cluster is accepted.
/// </summary>
public static class Kubectl
{
    static readonly Lazy<string> Config = new(() =>
    {
        var path = Environment.GetEnvironmentVariable("FF_KUBECONFIG");
        Assert.True(path is { Length: > 0 } && File.Exists(path), "FF_KUBECONFIG must name the kubeconfig deploy/k3d/up.sh wrote");
        var (exit, context) = RunAsync(["config", "current-context"], path).GetAwaiter().GetResult();
        Assert.True(exit == 0 && context.Trim().StartsWith("k3d-", StringComparison.Ordinal), $"{path} is not a k3d cluster's kubeconfig: {context}");
        return path!;
    });

    public static Task<(int Exit, string Output)> RunAsync(params string[] arguments) => RunAsync(arguments, Config.Value);

    static async Task<(int Exit, string Output)> RunAsync(string[] arguments, string config)
    {
        using var process = Process.Start(StartInfo(arguments, config))!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return (process.ExitCode, await output + await error);
    }

    public static async Task<string> OkAsync(params string[] arguments)
    {
        var (exit, output) = await RunAsync(arguments);
        Assert.True(exit == 0, $"kubectl {string.Join(' ', arguments)} failed: {output}");
        return output;
    }

    /// <summary>Starts a kubectl that runs until stopped, such as <c>logs -f</c> or <c>port-forward</c>, collecting its output.</summary>
    public static (Process Process, StringBuilder Output) Start(params string[] arguments)
    {
        var process = Process.Start(StartInfo(arguments, Config.Value))!;
        var output = new StringBuilder();
        DataReceivedEventHandler collect = (_, e) =>
        {
            lock (output)
                output.AppendLine(e.Data);
        };
        process.OutputDataReceived += collect;
        process.ErrorDataReceived += collect;
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return (process, output);
    }

    static ProcessStartInfo StartInfo(string[] arguments, string config)
    {
        var start = new ProcessStartInfo("kubectl") { RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("--kubeconfig");
        start.ArgumentList.Add(config);
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        return start;
    }
}
