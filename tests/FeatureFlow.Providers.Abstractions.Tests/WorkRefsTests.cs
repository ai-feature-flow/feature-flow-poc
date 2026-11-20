namespace FeatureFlow.Providers.Abstractions.Tests;

/// <summary>The references the engine keeps beside a repository's branches (docs/04 section 3.7, LOG 96).</summary>
public sealed class WorkRefsTests
{
    const string Revision = "0123456789abcdef0123456789abcdef01234567";

    [Fact]
    public void A_superseded_reference_names_the_run_the_step_and_twelve_characters_of_the_revision()
    {
        Assert.Equal("refs/feature-flow/superseded/run_1/plan.a/0123456789ab", WorkRefs.Superseded("run/1", "plan.a", Revision));
        Assert.Equal("refs/feature-flow/superseded/r/s/abc", WorkRefs.Superseded("r", "s", "abc"));
    }

    [Fact]
    public void A_hidden_reference_names_the_run_and_the_step()
    {
        Assert.Equal("refs/feature-flow/wip/run_1/implement", WorkRefs.Hidden("run/1", "implement"));
    }
}
