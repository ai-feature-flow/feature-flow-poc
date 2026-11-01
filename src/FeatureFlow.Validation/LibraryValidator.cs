using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;

namespace FeatureFlow.Validation;

/// <summary>The result of validating a library or one template.</summary>
public sealed class ValidationResult
{
    public required IReadOnlyList<Diagnostic> Diagnostics { get; init; }

    /// <summary>The expanded plans, by reference text, for the readable form.</summary>
    public IReadOnlyDictionary<string, Plan> Plans { get; init; } = new Dictionary<string, Plan>();

    public bool HasErrors => Diagnostics.Any(d => d.Blocks);

    public IReadOnlyList<int> BlockingChecks => Diagnostics.Where(d => d.Blocks).Select(d => d.Check).Distinct().Order().ToList();

    public IReadOnlyList<int> WarningChecks => Diagnostics.Where(d => !d.Blocks).Select(d => d.Check).Distinct().Order().ToList();
}

/// <summary>
/// <c>ff validate</c>: loads and checks a whole library (every template in full, every fragment on its own,
/// and every role, artifact type and decision type), or one template.
/// </summary>
public static class LibraryValidator
{
    public static ValidationResult ValidateLibrary(string root, IEnumerable<string>? otherLibraries = null)
    {
        var d = new DiagnosticBag();
        var library = Library.Library.Open(root, d);
        if (library is null)
            return new ValidationResult { Diagnostics = Distinct(d) };
        var set = OpenSet(library, otherLibraries, d);
        var plans = new Dictionary<string, Plan>();

        foreach (var name in library.Names("roles"))
            library.Role(name);
        foreach (var name in library.Names("artifact_types"))
            if (library.ArtifactType(name) is { } type)
                foreach (var compatible in type.CompatibleWith)
                    if (compatible.Reference is { } r && set.Resolve(r) is null)
                        d.Add(Checks.ReferenceResolves, $"{compatible.Text} does not resolve", compatible.Location);

        foreach (var entrypoint in library.Manifest.Entrypoints)
            if (library.PathOf("templates", entrypoint.Template) is null)
                d.Add(Checks.ReferenceResolves, $"entrypoint template '{entrypoint.Template}' is not in the library", entrypoint.Location);
        foreach (var dependency in library.Manifest.Dependencies)
            if (set[dependency.Name] is null)
                d.Add(Checks.ReferenceResolves, $"dependency library '{dependency.Name}' is not available", null);

        foreach (var name in library.Names("templates"))
        {
            var reference = new LibraryReference(library.Name, null, "templates", name);
            if (Validate(set, reference, d) is { } plan)
                plans[reference.ToString()] = plan;
        }

        foreach (var name in library.Names("fragments"))
            Validate(set, new LibraryReference(library.Name, null, "fragments", name), d);

        d.AddRange(library.Diagnostics);
        return new ValidationResult { Diagnostics = Distinct(d), Plans = plans };
    }

    /// <summary>Validates one template or fragment of the library in <paramref name="root"/>.</summary>
    public static ValidationResult ValidateItem(string root, LibraryReference reference, IEnumerable<string>? otherLibraries = null)
    {
        var d = new DiagnosticBag();
        var library = Library.Library.Open(root, d);
        if (library is null)
            return new ValidationResult { Diagnostics = Distinct(d) };
        var set = OpenSet(library, otherLibraries, d);
        var plan = Validate(set, reference, d);
        return new ValidationResult
        {
            Diagnostics = Distinct(d),
            Plans = plan is null ? new Dictionary<string, Plan>() : new Dictionary<string, Plan> { [reference.ToString()] = plan },
        };
    }

    static LibrarySet OpenSet(Library.Library library, IEnumerable<string>? others, DiagnosticBag d)
    {
        var all = new List<Library.Library> { library };
        foreach (var path in others ?? [])
            if (Library.Library.Open(path, d) is { } other)
                all.Add(other);
        return new LibrarySet(all);
    }

    static Plan? Validate(LibrarySet set, LibraryReference reference, DiagnosticBag d)
    {
        var plan = reference.Type == "fragments"
            ? PlanLoader.LoadFragment(set, reference, d)
            : PlanLoader.LoadTemplate(set, reference, d);
        if (plan is not null)
            PlanValidator.Validate(plan, set, d);
        return plan;
    }

    static IReadOnlyList<Diagnostic> Distinct(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Distinct()
            .OrderBy(x => x.Location?.File ?? "", StringComparer.Ordinal)
            .ThenBy(x => x.Location?.Line ?? 0)
            .ThenBy(x => x.Location?.Column ?? 0)
            .ThenBy(x => x.Check)
            .ToList();
}
