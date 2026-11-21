using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Providers.Abstractions.Tests;

/// <summary>A provider instance's settings, as its factory reads them: a malformed setting is refused, never ignored.</summary>
public sealed class ProviderContextTests
{
    static ProviderContext Context(string settings) =>
        new(new ProviderInstanceConfig("sandbox", "docker", "docker", (YMap)YamlWriter.Read(settings, "settings")), ".", _ => null);

    [Fact]
    public void A_list_setting_is_read_and_is_empty_when_absent()
    {
        Assert.Equal(["acme/api:*", "base:1"], Context("""{ allowed_images: ["acme/api:*", "base:1"] }""").SettingList("allowed_images"));
        Assert.Empty(Context("{ image: base:1 }").SettingList("allowed_images"));
    }

    [Theory]
    [InlineData("""{ allowed_images: ["acme/api:*", null] }""", "providers.sandbox.instances.docker.allowed_images[1] must be a string")]
    [InlineData("""{ allowed_images: [{ name: x }] }""", "providers.sandbox.instances.docker.allowed_images[0] must be a string")]
    [InlineData("""{ allowed_images: [["a"]] }""", "providers.sandbox.instances.docker.allowed_images[0] must be a string")]
    [InlineData("""{ allowed_images: "acme/api:*" }""", "providers.sandbox.instances.docker.allowed_images must be a list of strings")]
    public void A_list_setting_with_an_item_that_is_not_a_string_is_refused_with_its_path(string settings, string message) =>
        Assert.StartsWith(message, Assert.Throws<InvalidOperationException>(() => Context(settings).SettingList("allowed_images")).Message, StringComparison.Ordinal);
}
