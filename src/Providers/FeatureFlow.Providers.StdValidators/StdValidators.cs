using System.Text;
using System.Text.Json;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.StdValidators;

/// <summary>
/// The standard validator provider (docs/06 section 3.8, docs/04 section 10.2): the seven validators of std's
/// workflows. Deterministic: each reads only its request, artifact content from the artifact store, files at fixed
/// revisions from the code host (<c>tests_added</c>) and ancestry between fixed revisions from the VCS provider's clones
/// (<c>requirement_trace</c>). Evidence is only what an engine action produced: an index entry with an action and an
/// evidence type (F7). Problems in a subject's content are issues; a store or code host that fails is the provider's error.
/// </summary>
public sealed class StdValidators(Func<IArtifactStoreProvider?> store, Func<ICodeHostProvider?> codeHost, Func<IVcsProvider?> vcs) : IValidatorProvider
{
    static readonly IReadOnlyDictionary<string, string> None = new Dictionary<string, string>();

    static readonly IReadOnlyList<(ValidatorDescriptor Descriptor, Func<Check, Task> Run)> All =
    [
        (new ValidatorDescriptor(Names.RequirementSupport,
                "Every item's supports links resolve to an item of another subject of that name; every item supports something or gives a reason, not both; " +
                "with require_coverage (default true), every item of the first subject is supported by an item of another subject of this run",
                [Types.Requirements, Types.DeliveryPlan], new Dictionary<string, string> { ["require_coverage"] = "boolean" }, []),
            Requirements.SupportAsync),
        (new ValidatorDescriptor(Names.RequirementOwnership,
                "Every item of a parent-written plan names an owner its scope allows: an epic (or the project itself) in a project plan, a ticket (or the epic itself) in an epic plan",
                [Types.Requirements, Types.DeliveryPlan], None, []),
            Requirements.OwnershipAsync),
        (new ValidatorDescriptor(Names.RequirementInheritance,
                "The run's plan holds every item the parent's plan gives its scope, with id and links unchanged (and wording, unless allow_rewording), holds none another owns, " +
                "and names no owner; passes when there is no parent plan",
                [Types.Requirements, Types.DeliveryPlan], new Dictionary<string, string> { ["allow_rewording"] = "boolean" }, []),
            Requirements.InheritanceAsync),
        (new ValidatorDescriptor(Names.VerificationMethodSupport,
                "Every acceptance criterion without an owner has a verification method, and one with an owner has none; method ids are unique in their criterion",
                [Types.DeliveryPlan], None, []),
            Requirements.MethodSupportAsync),
        (new ValidatorDescriptor(Names.VerificationSatisfaction,
                "Every method of every criterion without an owner is claimed; each claimed test is of the method's repository and tier, of an allowed change, " +
                "and passed in the engine's own test results at the repository's frozen revision",
                [Types.DeliveryPlan, Types.EvidenceClaims, Types.TestResults], None, []),
            Evidence.SatisfactionAsync),
        (new ValidatorDescriptor(Names.RequirementTrace,
                "Every requirement of the run traces through owners (across children) and supporting items to criteria whose claimed tests passed in the " +
                "engine's test results at commits the frozen revisions contain",
                [Types.Requirements], None, [Types.TraceReport]),
            Evidence.TraceAsync),
        (new ValidatorDescriptor(Names.TestsAdded,
                "For every finding the triage left open with a regression test applicable, the fix claims a test, in the finding's repository, whose source file " +
                "is new or changed since the revision the finding was raised on, and holds the test",
                [Types.EvidenceClaims, Types.Triage], None, []),
            Evidence.TestsAddedAsync),
    ];

    public ProviderDescription Describe() => new("validator", "std", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) =>
        Task.FromResult(store() is null
            ? ProviderResult<Unit>.Failure("internal_error", "the standard validators need an artifact store")
            : ProviderResult<Unit>.Success(default));

    public IReadOnlyList<ValidatorDescriptor> Validators() => All.Select(v => v.Descriptor).ToList();

    public async Task<ProviderResult<ValidationResult>> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        var found = All.FirstOrDefault(v => v.Descriptor.Name == request.Validator);
        if (found.Descriptor is null)
            return ProviderResult<ValidationResult>.Failure("not_found", $"the standard validators have no {request.Validator}");
        if (found.Descriptor.Refusal(request) is { } refusal)
            return ProviderResult<ValidationResult>.Failure("validation_failed", refusal);
        if (store() is not { } artifacts)
            return ProviderResult<ValidationResult>.Failure("internal_error", "the standard validators need an artifact store");
        var check = new Check(request, artifacts, codeHost(), vcs(), cancellationToken);
        try
        {
            await found.Run(check);
        }
        catch (ProviderException e)
        {
            return ProviderResult<ValidationResult>.Failure(e.Error.Reason, e.Error.Message);
        }

        return ProviderResult<ValidationResult>.Success(ValidationResult.Of(check.Issues, check.Reports));
    }
}

/// <summary>The standard validators' names.</summary>
public static class Names
{
    public const string RequirementSupport = "requirement_support";
    public const string RequirementOwnership = "requirement_ownership";
    public const string RequirementInheritance = "requirement_inheritance";
    public const string VerificationMethodSupport = "verification_method_support";
    public const string VerificationSatisfaction = "verification_satisfaction";
    public const string RequirementTrace = "requirement_trace";
    public const string TestsAdded = "tests_added";
}

/// <summary>The artifact type names (std's) the standard validators read and offer.</summary>
public static class Types
{
    public const string Requirements = "requirements";
    public const string DeliveryPlan = "delivery_plan";
    public const string EvidenceClaims = "evidence_claims";
    public const string TestResults = "test_results";
    public const string MergeRecord = "merge_record";
    public const string Triage = "triage";
    public const string TraceReport = "trace_report";
}

/// <summary>One validator call: its request, the issues and reports it collects, and reads through the providers, each cached.</summary>
public sealed class Check(ValidationRequest request, IArtifactStoreProvider store, ICodeHostProvider? codeHost, IVcsProvider? vcs, CancellationToken ct)
{
    /// <summary>The actions whose artifacts are test evidence (docs/04 section 9).</summary>
    public static readonly IReadOnlySet<string> TestActions = new HashSet<string>(StringComparer.Ordinal) { "test.run", "test.integration" };

    readonly Dictionary<string, string> texts = new(StringComparer.Ordinal);
    readonly Dictionary<(string, string, string), RepoFile?> files = [];
    readonly Dictionary<(string, string, string), bool> ancestry = [];

    public ValidationRequest Request => request;
    public List<ValidationIssue> Issues { get; } = [];
    public List<OfferedReport> Reports { get; } = [];
    public CancellationToken Ct => ct;
    public IVcsProvider? Vcs => vcs;

    public void Error(string code, string message, string? subject = null, string? item = null) =>
        Issues.Add(new ValidationIssue(code, ValidationSeverities.Error, message, subject, item));

    public void Warn(string code, string message, string? subject = null, string? item = null) =>
        Issues.Add(new ValidationIssue(code, ValidationSeverities.Warning, message, subject, item));

    public bool Option(string name, bool fallback) =>
        request.Options.TryGetValue(name, out var text) && JsonSerializer.Deserialize<JsonElement>(text) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value
            ? value.GetBoolean()
            : fallback;

    /// <summary>Present subjects (a set's members in its place) of the given types, in the order given.</summary>
    public IReadOnlyList<ValidationSubject> Present(params string[] types) =>
        request.Subjects.SelectMany(s => s.Members ?? [s]).Where(s => !s.Absent && types.Contains(ValidatorDescriptor.TypeName(s.Type))).ToList();

    public static string Describe(ValidationSubject s) => s.From == "current" ? s.Name : $"{s.Name} (from {s.From})";

    public async Task<string> TextAsync(string reference)
    {
        if (texts.TryGetValue(reference, out var cached))
            return cached;
        await using var stream = (await store.GetAsync(new ArtifactRef(reference), ct)).Unwrap();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return texts[reference] = await reader.ReadToEndAsync(ct);
    }

    /// <summary>A subject in a library shape; one that does not read is a <c>malformed</c> issue, and null.</summary>
    public async Task<T?> ReadAsync<T>(ValidationSubject subject) where T : class
    {
        try
        {
            return ArtifactShapes.ReadLibrary<T>(await TextAsync(subject.Artifact!.Id), Describe(subject));
        }
        catch (FormatException e)
        {
            Error("malformed", e.Message, subject.Name);
            return null;
        }
    }

    /// <summary>An engine-shape artifact (evidence), or null when it does not read as <typeparamref name="T"/>.</summary>
    public async Task<T?> ReadEngineAsync<T>(string reference) where T : class
    {
        try
        {
            return JsonSerializer.Deserialize<T>(await TextAsync(reference), ArtifactShapes.Engine);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The plans among the subjects of the given types, read, with their structure checked: a scope, an id on every item, ids
    /// unique within the document (std's <c>rules</c>). Unreadable ones are left out.
    /// </summary>
    public async Task<List<Read<PlanDocument>>> PlansAsync(params string[] types)
    {
        var plans = new List<Read<PlanDocument>>();
        foreach (var subject in Present(types))
            if (await ReadAsync<PlanDocument>(subject) is { } plan)
            {
                if (plan.Scope?.Id is null || plan.Scope.Kind is null)
                    Error("malformed", $"{Describe(subject)} has no scope kind and id", subject.Name);
                foreach (var item in plan.Items.Where(i => string.IsNullOrEmpty(i.Id)))
                    Error("malformed", $"{Describe(subject)} has an item with no id ({item.Title ?? "untitled"})", subject.Name);
                foreach (var duplicate in plan.Items.Where(i => !string.IsNullOrEmpty(i.Id)).GroupBy(i => i.Id).Where(g => g.Count() > 1))
                    Error("duplicate_id", $"{Describe(subject)} uses id {duplicate.Key} {duplicate.Count()} times", subject.Name, duplicate.Key);
                plans.Add(new Read<PlanDocument>(subject, plan));
            }

        return plans;
    }

    /// <summary>The frozen revision of a repository, by its full name or (as a planner may write it) its name alone.</summary>
    public (string Repository, string Revision)? Frozen(string? repository)
    {
        foreach (var (name, revision) in request.Repositories.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (repository is not null && SameRepository(name, repository))
                return (name, revision);
        return null;
    }

    public static bool SameRepository(string a, string b) =>
        a == b || a.EndsWith("/" + b, StringComparison.Ordinal) || b.EndsWith("/" + a, StringComparison.Ordinal);

    /// <summary>
    /// The run whose step is checking: the run of the index entries of its own (<c>current</c>) subjects. The index also
    /// holds descendants' artifacts, which are not this run's evidence.
    /// </summary>
    public IReadOnlySet<string> OwnRuns()
    {
        var own = Present(Types.Requirements, Types.DeliveryPlan, Types.EvidenceClaims, Types.TestResults, Types.Triage)
            .Where(s => s.From == "current").Select(s => s.Artifact!.Id).ToHashSet(StringComparer.Ordinal);
        return request.Artifacts.Where(a => own.Contains(a.Ref)).Select(a => a.RunId).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>Index entries an engine action produced as evidence of a type (F7): never an agent's or a decision's.</summary>
    public IEnumerable<ArtifactRecord> EngineEvidence(string type, IReadOnlySet<string> actions) =>
        request.Artifacts.Where(a => a.Evidence && a.Action is { } action && actions.Contains(action) && ValidatorDescriptor.TypeName(a.Type) == type);

    public async Task<RepoFile?> FileAsync(string repository, string path, string revision)
    {
        if (files.TryGetValue((repository, path, revision), out var cached))
            return cached;
        var host = codeHost ?? throw new ProviderException(new ProviderError("internal_error", "tests_added needs a code host provider to read files"));
        return files[(repository, path, revision)] = (await host.ReadFileAsync(new RepoRef(repository), path, revision, ct)).Unwrap();
    }

    /// <summary>Whether <paramref name="revision"/> is <paramref name="head"/> or one of its ancestors, through the VCS provider's clones.</summary>
    public async Task<bool> ContainsAsync(string repository, string head, string revision)
    {
        if (head == revision)
            return true;
        if (vcs is null)
            return false;
        if (!ancestry.TryGetValue((repository, head, revision), out var contains))
        {
            // A revision the repository does not have (evidence of another repository, a commit since removed) is not contained.
            var result = await vcs.DescendsFromAsync(new RepoRef(repository), head, revision, ct);
            ancestry[(repository, head, revision)] = contains = result.Ok
                ? result.Value
                : result.Error!.Class == ProviderErrorClass.Infrastructure ? throw new ProviderException(result.Error) : false;
        }

        return contains;
    }

    public async Task<ArtifactRef> PutAsync(object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, ArtifactShapes.Engine);
        return (await store.PutAsync(new MemoryStream(bytes), ct)).Unwrap();
    }
}

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class StdValidatorProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("validator", "std", [], c => new StdValidators(
            () => c.Provider("artifact_store") as IArtifactStoreProvider,
            () => c.Provider("code_host") as ICodeHostProvider,
            () => c.Provider("vcs") as IVcsProvider)),
    ];
}
