using FeatureFlow.Model;

namespace FeatureFlow.Providers.Abstractions;

/// <summary>
/// What a validator says about itself: its name, the artifact types it takes as subjects (by type name, such as
/// <c>requirements</c>; empty takes any), its options with their types (docs/03 section 5.4 types), and the artifact
/// types of the reports it may offer (such as <c>trace_report</c>).
/// </summary>
public sealed record ValidatorDescriptor(
    string Name,
    string Description,
    IReadOnlyList<string> SubjectTypes,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyList<string> Reports)
{
    /// <summary>
    /// Why a provider refuses <paramref name="request"/> for this validator with <c>validation_failed</c>, or null: a present
    /// subject (or set member) of a type it does not take, or an option it does not declare. Shared by every provider.
    /// </summary>
    public string? Refusal(ValidationRequest request)
    {
        foreach (var option in request.Options.Keys)
            if (!Options.ContainsKey(option))
                return $"{Name} has no option '{option}' ({string.Join(", ", Options.Keys.Order(StringComparer.Ordinal))})";
        if (SubjectTypes.Count == 0)
            return null;
        foreach (var subject in request.Subjects.SelectMany(s => s.Members ?? [s]).Where(s => !s.Absent))
            if (TypeName(subject.Type) is not { } type || !SubjectTypes.Contains(type))
                return $"{Name} does not take {subject.Name} of type {subject.Type ?? "(none)"} ({string.Join(", ", SubjectTypes)})";
        return null;
    }

    /// <summary>An artifact type's name: <c>requirements</c> for <c>std:artifact_types/requirements</c>.</summary>
    public static string? TypeName(string? type) => type is null ? null : Model.LibraryReference.TryParse(type)?.Name ?? type;
}

/// <summary>
/// A subject as a step named it: its name, where it came from (<c>current</c>, <c>parent</c>, <c>root</c> or a child's
/// step id), its declared type, and its reference. A subject marked optional that does not exist is passed
/// <see cref="Absent"/> (LOG 63). A set (<see cref="ArtifactSet"/>) is passed with its <see cref="Members"/>.
/// </summary>
public sealed record ValidationSubject(string Name, string? Type, ArtifactRef? Artifact, string From = "current",
    IReadOnlyList<ValidationSubject>? Members = null)
{
    public bool Absent => Artifact is null && Members is null;
}

/// <summary>
/// One validator call (docs/04 section 10.3). <see cref="Options"/> values are JSON text, as step inputs are.
/// <see cref="Repositories"/> are the run's repositories frozen at their work branch heads (repository to revision), so
/// evidence can be compared with exactly those commits. <see cref="Artifacts"/> are the artifacts of the run and its
/// descendants (the evidence index, for a trace across completed children); <see cref="Findings"/> the run tree's ledger.
/// A validator reads artifact content from the artifact store, repository files at frozen or recorded revisions from
/// the code host, and ancestry between such revisions from the VCS provider's clones, all given it at construction.
/// </summary>
public sealed record ValidationRequest(
    string Validator,
    IReadOnlyList<ValidationSubject> Subjects,
    IReadOnlyDictionary<string, string> Options,
    IReadOnlyDictionary<string, string> Repositories,
    IReadOnlyList<ArtifactRecord> Artifacts,
    IReadOnlyList<FindingRecord> Findings);

/// <summary>A validator's severities: an <c>error</c> makes the result invalid; a <c>warning</c> does not.</summary>
public static class ValidationSeverities
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>One problem a validator found: a stable code, a severity, a message, and the subject and item it concerns.</summary>
public sealed record ValidationIssue(string Code, string Severity, string Message, string? Subject = null, string? Item = null);

/// <summary>A report a validator offers, stored in the artifact store: its artifact type and reference. It becomes evidence.</summary>
public sealed record OfferedReport(string Type, ArtifactRef Artifact);

/// <summary>A validator's verdict: <see cref="Valid"/> exactly when no issue is an error; its issues; any reports it offers.</summary>
public sealed record ValidationResult(bool Valid, IReadOnlyList<ValidationIssue> Issues, IReadOnlyList<OfferedReport> Reports)
{
    public static ValidationResult Of(IReadOnlyList<ValidationIssue> issues, IReadOnlyList<OfferedReport>? reports = null) =>
        new(issues.All(i => i.Severity != ValidationSeverities.Error), issues, reports ?? []);
}

/// <summary>
/// The validator provider (docs/06 section 3.8; docs/04 section 10.2): named, deterministic checks over artifacts. The same
/// request gives the same result. The engine ships one with the standard set; an organisation adds its own as another
/// instance.
/// </summary>
public interface IValidatorProvider : IProvider
{
    IReadOnlyList<ValidatorDescriptor> Validators();

    /// <summary>
    /// Runs one validator. An unknown validator is <c>not_found</c>; a subject of a type it does not take, or an option it
    /// does not declare, is <c>validation_failed</c>. Problems in the subjects' content are issues in the result, not errors.
    /// </summary>
    Task<ProviderResult<ValidationResult>> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken);
}

public static class ValidatorProviders
{
    /// <summary>
    /// The validator provider instance that offers <paramref name="validator"/>: the default first, then the others by name.
    /// Null, with why, when none does.
    /// </summary>
    public static (string Name, IValidatorProvider Provider)? Find(ProviderSet providers, string validator, out string why)
    {
        var offered = new List<string>();
        foreach (var (name, instance) in providers.Instances("validator"))
            if (instance is IValidatorProvider provider)
            {
                var names = provider.Validators().Select(v => v.Name).ToList();
                if (names.Contains(validator))
                {
                    why = "";
                    return (name, provider);
                }

                offered.AddRange(names);
            }

        why = offered.Count == 0
            ? $"no validator provider is configured to run {validator}"
            : $"no validator provider offers {validator} (offered: {string.Join(", ", offered.Distinct().Order(StringComparer.Ordinal))})";
        return null;
    }
}

/// <summary>A check of an artifact against its type that could not be made: a validator no provider offers, or one that failed.</summary>
public sealed class ArtifactCheckException(string message, bool infrastructure) : Exception(message)
{
    /// <summary>The validator's provider failed for a reason outside the artifact (try again), not a configuration fault.</summary>
    public bool Infrastructure => infrastructure;
}

/// <summary>
/// The check of a produced artifact against its declared type (LOG 246): it reads in its format, fits its schema or
/// Markdown structure, and passes the type's own validators. One check for what an agent reports and what a person
/// submits or an <c>artifact.import</c> brings in.
/// </summary>
public static class ArtifactTypeCheck
{
    /// <summary>The artifact's errors, each naming it and its path; warnings do not count. Throws <see cref="ArtifactCheckException"/> when it cannot be checked.</summary>
    public static async Task<IReadOnlyList<string>> CheckAsync(ArtifactTypeItem type, string name, string declared, string path, string text, string reference,
        ProviderSet providers, CancellationToken ct)
    {
        var errors = ArtifactContent.Check(type, text, path).Select(e => $"artifact '{name}' ({path}): {e}").ToList();
        foreach (var validator in type.Validators)
        {
            var (provider, instance) = ValidatorProviders.Find(providers, validator, out var why)
                                       ?? throw new ArtifactCheckException($"artifact {name}'s type {declared} names validator {validator}: {why}", false);
            var result = await instance.ValidateAsync(new ValidationRequest(validator, [new ValidationSubject(name, declared, new ArtifactRef(reference))],
                new Dictionary<string, string>(), new Dictionary<string, string>(), [], []), ct);
            if (!result.Ok)
            {
                var error = result.Error!;
                if (error.Class == ProviderErrorClass.Infrastructure && !error.IsConfigurationFault)
                    throw new ArtifactCheckException($"validator {validator} ({provider}): {error.Reason}: {error.Message}", true);
                throw new ArtifactCheckException($"validator {validator} ({provider}) could not check artifact {name}: " +
                                                 (error.IsConfigurationFault ? error.DescribeFault() : $"{error.Reason}: {error.Message}"), false);
            }

            errors.AddRange(result.Value!.Issues.Where(i => i.Severity == ValidationSeverities.Error)
                .Select(i => $"artifact '{name}' ({path}): {(i.Item is null ? "" : i.Item + ": ")}{i.Message} [{validator}: {i.Code}]"));
        }

        return errors;
    }
}
