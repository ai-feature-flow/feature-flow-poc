using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Library;

/// <summary>
/// One library: from a folder (the <c>directory</c> source, docs/08 section 3), or from a snapshot of its
/// files, which is how workflow code loads the libraries a run's lock names. Content items are named by
/// reference, never by path: the folder layout of docs/08 section 1 maps names to files.
/// </summary>
public sealed class Library
{
    readonly Dictionary<(string Type, string Name), string> items = [];
    readonly Dictionary<string, object?> parsed = [];

    Library(string root, LibraryFiles files, Manifest manifest)
    {
        Root = root;
        Files = files;
        Manifest = manifest;
    }

    /// <summary>The folder the library was read from, or "" for a library built from a snapshot.</summary>
    public string Root { get; }

    /// <summary>Every file of the library, by path relative to its root. Items are read only from here.</summary>
    public LibraryFiles Files { get; }

    public Manifest Manifest { get; }
    public string Name => Manifest.Name;

    /// <summary>Diagnostics from reading the manifest and the library's own items.</summary>
    public DiagnosticBag Diagnostics { get; } = new();

    public IReadOnlyList<DecisionTypeItem> DecisionTypes { get; private set; } = [];
    public IReadOnlyList<StatusItem> Statuses { get; private set; } = [];

    public IEnumerable<string> Names(string type) =>
        items.Keys.Where(k => k.Type == type).Select(k => k.Name).Order(StringComparer.Ordinal);

    /// <summary>The item's path relative to the library root, or null when the library has no such item.</summary>
    public string? PathOf(string type, string name) => items.GetValueOrDefault((type, name));

    /// <summary>A path relative to the library root, prefixed with the library's name, for messages and golden files.</summary>
    public string Relative(string path) => $"{Name}/" + path.Replace('\\', '/');

    /// <summary>Opens the library in <paramref name="root"/>. Returns null, with diagnostics, when the manifest is missing or invalid.</summary>
    public static Library? Open(string root, DiagnosticBag diagnostics)
    {
        root = Path.GetFullPath(root);
        if (!File.Exists(Path.Combine(root, "library.yaml")))
        {
            diagnostics.Add(Checks.Schema, "no library.yaml: not a library folder", new SourceLocation(root, 1, 1));
            return null;
        }

        return FromFiles(LibraryFiles.Read(root), diagnostics, root);
    }

    /// <summary>Builds a library from a snapshot of its files, with no file system access.</summary>
    public static Library? FromFiles(LibraryFiles files, DiagnosticBag diagnostics, string root = "")
    {
        if (files.TextOf("library.yaml") is not { } manifestText)
        {
            diagnostics.Add(Checks.Schema, "no library.yaml: not a library", new SourceLocation(root, 1, 1));
            return null;
        }

        var node = YamlText.Parse(manifestText, "library.yaml", diagnostics);
        var manifest = node is null ? null : LibraryItemReader.ReadManifest(node, diagnostics);
        if (manifest is null)
            return null;
        if (!Model.Plans.StepReader.SnakeCase().IsMatch(manifest.Name))
            diagnostics.Add(Checks.Schema, $"library name '{manifest.Name}' must be snake_case", node!.Location);

        var library = new Library(root, files, manifest);
        library.Index(diagnostics);
        return library;
    }

    void Index(DiagnosticBag diagnostics)
    {
        foreach (var type in new[] { "templates", "fragments", "roles", "artifact_types" })
            foreach (var file in FilesIn(type, ".yaml"))
                items[(type, Stem(file))] = file;
        foreach (var type in new[] { "tasks", "prompts" })
            foreach (var file in FilesIn(type, ".md"))
                items[(type, Stem(file))] = file;
        var skillFolders = Files.Paths
            .Where(p => p.StartsWith("skills/", StringComparison.Ordinal) && p.Count(c => c == '/') >= 2)
            .Select(p => p.Split('/')[1])
            .Distinct()
            .Order(StringComparer.Ordinal);
        foreach (var dir in skillFolders)
        {
            var skill = $"skills/{dir}/SKILL.md";
            if (Files.Contains(skill))
                items[("skills", dir)] = skill;
            else
                diagnostics.Add(Checks.Schema, "a skill folder needs a SKILL.md", new SourceLocation(Relative($"skills/{dir}"), 1, 1));
        }

        foreach (var (key, path) in items)
            if (!Model.Plans.StepReader.SnakeCase().IsMatch(key.Name))
                diagnostics.Add(Checks.Schema, $"item name '{key.Name}' must be snake_case (LOG 43)", new SourceLocation(Relative(path), 1, 1));

        var decisionTypes = new List<DecisionTypeItem>();
        foreach (var file in FilesIn("decision_types", ".yaml"))
        {
            var node = YamlText.Parse(Files.TextOf(file) ?? "", Relative(file), Diagnostics);
            if (node is not null)
                decisionTypes.AddRange(LibraryItemReader.ReadDecisionTypes(node, Diagnostics));
        }

        DecisionTypes = decisionTypes;

        if (Files.TextOf("status_map.yaml") is { } statusMap)
        {
            var node = YamlText.Parse(statusMap, "status_map.yaml", Diagnostics);
            if (node is not null)
                Statuses = LibraryItemReader.ReadStatusMap(node, Diagnostics);
        }
    }

    static string Stem(string path) => Path.GetFileNameWithoutExtension(path);

    /// <summary>Files directly in a top-level folder with an extension, in ordinal order.</summary>
    IEnumerable<string> FilesIn(string folder, string extension) =>
        Files.Paths.Where(p => p.StartsWith(folder + "/", StringComparison.Ordinal)
                               && p.IndexOf('/', folder.Length + 1) < 0
                               && p.EndsWith(extension, StringComparison.Ordinal));

    /// <summary>Parses a YAML item once; its diagnostics go to <see cref="Diagnostics"/>.</summary>
    public YNode? Yaml(string type, string name)
    {
        var key = $"yaml:{type}/{name}";
        if (parsed.TryGetValue(key, out var cached))
            return (YNode?)cached;
        var path = PathOf(type, name);
        var node = path is null ? null : YamlText.Parse(Files.TextOf(path) ?? "", Relative(path), Diagnostics);
        parsed[key] = node;
        return node;
    }

    public string? Text(string type, string name) => PathOf(type, name) is { } path ? Files.TextOf(path) : null;

    public RoleItem? Role(string name) => Item(("roles", name), node => LibraryItemReader.ReadRole(node, name, Diagnostics));

    public ArtifactTypeItem? ArtifactType(string name) =>
        Item(("artifact_types", name), node => LibraryItemReader.ReadArtifactType(node, name, Diagnostics));

    T? Item<T>((string Type, string Name) key, Func<YNode, T?> read) where T : class
    {
        var cacheKey = $"item:{key.Type}/{key.Name}";
        if (parsed.TryGetValue(cacheKey, out var cached))
            return (T?)cached;
        var node = Yaml(key.Type, key.Name);
        var item = node is null ? null : read(node);
        parsed[cacheKey] = item;
        return item;
    }
}

/// <summary>The libraries a template is loaded against, by name. References resolve through it.</summary>
public sealed class LibrarySet
{
    readonly Dictionary<string, Library> libraries = [];

    public LibrarySet(params IEnumerable<Library> libraries)
    {
        foreach (var library in libraries)
            this.libraries[library.Name] = library;
    }

    public IEnumerable<Library> All => libraries.Values;

    public Library? this[string name] => libraries.GetValueOrDefault(name);

    /// <summary>The library holding the item, or null when the library or the item does not exist.</summary>
    public Library? Resolve(LibraryReference reference) =>
        libraries.TryGetValue(reference.Library, out var library) && library.PathOf(reference.Type, reference.Name) is not null
            ? library
            : null;

    public IEnumerable<DecisionTypeItem> DecisionTypes => libraries.Values.SelectMany(l => l.DecisionTypes);

    public IEnumerable<StatusItem> Statuses => libraries.Values.SelectMany(l => l.Statuses);
}
