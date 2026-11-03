using System.Diagnostics;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Git;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// SP4's race scenario (<c>docs/spikes/sp4-results.md</c>) through the engine's merge path: the Git VCS provider's
/// <see cref="GitVcs.CatchUpAsync"/> and <see cref="GitVcs.MergeAsync"/>, driven in the loop <c>scm.catch_up</c>,
/// re-test when the catch-up changed the branch, <c>scm.merge</c> expecting the parent revision recorded at the
/// catch-up, and back to catch-up on <c>base_moved</c>. Each ticket is its own worker with its own clone cache, racing
/// on one local bare repository. The claim: no merge is lost, nothing untested lands, and every loop ends.
/// Control N1b (plain forced pushes, natural timing) must lose merges, showing the checks can fail.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MergeRaceTests(ITestOutputHelper output) : IDisposable
{
    const string Parent = "refs/heads/sp4/parent";
    static readonly RepoRef Repo = new("ai-feature-flow/sp4");
    readonly string root = Path.Combine(Path.GetTempPath(), "ff-sp4-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    sealed class Ticket(int index, GitVcs vcs)
    {
        public int Index { get; } = index;
        public GitVcs Vcs { get; } = vcs;
        public string Work => $"refs/heads/ff/ticket/sp4-{Index}";
        public string Key => $"sp4-run/ticket-{Index}/scm.merge";
        public string WorkHead { get; set; } = "";
        public string? Recorded { get; set; }
        public string? Tested { get; set; }
        public int Executions { get; set; }
        public int Refusals { get; set; }
        public string Outcome { get; set; } = "running";
        public string? Merge { get; set; }
        public List<string> Violations { get; } = [];
    }

    sealed record Run(IReadOnlyList<Ticket> Tickets, string Setup, string Final, Remote Remote)
    {
        public int Refusals => Tickets.Sum(t => t.Refusals);

        public IEnumerable<Ticket> Merged => Tickets.Where(t => t.Outcome == "merged");
    }

    async Task<Run> RunAsync(int count, bool forced, int allowance, bool control = false)
    {
        var remote = new Remote(Path.Combine(root, Repo.FullName + ".git"));
        var setup = remote.Commit(Parent, new Dictionary<string, string> { ["README.md"] = "# SP4\n" }, null);
        var tickets = Enumerable.Range(0, count).Select(i => new Ticket(i, new GitVcs(Path.Combine(root, "{repo}.git"),
            Path.Combine(root, $"cache-{i}"), () => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => null)
        {
            ControlPlainForcePush = control,
        })).ToList();
        foreach (var t in tickets)
            t.WorkHead = remote.Commit(t.Work, new Dictionary<string, string> { [$"ticket-{t.Index}.txt"] = $"ticket {t.Index}\n" }, setup);

        var random = new Random(4);
        TimeSpan Retest() => forced ? TimeSpan.Zero : TimeSpan.FromMilliseconds(random.Next(100, 600));
        if (forced)
        {
            // Every ticket still trying catches up and re-tests, then all push their merges at the same moment.
            var remaining = tickets.ToList();
            while (remaining.Count > 0)
            {
                await Task.WhenAll(remaining.Select(t => CatchUpAndTestAsync(t, TimeSpan.Zero)));
                foreach (var t in remaining.Where(t => t.Executions >= allowance))
                    t.Outcome = "attempts_exhausted";
                remaining.RemoveAll(t => t.Outcome != "running");
                await Task.WhenAll(remaining.Select(MergeAsync));
                remaining.RemoveAll(t => t.Outcome != "running");
            }
        }
        else
        {
            TimeSpan Delay()
            {
                lock (random)
                    return Retest();
            }

            await Task.WhenAll(tickets.Select(async t =>
            {
                while (t.Outcome == "running")
                {
                    await CatchUpAndTestAsync(t, Delay());
                    if (t.Executions >= allowance)
                        t.Outcome = "attempts_exhausted";
                    else
                        await MergeAsync(t);
                }
            }));
        }

        return new Run(tickets, setup, remote.Resolve(Parent)!, remote);
    }

    static async Task CatchUpAndTestAsync(Ticket t, TimeSpan retest)
    {
        var caughtUp = (await t.Vcs.CatchUpAsync(Repo, Parent, t.Work, t.WorkHead, $"Catch up ticket {t.Index}", Ct)).Unwrap();
        Assert.False(caughtUp.Conflicted);
        t.WorkHead = caughtUp.WorkRevision;
        t.Recorded = caughtUp.ParentRevision;
        if (caughtUp.Changed || t.Tested is null)
        {
            // The re-test: the evidence names the exact head tested.
            t.Tested = caughtUp.WorkRevision;
            await Task.Delay(retest, Ct);
        }
    }

    static async Task MergeAsync(Ticket t)
    {
        // C3: a merge only ever names the head of the latest catch-up, and only after it was tested.
        if (t.Tested != t.WorkHead)
            t.Violations.Add($"merge execution {t.Executions + 1} names {t.Tested}, not the caught-up head {t.WorkHead}");
        t.Executions++;
        var merged = await t.Vcs.MergeAsync(Repo, t.Tested!, Parent, t.Recorded!, MergeMethods.Merge, $"Merge ticket {t.Index}", t.Key, Ct);
        if (merged.Ok)
        {
            t.Outcome = "merged";
            t.Merge = merged.Value!.Revision;
            Assert.False(merged.Value.AlreadyMerged);
        }
        else if (merged.Error!.Reason == "base_moved")
            t.Refusals++;
        else
            throw new ProviderException(merged.Error);
    }

    /// <summary>C1: every ticket reported merged has its tested head in the final parent, and the parent's first-parent history is exactly the merges.</summary>
    static List<string> LostMerges(Run run)
    {
        var lost = run.Merged.Where(t => !run.Remote.IsAncestor(t.Tested!, run.Final)).Select(t => $"ticket {t.Index}'s merge {t.Merge} is not in the parent").ToList();
        var history = run.Remote.FirstParents(run.Setup, run.Final);
        if (!history.ToHashSet().SetEquals(run.Merged.Select(t => t.Merge!)) || history.Count != run.Merged.Count())
            lost.Add($"the parent's first-parent history ({history.Count} commits) is not the {run.Merged.Count()} merges");
        return lost;
    }

    /// <summary>C2: each merge commit's parents are the parent revision recorded at the ticket's last catch-up, and its last tested head.</summary>
    static List<string> Untested(Run run) =>
        run.Merged.Where(t => !run.Remote.Parents(t.Merge!).SequenceEqual([t.Recorded!, t.Tested!]))
            .Select(t => $"ticket {t.Index}'s merge {t.Merge} has parents {string.Join(", ", run.Remote.Parents(t.Merge!))}").ToList();

    void Report(string name, Run run) =>
        output.WriteLine($"{name}: refusals {run.Refusals}; merged {run.Merged.Count()}; exhausted {run.Tickets.Count(t => t.Outcome == "attempts_exhausted")}; " +
                         $"executions {string.Join(",", run.Tickets.Select(t => t.Executions))}; lost {LostMerges(run).Count}");

    void AssertClaim(Run run, int allowance)
    {
        Assert.Empty(LostMerges(run));
        Assert.Empty(Untested(run));
        Assert.Empty(run.Tickets.SelectMany(t => t.Violations));
        Assert.All(run.Tickets, t => Assert.InRange(t.Executions, 1, allowance));
        Assert.All(run.Tickets, t => Assert.Contains(t.Outcome, new[] { "merged", "attempts_exhausted" }));
    }

    [Fact]
    public async Task S1_three_tickets_forced_to_collide_all_merge_and_none_is_lost()
    {
        var run = await RunAsync(3, forced: true, allowance: 10);
        Report("S1", run);

        AssertClaim(run, 10);
        Assert.Equal(3, run.Refusals);
        Assert.Equal(3, run.Merged.Count());
        Assert.Equal([1, 2, 3], run.Tickets.Select(t => t.Executions).Order());
    }

    [Fact]
    public async Task S2_five_tickets_with_natural_timing_lose_nothing_and_end()
    {
        // The race allowance of LOG 61, limits.races, default 10.
        var run = await RunAsync(5, forced: false, allowance: 10);
        Report("S2", run);

        AssertClaim(run, 10);
        Assert.True(run.Refusals >= 1, "no merge was refused, so the race was not exercised");
    }

    [Fact]
    public async Task S3_four_tickets_forced_to_collide_with_an_allowance_of_three_exhaust_exactly_one()
    {
        var run = await RunAsync(4, forced: true, allowance: 3);
        Report("S3", run);

        AssertClaim(run, 3);
        Assert.Equal(6, run.Refusals);
        Assert.Equal(3, run.Merged.Count());
        var exhausted = Assert.Single(run.Tickets, t => t.Outcome == "attempts_exhausted");
        Assert.Equal(3, exhausted.Executions);
        Assert.Null(run.Remote.Read(run.Final, $"ticket-{exhausted.Index}.txt"));
    }

    [Fact]
    public async Task Control_N1b_plain_forced_pushes_with_natural_timing_lose_merges()
    {
        var run = await RunAsync(5, forced: false, allowance: 10, control: true);
        Report("N1b", run);

        Assert.Equal(5, run.Merged.Count());
        Assert.NotEmpty(LostMerges(run));
    }

    /// <summary>The bare repository the workers race on, read and written with plumbing.</summary>
    sealed class Remote
    {
        public Remote(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
            Git("init", "-q", "--bare");
        }

        public string Path { get; }

        public string? Resolve(string reference) => Run(["rev-parse", "--verify", "-q", reference + "^{commit}"], null) is (0, var o) ? o.Trim() : null;

        public string Commit(string reference, IReadOnlyDictionary<string, string> files, string? parent)
        {
            var index = System.IO.Path.Combine(Path, $"index-{Guid.NewGuid():N}");
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = index };
            try
            {
                if (parent is not null)
                    Git(env, "read-tree", parent);
                foreach (var (path, content) in files)
                {
                    var blob = Run(["hash-object", "-w", "--stdin"], env, content).Output.Trim();
                    Git(env, "update-index", "--add", "--cacheinfo", $"100644,{blob},{path}");
                }

                var tree = Git(env, "write-tree").Trim();
                var commit = parent is null ? Git(env, "commit-tree", tree, "-m", "setup") : Git(env, "commit-tree", tree, "-p", parent, "-m", "ticket work");
                Git("update-ref", reference, commit.Trim());
                return commit.Trim();
            }
            finally
            {
                File.Delete(index);
            }
        }

        public bool IsAncestor(string ancestor, string revision) => Run(["merge-base", "--is-ancestor", ancestor, revision], null).Exit == 0;

        public IReadOnlyList<string> FirstParents(string from, string to) =>
            Git("rev-list", "--first-parent", "--reverse", $"{from}..{to}").Split('\n', StringSplitOptions.RemoveEmptyEntries);

        public IReadOnlyList<string> Parents(string revision) => Git("rev-list", "--parents", "-n", "1", revision).Trim().Split(' ').Skip(1).ToList();

        public string? Read(string revision, string path) => Run(["show", $"{revision}:{path}"], null) is (0, var o) ? o : null;

        string Git(params string[] arguments) => Git(null, arguments);

        string Git(IReadOnlyDictionary<string, string>? env, params string[] arguments) =>
            Run(arguments, env) is (0, var o) ? o : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed");

        (int Exit, string Output) Run(IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? env, string? input = null)
        {
            var start = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false };
            foreach (var a in arguments)
                start.ArgumentList.Add(a);
            start.Environment["GIT_DIR"] = Path;
            start.Environment["GIT_AUTHOR_NAME"] = start.Environment["GIT_COMMITTER_NAME"] = "SP4";
            start.Environment["GIT_AUTHOR_EMAIL"] = start.Environment["GIT_COMMITTER_EMAIL"] = "sp4@example.com";
            foreach (var (k, v) in env ?? new Dictionary<string, string>())
                start.Environment[k] = v;
            using var process = Process.Start(start)!;
            if (input is not null)
            {
                process.StandardInput.Write(input);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEndAsync();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output.Result);
        }
    }
}
