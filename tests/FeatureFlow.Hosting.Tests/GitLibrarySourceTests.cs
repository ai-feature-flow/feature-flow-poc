using System.Diagnostics;
using FeatureFlow.Api;
using FeatureFlow.Model;

namespace FeatureFlow.Hosting.Tests;

/// <summary>
/// The <c>git</c> library source (docs/08 section 3) against a local bare repository holding the standard library: the one
/// resolver run start, answers and startup checks use. A branch is fetched as it is at each read; a tag stays put.
/// </summary>
public sealed class GitLibrarySourceTests : IDisposable
{
    const string Preamble = "prompts/reporting_preamble.md";

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-gitlib-" + Guid.NewGuid().ToString("N"));
    readonly string work, bare;

    public GitLibrarySourceTests()
    {
        work = Path.Combine(folder, "work");
        bare = Path.Combine(folder, "library.git");
        CopyTree(Path.Combine(FindRoot(), "library", "std"), work);
        Git(work, "init", "-q", "--initial-branch=main");
        Git(work, "add", "-A");
        Git(work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "std");
        Git(work, "tag", "v1");
        Git(folder, "clone", "-q", "--bare", work, bare);
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(folder, recursive: true);
    }

    LibrarySourceConfig Source(string? reference) => new("acme", "git", "", bare, reference);

    string Organisation => Path.Combine(folder, "organisation.yaml");

    [Fact]
    public async Task A_branch_is_read_as_it_is_now_and_a_tag_as_it_was()
    {
        var original = File.ReadAllText(Path.Combine(work, Preamble));

        var before = await LibrarySources.OpenAsync(Source("main"), Organisation, Ct);
        File.AppendAllText(Path.Combine(work, Preamble), "\nA line added on main.\n");
        Git(work, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-am", "change");
        Git(work, "push", "-q", bare, "main");
        var after = await LibrarySources.OpenAsync(Source("main"), Organisation, Ct);
        var tagged = await LibrarySources.OpenAsync(Source("v1"), Organisation, Ct);
        var defaultBranch = await LibrarySources.OpenAsync(Source(null), Organisation, Ct);

        Assert.Equal(original, before.Library!.Files.TextOf(Preamble));
        Assert.EndsWith("A line added on main.\n", after.Library!.Files.TextOf(Preamble));
        Assert.Equal(original, tagged.Library!.Files.TextOf(Preamble));
        Assert.Equal(after.Library.Files.TextOf(Preamble), defaultBranch.Library!.Files.TextOf(Preamble));
        Assert.Equal("std", after.Library.Manifest.Name);
    }

    [Fact]
    public async Task Nothing_is_left_behind_and_a_missing_ref_or_repository_says_which()
    {
        var temp = Directory.GetDirectories(Path.GetTempPath(), "ff-library-*").ToHashSet();

        var missingRef = await LibrarySources.OpenAsync(Source("no-such-branch"), Organisation, Ct);
        var missingRepository = await LibrarySources.OpenAsync(new LibrarySourceConfig("acme", "git", "", Path.Combine(folder, "nothing.git"), "main"), Organisation, Ct);
        Assert.NotNull((await LibrarySources.OpenAsync(Source("main"), Organisation, Ct)).Library);

        Assert.Null(missingRef.Library);
        Assert.Contains($"{bare} at no-such-branch does not clone", missingRef.Problem);
        Assert.Null(missingRepository.Library);
        Assert.Contains("nothing.git at main does not clone", missingRepository.Problem);
        Assert.Subset(temp, Directory.GetDirectories(Path.GetTempPath(), "ff-library-*").ToHashSet());
    }

    [Fact]
    public async Task A_repository_that_is_not_a_library_does_not_open()
    {
        var other = Path.Combine(folder, "other");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(other, "README.md"), "not a library");
        Git(other, "init", "-q", "--initial-branch=main");
        Git(other, "add", "-A");
        Git(other, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "x");

        var opened = await LibrarySources.OpenAsync(new LibrarySourceConfig("acme", "git", "", other, "main"), Organisation, Ct);

        Assert.Null(opened.Library);
        Assert.Contains("no library.yaml", opened.Problem);
    }

    [Theory]
    [InlineData("main", null)]
    [InlineData("no-such-branch", "organisation: libraries.acme: ")]
    public void Startup_checks_fetch_a_git_source(string reference, string? error)
    {
        File.WriteAllText(Path.Combine(folder, "installation.yaml"), """
            api_version: feature-flow/v1
            kind: installation
            development: true
            providers:
              agent_runtime: { default: scripted, instances: { scripted: { script: s.yaml } } }
            api:
              auth: { mode: local_token, token_ref: secrets/t }
            """);
        File.WriteAllText(Organisation, $$"""
            api_version: feature-flow/v1
            kind: organisation
            name: acme
            libraries: [{ name: acme, source: git, repository: "{{bare}}", ref: {{reference}} }]
            """);

        var thrown = Record.Exception(() => FeatureFlowConfiguration.Load(Path.Combine(folder, "installation.yaml"), Organisation));

        if (error is null)
            Assert.Null(thrown);
        else
            Assert.Contains(error + $"{bare} at {reference} does not clone", Assert.IsType<InvalidOperationException>(thrown).Message);
    }

    static void CopyTree(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    static void Git(string cwd, params string[] args)
    {
        Directory.CreateDirectory(cwd);
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args)
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)}: {error}");
    }

    static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "FeatureFlow.slnx")))
            dir = Path.GetDirectoryName(dir)!;
        return dir;
    }
}
