using System.Reflection;

namespace FeatureFlow.Cli;

/// <summary>The <c>ff</c> command line. M0 answers <c>--version</c> only.</summary>
public static class CliApp
{
    public const string Usage = "usage: ff --version";

    public static string Version { get; } =
        typeof(CliApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        if (args is ["--version"])
        {
            output.WriteLine($"ff {Version}");
            return 0;
        }

        error.WriteLine(Usage);
        return 2;
    }
}
