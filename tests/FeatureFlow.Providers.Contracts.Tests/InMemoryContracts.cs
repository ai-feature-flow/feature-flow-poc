using FeatureFlow.Providers.Abstractions;
using FeatureFlow.Providers.Local;

namespace FeatureFlow.Providers.Contracts.Tests;

// The contract suites run against the in-memory providers. The providers builder adds subclasses for Git,
// GitHub and GitHub Actions beside their own tests.

static class World
{
    public static (InMemoryForge Forge, RepoRef Repo) Fresh()
    {
        var forge = new InMemoryForge();
        return (forge, forge.CreateRepository("ai-feature-flow/contract"));
    }

    public static Task<string> Commit(InMemoryForge forge, RepoRef repo, string reference, IReadOnlyDictionary<string, string?> changes, string? from) =>
        Task.FromResult(forge.Commit(repo, reference, changes, from: from));
}

public sealed class InMemoryVcsContract : VcsContract
{
    readonly InMemoryForge forge;

    public InMemoryVcsContract()
    {
        (forge, Repo) = World.Fresh();
        Vcs = new InMemoryVcs(forge);
    }

    protected override IVcsProvider Vcs { get; }

    protected override RepoRef Repo { get; }

    protected override Task<string> CommitOnRemoteAsync(string reference, IReadOnlyDictionary<string, string?> changes, string? from = null) =>
        World.Commit(forge, Repo, reference, changes, from);

    protected override Task<string?> ReadOnRemoteAsync(string revision, string path) => Task.FromResult(forge.ReadFile(Repo, revision, path));
}

public sealed class InMemoryCodeHostContract : CodeHostContract
{
    readonly InMemoryForge forge;

    public InMemoryCodeHostContract()
    {
        (forge, Repo) = World.Fresh();
        CodeHost = new InMemoryCodeHost(forge);
    }

    protected override ICodeHostProvider CodeHost { get; }

    protected override RepoRef Repo { get; }

    protected override Task<string> CommitOnRemoteAsync(string branch, IReadOnlyDictionary<string, string?> changes, string? from = null) =>
        World.Commit(forge, Repo, branch, changes, from);

    protected override Task<string> MergeAsPersonAsync(PullRequestRef pullRequest)
    {
        forge.MergePullRequest(pullRequest, "a-person");
        return Task.FromResult("a-person");
    }
}

public sealed class InMemoryCiContract : CiContract, IDisposable
{
    readonly InMemoryForge forge;
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-ci-contract-" + Guid.NewGuid().ToString("N"));

    public InMemoryCiContract()
    {
        (forge, Repo) = World.Fresh();
        var store = new FileSystemArtifactStore(folder);
        Ci = new InMemoryCi(forge, () => store);
        forge.ScriptCi("ci.yml", new CiScript(Conclusions.Success, [new CiScriptFile("tests.trx", ResultFormats.Trx, "<TestRun />")], PollsUntilDone: 2, Log: "ok"));
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    protected override ICiProvider Ci { get; }

    protected override RepoRef Repo { get; }

    protected override CiJobRef PassingJob { get; } = new("ci.yml", new Dictionary<string, string>());

    protected override Task<string> HeadAsync(bool withCi)
    {
        forge.ConfigureCi(Repo, withCi);
        return Task.FromResult(forge.Resolve(Repo, "main")!);
    }
}

public sealed class InMemoryTrackerContract : TrackerContract
{
    readonly InMemoryTracker tracker = new();

    protected override ITrackerProvider Tracker => tracker;

    protected override (string First, string Second) Statuses => ("in_progress", "done");

    protected override Task<IReadOnlyList<string>> CommentsAsync(IssueRef issue) => Task.FromResult(tracker.Comments(issue));
}

public sealed class InMemoryFindingsSourceContract : FindingsSourceContract, IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ff-findings-contract-" + Guid.NewGuid().ToString("N"));
    readonly InMemoryFindingsSource source;
    static readonly RepoRef Repo = new("ai-feature-flow/contract");

    public InMemoryFindingsSourceContract()
    {
        Store = new FileSystemArtifactStore(folder);
        source = new InMemoryFindingsSource(() => Store);
        source.Review(Repo, "reviewed", new FindingsReport(1,
        [
            new ReportedFinding("null-check", "Missing null check", "Add throws on null.", "high", true, FindingBases.Reasoned,
                new FindingLocation(Repo.FullName, "src/Orders.cs", "Orders.Add", "items.Add(item);")),
        ]), pollsUntilReady: 2);
    }

    public void Dispose()
    {
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
    }

    protected override IFindingsSourceProvider Source => source;

    protected override IArtifactStoreProvider Store { get; }

    protected override Task<FindingsTarget> ReviewedTargetAsync() => Task.FromResult(new FindingsTarget(Repo, "reviewed", new PullRequestRef(Repo, 1)));

    protected override Task<FindingsTarget> UnavailableTargetAsync() => Task.FromResult(new FindingsTarget(Repo, "not-reviewed"));
}

public sealed class InMemoryValidatorsContract : ValidatorContract
{
    static ValidationRequest Request(string validator, IReadOnlyDictionary<string, string>? options = null, params ValidationSubject[] subjects) =>
        new(validator, subjects, options ?? new Dictionary<string, string>(), new Dictionary<string, string> { ["acme/api"] = "rev1" }, [], []);

    readonly InMemoryValidators validators = new InMemoryValidators().Add(
        new ValidatorDescriptor("has_subject", "The subject exists", ["requirements"], new Dictionary<string, string>(), []),
        r => ValidationResult.Of(r.Subjects.Where(s => s.Absent).Select(s => new ValidationIssue("absent", ValidationSeverities.Error, $"{s.Name} is absent", s.Name)).ToList()));

    protected override IValidatorProvider Validators => validators;

    protected override IReadOnlyList<ValidatorFixture> Fixtures { get; } =
    [
        new("scripted, valid", Request(InMemoryValidators.Scripted), true),
        new("scripted, invalid", Request(InMemoryValidators.Scripted, new Dictionary<string, string> { ["valid"] = "false", ["code"] = "\"nope\"" }), false, "nope"),
        new("has_subject, present", Request("has_subject", null, new ValidationSubject("requirements", "std:artifact_types/requirements", new ArtifactRef("sha256:1"))), true),
        new("has_subject, absent", Request("has_subject", null, new ValidationSubject("requirements", "std:artifact_types/requirements", null, "parent")), false, "absent"),
    ];
}

public sealed class InMemoryFindingsLedgerContract : FindingsLedgerContract
{
    protected override IRecordStoreProvider Store { get; } = new InMemoryRecordStore();
}
