using System.Text.Json;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>
/// A validator provider in memory (docs/06 section 3.8), for engine tests before the standard validators exist and for
/// checks of <c>artifact.validate</c> itself. It offers <c>scripted</c>, whose verdict is its <c>valid</c> option (default
/// true) and whose error issue has the <c>code</c> option as its code; a test adds its own validators with <see cref="Add"/>.
/// </summary>
public sealed class InMemoryValidators : IValidatorProvider
{
    public const string Scripted = "scripted";

    readonly Lock gate = new();
    readonly List<(ValidatorDescriptor Descriptor, Func<ValidationRequest, ValidationResult> Run)> validators = [];

    public InMemoryValidators() =>
        Add(new ValidatorDescriptor(Scripted, "Valid or not as its options say", [], new Dictionary<string, string> { ["valid"] = "boolean", ["code"] = "string" }, []),
            request =>
            {
                var valid = request.Options.TryGetValue("valid", out var v) ? JsonSerializer.Deserialize<bool>(v) : true;
                var code = request.Options.TryGetValue("code", out var c) ? JsonSerializer.Deserialize<string>(c) ?? "scripted" : "scripted";
                return ValidationResult.Of(valid ? [] : [new ValidationIssue(code, ValidationSeverities.Error, "scripted to fail", request.Subjects.FirstOrDefault()?.Name)]);
            });

    /// <summary>Adds a validator; a later one with the same name replaces it.</summary>
    public InMemoryValidators Add(ValidatorDescriptor descriptor, Func<ValidationRequest, ValidationResult> run)
    {
        lock (gate)
        {
            validators.RemoveAll(v => v.Descriptor.Name == descriptor.Name);
            validators.Add((descriptor, run));
        }

        return this;
    }

    public ProviderDescription Describe() => new("validator", "memory", "0.1.0", new NoCapabilities());

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public IReadOnlyList<ValidatorDescriptor> Validators()
    {
        lock (gate)
            return validators.Select(v => v.Descriptor).ToList();
    }

    public Task<ProviderResult<ValidationResult>> ValidateAsync(ValidationRequest request, CancellationToken cancellationToken)
    {
        (ValidatorDescriptor Descriptor, Func<ValidationRequest, ValidationResult> Run) validator;
        lock (gate)
        {
            var found = validators.FindIndex(v => v.Descriptor.Name == request.Validator);
            if (found < 0)
                return Task.FromResult(ProviderResult<ValidationResult>.Failure("not_found", $"no validator {request.Validator}"));
            validator = validators[found];
        }

        return Task.FromResult(validator.Descriptor.Refusal(request) is { } refusal
            ? ProviderResult<ValidationResult>.Failure("validation_failed", refusal)
            : ProviderResult<ValidationResult>.Success(validator.Run(request)));
    }
}
