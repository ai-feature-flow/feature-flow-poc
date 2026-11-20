using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model.Tests;

/// <summary>Durations (docs/03 section 2.1): <see cref="Durations.Parse"/>, and the stricter rule for plan limits.</summary>
public class DurationsTests
{
    public static TheoryData<string, TimeSpan> Valid => new()
    {
        { "500ms", TimeSpan.FromMilliseconds(500) },
        { "30s", TimeSpan.FromSeconds(30) },
        { "5m", TimeSpan.FromMinutes(5) },
        { "2h", TimeSpan.FromHours(2) },
        { "7d", TimeSpan.FromDays(7) },
        { "1.5h", TimeSpan.FromMinutes(90) },
        { "0.25s", TimeSpan.FromMilliseconds(250) },
    };

    [Theory]
    [MemberData(nameof(Valid))]
    public void A_number_and_a_unit_is_a_duration(string text, TimeSpan expected) => Assert.Equal(expected, Durations.Parse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("5")]
    [InlineData("ms")]
    [InlineData("5x")]
    [InlineData("-1m")]
    [InlineData("1e400s")]
    public void Anything_else_is_not(string? text) => Assert.Null(Durations.Parse(text));

    [Theory]
    [InlineData("30m", true)]
    [InlineData("4h", true)]
    [InlineData("1.5h", false)]
    [InlineData("500ms", false)]
    public void A_plan_limit_is_a_whole_number_of_seconds_minutes_hours_or_days(string timeout, bool valid)
    {
        var d = new DiagnosticBag();
        StepReader.ReadLimits(YamlText.Parse($"timeout: {timeout}", "plan.yaml", d), d, "s");
        Assert.Equal(valid, !d.HasErrors);
    }
}
