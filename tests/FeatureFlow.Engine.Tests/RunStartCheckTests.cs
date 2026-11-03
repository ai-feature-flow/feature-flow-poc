using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Library;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Engine.Tests;

/// <summary>
/// The run-start checks that read the code host (docs/09 section 2): 23 (F1), 24 (default branch protection) and 20 (a
/// repository's library section adds only), on std's <c>ticket_delivery</c> and the in-memory code host.
/// </summary>
public sealed class RunStartCheckTests
{
    static readonly string StdRoot = Path.Combine(FindRoot(), "library", "std");

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "FeatureFlow.slnx")))
                return dir.FullName;
        throw new InvalidOperationException("FeatureFlow.slnx not found");
    }

    sealed record Setup(InMemoryForge Forge, RepoRef Repo)
    {
        public static Setup Protected(string? manifest = null)
        {
            var forge = new InMemoryForge();
            var repo = forge.CreateRepository("acme/api", manifest is null ? null : new Dictionary<string, string> { ["feature-flow.yaml"] = manifest });
            forge.Protect(repo, new BranchProtection("main", true, true, 1, [], false));
            return new Setup(forge, repo);
        }
    }

    static async Task<DiagnosticBag> CheckAsync(Setup s, bool? engineCanMerge, IReadOnlyList<string>? engineMayMerge = null, string? parentBranch = null,
        string template = "std:templates/ticket_delivery")
    {
        var d = new DiagnosticBag();
        var libraries = new LibrarySet(Library.Library.Open(StdRoot, d)!,
            Library.Library.Open(Path.Combine(FindRoot(), "tests", "FeatureFlow.Integration.Tests", "libraries", "m5"), d)!);
        var parameters = new Dictionary<string, YNode> { ["ticket_id"] = YamlWriter.Read("T-1") };
        if (engineCanMerge is { } ecm)
            parameters["engine_can_merge"] = YamlWriter.Read(ecm ? "true" : "false");
        var (core, loaded) = RunCore.Start(libraries, LibraryReference.TryParse(template)!, new RunInfo("run-1", [s.Repo.FullName], parameters),
            new RunOptions { BranchFromPattern = true });
        Assert.DoesNotContain(loaded, x => x.Blocks);
        var start = new RunStart("run-1", "std:templates/ticket_delivery", [s.Repo.FullName], new Dictionary<string, string>(), "lock", false, "tester",
            ParentBranch: parentBranch);
        var policy = new RunPolicy(3, 10, 200, null, null, [], EngineMayMerge: engineMayMerge ?? []);
        var checks = new DiagnosticBag();
        await RunActivities.RepositoryChecksAsync(new InMemoryCodeHost(s.Forge), core!, start, libraries, policy, checks, TestContext.Current.CancellationToken);
        return checks;
    }

    [Fact]
    public async Task F1_a_standalone_ticket_asking_for_an_engine_merge_into_the_default_branch_is_refused_at_run_start()
    {
        var refused = await CheckAsync(Setup.Protected(), engineCanMerge: true);

        var f1 = Assert.Single(refused);
        Assert.Equal((Checks.EngineMerge, true), (f1.Check, f1.Blocks));
        Assert.Contains("deliver.merge asks for an engine merge into acme/api's default branch main", f1.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task F1_controls_the_default_false_the_organisations_leave_and_a_childs_parent_branch_all_pass()
    {
        Assert.Empty(await CheckAsync(Setup.Protected(), engineCanMerge: false));
        Assert.Empty(await CheckAsync(Setup.Protected(), engineCanMerge: null));                        // std's default is false
        Assert.Empty(await CheckAsync(Setup.Protected(), engineCanMerge: true, engineMayMerge: ["acme/*"]));
        Assert.Single(await CheckAsync(Setup.Protected(), engineCanMerge: true, engineMayMerge: ["acme/web"]));
        Assert.Empty(await CheckAsync(Setup.Protected(), engineCanMerge: true, parentBranch: "ff/epic/E-1"));
    }

    [Fact]
    public async Task F1_still_applies_when_a_start_names_the_default_branch_as_the_parent_branch()
    {
        var refused = await CheckAsync(Setup.Protected(), engineCanMerge: true, parentBranch: "main");

        Assert.Equal(Checks.EngineMerge, Assert.Single(refused).Check);
    }

    [Fact]
    public async Task F1_reads_the_default_branch_from_the_code_host_not_main()
    {
        var s = Setup.Protected();
        s.Forge.SetReference(s.Repo, "trunk", s.Forge.Resolve(s.Repo, "main"));
        s.Forge.SetDefaultBranch(s.Repo, "trunk");
        s.Forge.Protect(s.Repo, new BranchProtection("trunk", true, true, 1, [], false));

        var refused = await CheckAsync(s, engineCanMerge: true);

        Assert.Contains("default branch trunk", Assert.Single(refused).Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_work_branch_that_is_the_default_or_parent_branch_is_refused_at_run_start()
    {
        var main = await CheckAsync(Setup.Protected(), engineCanMerge: null, template: "m5:templates/branch_main");
        var parent = await CheckAsync(Setup.Protected(), engineCanMerge: false, parentBranch: "ff/ticket/T-1");

        Assert.Equal(Checks.WorkBranch, Assert.Single(main).Check);
        Assert.Contains("work branch 'main' is its default branch", main[0].Message, StringComparison.Ordinal);
        Assert.Contains("work branch 'ff/ticket/T-1' is its parent branch", Assert.Single(parent).Message, StringComparison.Ordinal);
        Assert.Empty(await CheckAsync(Setup.Protected(), engineCanMerge: false));          // control: ff/ticket/T-1 from main
    }

    [Fact]
    public async Task Check_24_an_unprotected_default_branch_is_refused()
    {
        var s = Setup.Protected();
        s.Forge.Protect(s.Repo, new BranchProtection("main", false, false, 0, [], false));

        var refused = await CheckAsync(s, engineCanMerge: false);

        Assert.Equal(Checks.BranchProtection, Assert.Single(refused).Check);
    }

    [Fact]
    public async Task Check_20_a_repository_library_section_that_overrides_is_refused_and_one_that_adds_passes()
    {
        const string adds = """
            api_version: feature-flow/v1
            kind: repository
            library:
              default_template: std:templates/ticket_delivery
              adds: { skills: [{ name: acme_conventions, path: .feature-flow/skills/acme_conventions }] }
            """;
        var overrides = adds.Replace("acme_conventions", "verification", StringComparison.Ordinal);

        Assert.Empty(await CheckAsync(Setup.Protected(adds), engineCanMerge: false));
        var refused = await CheckAsync(Setup.Protected(overrides), engineCanMerge: false);
        Assert.Equal(Checks.RepositoryLibraryAddsOnly, Assert.Single(refused).Check);
        Assert.Contains("std:skills/verification", refused[0].Message, StringComparison.Ordinal);
    }
}
