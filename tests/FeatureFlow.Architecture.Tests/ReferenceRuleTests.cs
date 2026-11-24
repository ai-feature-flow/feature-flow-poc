using System.Xml.Linq;

namespace FeatureFlow.Architecture.Tests;

/// <summary>Enforces docs/11 section 2.3 on the project files as they are on disk.</summary>
public class ReferenceRuleTests
{
    static readonly string Root = Repository.Root;

    [Fact]
    public void Every_project_under_src_and_hosts_obeys_the_reference_rules()
    {
        var violations = ProductProjects()
            .SelectMany(path => ReferenceRules.Violations(
                Path.GetFileNameWithoutExtension(path),
                References(path, "ProjectReference").Select(Path.GetFileNameWithoutExtension).OfType<string>(),
                References(path, "PackageReference")))
            .ToList();

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void No_src_project_references_the_test_doubles()
    {
        var offending = Directory.EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(path => References(path, "ProjectReference").Select(Path.GetFileNameWithoutExtension).Contains(ReferenceRules.TestDoubles))
            .Select(path => Path.GetRelativePath(Root, path))
            .ToList();

        Assert.Empty(offending);
    }

    [Fact]
    public void Every_project_of_the_layout_exists_and_is_in_the_solution()
    {
        var onDisk = ProductProjects().Select(Path.GetFileNameWithoutExtension).ToHashSet();
        var solution = File.ReadAllText(Path.Combine(Root, "FeatureFlow.slnx"));

        var missing = ReferenceRules.AllProjects.Where(p => !onDisk.Contains(p)).ToList();
        var notInSolution = Directory.EnumerateFiles(Root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !IsUnder(p, "spikes"))
            .Select(p => Path.GetRelativePath(Root, p).Replace('\\', '/'))
            .Where(p => !solution.Contains(p, StringComparison.Ordinal))
            .ToList();

        Assert.Empty(missing);
        Assert.Empty(notInSolution);
    }

    /// <summary>References added outside project files would escape the test above.</summary>
    [Fact]
    public void No_shared_build_file_adds_references_to_product_projects()
    {
        var shared = new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" }
            .SelectMany(name => Directory.EnumerateFiles(Root, name, SearchOption.AllDirectories))
            .Where(p => !IsUnder(p, "spikes") && !IsUnder(p, "tests"));

        var offending = shared
            .Where(p => XDocument.Load(p).Descendants().Any(e =>
                e.Name.LocalName is "ProjectReference" or "PackageReference" or "GlobalPackageReference"))
            .ToList();

        Assert.Empty(offending);
    }

    /// <summary>
    /// The test support project, which tests/Directory.Build.props gives every test project, references nothing, so no
    /// test reaches a product project through it.
    /// </summary>
    [Fact]
    public void Test_support_references_no_project_or_package()
    {
        var support = Path.Combine(Root, "tests", "FeatureFlow.Testing.Support", "FeatureFlow.Testing.Support.csproj");

        Assert.Empty(References(support, "ProjectReference").Concat(References(support, "PackageReference")));
    }

    [Theory]
    [InlineData("FeatureFlow.Validation", "FeatureFlow.Model", null)]
    [InlineData("FeatureFlow.Interpreter", "FeatureFlow.Validation", null)]
    [InlineData("FeatureFlow.Engine", "FeatureFlow.Interpreter", "Temporalio")]
    [InlineData("FeatureFlow.Providers.Git", "FeatureFlow.Providers.Abstractions", "LibGit2Sharp")]
    [InlineData("FeatureFlow.Host.AgentWorker", "FeatureFlow.Providers.Docker", null)]
    [InlineData("FeatureFlow.Host.AgentWorker", "FeatureFlow.Providers.Testing", null)]
    [InlineData("FeatureFlow.Cli", "FeatureFlow.Engine", null)]
    [InlineData("FeatureFlow.Testing.Workflows", "FeatureFlow.Interpreter", null)]
    public void Allowed_references_pass(string project, string reference, string? package)
    {
        Assert.Empty(ReferenceRules.Violations(project, [reference], package is null ? [] : [package]));
    }

    [Theory]
    [InlineData("FeatureFlow.Model", "FeatureFlow.Validation", null)]
    [InlineData("FeatureFlow.Interpreter", "FeatureFlow.Engine", null)]
    [InlineData("FeatureFlow.Interpreter", null, "Temporalio")]
    [InlineData("FeatureFlow.Model", null, "Temporalio.Extensions.Hosting")]
    [InlineData("FeatureFlow.Providers.Git", "FeatureFlow.Engine", null)]
    [InlineData("FeatureFlow.Engine", "FeatureFlow.Hosting", null)]
    [InlineData("FeatureFlow.Host.Api", "FeatureFlow.Engine", null)]
    [InlineData("FeatureFlow.Unlisted", null, null)]
    [InlineData("FeatureFlow.Testing.Workflows", null, "Temporalio")]
    [InlineData("FeatureFlow.Testing.Workflows", "FeatureFlow.Engine", null)]
    public void Forbidden_references_fail(string project, string? reference, string? package)
    {
        Assert.NotEmpty(ReferenceRules.Violations(project, reference is null ? [] : [reference], package is null ? [] : [package]));
    }

    static IEnumerable<string> ProductProjects() =>
        new[] { "src", "hosts" }.SelectMany(dir =>
            Directory.EnumerateFiles(Path.Combine(Root, dir), "*.csproj", SearchOption.AllDirectories));

    static IEnumerable<string> References(string projectPath, string kind) =>
        XDocument.Load(projectPath).Descendants()
            .Where(e => e.Name.LocalName == kind)
            .Select(e => (string?)e.Attribute("Include"))
            .OfType<string>()
            .Select(include => include.Replace('\\', '/'));

    static bool IsUnder(string path, string topDirectory) =>
        Path.GetRelativePath(Root, path).Replace('\\', '/').StartsWith(topDirectory + "/", StringComparison.Ordinal);
}
