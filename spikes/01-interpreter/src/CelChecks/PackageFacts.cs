using System.Reflection;

namespace CelChecks;

/// <summary>
/// Package facts: resolved version, target frameworks, transitive dependencies.
/// </summary>
internal static class PackageFacts
{
    public static void Run()
    {
        Console.WriteLine("=== PACKAGE FACTS ===");
        Console.WriteLine();

        var asm = typeof(Cel.Env).Assembly;
        var informational = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var fileVersion = asm.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version;
        var asmVersion = asm.GetName().Version;

        Console.WriteLine($"  assembly name:              {asm.GetName().Name}");
        Console.WriteLine($"  assembly version:           {asmVersion}");
        Console.WriteLine($"  assembly file version:      {fileVersion}");
        Console.WriteLine($"  informational version:      {informational}");
        Console.WriteLine($"  assembly location:          {asm.Location}");
        Console.WriteLine();

        Console.WriteLine("  target frameworks shipped in the package (lib/ folders):");
        var libDir = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(asm.Location)!, "..", "..", "..", "..", "..", "..",
            "..", "..", ".."));
        // Fall back to a direct probe of the nuget cache if the path math fails.
        var pkgRoot = ProbePackageRoot();
        if (pkgRoot is not null)
        {
            var lib = Path.Combine(pkgRoot, "lib");
            if (Directory.Exists(lib))
            {
                foreach (var d in Directory.GetDirectories(lib).OrderBy(x => x, StringComparer.Ordinal))
                {
                    var dll = Path.Combine(d, "Cel.main.dll");
                    var size = File.Exists(dll) ? new FileInfo(dll).Length : -1;
                    Console.WriteLine($"      {Path.GetFileName(d),-20} Cel.main.dll {size,8} bytes");
                }
            }
        }

        Console.WriteLine();
        Console.WriteLine("  transitive dependencies, resolved by NuGet (obj/project.assets.json):");
        var assetsPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(typeof(PackageFacts).Assembly.Location)!, "..", "..", "..",
            "obj", "project.assets.json"));
        if (File.Exists(assetsPath))
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(assetsPath));
            var targets = doc.RootElement.GetProperty("targets");
            foreach (var tfm in targets.EnumerateObject())
            {
                Console.WriteLine($"    target: {tfm.Name}");
                foreach (var lib in tfm.Value.EnumerateObject())
                {
                    var name = lib.Name;
                    var type = lib.Value.TryGetProperty("type", out var t) ? t.GetString() : "?";
                    var deps = new List<string>();
                    if (lib.Value.TryGetProperty("dependencies", out var d))
                        foreach (var dd in d.EnumerateObject()) deps.Add(dd.Name);

                    var marker = name.StartsWith("Cel.NET", StringComparison.Ordinal) ? "  <-- the package under test" : "";
                    Console.WriteLine($"      {name,-40} [{type}]  deps: {(deps.Count == 0 ? "(none)" : string.Join(", ", deps))}{marker}");
                }
            }
        }
        else
        {
            Console.WriteLine($"    (assets file not found at {assetsPath})");
        }

        Console.WriteLine();
        Console.WriteLine("  dependency assemblies actually loaded into this process at runtime:");
        // Force-load the dependency assemblies so the list is not just Cel.main.
        ForceLoad("Antlr4.Runtime.Standard");
        ForceLoad("Avro");
        ForceLoad("Google.Protobuf");
        ForceLoad("Grpc.Core.Api");
        ForceLoad("Newtonsoft.Json");
        ForceLoad("NodaTime");
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies()
                     .Where(a => !a.IsDynamic)
                     .Select(a => a.GetName())
                     .Where(n => n.Name is not null && !n.Name.StartsWith("System.")
                                                    && !n.Name.StartsWith("Microsoft.")
                                                    && n.Name is not ("netstandard" or "mscorlib" or "CelChecks"))
                     .OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            Console.WriteLine($"      {a.Name,-32} {a.Version}");
        }

        Console.WriteLine();
    }

    private static void ForceLoad(string name)
    {
        try
        {
            Assembly.Load(name);
            Console.WriteLine($"      (loaded {name})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"      (could not load {name}: {ex.GetType().Name})");
        }
    }

    private static string? ProbePackageRoot()
    {
        // Walk up from the assembly location looking for a directory named "cel.net".
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(PackageFacts).Assembly.Location)!);
        while (dir is not null)
        {
            if (string.Equals(dir.Name, "cel.net", StringComparison.OrdinalIgnoreCase)) return dir.FullName;
            dir = dir.Parent;
        }

        // Fall back to the global packages folder.
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidate = Path.Combine(home, ".nuget", "packages", "cel.net", "2.3.1");
        return Directory.Exists(candidate) ? candidate : null;
    }
}
