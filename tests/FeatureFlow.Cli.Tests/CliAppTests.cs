using FeatureFlow.Cli;

namespace FeatureFlow.Cli.Tests;

public class CliAppTests
{
    [Fact]
    public void Version_prints_the_assembly_version_and_succeeds()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CliApp.Run(["--version"], output, error);

        Assert.Equal(0, exitCode);
        Assert.StartsWith("ff 0.1.0", output.ToString());
        Assert.Empty(error.ToString());
    }

    [Theory]
    [InlineData()]
    [InlineData("validate")]
    [InlineData("--version", "extra")]
    public void Anything_else_prints_usage_and_fails(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var exitCode = CliApp.Run(args, output, error);

        Assert.Equal(2, exitCode);
        Assert.Empty(output.ToString());
        Assert.Equal(CliApp.Usage, error.ToString().Trim());
    }
}
