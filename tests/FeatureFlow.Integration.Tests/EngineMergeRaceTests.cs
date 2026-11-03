using FeatureFlow.Engine.ActionSteps;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Git;
using FeatureFlow.Providers.Local;
using System.Text.Json;

namespace FeatureFlow.Integration.Tests;

/// <summary>
/// SP4's race scenario (<c>docs/spikes/sp4-results.md</c>) through the engine's own steps (LOG 117's follow-up): each
/// ticket runs <c>scm.catch_up</c>, a re-test when the catch-up changed the branch, and <c>scm.merge</c>, as
/// <see cref="EngineActions"/> runs them for the workflow, over the real Git provider and one local bare repository.
/// Each ticket is its own worker: its own engine, clone cache and record store. The parent branch is named by the run
/// (<c>parent_branch</c>, LOG 137), not the default branch, as for a ticket inside an epic.
/// <para>
/// The re-test is simulated as in SP4: it records passing <c>test.run</c> and <c>ci.checks</c> evidence for the exact
/// head tested, which is what <c>scm.merge</c> reads. Every push to the parent waits a moment first (a test hook in the
/// provider), standing for the network between the engine's read of the parent and its push, so races are wide.
/// </para>
/// The pre-registered claim checks are SP4's: C1 no lost merge, C2 nothing untested lands, C3 loops well formed, C4
/// termination. Controls: N1b (plain forced pushes) loses merges; N2 (no re-test after a changing catch-up) is refused
/// by the engine as <c>checks_failed</c>, and lands an untested head when the engine's evidence check is broken
/// (<c>tools/check_m5_actions_controls.py</c>).
/// </summary>
[Trait("Category", "Integration")]
public sealed class EngineMergeRaceTests(ITestOutputHelper output) : IDisposable
{
    const string ParentBranch = "sp4/parent";
    const string ParentReference = "refs/heads/" + ParentBranch;
    static readonly RepoRef Repo = new("ai-feature-flow/sp4");
    readonly string root = Path.Combine(Path.GetTempPath(), "ff-esp4-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
    }

    static CancellationToken Ct => TestContext.Current.CancellationToken;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    sealed class Ticket(int index, EngineActions actions)
    {
        public int Index { get; } = index;
        public EngineActions Actions { get; } = actions;
        public string RunId => $"sp4-run-{Index}";
        public string Branch => $"ff/ticket/sp4-{Index}";
        public string WorkHead { get; set; } = "";
        public string? Tested { get; set; }

        /// <summary>The parent's revision as the harness read it right after the ticket's latest catch-up (SP4's "recorded" revision).</summary>
        public string? Recorded { get; set; }

        public HashSet<string> TestedHeads { get; } = [];
        public List<ArtifactEntry> Artifacts { get; } = [];
        public Dictionary<string, int> Executions { get; } = [];
        public int Merges => Executions.GetValueOrDefault("deliver_merge");
        public int Refusals { get; set; }
        public int ChecksFailed { get; set; }
        public string Outcome { get; set; } = "running";
        public MergeEvidence? Record { get; set; }
        public List<string> Violations { get; } = [];
    }

    sealed record Run(IReadOnlyList<Ticket> Tickets, string Setup, string Final, MergeRaceTests.Remote Remote)
    {
        public int Refusals => Tickets.Sum(t => t.Refusals);

        public IEnumerable<Ticket> Merged => Tickets.Where(t => t.Outcome == "merged");
    }

    /// <param name="forced">Every ticket still trying catches up, re-tests, then pushes its merge in the same round (SP4's barrier).</param>
    /// <param name="skipRetest">N2: the re-test after a changing catch-up is skipped.</param>
    /// <param name="plainForce">N1b: the provider pushes the merge with a plain forced push.</param>
    async Task<Run> RunAsync(int count, bool forced, int allowance, bool skipRetest = false, bool plainForce = false)
    {
        var remote = new MergeRaceTests.Remote(Path.Combine(root, Repo.FullName + ".git"));
        var setup = remote.Commit(ParentReference, new Dictionary<string, string> { ["README.md"] = "# SP4\n" }, null);
        var forge = new InMemoryForge();
        // The code host only names the default branch, main, which the run's parent branch is not.
        forge.CreateRepository(Repo.FullName);
        var store = new FileSystemArtifactStore(Path.Combine(root, "artifacts"));
        var random = new Random(4);

        int Jitter(int from, int to)
        {
            lock (random)
                return random.Next(from, to);
        }

        var tickets = Enumerable.Range(0, count).Select(i =>
        {
            var vcs = new GitVcs(Path.Combine(root, "{repo}.git"), Path.Combine(root, $"cache-{i}"), () => throw new InvalidOperationException(), () => store, () => null)
            {
                ControlPlainForcePush = plainForce,
                // The time between reading the parent and pushing to it: staggered when forced, so later pushes land after earlier ones.
                BeforePushForTests = reference => reference == ParentReference
                    ? Task.Delay(forced ? 400 * i : Jitter(0, 250), Ct)
                    : Task.CompletedTask,
            };
            var instances = new Dictionary<string, IReadOnlyDictionary<string, IProvider>>
            {
                ["vcs"] = One(vcs),
                ["code_host"] = One(new InMemoryCodeHost(forge)),
                ["artifact_store"] = One(store),
                ["record_store"] = One(new SqliteRecordStore(Path.Combine(root, $"records-{i}.db"))),
            };
            var providers = new ProviderSet(instances, instances.Keys.ToDictionary(k => k, _ => "one"), new Dictionary<string, string>());
            return new Ticket(i, new EngineActions(providers, new ActionOptions(TimeSpan.FromMilliseconds(5), TimeSpan.FromMilliseconds(5), TimeSpan.FromSeconds(30))));
        }).ToList();
        foreach (var t in tickets)
            t.WorkHead = remote.Commit("refs/heads/" + t.Branch, new Dictionary<string, string> { [$"ticket-{t.Index}.txt"] = $"ticket {t.Index}\n" }, setup);

        if (forced)
        {
            var remaining = tickets.ToList();
            while (remaining.Count > 0)
            {
                await Task.WhenAll(remaining.Select(t => CatchUpAndTestAsync(t, remote, store, TimeSpan.Zero, skipRetest)));
                foreach (var t in remaining.Where(t => t.Merges >= allowance))
                    t.Outcome = "attempts_exhausted";
                remaining.RemoveAll(t => t.Outcome != "running");
                await Task.WhenAll(remaining.Select(t => MergeAsync(t, store, skipRetest)));
                remaining.RemoveAll(t => t.Outcome != "running");
            }
        }
        else
        {
            await Task.WhenAll(tickets.Select(async t =>
            {
                while (t.Outcome == "running")
                {
                    await CatchUpAndTestAsync(t, remote, store, TimeSpan.FromMilliseconds(Jitter(100, 600)), skipRetest);
                    if (t.Merges >= allowance)
                        t.Outcome = "attempts_exhausted";
                    else
                        await MergeAsync(t, store, skipRetest);
                }
            }));
        }

        return new Run(tickets, setup, remote.Resolve(ParentReference)!, remote);
    }

    static IReadOnlyDictionary<string, IProvider> One(IProvider provider) => new Dictionary<string, IProvider> { ["one"] = provider };

    /// <summary>One step, as the workflow calls it: the run's artifacts so far are in its scope; each call is a new execution.</summary>
    static async Task<ActionResult> StepAsync(Ticket t, string action, string stepId)
    {
        var execution = t.Executions[stepId] = t.Executions.GetValueOrDefault(stepId) + 1;
        var contract = Actions.All[action];
        var outputs = (contract.Outputs ?? []).ToDictionary(n => n, n => Actions.EngineOutputTypes.GetValueOrDefault(n, "dyn"));
        var zero = outputs.ToDictionary(kv => kv.Key, kv => kv.Value == "boolean" ? "false" : "\"\"");
        var artifacts = action == "scm.merge" ? contract.Outcomes.Select(o => new ArtifactSpec(o.Name, "evidence")).ToList() : [];
        var call = new StepCall(new StepIdentity(t.RunId, "ticket_delivery", stepId, execution, $"{t.RunId}/{stepId}/{execution}"), "action", action,
            new Dictionary<string, string>(), new StepContract(contract.Outcomes.Select(o => new OutcomeSpec(o.Name, o.Class.Name())).ToList(), outputs, artifacts, zero),
            false, null);
        var scope = new ActionScope([Repo.FullName], Repo.FullName, t.Branch, ParentBranch, t.Artifacts.ToList(), [], []);
        return await t.Actions.RunAsync(call, scope, () => { }, Ct);
    }

    static async Task CatchUpAndTestAsync(Ticket t, MergeRaceTests.Remote remote, IArtifactStoreProvider store, TimeSpan retest, bool skipRetest)
    {
        var caught = await StepAsync(t, "scm.catch_up", "deliver_catch_up");
        Assert.True(caught.Report?.Outcome == "updated", $"ticket {t.Index}'s catch-up: {caught.Kind} {caught.Reason}");
        var changed = YamlWriter.ReadPlain(caught.Report!.Outputs["changed"]) is true;
        t.WorkHead = remote.Resolve("refs/heads/" + t.Branch)!;
        // Read after the catch-up: if the parent moved in between, the head lacks it and the engine's merge must be refused,
        // so a merge that lands always has this revision as its first parent.
        t.Recorded = remote.Resolve(ParentReference)!;
        if (t.Tested is null || (changed && !skipRetest))
        {
            // The re-test: passing evidence naming the exact head tested, as test.run and ci.checks record it.
            t.Tested = t.WorkHead;
            t.TestedHeads.Add(t.WorkHead);
            t.Artifacts.Add(await EvidenceAsync(store, "verify_tests", "test_results", "test.run",
                new TestEvidence(Repo.FullName, t.WorkHead, false, true, [new TestResults(t.WorkHead, "unit", 3, 0, 0, [])])));
            t.Artifacts.Add(await EvidenceAsync(store, "deliver_checks", "check_results", "ci.checks",
                new CheckEvidence(Repo.FullName, t.WorkHead, null, true, 0, [new CheckRun("unit", "completed", "success", "unit.yml")])));
            await Task.Delay(retest, Ct);
        }
    }

    static async Task<ArtifactEntry> EvidenceAsync(IArtifactStoreProvider store, string stepId, string type, string action, object value)
    {
        var stored = (await store.PutAsync(new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(value, Json)), Ct)).Unwrap();
        return new ArtifactEntry(stepId, type, "std:artifact_types/" + type, stored.Id, action, Evidence: true);
    }

    static async Task MergeAsync(Ticket t, IArtifactStoreProvider store, bool skipRetest)
    {
        // C3: a merge only ever names the head of the latest catch-up, and only after it was tested (N2 breaks this on purpose).
        if (t.Tested != t.WorkHead && !skipRetest)
            t.Violations.Add($"merge execution {t.Merges + 1} follows a catch-up to {t.WorkHead}, but {t.Tested} was tested");
        var merged = await StepAsync(t, "scm.merge", "deliver_merge");
        switch (merged.Report?.Outcome)
        {
            case "merged":
                t.Outcome = "merged";
                await using (var stream = (await store.GetAsync(new ArtifactRef(merged.Report.Artifacts["evidence"]), Ct)).Unwrap())
                    t.Record = JsonSerializer.Deserialize<MergeEvidence>(stream, Json);
                Assert.False(t.Record!.AlreadyMerged);
                break;
            case "base_moved":
                t.Refusals++;
                break;
            case "checks_failed":
                // std routes checks_failed back to the fix loop; here the ticket stops, with nothing merged.
                t.ChecksFailed++;
                t.Outcome = "checks_failed";
                break;
            default:
                Assert.Fail($"ticket {t.Index}'s merge: {merged.Kind} {merged.Reason} {string.Join("; ", merged.Events)}");
                break;
        }
    }

    /// <summary>C1: every ticket reported merged has its merged head in the final parent, and the parent's first-parent history is exactly the merges.</summary>
    static List<string> LostMerges(Run run)
    {
        var lost = run.Merged.Where(t => !run.Remote.IsAncestor(t.Record!.Head, run.Final)).Select(t => $"ticket {t.Index}'s merge {t.Record!.Revision} is not in the parent").ToList();
        var history = run.Remote.FirstParents(run.Setup, run.Final);
        if (!history.ToHashSet().SetEquals(run.Merged.Select(t => t.Record!.Revision)) || history.Count != run.Merged.Count())
            lost.Add($"the parent's first-parent history ({history.Count} commits) is not the {run.Merged.Count()} merges");
        return lost;
    }

    /// <summary>
    /// C2: each merge lands a head the ticket tested last, with passing evidence: its parents are the parent revision the
    /// harness read after the ticket's last catch-up and that head (SP4's definition), the engine's record agrees, and its
    /// tree is exactly the tested head's.
    /// </summary>
    static List<string> Untested(Run run) =>
        run.Merged.SelectMany(t =>
        {
            var r = t.Record!;
            var problems = new List<string>();
            if (!t.TestedHeads.Contains(r.Head) || r.Head != t.Tested)
                problems.Add($"ticket {t.Index} merged {r.Head}, which it never tested (last tested {t.Tested})");
            if (!run.Remote.Parents(r.Revision).SequenceEqual([t.Recorded!, r.Head]))
                problems.Add($"ticket {t.Index}'s merge {r.Revision} has parents {string.Join(", ", run.Remote.Parents(r.Revision))}, not {t.Recorded} and {r.Head}");
            if (r.TargetBefore != t.Recorded)
                problems.Add($"ticket {t.Index}'s merge record names {r.TargetBefore} as the parent it replaced, not {t.Recorded}");
            if (run.Remote.Tree(r.Revision) != run.Remote.Tree(r.Head))
                problems.Add($"ticket {t.Index}'s merge {r.Revision} does not hold exactly the tested tree");
            return problems;
        }).ToList();

    void Report(string name, Run run) =>
        output.WriteLine($"{name}: refusals {run.Refusals}; merged {run.Merged.Count()}; exhausted {run.Tickets.Count(t => t.Outcome == "attempts_exhausted")}; " +
                         $"checks_failed {run.Tickets.Sum(t => t.ChecksFailed)}; merge executions {string.Join(",", run.Tickets.Select(t => t.Merges))}; " +
                         $"lost {LostMerges(run).Count}; untested {Untested(run).Count}");

    static void AssertClaim(Run run, int allowance)
    {
        Assert.Empty(LostMerges(run));
        Assert.Empty(Untested(run));
        Assert.Empty(run.Tickets.SelectMany(t => t.Violations));
        Assert.All(run.Tickets, t => Assert.InRange(t.Merges, 1, allowance));
        Assert.All(run.Tickets, t => Assert.Contains(t.Outcome, new[] { "merged", "attempts_exhausted" }));
    }

    [Fact]
    public async Task S1_three_tickets_forced_to_collide_all_merge_through_the_engine_and_none_is_lost()
    {
        var run = await RunAsync(3, forced: true, allowance: 10);
        Report("S1", run);

        AssertClaim(run, 10);
        Assert.Equal(3, run.Refusals);
        Assert.Equal(3, run.Merged.Count());
        Assert.Equal([1, 2, 3], run.Tickets.Select(t => t.Merges).Order());
    }

    [Fact]
    public async Task S2_five_tickets_with_natural_timing_lose_nothing_and_end_through_the_engine()
    {
        // The race allowance of LOG 61, limits.races, default 10.
        var run = await RunAsync(5, forced: false, allowance: 10);
        Report("S2", run);

        AssertClaim(run, 10);
        Assert.True(run.Refusals >= 1, "no merge was refused, so the race was not exercised");
    }

    [Fact]
    public async Task S3_four_tickets_forced_to_collide_with_an_allowance_of_three_exhaust_exactly_one_through_the_engine()
    {
        var run = await RunAsync(4, forced: true, allowance: 3);
        Report("S3", run);

        AssertClaim(run, 3);
        Assert.Equal(6, run.Refusals);
        Assert.Equal(3, run.Merged.Count());
        var exhausted = Assert.Single(run.Tickets, t => t.Outcome == "attempts_exhausted");
        Assert.Equal(3, exhausted.Merges);
        Assert.Null(run.Remote.Read(run.Final, $"ticket-{exhausted.Index}.txt"));
    }

    [Fact]
    public async Task N2_a_merge_after_a_changing_catch_up_without_a_re_test_is_refused_by_the_engine()
    {
        var run = await RunAsync(3, forced: true, allowance: 10, skipRetest: true);
        Report("N2", run);

        // The first round merges one ticket; the others catch up (changing their heads), are not re-tested, and the
        // engine refuses to merge a head with no passing evidence.
        Assert.True(run.Tickets.Sum(t => t.ChecksFailed) >= 1, "no merge of an untested head was attempted, so N2 was not exercised");
        Assert.Empty(Untested(run));
        Assert.Empty(LostMerges(run));
    }

    [Fact]
    public async Task Control_N1b_plain_forced_pushes_lose_merges_through_the_engine()
    {
        var run = await RunAsync(3, forced: true, allowance: 10, plainForce: true);
        Report("N1b", run);

        Assert.Equal(3, run.Merged.Count());
        Assert.NotEmpty(LostMerges(run));
    }
}
