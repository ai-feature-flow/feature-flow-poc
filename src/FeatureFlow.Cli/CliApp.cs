using System.Reflection;
using FeatureFlow.Model;
using FeatureFlow.Testing.Workflows;
using FeatureFlow.Validation;

namespace FeatureFlow.Cli;

/// <summary>
/// The <c>ff</c> command line. M1: <c>ff validate &lt;library or template&gt;</c> and
/// <c>ff show &lt;template&gt;</c>. M2: <c>ff test &lt;test file or folder&gt;</c>.
/// </summary>
public static class CliApp
{
    public const string Usage = """
        usage: ff --version
               ff validate <library folder | template or fragment file> [--library <folder>]...
               ff show <template file> [--library <folder>]...
               ff test <workflow test file | folder of them> [--library <folder>]... [--trace]
        """;

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

        var trace = args.Length >= 2 && args[0] == "test" && args.Contains("--trace");
        if (trace)
            args = args.Where(a => a != "--trace").ToArray();
        if (args.Length >= 2 && args[0] is "validate" or "show" or "test" && ParseLibraries(args[2..]) is { } others)
            return args[0] switch
            {
                "validate" => Validate(args[1], others, output, error),
                "show" => Show(args[1], others, output, error),
                _ => Test(args[1], others, trace, output, error),
            };

        error.WriteLine(Usage);
        return 2;
    }

    static List<string>? ParseLibraries(string[] rest)
    {
        var list = new List<string>();
        for (var i = 0; i < rest.Length; i++)
        {
            if (rest[i] != "--library" || i + 1 >= rest.Length)
                return null;
            list.Add(rest[++i]);
        }

        return list;
    }

    static int Validate(string path, List<string> others, TextWriter output, TextWriter error)
    {
        ValidationResult result;
        if (Directory.Exists(path))
            result = LibraryValidator.ValidateLibrary(path, others);
        else if (Locate(path, error) is { } item)
            result = LibraryValidator.ValidateItem(item.Root, item.Reference, others);
        else
            return 2;

        foreach (var diagnostic in result.Diagnostics)
            output.WriteLine(diagnostic);
        var errors = result.Diagnostics.Count(d => d.Blocks);
        var warnings = result.Diagnostics.Count - errors;
        output.WriteLine($"{errors} error(s), {warnings} warning(s)");
        return errors == 0 ? 0 : 1;
    }

    /// <summary>Runs workflow test files (docs/09 section 4.3) against the library they sit in and any others.</summary>
    static int Test(string path, List<string> others, bool trace, TextWriter output, TextWriter error)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            error.WriteLine($"no such file or folder: {path}");
            return 2;
        }

        var results = WorkflowTestRunner.RunPath(path, others);
        if (results.Count == 0)
        {
            error.WriteLine($"no workflow tests in {path}");
            return 2;
        }

        foreach (var result in results)
        {
            output.WriteLine($"{(result.Passed ? "pass" : "FAIL")} {result.Name} ({result.Status})");
            foreach (var failure in result.Failures)
                output.WriteLine($"  {failure}");
            if (trace)
                foreach (var entry in result.Trace)
                    output.WriteLine($"    {entry}");
        }

        var failed = results.Count(r => !r.Passed);
        output.WriteLine($"{results.Count - failed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static int Show(string path, List<string> others, TextWriter output, TextWriter error)
    {
        if (Locate(path, error) is not { } item)
            return 2;
        if (item.Reference.Type != "templates")
        {
            error.WriteLine($"ff show takes a template; {path} is in {item.Reference.Type}");
            return 2;
        }

        var result = LibraryValidator.ValidateItem(item.Root, item.Reference, others);
        if (result.HasErrors || !result.Plans.TryGetValue(item.Reference.ToString(), out var plan))
        {
            foreach (var diagnostic in result.Diagnostics)
                error.WriteLine(diagnostic);
            return 1;
        }

        output.Write(ReadableForm.Render(plan));
        return 0;
    }

    /// <summary>Finds the library a template or fragment file belongs to: the nearest folder above it with a library.yaml.</summary>
    static (string Root, LibraryReference Reference)? Locate(string path, TextWriter error)
    {
        var file = Path.GetFullPath(path);
        if (!File.Exists(file))
        {
            error.WriteLine($"no such file or folder: {path}");
            return null;
        }

        var folder = Path.GetDirectoryName(file)!;
        var type = Path.GetFileName(folder);
        var root = Path.GetDirectoryName(folder);
        if (type is not ("templates" or "fragments") || root is null || !File.Exists(Path.Combine(root, "library.yaml")))
        {
            error.WriteLine($"{path} is not a template or fragment in a library (expected <library>/templates/<name>.yaml)");
            return null;
        }

        var name = Path.GetFileNameWithoutExtension(file);
        var libraryName = Library.Library.Open(root, new DiagnosticBag())?.Name;
        if (libraryName is null)
        {
            error.WriteLine($"{root}/library.yaml is not a valid manifest; run ff validate {root}");
            return null;
        }

        return (root, new LibraryReference(libraryName, null, type, name));
    }
}
