namespace FeatureFlow.Providers.Abstractions.Tests;

/// <summary>Which image a container sandbox is made from (docs/06 section 3.1; LOG A5a-1, A5a-2).</summary>
public sealed class SandboxImagesTests
{
    const string Own = "feature-flow-sandbox:1";

    static SandboxSpec Spec(string? image) => new("run/step/1", "/ff/home", new Dictionary<string, string>(), [], image);

    [Fact]
    public void A_spec_naming_no_image_or_the_sandboxs_own_gets_the_sandboxs_own()
    {
        Assert.Equal(Own, SandboxImages.Choose(Spec(null), Own, []).Value);
        Assert.Equal(Own, SandboxImages.Choose(Spec(Own), Own, []).Value);
    }

    [Fact]
    public void A_repository_image_is_used_when_an_allowed_pattern_matches_it_and_refused_for_a_person_otherwise()
    {
        string[] allowed = ["registry.acme/feature-flow/*", "acme-api-sandbox:2"];

        Assert.Equal("registry.acme/feature-flow/api:3", SandboxImages.Choose(Spec("registry.acme/feature-flow/api:3"), Own, allowed).Value);
        Assert.Equal("acme-api-sandbox:2", SandboxImages.Choose(Spec("acme-api-sandbox:2"), Own, allowed).Value);
        var refused = SandboxImages.Choose(Spec("docker.io/someone/else:1"), Own, allowed);
        Assert.Equal("cannot_answer", refused.Error?.Reason);
        Assert.Contains("docker.io/someone/else:1", refused.Error!.Message, StringComparison.Ordinal);
        // With no allowed_images, only the sandbox's own image.
        Assert.Equal("cannot_answer", SandboxImages.Choose(Spec("acme-api-sandbox:2"), Own, []).Error?.Reason);
    }
}
