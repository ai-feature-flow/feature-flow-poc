using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model.Plans;

public enum ScopeKind
{
    Template,
    Fragment,
    Branch,
}

/// <summary>
/// Where a step or expression came from after expansion. Inside a fragment, <c>params</c> is the
/// fragment's parameters and <c>steps.&lt;id&gt;</c> its own steps (LOG 62); inside a <c>for_each</c>
/// branch, <c>item</c> is the branch's item and step ids carry the branch prefix (docs/03 section 8.2).
/// </summary>
public sealed class Scope
{
    public required string Id { get; init; }

    /// <summary>Prefix of every step id expanded in this scope: "" for the template, "verify." for a fragment used as <c>verify</c>.</summary>
    public required string Prefix { get; init; }

    public required ScopeKind Kind { get; init; }

    /// <summary>The enclosing scope: a branch's container scope, or a fragment's caller.</summary>
    public Scope? Parent { get; init; }

    public required DocumentInfo Document { get; init; }

    /// <summary>A fragment's parameter bindings: the <c>use</c> entry's <c>with</c>, evaluated in <see cref="Parent"/>.</summary>
    public IReadOnlyDictionary<string, YNode> Bindings { get; init; } = new Dictionary<string, YNode>();

    /// <summary>A fragment's <c>use</c> entry: its full id, and its routes for the fragment's exits (in <see cref="Parent"/>).</summary>
    public string? UseId { get; init; }

    public IReadOnlyList<RouteSpec> ExitRoutes { get; init; } = [];

    public SourceLocation? UseLocation { get; init; }

    /// <summary>A fragment used as a branch's only step: its exits end the branch (docs/03 section 8.2).</summary>
    public bool BranchRoot { get; init; }

    /// <summary>A fragment validated on its own, with no caller.</summary>
    public bool Standalone { get; init; }

    /// <summary>Fragment nesting depth: 0 for the template, 1 for a fragment it uses.</summary>
    public int Depth { get; init; }

    public int? BranchIndex { get; init; }

    /// <summary>A branch's item, when the list was known at load; null otherwise.</summary>
    public YNode? Item { get; init; }

    public string? ContainerId { get; init; }

    /// <summary>Whether this branch came from a <c>for_each</c>, so that <c>item</c> is defined in it.</summary>
    public bool IsForEach { get; init; }

    /// <summary>The template or fragment scope this scope belongs to: a branch belongs to its container's.</summary>
    public Scope DocumentScope => Kind == ScopeKind.Branch ? Parent!.DocumentScope : this;

    /// <summary>Whether <c>item</c> is in scope: inside a <c>for_each</c> branch, not across a fragment boundary.</summary>
    public bool HasItem => Kind == ScopeKind.Branch && (IsForEach || Parent!.HasItem);

    public override string ToString() => Id.Length == 0 ? "(template)" : Id;
}

public sealed record ParameterDecl(string Name, string Type, bool Required, YNode? Default, string? Description, SourceLocation Location);

public sealed record Defaults(
    IReadOnlyDictionary<OutcomeClass, RouteSpec> ByClass,
    IReadOnlyDictionary<string, RouteSpec> ByOutcome,
    Limits? Limits)
{
    public static readonly Defaults None = new(new Dictionary<OutcomeClass, RouteSpec>(), new Dictionary<string, RouteSpec>(), null);
}

/// <summary>The document-level parts of a template or fragment.</summary>
public sealed class DocumentInfo
{
    public required string Kind { get; init; }
    public required string Name { get; init; }
    public required string File { get; init; }
    public LibraryReference? Reference { get; init; }
    public string? Level { get; init; }
    public string? Description { get; init; }
    public string? BranchPattern { get; init; }
    public SourceLocation? BranchPatternLocation { get; init; }
    public IReadOnlyList<ParameterDecl> Parameters { get; init; } = [];
    public IReadOnlyList<ItemRef> ArtifactTypes { get; init; } = [];
    public Defaults Defaults { get; set; } = Defaults.None;
    public IReadOnlyList<Outcome> Exits { get; init; } = [];
    public SourceLocation? ExitsLocation { get; init; }
    public long? MaxWorkItems { get; init; }
    public ItemRef? DeprecatedBy { get; init; }

    public ParameterDecl? Parameter(string name) => Parameters.FirstOrDefault(p => p.Name == name);
}

/// <summary>
/// A library reference as written in a field. <see cref="Unbound"/> marks a fragment parameter with no
/// literal value, whose reference cannot be known until the fragment is used.
/// </summary>
public sealed record ItemRef(string Text, LibraryReference? Reference, SourceLocation Location, bool Unbound = false)
{
    public override string ToString() => Unbound ? $"{{ param: {Text} }}" : Text;
}
