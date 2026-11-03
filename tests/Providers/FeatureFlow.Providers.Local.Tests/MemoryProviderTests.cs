using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local.Tests;

/// <summary>The in-memory source control fakes (M5), beyond what the contract suites check.</summary>
public sealed class MemoryProviderTests
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_scripted_fault_fails_the_next_call_only()
    {
        var forge = new InMemoryForge();
        var repo = forge.CreateRepository("o/r");
        var vcs = new InMemoryVcs(forge);
        var main = forge.Resolve(repo, "main")!;
        forge.Fail("vcs.Push", new ProviderError("unreachable", "scripted"));

        Assert.Equal("unreachable", (await vcs.PushAsync(repo, "refs/heads/w", main, null, null, Ct)).Error!.Reason);
        Assert.True((await vcs.PushAsync(repo, "refs/heads/w", main, null, null, Ct)).Ok);
    }

    [Fact]
    public async Task A_working_copy_is_checked_out_changed_committed_and_pushed()
    {
        var forge = new InMemoryForge();
        var repo = forge.CreateRepository("o/r");
        var vcs = new InMemoryVcs(forge);
        var main = forge.Resolve(repo, "main")!;
        var copy = new WorkingCopyRef(new SandboxHandle("s1", "memory"), "/work");

        (await vcs.CheckOutAsync(repo, "refs/heads/main", main, "ff/w", copy, Ct)).Unwrap();
        Assert.False((await vcs.IsDirtyAsync(copy, Ct)).Unwrap());
        Assert.Null((await vcs.CommitAllAsync(copy, "nothing", Ct)).Unwrap());
        forge.WorkingFiles(copy)!["a.txt"] = "agent's change";
        Assert.True((await vcs.IsDirtyAsync(copy, Ct)).Unwrap());
        var head = (await vcs.CommitAllAsync(copy, "change", Ct)).Unwrap()!;
        var bundle = (await vcs.FetchCommitsAsync(copy, main, Ct)).Unwrap();
        (await vcs.PushAsync(repo, "refs/heads/ff/w", bundle.Head, bundle, null, Ct)).Unwrap();

        Assert.Equal((head, 1), (bundle.Head, bundle.Commits));
        Assert.Equal(["a.txt"], (await vcs.ChangedFilesAsync(copy, main, head, Ct)).Unwrap());
        Assert.Equal("agent's change", forge.ReadFile(repo, "ff/w", "a.txt"));
    }

    [Fact]
    public async Task A_ci_run_reports_a_check_under_its_workflow_until_it_completes()
    {
        var forge = new InMemoryForge();
        var repo = forge.CreateRepository("o/r");
        var folder = Path.Combine(Path.GetTempPath(), "ff-memory-ci-" + Guid.NewGuid().ToString("N"));
        var ci = new InMemoryCi(forge, () => new FileSystemArtifactStore(folder));
        var host = new InMemoryCodeHost(forge);
        var head = forge.Resolve(repo, "main")!;
        forge.ScriptCi("ci.yml", new CiScript(Conclusions.Failure, [], PollsUntilDone: 1));
        try
        {
            var run = (await ci.TriggerAsync(repo, new CiJobRef("ci.yml", new Dictionary<string, string>()), head, Ct)).Unwrap();
            var before = (await host.GetCheckStatusAsync(repo, head, Ct)).Unwrap().Checks.Single();
            Assert.Equal("validation_failed", (await ci.ReadResultsAsync(run, Ct)).Error!.Reason);
            var status = (await ci.GetRunStatusAsync(run, Ct)).Unwrap();
            var after = (await host.GetCheckStatusAsync(repo, head, Ct)).Unwrap().Checks.Single();

            Assert.Equal((CheckStates.InProgress, "ci.yml"), (before.State, before.Suite));
            Assert.Equal(new CiRunStatus(CheckStates.Completed, Conclusions.Failure), status);
            Assert.Equal((CheckStates.Completed, Conclusions.Failure), (after.State, after.Conclusion));
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Providers_configured_with_one_world_share_it()
    {
        var world = "test-" + Guid.NewGuid().ToString("N");
        var vcs = (InMemoryVcs)Create("vcs", world);
        var host = (InMemoryCodeHost)Create("code_host", world);

        Assert.Same(vcs.Forge, host.Forge);
        Assert.Same(InMemoryForge.Named(world), vcs.Forge);
    }

    static IProvider Create(string type, string world)
    {
        var factory = LocalProviders.Factories.Single(f => f.Type == type && f.Kind == "memory");
        var d = new Model.DiagnosticBag();
        var config = Model.ConfigurationReader.ReadInstallation($$"""
            api_version: feature-flow/v1
            kind: installation
            development: true
            temporal: { address: "localhost:7233", namespace: default, task_queue: q }
            providers:
              {{type}}: { instances: { memory: { world: {{world}} } } }
            """, "installation.yaml", d)!;
        Assert.DoesNotContain(d, x => x.Blocks);
        return factory.Create(new ProviderContext(config.Providers[type].Instances["memory"], ".", _ => null));
    }
}
