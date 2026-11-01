using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Model.Tests;

public class YamlReadingTests
{
    [Fact]
    public void Scalars_keep_their_yaml_meaning()
    {
        var d = new DiagnosticBag();
        var node = (YMap)YamlText.Parse("a: ~\nb: true\nc: '3'\nd: 3\ne: \"~\"", "t.yaml", d)!;

        Assert.Empty(d);
        Assert.True(((YScalar)node["a"]!).IsNull);
        Assert.Equal(true, ((YScalar)node["b"]!).AsBool());
        Assert.Null(((YScalar)node["c"]!).AsLong());
        Assert.Equal(3, ((YScalar)node["d"]!).AsLong());
        Assert.False(((YScalar)node["e"]!).IsNull);
    }

    [Fact]
    public void Positions_are_recorded()
    {
        var node = (YMap)YamlText.Parse("a: 1\nsteps:\n  - id: x\n", "t.yaml", new DiagnosticBag())!;

        Assert.Equal(new SourceLocation("t.yaml", 3, 5), ((YSeq)node["steps"]!).Items[0].Location);
    }

    [Fact]
    public void Duplicate_keys_are_a_schema_error()
    {
        var d = new DiagnosticBag();

        YamlText.Parse("a: 1\na: 2\n", "t.yaml", d);

        Assert.Equal([Checks.Schema], d.Select(x => x.Check));
    }

    [Fact]
    public void Unknown_keys_are_refused()
    {
        var d = new DiagnosticBag();
        var r = new MapReader((YMap)YamlText.Parse("name: x\nnmae: y\n", "t.yaml", d)!, d, "a test map");

        r.String("name");
        r.Finish();

        var error = Assert.Single(d);
        Assert.Equal(Checks.Schema, error.Check);
        Assert.Contains("unknown key 'nmae'", error.Message);
        Assert.Equal(2, error.Location!.Line);
    }

    [Theory]
    [InlineData("std:roles/implementer", "std", null, "roles", "implementer")]
    [InlineData("acme@>=2.0.0:tasks/implement_ticket", "acme", ">=2.0.0", "tasks", "implement_ticket")]
    public void References_parse(string text, string library, string? constraint, string type, string name)
    {
        Assert.Equal(new LibraryReference(library, constraint, type, name), LibraryReference.TryParse(text));
    }

    [Theory]
    [InlineData("std:role/implementer")]
    [InlineData("roles/implementer")]
    [InlineData("std:roles/Implementer")]
    [InlineData("library/std/roles/implementer.yaml")]
    public void Malformed_references_do_not_parse(string text)
    {
        Assert.Null(LibraryReference.TryParse(text));
    }

    [Fact]
    public void Decision_type_patterns_match()
    {
        var pattern = new DecisionTypeItem("*.escalation", null, new SourceLocation("x", 1, 1));

        Assert.True(pattern.Matches("ticket.escalation"));
        Assert.False(pattern.Matches("escalation"));
        Assert.False(pattern.Matches("ticket.question"));
    }
}
