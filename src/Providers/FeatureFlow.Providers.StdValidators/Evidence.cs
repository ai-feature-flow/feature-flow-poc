using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.StdValidators;

/// <summary>
/// The validators that compare claims with the engine's evidence: satisfaction, the trace, and tests added by a fix. Test
/// evidence is only what an engine test action recorded (<see cref="Check.EngineEvidence"/>, F7); a baseline run proves nothing
/// about the change.
/// </summary>
static class Evidence
{
    static readonly IReadOnlyList<string> AllChanges = ["new", "reused", "modified"];

    /// <summary>One tier's results, by repository and revision, from a test evidence artifact of a run.</summary>
    sealed record Observed(string RunId, string Repository, string Revision, string Tier, TestResults Results);

    static string Short(string revision) => revision.Length > 10 ? revision[..10] : revision;

    static async Task<List<Observed>> ObservedAsync(Check c, IEnumerable<ArtifactRecord> records)
    {
        var observed = new List<Observed>();
        foreach (var record in records)
        {
            if (await c.ReadEngineAsync<TestEvidence>(record.Ref) is not { Baseline: false } evidence)
                continue;
            // test.integration across repositories names each tier "<repository>: <tier>" (LOG 120).
            var repositories = evidence.Repository.Split(", ");
            foreach (var tier in evidence.Tiers)
                observed.Add(repositories.Length > 1 && tier.Tier.Split(": ", 2) is [var repository, var name] && repositories.Contains(repository)
                    ? new Observed(record.RunId, repository, tier.Revision, name, tier)
                    : new Observed(record.RunId, evidence.Repository, tier.Revision, tier.Tier, tier));
        }

        return observed;
    }

    /// <summary>The tiers of a method at one revision: those named as the method's tier, else (a tier tag) every tier there.</summary>
    static List<Observed> Tiers(IEnumerable<Observed> observed, string repository, string revision, string? tier)
    {
        var there = observed.Where(o => Check.SameRepository(o.Repository, repository) && o.Revision == revision).ToList();
        var named = there.Where(o => o.Tier == tier).ToList();
        return named.Count > 0 ? named : there;
    }

    /// <summary>Why a test did not pass in <paramref name="tiers"/>, or null when it passed. A skipped test does not pass.</summary>
    static string? NotPassed(string id, IReadOnlyList<Observed> tiers)
    {
        if (tiers.Any(t => t.Results.Failures.Any(f => f.Name == id)))
            return "it failed";
        if (tiers.Any(t => t.Results.Passing?.Contains(id) == true))
            return null;
        return tiers.All(t => t.Results.Passing is null)
            ? "its tier was judged by exit code, so no test of it can be claimed"
            : "it is not among the tests the engine saw pass";
    }

    static string Name(ClaimedTest test) => test.Id ?? "(a test with no id)";

    /// <summary><c>verification_satisfaction</c> (std's verification skill, "Satisfaction").</summary>
    public static async Task SatisfactionAsync(Check c)
    {
        var plans = await c.PlansAsync(Types.DeliveryPlan);
        var claimSubjects = c.Present(Types.EvidenceClaims);
        var evidence = c.EngineEvidence(Types.TestResults, Check.TestActions).ToList();
        foreach (var subject in c.Present(Types.TestResults).Where(s => evidence.All(e => e.Ref != s.Artifact!.Id)))
            c.Error("not_evidence", $"{Check.Describe(subject)} was not produced by an engine test action, so it is not evidence (F7)", subject.Name);
        if (c.Present(Types.DeliveryPlan).Count == 0 && claimSubjects.Count == 0)
            // An epic's criteria are owned and proved by its tickets; it has neither (std's check_satisfaction).
            return;
        if (c.Present(Types.DeliveryPlan).Count == 0 || claimSubjects.Count == 0)
        {
            c.Error("missing_subject", claimSubjects.Count == 0 ? "the delivery plan's methods have no claims" : "there are claims but no delivery plan to check them against");
            return;
        }

        // This run's evidence only: a child's test results, in the index too, prove nothing about this run's head.
        var own = c.OwnRuns();
        if (own.Count != 1)
        {
            c.Error("own_run_unknown", own.Count == 0
                ? "none of the subjects is in the artifact index, so this run's own test results cannot be told from its children's"
                : $"the subjects come from several runs ({string.Join(", ", own.Order(StringComparer.Ordinal))})");
            return;
        }

        evidence = evidence.Where(e => own.Contains(e.RunId)).ToList();
        var claims = new List<Claim>();
        foreach (var subject in claimSubjects)
            claims.AddRange((await c.ReadAsync<ClaimsDocument>(subject))?.Claims ?? []);
        var observed = await ObservedAsync(c, evidence);
        var methods = new HashSet<(string?, string?)>();
        foreach (var plan in plans)
            foreach (var criterion in plan.Value.Items.Where(i => i.Owner is null && !string.IsNullOrEmpty(i.Id)))
                foreach (var method in criterion.Verification ?? [])
                {
                    var where = $"{criterion.Id}/{method.Id}";
                    methods.Add((criterion.Id, method.Id));
                    var tests = claims.Where(x => x.Criterion == criterion.Id && x.Verification == method.Id).SelectMany(x => x.Tests ?? []).ToList();
                    if (tests.Count == 0)
                    {
                        c.Error("unclaimed", $"{where} has no claimed test", plan.Subject.Name, criterion.Id);
                        continue;
                    }

                    var frozen = c.Frozen(method.Repository);
                    foreach (var test in tests)
                    {
                        if (test.Repository is null || method.Repository is null || !Check.SameRepository(test.Repository, method.Repository) || test.Tier != method.Tier)
                            c.Error("claim_mismatch", $"{where} claims {Name(test)} in {test.Repository} {test.Tier}; the method is {method.Repository} {method.Tier}", plan.Subject.Name, criterion.Id);
                        if (!(method.AllowedTestChanges ?? AllChanges).Contains(test.Change ?? ""))
                            c.Error("change_not_allowed", $"{where} claims {Name(test)} as {test.Change ?? "(no change)"}; the method allows {string.Join(", ", method.AllowedTestChanges ?? AllChanges)}",
                                plan.Subject.Name, criterion.Id);
                        if (frozen is not var (repository, revision))
                        {
                            c.Error("not_frozen", $"{where} names {method.Repository}, which this run has not prepared", plan.Subject.Name, criterion.Id);
                            continue;
                        }

                        var tiers = Tiers(observed, repository, revision, method.Tier);
                        if (tiers.Count == 0)
                            c.Error("not_tested", $"{where}: the engine has no test results for {repository} at {Short(revision)}, the revision checked", plan.Subject.Name, criterion.Id);
                        else if (NotPassed(test.Id ?? "", tiers) is { } why)
                            c.Error("test_not_passed", $"{where} claims {Name(test)}, which did not pass at {Short(revision)}: {why}", plan.Subject.Name, criterion.Id);
                    }
                }

        foreach (var claim in claims.Where(x => !methods.Contains((x.Criterion, x.Verification))))
            c.Error("unknown_claim", $"a claim names {claim.Criterion}/{claim.Verification}, which is no method of a criterion without an owner", item: claim.Criterion);
    }

    /// <summary>A plan in the run tree: the run that produced it, its name there, and its content.</summary>
    sealed record Plan(string RunId, string Name, PlanDocument Document);

    /// <summary>What the trace knows of the run tree: its plans, claims per run, tests observed and merges.</summary>
    sealed class Tree
    {
        public List<Plan> Plans { get; } = [];
        public Dictionary<string, List<Claim>> Claims { get; } = new(StringComparer.Ordinal);
        public List<Observed> Observed { get; set; } = [];
        public List<MergeEvidence> Merges { get; } = [];
    }

    static IEnumerable<ArtifactRecord> Latest(IEnumerable<ArtifactRecord> records) =>
        records.GroupBy(r => (r.RunId, r.Name)).Select(g => g.Last());

    static async Task<Tree> TreeAsync(Check c)
    {
        var tree = new Tree();
        var artifacts = c.Request.Artifacts;
        foreach (var record in Latest(artifacts.Where(a => ValidatorDescriptor.TypeName(a.Type) is Types.Requirements or Types.DeliveryPlan)))
            try
            {
                tree.Plans.Add(new Plan(record.RunId, record.Name, ArtifactShapes.ReadLibrary<PlanDocument>(await c.TextAsync(record.Ref), record.Name)));
            }
            catch (FormatException e)
            {
                c.Warn("malformed", $"{record.Name} of run {record.RunId} is left out of the trace: {e.Message}", record.Name);
            }

        foreach (var record in Latest(artifacts.Where(a => ValidatorDescriptor.TypeName(a.Type) == Types.EvidenceClaims)))
            try
            {
                var claims = ArtifactShapes.ReadLibrary<ClaimsDocument>(await c.TextAsync(record.Ref), record.Name).Claims ?? [];
                (tree.Claims.TryGetValue(record.RunId, out var list) ? list : tree.Claims[record.RunId] = []).AddRange(claims);
            }
            catch (FormatException e)
            {
                c.Warn("malformed", $"{record.Name} of run {record.RunId} is left out of the trace: {e.Message}", record.Name);
            }

        tree.Observed = await ObservedAsync(c, c.EngineEvidence(Types.TestResults, Check.TestActions));
        foreach (var record in c.EngineEvidence(Types.MergeRecord, new HashSet<string> { "scm.merge" }))
            if (await c.ReadEngineAsync<MergeEvidence>(record.Ref) is { } merge)
                tree.Merges.Add(merge);
        return tree;
    }

    /// <summary>
    /// Whether the frozen work contains <paramref name="revision"/>: it is the frozen revision or an ancestor, or a merge
    /// landed it (a squash merge's tested head is not an ancestor of what it produced) in a revision the work contains.
    /// </summary>
    static async Task<bool> ContainedAsync(Check c, Tree tree, string repository, string head, string revision, int depth = 0)
    {
        if (await c.ContainsAsync(repository, head, revision))
            return true;
        foreach (var merge in tree.Merges.Where(m => Check.SameRepository(m.Repository, repository) && m.Head == revision && m.Revision != revision))
            if (depth < 8 && await ContainedAsync(c, tree, repository, head, merge.Revision, depth + 1))
                return true;
        return false;
    }

    static bool Owns(PlanOwner owner, PlanScope? scope) => scope is not null && owner.Kind == scope.Kind && owner.Id == scope.Id;

    /// <summary>
    /// Traces one item: through its owner's copy (a child's plan of the owner's scope holding the same id), through its
    /// own methods' claims to passing tests, or through the items of its run that support it. Gaps and proving tests are
    /// added to the lists.
    /// </summary>
    static async Task TraceItemAsync(Check c, Tree tree, Plan plan, PlanItem item, List<string> gaps, List<string> tests, HashSet<(string, string, string)> seen)
    {
        if (!seen.Add((plan.RunId, plan.Name, item.Id!)))
            return;
        if (item.Owner is { } owner && !Owns(owner, plan.Document.Scope))
        {
            var holders = tree.Plans.Where(p => p.RunId != plan.RunId && Owns(owner, p.Document.Scope)).Select(p => (Plan: p, Item: p.Document.Items.FirstOrDefault(i => i.Id == item.Id)))
                .Where(h => h.Item is not null).ToList();
            if (holders.Count == 0)
            {
                gaps.Add($"{item.Id}: its owner, {owner.Kind} {owner.Id}, has no plan holding it (has it run?)");
                return;
            }

            // A child run again holds the item twice; one that traces is enough.
            var tries = new List<(List<string> Gaps, List<string> Tests)>();
            foreach (var (holder, copy) in holders)
            {
                var (g, t) = (new List<string>(), new List<string>());
                await TraceItemAsync(c, tree, holder, copy!, g, t, seen);
                tries.Add((g, t));
                if (g.Count == 0)
                    break;
            }

            var best = tries.FirstOrDefault(x => x.Gaps.Count == 0) is { Gaps: not null } traced ? traced : tries[0];
            gaps.AddRange(best.Gaps);
            tests.AddRange(best.Tests);
            return;
        }

        if (plan.Document.IsPlan && item.Verification is { Count: > 0 } methods)
        {
            var claims = tree.Claims.GetValueOrDefault(plan.RunId) ?? [];
            foreach (var method in methods)
            {
                var where = $"{item.Id}/{method.Id}";
                var claimed = claims.Where(x => x.Criterion == item.Id && x.Verification == method.Id).SelectMany(x => x.Tests ?? []).ToList();
                if (claimed.Count == 0)
                {
                    gaps.Add($"{where}: no test is claimed");
                    continue;
                }

                if (c.Frozen(method.Repository) is not var (repository, head))
                {
                    gaps.Add($"{where}: {method.Repository} is not a repository of this run");
                    continue;
                }

                foreach (var test in claimed)
                {
                    var proved = false;
                    var runs = tree.Observed.Where(o => o.RunId == plan.RunId);
                    foreach (var revision in runs.Where(o => Check.SameRepository(o.Repository, repository)).Select(o => o.Revision).Distinct())
                        if (NotPassed(test.Id ?? "", Tiers(runs, repository, revision, method.Tier)) is null && await ContainedAsync(c, tree, repository, head, revision))
                        {
                            tests.Add($"{repository} {method.Tier} {Name(test)} at {Short(revision)}");
                            proved = true;
                            break;
                        }

                    if (!proved)
                        gaps.Add($"{where}: {Name(test)} did not pass at a commit {repository} at {Short(head)} contains");
                }
            }

            return;
        }

        var supporters = tree.Plans.Where(p => p.RunId == plan.RunId && p != plan)
            .SelectMany(p => p.Document.Items.Where(i => !string.IsNullOrEmpty(i.Id) && (i.Supports ?? []).Any(l => l.Artifact == plan.Name && l.Requirement == item.Id)).Select(i => (p, i)))
            .ToList();
        if (supporters.Count == 0)
            gaps.Add(plan.Document.IsPlan ? $"{item.Id}: it has no verification method and no owner" : $"{item.Id}: nothing supports it");
        foreach (var (p, i) in supporters)
            await TraceItemAsync(c, tree, p, i, gaps, tests, seen);
    }

    /// <summary><c>requirement_trace</c> (docs/01 section 8): offers a trace report whether or not the trace holds.</summary>
    public static async Task TraceAsync(Check c)
    {
        var subjects = c.Present(Types.Requirements);
        if (subjects.Count == 0)
        {
            c.Error("no_requirements", "there is no requirements document to trace");
            return;
        }

        var tree = await TreeAsync(c);
        var entries = new List<TracedRequirement>();
        foreach (var subject in subjects)
        {
            if (await c.ReadAsync<PlanDocument>(subject) is not { } document)
                continue;
            // The subject's run is the one whose index entry is this very artifact; its supporters are found there.
            var record = c.Request.Artifacts.FirstOrDefault(a => a.Ref == subject.Artifact!.Id);
            var plan = tree.Plans.FirstOrDefault(p => p.RunId == record?.RunId && p.Name == record?.Name) ?? new Plan(record?.RunId ?? "", subject.Name, document);
            foreach (var requirement in document.Items.Where(i => !string.IsNullOrEmpty(i.Id)))
            {
                var (gaps, tests) = (new List<string>(), new List<string>());
                await TraceItemAsync(c, tree, plan, requirement, gaps, tests, []);
                entries.Add(new TracedRequirement(requirement.Id!, gaps.Count == 0, tests, gaps));
                if (gaps.Count > 0)
                    c.Error("trace_gap", $"{requirement.Id} does not trace: {string.Join("; ", gaps)}", subject.Name, requirement.Id);
            }
        }

        var report = new TraceReport(entries.All(e => e.Traced), c.Request.Repositories, entries);
        c.Reports.Add(new OfferedReport(Types.TraceReport, await c.PutAsync(report)));
    }

    /// <summary>
    /// <c>tests_added</c> (docs/04 section 10.2). A claimed test counts for a finding when it is in the finding's repository,
    /// its source file exists at the frozen revision and holds the test (its symbol, else its id's last part), and the file
    /// is new or different since the revision the finding was raised on. Files are read through the code host at those
    /// revisions, so the check needs no checkout (LOG 172).
    /// </summary>
    public static async Task TestsAddedAsync(Check c)
    {
        var entries = new List<TriageEntry>();
        foreach (var subject in c.Present(Types.Triage))
            entries.AddRange((await c.ReadAsync<Triage>(subject))?.Entries ?? []);
        var tests = new List<ClaimedTest>();
        foreach (var subject in c.Present(Types.EvidenceClaims))
            tests.AddRange(((await c.ReadAsync<ClaimsDocument>(subject))?.Claims ?? []).SelectMany(x => x.Tests ?? []));

        foreach (var entry in entries.Where(e => e.State == FindingStates.Open && e.RegressionTest?.Applicable != false))
        {
            if (c.Request.Findings.FirstOrDefault(f => f.FindingId == entry.Finding) is not { } finding)
            {
                c.Error("unknown_finding", $"the triage names {entry.Finding}, which is not in the ledger", item: entry.Finding);
                continue;
            }

            if (finding.RaisedRevision is not { } raised)
            {
                c.Error("no_raised_revision", $"{entry.Finding} has no revision it was raised on, so a new test cannot be told from an old one", item: entry.Finding);
                continue;
            }

            if (c.Frozen(finding.Finding.Location.Repository) is not var (repository, head))
            {
                c.Error("not_frozen", $"{entry.Finding} is in {finding.Finding.Location.Repository}, which this run has not prepared", item: entry.Finding);
                continue;
            }

            var why = new List<string>();
            var added = false;
            foreach (var test in tests.Where(t => t.Repository is not null && Check.SameRepository(t.Repository, repository)))
            {
                if (test.Source?.File is not { Length: > 0 } file)
                {
                    why.Add($"{Name(test)} names no source file");
                    continue;
                }

                if (await c.FileAsync(repository, file, head) is not { } after)
                {
                    why.Add($"{file} does not exist at {Short(head)}");
                    continue;
                }

                if (test.Symbol is { } symbol && !after.Content.Contains(symbol, StringComparison.Ordinal))
                {
                    why.Add($"{file} does not hold {symbol}");
                    continue;
                }

                if (await c.FileAsync(repository, file, raised) is { } before && before.Hash == after.Hash)
                {
                    why.Add($"{file} is unchanged since {Short(raised)}");
                    continue;
                }

                added = true;
                break;
            }

            if (!added)
                c.Error("no_test_added",
                    $"{entry.Finding} was raised at {Short(raised)}; the fix claims no test in {repository} that is new or changed since then" + (why.Count > 0 ? ": " + string.Join("; ", why) : ""),
                    item: entry.Finding);
        }
    }
}
