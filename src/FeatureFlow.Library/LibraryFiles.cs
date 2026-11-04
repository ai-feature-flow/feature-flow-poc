using System.Security.Cryptography;
using System.Text;
using FeatureFlow.Model;

namespace FeatureFlow.Library;

/// <summary>
/// A snapshot of a library's content files, by path relative to the library root with <c>/</c> separators,
/// in ordinal order. The library's tests and README are not content and are not included.
/// </summary>
public sealed class LibraryFiles
{
    /// <summary>The top-level folders and files that hold library content (docs/08 section 1).</summary>
    public static readonly IReadOnlyList<string> ContentFolders =
        ["templates", "fragments", "roles", "artifact_types", "decision_types", "tasks", "prompts", "skills", "references"];

    public static readonly IReadOnlyList<string> ContentFiles = ["library.yaml", "status_map.yaml"];

    readonly SortedDictionary<string, string> files;

    public LibraryFiles(IEnumerable<KeyValuePair<string, string>> files)
    {
        this.files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, text) in files)
            this.files[path.Replace('\\', '/')] = text;
    }

    public IEnumerable<string> Paths => files.Keys;

    public IReadOnlyDictionary<string, string> All => files;

    public bool Contains(string path) => files.ContainsKey(path);

    public string? TextOf(string path) => files.GetValueOrDefault(path);

    /// <summary>Reads a library folder's content files.</summary>
    public static LibraryFiles Read(string root)
    {
        root = Path.GetFullPath(root);
        var found = new List<KeyValuePair<string, string>>();
        foreach (var name in ContentFiles)
        {
            var path = Path.Combine(root, name);
            if (File.Exists(path))
                found.Add(new(name, File.ReadAllText(path)));
        }

        foreach (var folder in ContentFolders)
        {
            var dir = Path.Combine(root, folder);
            if (!Directory.Exists(dir))
                continue;
            foreach (var file in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                found.Add(new(Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file)));
        }

        return new LibraryFiles(found);
    }

    /// <summary>The content hash of one file: <c>sha256:</c> and the hex digest of its UTF-8 text.</summary>
    public static string Hash(string text) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}

/// <summary>One library pinned by a run's lock: its name, version and every content file by hash (docs/08 section 5.1).</summary>
public sealed record LockedLibrary(string Name, string? Version, IReadOnlyDictionary<string, string> Files);

/// <summary>
/// A run's library lock: every library and content file the run may use, pinned by content hash. A child run
/// uses its root's lock. Workers resolve content only by hash from the lock.
/// </summary>
public sealed record LibraryLock(IReadOnlyList<LockedLibrary> Libraries)
{
    public static LockedLibrary Pin(Library library) =>
        new(library.Name, library.Manifest.Version,
            new SortedDictionary<string, string>(library.Files.All.ToDictionary(kv => kv.Key, kv => LibraryFiles.Hash(kv.Value)), StringComparer.Ordinal));

    /// <summary>The artifact type a reference names, in this lock's version; <paramref name="read"/> gives a file's text by its hash. Null when the lock has none.</summary>
    public async Task<ArtifactTypeItem?> ArtifactTypeAsync(string declared, Func<string, Task<string>> read)
    {
        if (LibraryReference.TryParse(declared) is not { Type: "artifact_types" } reference
            || Libraries.FirstOrDefault(l => l.Name == reference.Library)?.Files.GetValueOrDefault($"artifact_types/{reference.Name}.yaml") is not { } hash)
            return null;
        var d = new DiagnosticBag();
        return Model.Yaml.YamlText.Parse(await read(hash), declared, d) is { } node ? LibraryItemReader.ReadArtifactType(node, reference.Name, d) : null;
    }
}
