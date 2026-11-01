namespace FeatureFlow.Validation.Tests;

/// <summary>
/// The readable forms of the three std templates are golden files (docs/09 section 4.2). To accept a
/// deliberate change, run the tests with <c>FF_UPDATE_GOLDEN=1</c> and review the diff.
/// </summary>
public class ReadableFormTests
{
    [Theory]
    [InlineData("ticket_delivery")]
    [InlineData("epic_delivery")]
    [InlineData("project_delivery")]
    public void Readable_form_matches_its_golden_file(string template)
    {
        var result = LibraryValidator.ValidateLibrary(Std.Root);
        var rendered = ReadableForm.Render(result.Plans[$"std:templates/{template}"]);
        var golden = Path.Combine(Std.GoldenFolder, $"{template}.txt");

        if (Environment.GetEnvironmentVariable("FF_UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Std.GoldenFolder);
            File.WriteAllText(golden, rendered);
        }

        Assert.True(File.Exists(golden), $"no golden file {golden}; run with FF_UPDATE_GOLDEN=1 to create it");
        Assert.Equal(File.ReadAllText(golden).ReplaceLineEndings("\n"), rendered);
    }

    [Fact]
    public void A_change_to_a_template_changes_its_readable_form()
    {
        var before = ReadableForm.Render(LibraryValidator.ValidateLibrary(Std.Root).Plans["std:templates/ticket_delivery"]);
        var after = ReadableForm.Render(Std.Mutate(new Std.Edit("templates/ticket_delivery.yaml",
            "      rejected: { to: plan, clean_up: true }", "      rejected: { to: implement }")).Plans["std:templates/ticket_delivery"]);

        Assert.NotEqual(before, after);
        Assert.Contains("rejected->implement", after);
    }
}
