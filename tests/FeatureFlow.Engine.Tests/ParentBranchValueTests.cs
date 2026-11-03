using FeatureFlow.Interpreter;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;

namespace FeatureFlow.Engine.Tests;

/// <summary><c>run.parent_branch</c> (docs/03 section 5): the named parent branch, else the default branch run start read.</summary>
public sealed class ParentBranchValueTests
{
    static string Root()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("FeatureFlow.slnx not found");
    }

    static string Render(RunInfo run)
    {
        var d = new DiagnosticBag();
        var root = Root();
        var libraries = new LibrarySet(Library.Library.Open(Path.Combine(root, "library", "std"), d)!,
            Library.Library.Open(Path.Combine(root, "tests", "FeatureFlow.Integration.Tests", "libraries", "m5"), d)!);
        var (core, loaded) = RunCore.Start(libraries, LibraryReference.TryParse("m5:templates/where")!, run);
        Assert.DoesNotContain(loaded, x => x.Blocks);
        return core!.RenderPrompt("open_pr", "m5:prompts/where")!.Trim();
    }

    static RunInfo Run(string? parent = null, string? defaultBranch = null) =>
        new("run-1", ["acme/api"], new Dictionary<string, YNode>(), ParentBranch: parent,
            DefaultBranches: defaultBranch is null ? null : new Dictionary<string, string> { ["acme/api"] = defaultBranch });

    [Fact]
    public void It_is_the_default_branch_run_start_read_from_the_code_host()
    {
        Assert.Equal("trunk", Render(Run(defaultBranch: "trunk")));
        Assert.Equal("ff/epic/E-1", Render(Run(parent: "ff/epic/E-1", defaultBranch: "trunk")));
        Assert.Equal("main", Render(Run()));                 // no code host at run start
    }

    [Fact]
    public void The_default_branches_survive_continue_as_new()
    {
        var run = Run(defaultBranch: "trunk");

        var carried = CarriedRunInfo.From(run).ToRunInfo();

        Assert.Equal("trunk", carried.DefaultBranches!["acme/api"]);
    }
}
