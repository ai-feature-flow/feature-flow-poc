using FeatureFlow.Spike.CodeHost;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike3;

/// <summary>
/// The harness's own access to the fixture, through the same broker and code
/// host the engine uses. Tokens go into one command's URL only; never printed.
/// </summary>
public sealed class GitHubOps
{
    public RunContext Context { get; }

    public GitHubOps(string scratchDirectory, string runId)
    {
        Directory.CreateDirectory(scratchDirectory);
        var records = Path.Combine(scratchDirectory, "records");
        Context = new RunContext(runId, new Plan(), scratchDirectory, "main", "main", "HARNESS", records,
            OrganisationConfiguration.Load(Path.Combine(Cli.PlansDir, "organisation.yaml")));
    }

    public FeatureFlow.Spike.CodeHost.RepoRef Repo => Context.CodeHostRepo;

    public ICodeHostProvider CodeHost => Context.CodeHost;

    public async Task<string> TokenUrlAsync()
    {
        var issued = await Context.Broker.IssueAsync(
            FeatureFlow.Spike.Providers.Actor.Engine("spike3-harness"), GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository($"{Cli.Owner}/{Cli.Repo}", "contents:write", "contents:read", "metadata:read").Text,
            TimeSpan.FromMinutes(30), CancellationToken.None);
        if (!issued.IsOk)
            throw new InvalidOperationException($"token: {issued.Error!.Class}/{issued.Error.Reason}");
        return $"https://x-access-token:{issued.Value!.Secret}@github.com/{Cli.Owner}/{Cli.Repo}.git";
    }

    public async Task PushAsync(string cwd, string refspec)
    {
        var url = await TokenUrlAsync();
        var (code, _, err) = Cli.Run("git", cwd, "push", "--quiet", url, refspec);
        if (code != 0)
            throw new InvalidOperationException($"push {refspec} exited {code}: {Scrub(err)}");
    }

    private static string Scrub(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, "x-access-token:[^@]+@", "x-access-token:***@");

    /// <summary>A clone of the fixture with origin on the SSH alias, from the local reference clone when present.</summary>
    public static string Clone(string target)
    {
        if (Directory.Exists(target))
            Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var reference = Path.Combine(Cli.WorkDir, "fixture-ref");
        if (Directory.Exists(reference))
        {
            Cli.Git(Cli.WorkDir, "clone", "--quiet", reference, target);
            Cli.Git(target, "remote", "set-url", "origin", Cli.SshUrl);
        }
        else
        {
            Cli.Git(Cli.WorkDir, "clone", "--quiet", Cli.SshUrl, target);
        }
        Cli.Git(target, "fetch", "--quiet", "--prune", "origin");
        Cli.Git(target, "config", "user.name", "feature-flow-spike3");
        Cli.Git(target, "config", "user.email", "spike3@feature-flow.invalid");
        return target;
    }
}

/// <summary>
/// C3's method sanity check (docs/17 5): hand-made branches with A's and C's
/// edits through MergeBaseForwardAsync return Work/conflict; two
/// non-conflicting branches return completed.
/// </summary>
public static class ConflictSanity
{
    public static async Task<int> RunAsync()
    {
        var stamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var dir = Path.Combine(Cli.WorkDir, "sanity", $"conflict-{stamp}");
        var ops = new GitHubOps(Path.Combine(dir, "scratch"), $"sanity-conflict-{stamp}");
        var clone = GitHubOps.Clone(Path.Combine(dir, "clone"));
        var prefix = $"ff/spike-3-sanity-{stamp}";
        var diffs = Path.Combine(Cli.SpikeDir, "pilot", "diffs");

        string Make(string name, Action edit)
        {
            Cli.Git(clone, "checkout", "--quiet", "-B", $"{prefix}/{name}", "origin/main");
            edit();
            Cli.Git(clone, "add", "-A");
            Cli.Git(clone, "commit", "--quiet", "-m", $"sanity: {name}");
            return Cli.Git(clone, "rev-parse", "HEAD");
        }

        Make("a", () => Cli.Git(clone, "apply", "--ignore-whitespace", Path.Combine(diffs, "A.diff")));
        Make("b", () => Cli.Git(clone, "apply", "--ignore-whitespace", Path.Combine(diffs, "B.diff")));
        Make("c", () =>
        {
            var store = Path.Combine(clone, "src/FeatureFlow.Example.Api/WidgetStore.cs");
            File.WriteAllText(store, File.ReadAllText(store).Replace(
                "new Widget(_nextId++, name);", "new Widget(_nextId++, name, DateTimeOffset.UtcNow);"));
            var program = Path.Combine(clone, "src/FeatureFlow.Example.Api/Program.cs");
            File.WriteAllText(program, File.ReadAllText(program).Replace(
                "public record Widget(int Id, string Name);", "public record Widget(int Id, string Name, DateTimeOffset CreatedAt);"));
        });
        foreach (var n in new[] { "a", "b", "c" })
            await ops.PushAsync(clone, $"{prefix}/{n}:refs/heads/{prefix}/{n}");
        Cli.Log($"CONFLICT sanity: pushed {prefix}/a (A's edit), /b (B's edit), /c (C's edit), each from main");

        var results = new List<object>();
        async Task<bool> Forward(string from, string into, string expect)
        {
            var r = await ops.CodeHost.MergeBaseForwardAsync(ops.Repo, $"{prefix}/{from}", $"{prefix}/{into}", CancellationToken.None);
            var got = r.IsOk ? $"completed branchChanged={r.Value!.BranchChanged}" : $"{r.Error!.Class}/{r.Error.Reason}";
            var pass = got == expect;
            Cli.Log($"  forward {from} -> {into}: {got} (expected {expect}) {(pass ? "PASS" : "FAIL")}");
            results.Add(new { from, into, got, expect, pass, newHead = r.Value?.NewHeadSha });
            return pass;
        }

        var ok = await Forward("c", "a", "Work/conflict");
        var (_, tree, _) = Cli.Run("git", clone, "merge-tree", "--write-tree", "--name-only", "--no-messages",
            $"{prefix}/a", $"{prefix}/c");
        var files = tree.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
        Cli.Log($"  conflicted files (merge-tree): {string.Join(", ", files)}");
        // B and C touch different lines of the same files; A and B both append
        // at the end of WidgetStore, so they are not a non-conflicting pair.
        ok &= await Forward("b", "c", "completed branchChanged=True");
        ok &= await Forward("b", "c", "completed branchChanged=False");

        Sanity.Save("sanity-conflict.json", new { prefix, results, conflictedFiles = files, pass = ok });
        Cli.Log($"CONFLICT sanity: {(ok ? "PASS" : "FAIL")}");
        return ok ? 0 : 1;
    }
}
