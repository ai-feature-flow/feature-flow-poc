using System.Text.Json;
using FeatureFlow.Providers.Abstractions;

namespace Acme.Providers;

/// <summary>
/// Acme's validator provider (docs/06 section 3.8), kind <c>acme</c>: offers <c>acme_line_limit</c>, which reports an error when a
/// subject has more lines than its <c>max_lines</c> option (default 20). It reads the subjects from the installation's
/// artifact store, as every validator does. A distribution adds it beside the standard validators, with no fork.
/// </summary>
public sealed class AcmeValidators(Func<IArtifactStoreProvider?> artifacts) : IValidatorProvider
{
    public const string LineLimit = "acme_line_limit";

    static int calls;

    /// <summary>How many times <c>acme_line_limit</c> has run in this process: a host's test reads it to see the provider was used.</summary>
    public static int Calls => Volatile.Read(ref calls);

    static readonly ValidatorDescriptor Descriptor = new(LineLimit, "No subject longer than max_lines lines", [],
        new Dictionary<string, string> { ["max_lines"] = "number" }, []);

    public ProviderDescription Describe() => new("validator", "acme", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public IReadOnlyList<ValidatorDescriptor> Validators() => [Descriptor];

    public async Task<ProviderResult<ValidationResult>> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        if (request.Validator != LineLimit)
            return ProviderResult<ValidationResult>.Failure("not_found", $"no validator {request.Validator}");
        if (Descriptor.Refusal(request) is { } refusal)
            return ProviderResult<ValidationResult>.Failure("validation_failed", refusal);
        if (artifacts() is not { } store)
            return ProviderResult<ValidationResult>.Failure("internal_error", "acme_line_limit needs the artifact store");
        Interlocked.Increment(ref calls);

        var max = request.Options.TryGetValue("max_lines", out var m) ? JsonSerializer.Deserialize<int>(m) : 20;
        var issues = new List<ValidationIssue>();
        foreach (var subject in request.Subjects.SelectMany(s => s.Members ?? [s]).Where(s => !s.Absent))
        {
            var read = await store.GetAsync(subject.Artifact!, cancellationToken);
            if (!read.Ok)
                return ProviderResult<ValidationResult>.Failure(read.Error!.Reason, read.Error.Message);
            using var reader = new StreamReader(read.Value!);
            var lines = (await reader.ReadToEndAsync(cancellationToken)).TrimEnd('\n').Split('\n').Length;
            if (lines > max)
                issues.Add(new ValidationIssue("too_long", ValidationSeverities.Error, $"{subject.Name} has {lines} lines; at most {max} are allowed", subject.Name));
        }

        return ProviderResult<ValidationResult>.Success(ValidationResult.Of(issues));
    }
}

/// <summary>Acme's provider factories, which its host composes after the standard ones.</summary>
public static class AcmeProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("validator", "acme", [], c => new AcmeValidators(() => c.Provider("artifact_store") as IArtifactStoreProvider)),
    ];
}
