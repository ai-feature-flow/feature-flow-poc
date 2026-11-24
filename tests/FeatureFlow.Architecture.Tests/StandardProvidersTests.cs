using FeatureFlow.Providers.Standard;

namespace FeatureFlow.Architecture.Tests;

/// <summary>docs/06 section 4: <see cref="StandardProviders"/> is every standard provider package's factories in one list.</summary>
public class StandardProvidersTests
{
    /// <summary>
    /// Each project under <c>src/Providers</c> that registers factories contributes at least one to the list, so a new
    /// provider package cannot be left out of the hosts and distributions. Testing (the doubles, development only) and
    /// Standard itself are not standard providers.
    /// </summary>
    [Fact]
    public void StandardProviders_lists_every_standard_provider_package()
    {
        var packages = Directory.GetDirectories(Path.Combine(Repository.Root, "src", "Providers"))
            .Select(Path.GetFileName).OfType<string>()
            .Except([ReferenceRules.TestDoubles, ReferenceRules.Standard])
            .Order(StringComparer.Ordinal)
            .ToList();
        var contributing = StandardProviders.Factories
            .Select(f => f.Create.Method.DeclaringType!.Assembly.GetName().Name).OfType<string>()
            .Distinct().Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(packages, contributing);
    }
}
