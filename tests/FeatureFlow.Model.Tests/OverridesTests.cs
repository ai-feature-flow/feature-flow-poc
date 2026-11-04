namespace FeatureFlow.Model.Tests;

/// <summary>The run-time override catalogue (docs/05 section 5): default roles, the organisation's replacements, and variants.</summary>
public sealed class OverridesTests
{
    static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> None = new Dictionary<string, IReadOnlyList<string>>();

    [Fact]
    public void Every_key_of_docs_05_section_5_and_its_variants_is_in_the_catalogue()
    {
        string[] keys = ["answer", "recover", "breakpoint", "skip", "amend", "change_role", "grant_attempts", "override_library", "answer_question", "abort",
            "skip_protected", "amend_protected", "override_library_protected", "grant_attempts_beyond"];

        Assert.Equal(keys.Order(), Overrides.DefaultRoles.Keys.Order());
        Assert.All(Overrides.Variants.SelectMany(v => v.Value.Append(v.Key)), key => Assert.True(Overrides.IsKey(key), key));
    }

    [Theory]
    [InlineData("skip", "steerer,admin")]
    [InlineData("skip_protected", "admin")]
    [InlineData("amend_protected", "admin")]
    [InlineData("change_role", "admin")]
    [InlineData("grant_attempts", "steerer,admin")]
    [InlineData("grant_attempts_beyond", "admin")]
    [InlineData("override_library_protected", "admin")]
    [InlineData("abort", "steerer,admin")]
    [InlineData("answer", "")]
    public void A_key_has_its_default_roles_and_an_administrator_may_do_what_a_steerer_may(string key, string roles) =>
        Assert.Equal(roles.Split(',', StringSplitOptions.RemoveEmptyEntries), Overrides.RolesFor(key, None));

    [Fact]
    public void The_organisation_replaces_a_keys_roles_and_an_unknown_key_is_refused()
    {
        var organisation = new Dictionary<string, IReadOnlyList<string>> { ["skip_protected"] = ["lead"], ["abort"] = ["steerer"] };

        Assert.Equal(["lead"], Overrides.RolesFor("skip_protected", organisation));
        Assert.Equal(["steerer", "admin"], Overrides.RolesFor("abort", organisation));
        Assert.Equal(["steerer", "admin"], Overrides.RolesFor("skip", organisation));
        Assert.Throws<ArgumentException>(() => Overrides.RolesFor("skip_everything", organisation));
    }
}
