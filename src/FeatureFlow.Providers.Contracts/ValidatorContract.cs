using System.Text.Json;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Contracts;

/// <summary>A request and the verdict it must get: <paramref name="Code"/>, when given, is a code an error issue must carry.</summary>
public sealed record ValidatorFixture(string Description, ValidationRequest Request, bool Valid, string? Code = null);

/// <summary>
/// The validator provider's contract (docs/06 section 3.8, docs/04 section 10.2): every validator it describes has a
/// passing and a failing fixture and meets them, the verdict agrees with the issues, the same request gives the same
/// result, offered reports are of the types described, and unknown validators and options are refused. A provider's test
/// project subclasses it with its fixtures.
/// </summary>
public abstract class ValidatorContract : ContractBase
{
    protected abstract IValidatorProvider Validators { get; }

    protected abstract IReadOnlyList<ValidatorFixture> Fixtures { get; }

    static string Json(ValidationResult result) => JsonSerializer.Serialize(result);

    [Fact]
    public void Every_validator_is_named_once_and_has_a_passing_and_a_failing_fixture()
    {
        var names = Validators.Validators().Select(v => v.Name).ToList();

        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
        Assert.Equal(names.Distinct(StringComparer.Ordinal).Count(), names.Count);
        foreach (var name in names)
        {
            Assert.True(Fixtures.Any(f => f.Request.Validator == name && f.Valid), $"{name} has no passing fixture");
            Assert.True(Fixtures.Any(f => f.Request.Validator == name && !f.Valid), $"{name} has no failing fixture");
        }
    }

    [Fact]
    public async Task Every_fixture_gets_its_verdict_and_the_verdict_agrees_with_the_issues()
    {
        foreach (var fixture in Fixtures)
        {
            var result = Ok(await Validators.ValidateAsync(fixture.Request, Ct));
            var errors = result.Issues.Where(i => i.Severity == ValidationSeverities.Error).ToList();

            Assert.True(fixture.Valid == result.Valid, $"{fixture.Description}: expected {(fixture.Valid ? "valid" : "invalid")}, got {Json(result)}");
            Assert.True(result.Valid == (errors.Count == 0), $"{fixture.Description}: the verdict disagrees with the issues: {Json(result)}");
            Assert.All(result.Issues, i =>
            {
                Assert.False(string.IsNullOrWhiteSpace(i.Code), $"{fixture.Description}: an issue has no code");
                Assert.Contains(i.Severity, new[] { ValidationSeverities.Error, ValidationSeverities.Warning });
            });
            if (fixture.Code is { } code)
                Assert.True(errors.Any(e => e.Code == code), $"{fixture.Description}: no error with code {code}: {Json(result)}");
        }
    }

    [Fact]
    public async Task The_same_request_gives_the_same_result()
    {
        foreach (var fixture in Fixtures)
            Assert.Equal(Json(Ok(await Validators.ValidateAsync(fixture.Request, Ct))), Json(Ok(await Validators.ValidateAsync(fixture.Request, Ct))));
    }

    [Fact]
    public async Task Offered_reports_are_of_the_types_the_validator_describes()
    {
        var descriptors = Validators.Validators().ToDictionary(v => v.Name);
        foreach (var fixture in Fixtures)
            foreach (var report in Ok(await Validators.ValidateAsync(fixture.Request, Ct)).Reports)
                Assert.Contains(report.Type, descriptors[fixture.Request.Validator].Reports);
    }

    [Fact]
    public async Task An_unknown_validator_is_not_found()
    {
        var request = Fixtures[0].Request with { Validator = "no_such_validator" };

        Refused(await Validators.ValidateAsync(request, Ct), "not_found");
    }

    [Fact]
    public async Task An_option_the_validator_does_not_declare_is_refused()
    {
        var request = Fixtures[0].Request;
        request = request with { Options = new Dictionary<string, string>(request.Options) { ["no_such_option"] = "true" } };

        Refused(await Validators.ValidateAsync(request, Ct), "validation_failed");
    }
}
