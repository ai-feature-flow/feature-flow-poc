using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using FeatureFlow.Providers.Abstractions;
using static FeatureFlow.Providers.StdValidators.Tests.World;

namespace FeatureFlow.Providers.StdValidators.Tests;

/// <summary>The standard validators beyond the contract's fixtures: a real git history, the trace across a child, and F7.</summary>
public sealed class StdValidatorTests : IDisposable
{
    static CancellationToken Ct => TestContext.Current.CancellationToken;

    readonly World w = new();

    public void Dispose() => w.Dispose();

    async Task<ValidationResult> ValidateAsync(IValidatorProvider validators, ValidationRequest request)
    {
        var result = await validators.ValidateAsync(request, Ct);
        Assert.True(result.Ok, $"{result.Error?.Reason}: {result.Error?.Message}");
        return result.Value!;
    }

    Task<ValidationResult> ValidateAsync(ValidationRequest request) => ValidateAsync(w.Validators, request);

    static void Invalid(ValidationResult result, string code) =>
        Assert.True(!result.Valid && result.Issues.Any(i => i.Code == code && i.Severity == ValidationSeverities.Error),
            $"expected an error {code}, got {string.Join("; ", result.Issues.Select(i => $"{i.Code}: {i.Message}"))}");

    static void Valid(ValidationResult result) =>
        Assert.True(result.Valid, string.Join("; ", result.Issues.Select(i => $"{i.Code}: {i.Message}")));

    // ---- tests_added over a real git history (M6's verified-by) ----

    /// <summary>
    /// A finding raised at commit A; the fix at B changes the code and no test, so <c>tests_added</c> is invalid; the next
    /// fix at C also changes the test file, adding the claimed test, so it is valid. Files are read at A, B and C from a real
    /// git repository through a code host over it.
    /// </summary>
    [Fact]
    public async Task Tests_added_catches_a_fix_that_adds_no_test_in_a_real_git_history()
    {
        using var git = new GitRepository();
        var a = git.Commit(new() { ["src/Widgets.cs"] = "class Widgets { void Add(string name) { } }\n", ["tests/WidgetTests.cs"] = "class WidgetTests { void Adds() { } }\n" });
        var b = git.Commit(new() { ["src/Widgets.cs"] = "class Widgets { void Add(string name) { if (name.Length > 100) throw new(); } }\n" });
        var c = git.Commit(new() { ["tests/WidgetTests.cs"] = "class WidgetTests { void Adds() { } void RefusesLongNames() { } }\n" });
        var validators = new StdValidators(() => w.Store, () => git.Host, () => null);
        var findings = new[] { Finding("F-1", a) };
        // The engine's results at each head: what passed there, as test.run (the fix loop's retest) saw it.
        ValidationRequest Request(string claims, string head, params string[] passing)
        {
            var (subject, record) = w.Indexed("fix_claims", "evidence_claims", claims);
            return World.Request(Names.TestsAdded, [subject, w.Subject("triage", "triage", TriageText)], Frozen(head), [record, w.TestEvidence(head, passing)], findings);
        }

        // B: the agent claims the existing test as its regression test.
        var atB = await ValidateAsync(validators, Request(Claims("WidgetTests.Adds", "modified"), b, "WidgetTests.Adds"));
        Invalid(atB, "no_test_added");
        Assert.Contains("tests/WidgetTests.cs is unchanged since", atB.Issues.Single().Message);
        // B: it claims the new test, which B does not have.
        var missing = await ValidateAsync(validators, Request(Claims(), b, "WidgetTests.Adds"));
        Invalid(missing, "no_test_added");
        Assert.Contains("did not pass in this run's test results", missing.Issues.Single().Message);
        // C: the test file changed and holds the claimed test.
        Valid(await ValidateAsync(validators, Request(Claims(), c, "WidgetTests.Adds", "WidgetTests.RefusesLongNames")));
        // C: a test that passed somewhere, but not in the changed file the claim names.
        var ghost = await ValidateAsync(validators, Request(Claims("WidgetTests.Ghost"), c, "WidgetTests.Ghost"));
        Invalid(ghost, "no_test_added");
        Assert.Contains("does not hold Ghost", ghost.Issues.Single().Message);
    }

    [Fact]
    public async Task Tests_added_asks_nothing_of_a_finding_where_a_regression_test_does_not_apply_and_names_a_claim_without_a_source()
    {
        var head = w.Commit("src/Widgets.cs", "class Widgets { const int Max = 100; }\n");
        var notApplicable = TriageText.Replace("{ applicable: true }", "{ applicable: false, reason: configuration }");
        var noSource = Claims().Replace(", source: { file: tests/WidgetTests.cs }", "");
        ValidationRequest Request(string claims, string triage, bool ledger = true)
        {
            var (subject, record) = w.Indexed("fix_claims", "evidence_claims", claims);
            return World.Request(Names.TestsAdded, [subject, w.Subject("triage", "triage", triage)], Frozen(head),
                [record, w.TestEvidence(head, ["WidgetTests.RefusesLongNames"])], ledger ? [Finding("F-1", w.Main)] : []);
        }

        Valid(await ValidateAsync(Request(noSource, notApplicable)));
        var result = await ValidateAsync(Request(noSource, TriageText));
        Invalid(result, "no_test_added");
        Assert.Contains("names no source file", result.Issues.Single().Message);
        Invalid(await ValidateAsync(Request(Claims(), TriageText, ledger: false)), "unknown_finding");
    }

    // ---- requirement_trace across a child ----

    const string EpicRun = "run-epic";
    const string EpicBranch = "refs/heads/ff/epic/E";

    /// <summary>
    /// An epic run's requirement E-1, supported by AC-1 of its plan for its tickets, owned by ticket T-1, whose run holds AC-1
    /// with a method, a claim, and a passing test at the ticket's head T. The ticket was squash-merged into the epic's branch
    /// as M, so T is not an ancestor of the epic's head: the merge record says T landed as M.
    /// </summary>
    (List<ArtifactRecord> Artifacts, string EpicHead, ArtifactRecord Merge, ArtifactRecord Tests) EpicWithTicket()
    {
        w.Forge.Commit(w.Repository, EpicBranch, new Dictionary<string, string?> { ["docs/epic.md"] = "E\n" }, "epic design");
        var ticketHead = w.Forge.Commit(w.Repository, "refs/heads/ff/ticket/T-1", new Dictionary<string, string?> { ["tests/WidgetTests.cs"] = "RefusesLongNames\n" },
            "ticket", from: EpicBranch);
        var squash = w.Forge.Commit(w.Repository, EpicBranch, new Dictionary<string, string?> { ["tests/WidgetTests.cs"] = "RefusesLongNames\n" }, "squash T-1");
        var epicHead = w.Forge.Commit(w.Repository, EpicBranch, new Dictionary<string, string?> { ["docs/epic.md"] = "E, reviewed\n" }, "epic fix");
        var tests = w.TestEvidence(ticketHead, ["WidgetTests.RefusesLongNames"]);
        var merge = Record("merge_record", "merge_record",
            w.PutEngine(new MergeEvidence(Repo, "ff/epic/E", MergeMethods.Squash, ticketHead, null, squash, "feature-flow[bot]", false, null)), "scm.merge", true, TicketRun, "merge");
        var artifacts = new List<ArtifactRecord>
        {
            Record("requirements", "requirements", w.Put(EpicRequirements), runId: EpicRun, step: "design"),
            Record("child_requirements", "delivery_plan", w.Put(EpicPlan), runId: EpicRun, step: "tickets.plan"),
            Record("delivery_plan", "delivery_plan", w.Put(InheritedPlan()), runId: TicketRun, step: "plan"),
            Record("implementation_claims", "evidence_claims", w.Put(Claims()), runId: TicketRun, step: "implement"),
            tests,
            merge,
        };
        return (artifacts, epicHead, merge, tests);
    }

    ValidationRequest Trace(IReadOnlyList<ArtifactRecord> artifacts, string head) =>
        World.Request(Names.RequirementTrace, [new ValidationSubject("requirements", artifacts[0].Type, new ArtifactRef(artifacts[0].Ref))], Frozen(head), artifacts);

    [Fact]
    public async Task The_trace_follows_an_owner_into_a_child_and_a_squash_merge_into_the_work()
    {
        var (artifacts, head, _, _) = EpicWithTicket();

        var result = await ValidateAsync(Trace(artifacts, head));

        Valid(result);
        var report = System.Text.Json.JsonSerializer.Deserialize<TraceReport>(await w.ReadAsync(result.Reports.Single().Artifact), ArtifactShapes.Engine)!;
        Assert.True(report.Traced);
        Assert.Equal("E-1", Assert.Single(report.Requirements).Id);
        Assert.Contains("WidgetTests.RefusesLongNames", Assert.Single(report.Requirements[0].Tests));
    }

    [Fact]
    public async Task The_trace_has_a_gap_when_the_tested_head_never_landed_in_the_work()
    {
        var (artifacts, head, merge, _) = EpicWithTicket();
        artifacts.Remove(merge);

        var result = await ValidateAsync(Trace(artifacts, head));

        Invalid(result, "trace_gap");
        Assert.Contains("did not pass at a commit acme/api at", result.Issues.Single().Message);
    }

    [Fact]
    public async Task The_trace_has_a_gap_when_the_owner_never_planned()
    {
        var (artifacts, head, _, _) = EpicWithTicket();
        artifacts.RemoveAll(a => a.Name == "delivery_plan");

        var result = await ValidateAsync(Trace(artifacts, head));

        Invalid(result, "trace_gap");
        Assert.Contains("its owner, ticket T-1, has no plan holding it", result.Issues.Single().Message);
    }

    /// <summary>
    /// The trace proves an item only with the tests and claims of the run whose plan holds it (review F4): a sibling ticket's
    /// passing tests or claims, also in the epic's index, prove nothing about T-1's criterion.
    /// </summary>
    [Theory]
    [InlineData("test_results")]
    [InlineData("implementation_claims")]
    public async Task The_trace_takes_tests_and_claims_only_from_the_run_whose_plan_holds_the_item(string moved)
    {
        var (artifacts, head, _, _) = EpicWithTicket();
        var at = artifacts.FindIndex(a => a.Name == moved);
        artifacts[at] = artifacts[at] with { RunId = "run-ticket-2" };

        Invalid(await ValidateAsync(Trace(artifacts, head)), "trace_gap");
    }

    [Fact]
    public async Task Tests_at_an_ancestor_of_the_head_are_contained_through_the_VCS_and_not_without_it()
    {
        var tested = w.Commit("tests/WidgetTests.cs", "RefusesLongNames\n");
        var head = w.Commit("docs/notes.md", "later\n");
        var artifacts = new List<ArtifactRecord>
        {
            Record("requirements", "requirements", w.Put(TicketRequirements)),
            Record("delivery_plan", "delivery_plan", w.Put(TicketPlan())),
            Record("implementation_claims", "evidence_claims", w.Put(Claims())),
            w.TestEvidence(tested, ["WidgetTests.RefusesLongNames"]),
        };

        Valid(await ValidateAsync(Trace(artifacts, head)));
        var noVcs = new StdValidators(() => w.Store, () => new FeatureFlow.Providers.Local.InMemoryCodeHost(w.Forge), () => null);
        Invalid(await ValidateAsync(noVcs, Trace(artifacts, head)), "trace_gap");
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("artifact.compose", true)]
    public async Task Test_results_not_made_by_an_engine_test_action_prove_nothing_to_the_trace(string? action, bool evidence)
    {
        var (artifacts, head, _, tests) = EpicWithTicket();
        artifacts[artifacts.IndexOf(tests)] = tests with { Action = action, Evidence = evidence };

        Invalid(await ValidateAsync(Trace(artifacts, head)), "trace_gap");
    }

    // ---- verification_satisfaction ----

    [Fact]
    public async Task Satisfaction_passes_with_neither_plan_nor_claims_as_for_an_epic_and_refuses_a_tier_judged_by_exit_code()
    {
        Valid(await ValidateAsync(World.Request(Names.VerificationSatisfaction, [Absent("delivery_plan", "current"), Absent("implementation_claims", "current")])));

        var head = w.Commit("tests/WidgetTests.cs", "RefusesLongNames\n");
        var exitOnly = w.TestEvidence(head, ["WidgetTests.RefusesLongNames"], exitCodeOnly: true);
        var result = await ValidateAsync(World.Request(Names.VerificationSatisfaction,
            [w.Subject("delivery_plan", "delivery_plan", TicketPlan()), w.Subject("implementation_claims", "evidence_claims", Claims()), Results(exitOnly)], Frozen(head), [exitOnly]));
        Invalid(result, "test_not_passed");
        Assert.Contains("judged by exit code", result.Issues.Single().Message);
    }

    [Fact]
    public async Task Satisfaction_checks_the_frozen_head_not_an_earlier_passing_run()
    {
        var tested = w.Commit("tests/WidgetTests.cs", "RefusesLongNames\n");
        var later = w.Commit("src/Widgets.cs", "class Widgets { }\n// later\n");
        var earlier = w.TestEvidence(tested, ["WidgetTests.RefusesLongNames"]);
        var result = await ValidateAsync(World.Request(Names.VerificationSatisfaction,
            [w.Subject("delivery_plan", "delivery_plan", TicketPlan()), w.Subject("implementation_claims", "evidence_claims", Claims()), Results(earlier)], Frozen(later),
            [earlier]));

        Invalid(result, "not_tested");
    }

    /// <summary>
    /// The artifact index holds a run's descendants' artifacts too (W1). An epic's own claim is satisfied only by the epic's
    /// own test results: a child ticket's passing results at the same revision are not the epic's evidence.
    /// </summary>
    [Fact]
    public async Task Satisfaction_counts_only_this_runs_test_results_not_a_childs()
    {
        var head = w.Commit("tests/WidgetTests.cs", "RefusesLongNames\n");
        var own = w.TestEvidence(head, ["WidgetTests.Other"], runId: "run-epic");
        var childs = w.TestEvidence(head, ["WidgetTests.RefusesLongNames"], runId: TicketRun);
        ValidationRequest Request(params ArtifactRecord[] artifacts) => World.Request(Names.VerificationSatisfaction,
            [w.Subject("delivery_plan", "delivery_plan", TicketPlan()), w.Subject("implementation_claims", "evidence_claims", Claims()), Results(own)], Frozen(head), artifacts);

        Invalid(await ValidateAsync(Request(own, childs)), "test_not_passed");
        // The same passing results as the run's own satisfy the claim: the run, not the content, made the difference.
        Valid(await ValidateAsync(World.Request(Names.VerificationSatisfaction,
            [w.Subject("delivery_plan", "delivery_plan", TicketPlan()), w.Subject("implementation_claims", "evidence_claims", Claims()), Results(childs)], Frozen(head), [own, childs])));
        // A subject not in the index leaves this run unknown: nothing is counted.
        Invalid(await ValidateAsync(Request(childs)), "own_run_unknown");
    }

    static ValidationSubject Results(ArtifactRecord r) => new("test_results", r.Type, new ArtifactRef(r.Ref));

    [Fact]
    public void The_factory_registers_kind_std_for_the_validator_type()
    {
        var factory = Assert.Single(StdValidatorProviders.Factories);
        Assert.Equal(("validator", "std"), (factory.Type, factory.Kind));
        Assert.Equal(8, w.Validators.Validators().Count);
    }

    /// <summary>A real git repository in a temporary folder, and a code host that reads its files at revisions with <c>git show</c>.</summary>
    sealed class GitRepository : IDisposable
    {
        readonly string dir = Path.Combine(Path.GetTempPath(), "ff-std-git-" + Guid.NewGuid().ToString("N"));

        public GitRepository()
        {
            Directory.CreateDirectory(dir);
            Git("init", "-q", "-b", "main");
            Host = new GitFileHost(this);
        }

        public ICodeHostProvider Host { get; }

        public string Commit(Dictionary<string, string> files)
        {
            foreach (var (path, content) in files)
            {
                var full = Path.Combine(dir, path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, content);
            }

            Git("add", "-A");
            Git("-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "change");
            return Git("rev-parse", "HEAD").Output.Trim();
        }

        public (int Exit, string Output) Git(params string[] arguments)
        {
            var start = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var a in arguments)
                start.ArgumentList.Add(a);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        sealed class GitFileHost(GitRepository repository) : ICodeHostProvider
        {
            public ProviderDescription Describe() => new("code_host", "git-files", "0", new NoCapabilities());
            public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

            public Task<ProviderResult<RepoFile?>> ReadFileAsync(RepoRef repo, string path, string reference, CancellationToken cancellationToken)
            {
                var (exit, content) = repository.Git("show", $"{reference}:{path}");
                return Task.FromResult(ProviderResult<RepoFile?>.Success(exit != 0
                    ? null
                    : new RepoFile(path, reference, content, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))))));
            }

            public Task<ProviderResult<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestSpec spec, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pullRequest, string? comment, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pullRequest, string key, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(RepoRef repo, string revision, CancellationToken cancellationToken) => throw new NotSupportedException();
            public Task<ProviderResult<CommentRef>> AddCommentAsync(PullRequestRef pullRequest, string body, string key, CancellationToken cancellationToken) => throw new NotSupportedException();
        }
    }
}
