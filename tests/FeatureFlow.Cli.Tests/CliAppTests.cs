using FeatureFlow.Cli;

namespace FeatureFlow.Cli.Tests;

public class CliAppTests
{
    static readonly string StdRoot = FindStd();

    static string FindStd()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return Path.Combine(dir.FullName, "library", "std");
        throw new InvalidOperationException("FeatureFlow.slnx not found");
    }

    static (int Code, string Output, string Error) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CliApp.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void Version_prints_the_assembly_version_and_succeeds()
    {
        var (code, output, error) = Run("--version");

        Assert.Equal(0, code);
        Assert.StartsWith("ff 0.1.0", output);
        Assert.Empty(error);
    }

    [Theory]
    [InlineData()]
    [InlineData("validate")]
    [InlineData("show")]
    [InlineData("--version", "extra")]
    [InlineData("validate", "x", "--library")]
    [InlineData("frobnicate", "x")]
    public void Anything_else_prints_usage_and_fails(params string[] args)
    {
        var (code, output, error) = Run(args);

        Assert.Equal(2, code);
        Assert.Empty(output);
        Assert.Equal(CliApp.Usage.Trim(), error.Trim());
    }

    [Fact]
    public void Validate_a_library_succeeds_on_std()
    {
        var (code, output, _) = Run("validate", StdRoot);

        Assert.Equal(0, code);
        Assert.Equal("0 error(s), 0 warning(s)", output.Trim());
    }

    [Fact]
    public void Validate_a_template_succeeds_on_std()
    {
        var (code, output, _) = Run("validate", Path.Combine(StdRoot, "templates", "ticket_delivery.yaml"));

        Assert.Equal(0, code);
        Assert.Contains("0 error(s)", output);
    }

    [Fact]
    public void Validate_reports_errors_and_fails()
    {
        var copy = Path.Combine(Path.GetTempPath(), "ff-cli-" + Guid.NewGuid().ToString("N"));
        CopyDirectory(StdRoot, copy);
        try
        {
            var template = Path.Combine(copy, "templates", "ticket_delivery.yaml");
            File.WriteAllText(template, File.ReadAllText(template).Replace("type: ticket.spec_approval", "type: ticket.spec_approvel"));

            var (code, output, _) = Run("validate", copy);

            Assert.Equal(1, code);
            Assert.Contains("error check 16", output);
            Assert.Contains("1 error(s)", output);
        }
        finally
        {
            Directory.Delete(copy, recursive: true);
        }
    }

    [Fact]
    public void Show_prints_the_readable_form()
    {
        var (code, output, error) = Run("show", Path.Combine(StdRoot, "templates", "ticket_delivery.yaml"));

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.StartsWith("Template ticket_delivery (level ticket)", output);
        Assert.Contains("deliver.merge", output);
    }

    [Fact]
    public void Show_refuses_a_fragment()
    {
        var (code, _, error) = Run("show", Path.Combine(StdRoot, "fragments", "fix_loop.yaml"));

        Assert.Equal(2, code);
        Assert.Contains("takes a template", error);
    }

    [Fact]
    public void Test_runs_stds_workflow_tests_and_succeeds()
    {
        var count = Directory.GetFiles(Path.Combine(StdRoot, "tests"), "*.yaml").Length;

        var (code, output, error) = Run("test", Path.Combine(StdRoot, "tests"));

        Assert.Equal(0, code);
        Assert.Empty(error);
        Assert.Contains("pass ticket_main_path (completed)", output);
        Assert.EndsWith($"{count} passed, 0 failed", output.Trim());
    }

    [Fact]
    public void Test_reports_a_failing_test_and_fails()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ff-cli-{Guid.NewGuid():N}.yaml");
        File.WriteAllText(file, File.ReadAllText(Path.Combine(StdRoot, "tests", "ticket_main_path.yaml")).Replace("status: completed", "status: failed"));
        try
        {
            var (code, output, _) = Run("test", file, "--library", StdRoot, "--trace");

            Assert.Equal(1, code);
            Assert.Contains("FAIL ticket_main_path (completed)", output);
            Assert.Contains("status is completed, expected failed", output);
            Assert.Contains("    started deliver.merge: execution 1", output);
            Assert.EndsWith("0 passed, 1 failed", output.Trim());
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Test_of_a_missing_path_fails_with_usage_code()
    {
        var (code, _, error) = Run("test", Path.Combine(StdRoot, "no_such_tests"));

        Assert.Equal(2, code);
        Assert.Contains("no such file or folder", error);
    }

    static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
