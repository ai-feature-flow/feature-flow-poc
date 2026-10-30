using System.Diagnostics;

namespace FeatureFlow.Spike3;

public static class Cli
{
    public const string DefaultAddress = "localhost:7533";
    public const string Owner = "ai-feature-flow";
    public const string Repo = "feature-flow-example";
    public const string SshUrl = "git@github.com-sparkkz-nz:ai-feature-flow/feature-flow-example.git";

    public static string SpikeDir { get; } = FindSpikeDir();
    public static string PlansDir => Path.Combine(SpikeDir, "plans");
    public static string WorkDir => Path.Combine(SpikeDir, "work");
    public static string ResultsDir => Path.Combine(SpikeDir, "results");

    private static string FindSpikeDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "plans", "epic.yaml")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("spikes/03-concurrent not found");
    }

    public static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
                return args[i + 1];
        }
        return null;
    }

    public static bool Flag(string[] args, string name) => args.Contains(name);

    public static (int Code, string Out, string Err) Run(string file, string? cwd, params string[] args)
    {
        var psi = new ProcessStartInfo(file)
        {
            WorkingDirectory = cwd ?? Environment.CurrentDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var a in args)
            psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, outTask.Result, errTask.Result);
    }

    public static string Git(string cwd, params string[] args)
    {
        var (code, output, err) = Run("git", cwd, args);
        if (code != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} exited {code}: {err.Trim()}");
        return output.Trim();
    }

    public static void Log(string line) =>
        Console.WriteLine($"{DateTimeOffset.UtcNow:HH:mm:ss.f} {line}");
}
