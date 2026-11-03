using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Local;

/// <summary>A scripted file a fake CI run produces: name, format (<see cref="ResultFormats"/>) and content.</summary>
public sealed record CiScriptFile(string Name, string Format, string Content);

/// <summary>
/// How a fake CI run of a workflow behaves: its conclusion, the result files it produces, how many status polls it
/// stays in progress for, and its log.
/// </summary>
public sealed record CiScript(string Conclusion, IReadOnlyList<CiScriptFile> Files, int PollsUntilDone = 0, string Log = "")
{
    public static readonly CiScript Passing = new(Conclusions.Success, []);
}

/// <summary>
/// An in-memory code host world for the <c>memory</c> VCS, code host and CI providers (M5): repositories of
/// commits and references, pull requests, checks and CI runs. Tests arrange it through the public methods (commits
/// by someone other than the engine, a person's merge or review, checks, scripted CI) and inject provider errors
/// with <see cref="Fail"/>. Providers configured with the same <c>world</c> name share one instance
/// (<see cref="Named"/>). Revisions are content hashes, so the same commit made twice has the same revision.
/// </summary>
public sealed class InMemoryForge
{
    static readonly ConcurrentDictionary<string, InMemoryForge> Worlds = new(StringComparer.Ordinal);

    /// <summary>The world providers configured with <c>world: &lt;name&gt;</c> share.</summary>
    public static InMemoryForge Named(string name) => Worlds.GetOrAdd(name, _ => new InMemoryForge());

    /// <summary>The identity the engine's own writes are recorded under.</summary>
    public const string EngineActor = "feature-flow[bot]";

    public sealed record StoredCommit(string Id, IReadOnlyList<string> Parents, IReadOnlyDictionary<string, string> Files, string Message);

    internal sealed class Repo
    {
        public string DefaultBranch = "main";
        public readonly Dictionary<string, StoredCommit> Commits = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> Refs = new(StringComparer.Ordinal);
        public readonly Dictionary<string, string> RefActors = new(StringComparer.Ordinal);
        public readonly Dictionary<string, BranchProtection> Protection = new(StringComparer.Ordinal);
        public readonly List<PullRequest> PullRequests = [];
        public readonly Dictionary<string, List<CheckRun>> Checks = new(StringComparer.Ordinal);
        public bool HasCi;
        public readonly Dictionary<string, CiRun> Runs = new(StringComparer.Ordinal);
    }

    internal sealed class PullRequest
    {
        public required int Number;
        public required string Key;
        public required PullRequestSpec Spec;
        public string State = PullRequestStates.Open;
        public string HeadRevision = "";
        public readonly Dictionary<string, bool> Reviews = new(StringComparer.Ordinal);
        public readonly List<string> Comments = [];
        public string? MergedBy;
        public string? MergeRevision;
    }

    internal sealed class CiRun
    {
        public required CiRunRef Ref;
        public required CiJobRef Job;
        public required CiScript Script;
        public int PollsLeft;
        public CiRunResult? Result;
    }

    internal sealed record WorkingCopy(RepoRef Repo, Dictionary<string, string> Files)
    {
        public string Head = "";
    }

    internal readonly object Gate = new();
    readonly Dictionary<string, Repo> repos = new(StringComparer.Ordinal);
    readonly Dictionary<string, Queue<ProviderError>> faults = new(StringComparer.Ordinal);
    readonly Dictionary<string, CiScript> ciScripts = new(StringComparer.Ordinal);
    internal readonly Dictionary<WorkingCopyRef, WorkingCopy> WorkingCopies = [];
    int runCounter;

    // ---- Arranging the world ----

    /// <summary>Creates a repository with one commit of <paramref name="files"/> on its default branch.</summary>
    public RepoRef CreateRepository(string fullName, IReadOnlyDictionary<string, string>? files = null, string defaultBranch = "main")
    {
        lock (Gate)
        {
            var repo = new Repo { DefaultBranch = defaultBranch };
            repos[fullName] = repo;
            var root = Add(repo, [], files ?? new Dictionary<string, string> { ["README.md"] = $"# {fullName}\n" }, "initial commit");
            Move(repo, Branch(defaultBranch), root, "someone");
            return new RepoRef(fullName);
        }
    }

    /// <summary>
    /// Commits <paramref name="changes"/> (a null value deletes the path) on top of <paramref name="reference"/> as
    /// <paramref name="actor"/> and moves the reference. A reference that does not exist starts from
    /// <paramref name="from"/>, default the default branch. Returns the new revision.
    /// </summary>
    public string Commit(RepoRef repo, string reference, IReadOnlyDictionary<string, string?> changes, string message = "a change", string? from = null,
        string actor = "someone")
    {
        lock (Gate)
        {
            var r = Get(repo);
            var parent = Lookup(r, reference) ?? Lookup(r, from ?? Branch(r.DefaultBranch)) ?? throw new InvalidOperationException($"{from} does not exist");
            var files = new Dictionary<string, string>(r.Commits[parent].Files, StringComparer.Ordinal);
            foreach (var (path, content) in changes)
                if (content is null)
                    files.Remove(path);
                else
                    files[path] = content;
            var id = Add(r, [parent], files, message);
            Move(r, Full(reference), id, actor);
            return id;
        }
    }

    /// <summary>Moves a reference as <paramref name="actor"/> (null deletes it), with no expectation: someone else's forced push.</summary>
    public void SetReference(RepoRef repo, string reference, string? revision, string actor = "someone")
    {
        lock (Gate)
        {
            var r = Get(repo);
            if (revision is null)
                r.Refs.Remove(Full(reference));
            else
                Move(r, Full(reference), revision, actor);
        }
    }

    public string? Resolve(RepoRef repo, string reference)
    {
        lock (Gate)
            return Lookup(Get(repo), reference);
    }

    public StoredCommit GetCommit(RepoRef repo, string revision)
    {
        lock (Gate)
            return Get(repo).Commits[revision];
    }

    /// <summary>The file's content at a revision or reference, or null.</summary>
    public string? ReadFile(RepoRef repo, string revisionOrReference, string path)
    {
        lock (Gate)
        {
            var r = Get(repo);
            return Lookup(r, revisionOrReference) is { } rev ? r.Commits[rev].Files.GetValueOrDefault(path) : null;
        }
    }

    /// <summary>Whether <paramref name="revision"/> is <paramref name="ancestor"/> or descends from it.</summary>
    public bool Contains(RepoRef repo, string revision, string ancestor)
    {
        lock (Gate)
            return Ancestors(Get(repo), revision).Contains(ancestor);
    }

    public void SetDefaultBranch(RepoRef repo, string branch)
    {
        lock (Gate)
            Get(repo).DefaultBranch = branch;
    }

    public void Protect(RepoRef repo, BranchProtection protection)
    {
        lock (Gate)
            Get(repo).Protection[protection.Branch] = protection;
    }

    /// <summary>A person's review of a pull request: approve, or request changes. Their latest review counts.</summary>
    public void Review(PullRequestRef pullRequest, string reviewer, bool approve)
    {
        lock (Gate)
            Find(pullRequest).Reviews[reviewer] = approve;
    }

    /// <summary>A person merges a pull request on the code host (a merge commit). Returns the merge revision.</summary>
    public string MergePullRequest(PullRequestRef pullRequest, string actor = "a-person")
    {
        lock (Gate)
        {
            var r = Get(pullRequest.Repo);
            var pr = Find(pullRequest);
            var head = Lookup(r, Branch(pr.Spec.Head))!;
            var target = Lookup(r, Branch(pr.Spec.Base))!;
            var (files, conflicts) = Merge(r, target, head);
            if (conflicts.Count > 0)
                throw new InvalidOperationException($"pull request {pullRequest.Number} has conflicts");
            var id = Add(r, [target, head], files, $"Merge pull request #{pr.Number}");
            Move(r, Branch(pr.Spec.Base), id, actor);
            Refresh(r, pr);
            return id;
        }
    }

    /// <summary>Adds or replaces (by name) a check on a commit.</summary>
    public void ReportCheck(RepoRef repo, string revision, CheckRun check)
    {
        lock (Gate)
        {
            var list = Checks(Get(repo), revision);
            list.RemoveAll(c => c.Name == check.Name);
            list.Add(check);
        }
    }

    /// <summary>Whether the CI provider says CI is configured, for any head of the repository. Default: not.</summary>
    public void ConfigureCi(RepoRef repo, bool configured = true)
    {
        lock (Gate)
            Get(repo).HasCi = configured;
    }

    /// <summary>How many CI runs were triggered in the repository.</summary>
    public int CiRunCount(RepoRef repo)
    {
        lock (Gate)
            return Get(repo).Runs.Count;
    }

    /// <summary>How runs of <paramref name="workflow"/> behave. Default: <see cref="CiScript.Passing"/>.</summary>
    public void ScriptCi(string workflow, CiScript script)
    {
        lock (Gate)
            ciScripts[workflow] = script;
    }

    /// <summary>
    /// The next <paramref name="times"/> calls of <paramref name="operation"/>, named <c>&lt;type&gt;.&lt;method without
    /// Async&gt;</c> such as <c>vcs.Push</c> or <c>code_host.OpenPullRequest</c>, fail with <paramref name="error"/>.
    /// </summary>
    public void Fail(string operation, ProviderError error, int times = 1)
    {
        lock (Gate)
        {
            if (!faults.TryGetValue(operation, out var queue))
                faults[operation] = queue = new Queue<ProviderError>();
            for (var i = 0; i < times; i++)
                queue.Enqueue(error);
        }
    }

    /// <summary>A working copy's files, to change as an agent would. Null when nothing is checked out there.</summary>
    public IDictionary<string, string>? WorkingFiles(WorkingCopyRef copy)
    {
        lock (Gate)
            return WorkingCopies.GetValueOrDefault(copy)?.Files;
    }

    /// <summary>The comments added to a pull request (on close).</summary>
    public IReadOnlyList<string> Comments(PullRequestRef pullRequest)
    {
        lock (Gate)
            return Find(pullRequest).Comments.ToList();
    }

    // ---- Shared by the providers ----

    internal ProviderError? TakeFault(string operation)
    {
        lock (Gate)
            return faults.TryGetValue(operation, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;
    }

    internal Repo? TryGet(RepoRef repo) => repos.GetValueOrDefault(repo.FullName);

    internal Repo Get(RepoRef repo) => TryGet(repo) ?? throw new InvalidOperationException($"no repository {repo.FullName} in this world");

    internal CiScript ScriptFor(string workflow) => ciScripts.GetValueOrDefault(workflow) ?? CiScript.Passing;

    internal int NextRun() => ++runCounter;

    internal static string Branch(string name) => name.StartsWith("refs/", StringComparison.Ordinal) ? name : "refs/heads/" + name;

    static string Full(string reference) => Branch(reference);

    /// <summary>A revision, a full reference, or a branch name, to a revision; null when none matches.</summary>
    internal static string? Lookup(Repo r, string what) =>
        r.Commits.ContainsKey(what) ? what : r.Refs.GetValueOrDefault(Full(what));

    internal static void Move(Repo r, string reference, string revision, string actor)
    {
        if (!r.Commits.ContainsKey(revision))
            throw new InvalidOperationException($"no commit {revision}");
        r.Refs[reference] = revision;
        r.RefActors[reference] = actor;
    }

    internal static string Add(Repo r, IReadOnlyList<string> parents, IReadOnlyDictionary<string, string> files, string message)
    {
        var sorted = new SortedDictionary<string, string>(files.ToDictionary(), StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(new { parents, files = sorted, message });
        var id = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(json)));
        r.Commits.TryAdd(id, new StoredCommit(id, parents, sorted, message));
        return id;
    }

    internal static HashSet<string> Ancestors(Repo r, string revision)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([revision]);
        while (queue.TryDequeue(out var id))
            if (r.Commits.TryGetValue(id, out var commit) && seen.Add(id))
                foreach (var p in commit.Parents)
                    queue.Enqueue(p);
        return seen;
    }

    /// <summary>The commits in <paramref name="to"/>'s history and not in <paramref name="from"/>'s.</summary>
    internal static int Count(Repo r, string from, string to) => Ancestors(r, to).Except(Ancestors(r, from)).Count();

    static string? MergeBase(Repo r, string a, string b)
    {
        var ofA = Ancestors(r, a);
        var queue = new Queue<string>([b]);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (queue.TryDequeue(out var id))
        {
            if (ofA.Contains(id))
                return id;
            if (seen.Add(id) && r.Commits.TryGetValue(id, out var c))
                foreach (var p in c.Parents)
                    queue.Enqueue(p);
        }

        return null;
    }

    /// <summary>A file-level three-way merge of <paramref name="theirs"/> into <paramref name="ours"/>: the merged files and the conflicting paths.</summary>
    internal static (Dictionary<string, string> Files, List<string> Conflicts) Merge(Repo r, string ours, string theirs)
    {
        var baseFiles = MergeBase(r, ours, theirs) is { } b ? r.Commits[b].Files : new Dictionary<string, string>();
        return ThreeWay(baseFiles, r.Commits[ours].Files, r.Commits[theirs].Files);
    }

    internal static (Dictionary<string, string> Files, List<string> Conflicts) ThreeWay(IReadOnlyDictionary<string, string> @base,
        IReadOnlyDictionary<string, string> ours, IReadOnlyDictionary<string, string> theirs)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (var path in @base.Keys.Union(ours.Keys).Union(theirs.Keys).Order(StringComparer.Ordinal))
        {
            var (b, o, t) = (@base.GetValueOrDefault(path), ours.GetValueOrDefault(path), theirs.GetValueOrDefault(path));
            var result = o == t ? o : o == b ? t : t == b ? o : null;
            if (result is null && !(o == b || t == b || o == t))
                conflicts.Add(path);
            else if (result is not null)
                files[path] = result;
        }

        return (files, conflicts);
    }

    internal static List<CheckRun> Checks(Repo r, string revision)
    {
        if (!r.Checks.TryGetValue(revision, out var list))
            r.Checks[revision] = list = [];
        return list;
    }

    internal PullRequest Find(PullRequestRef pullRequest) =>
        Get(pullRequest.Repo).PullRequests.FirstOrDefault(p => p.Number == pullRequest.Number)
        ?? throw new InvalidOperationException($"no pull request {pullRequest.Number}");

    /// <summary>An open pull request whose base contains its head has been merged, as a code host sees a merge by push.</summary>
    internal static void Refresh(Repo r, PullRequest pr)
    {
        if (pr.State != PullRequestStates.Open)
            return;
        if (Lookup(r, Branch(pr.Spec.Head)) is { } head)
            pr.HeadRevision = head;
        var target = Lookup(r, Branch(pr.Spec.Base));
        if (target is not null && pr.HeadRevision.Length > 0 && Ancestors(r, target).Contains(pr.HeadRevision))
        {
            pr.State = PullRequestStates.Merged;
            pr.MergedBy = r.RefActors.GetValueOrDefault(Branch(pr.Spec.Base));
            pr.MergeRevision = target;
        }
    }
}

/// <summary>The <c>memory</c> VCS provider over an <see cref="InMemoryForge"/>. Working copies are the world's, not a sandbox's.</summary>
public sealed class InMemoryVcs(InMemoryForge forge) : IVcsProvider
{
    const string Trailer = "Feature-Flow-Merge: ";

    public InMemoryForge Forge => forge;

    public ProviderDescription Describe() => new("vcs", "memory", "0.1.0", new VcsCapabilities("git", true, true, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Done(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<string?>> ResolveAsync(RepoRef repo, string reference, CancellationToken cancellationToken) =>
        Run("Resolve", repo, r => ProviderResult<string?>.Success(r.Refs.GetValueOrDefault(reference)));

    public Task<ProviderResult<Unit>> CheckOutAsync(RepoRef repo, string sourceReference, string revision, string branch, WorkingCopyRef target,
        CancellationToken cancellationToken) =>
        Run("CheckOut", repo, r =>
        {
            if (!r.Commits.TryGetValue(revision, out var commit))
                return ProviderResult<Unit>.Failure("validation_failed", $"no commit {revision}");
            forge.WorkingCopies[target] = new InMemoryForge.WorkingCopy(repo, new Dictionary<string, string>(commit.Files, StringComparer.Ordinal)) { Head = revision };
            return ProviderResult<Unit>.Success(default);
        });

    public Task<ProviderResult<string>> GetRevisionAsync(WorkingCopyRef copy, CancellationToken cancellationToken) =>
        InCopy("GetRevision", copy, (_, wc) => ProviderResult<string>.Success(wc.Head));

    public Task<ProviderResult<bool>> IsDirtyAsync(WorkingCopyRef copy, CancellationToken cancellationToken) =>
        InCopy("IsDirty", copy, (r, wc) => ProviderResult<bool>.Success(!SameFiles(wc.Files, r.Commits[wc.Head].Files)));

    public Task<ProviderResult<string?>> CommitAllAsync(WorkingCopyRef copy, string message, CancellationToken cancellationToken) =>
        InCopy("CommitAll", copy, (r, wc) =>
        {
            if (SameFiles(wc.Files, r.Commits[wc.Head].Files))
                return ProviderResult<string?>.Success(null);
            wc.Head = InMemoryForge.Add(r, [wc.Head], wc.Files, message);
            return ProviderResult<string?>.Success(wc.Head);
        });

    public Task<ProviderResult<CommitBundle>> FetchCommitsAsync(WorkingCopyRef copy, string sinceRevision, CancellationToken cancellationToken) =>
        InCopy("FetchCommits", copy, (r, wc) =>
            ProviderResult<CommitBundle>.Success(new CommitBundle(new ArtifactRef("memory:" + wc.Head), wc.Head, InMemoryForge.Count(r, sinceRevision, wc.Head))));

    public Task<ProviderResult<bool>> DescendsFromAsync(RepoRef repo, string revision, string ancestor, CancellationToken cancellationToken) =>
        Run("DescendsFrom", repo, r => !r.Commits.ContainsKey(revision) || !r.Commits.ContainsKey(ancestor)
            ? ProviderResult<bool>.Failure("not_found", $"{revision} or {ancestor} is not in {repo.FullName}")
            : ProviderResult<bool>.Success(InMemoryForge.Ancestors(r, revision).Contains(ancestor)));

    public Task<ProviderResult<int>> CountCommitsAsync(WorkingCopyRef copy, string fromRevision, string toRevision, CancellationToken cancellationToken) =>
        InCopy("CountCommits", copy, (r, _) => ProviderResult<int>.Success(InMemoryForge.Count(r, fromRevision, toRevision)));

    public Task<ProviderResult<IReadOnlyList<string>>> ChangedFilesAsync(WorkingCopyRef copy, string fromRevision, string toRevision,
        CancellationToken cancellationToken) =>
        InCopy("ChangedFiles", copy, (r, _) =>
        {
            var (a, b) = (r.Commits[fromRevision].Files, r.Commits[toRevision].Files);
            IReadOnlyList<string> changed = a.Keys.Union(b.Keys).Where(p => a.GetValueOrDefault(p) != b.GetValueOrDefault(p)).Order(StringComparer.Ordinal).ToList();
            return ProviderResult<IReadOnlyList<string>>.Success(changed);
        });

    public Task<ProviderResult<PushResult>> PushAsync(RepoRef repo, string reference, string revision, CommitBundle? commits, string? expectedOldRevision,
        CancellationToken cancellationToken) =>
        Run("Push", repo, r =>
        {
            if (!r.Commits.ContainsKey(revision))
                return ProviderResult<PushResult>.Failure("validation_failed", $"no commit {revision}");
            var current = r.Refs.GetValueOrDefault(reference);
            if (current != expectedOldRevision)
                return ProviderResult<PushResult>.Failure("base_moved", $"{reference} is not at {expectedOldRevision ?? "nothing"}");
            InMemoryForge.Move(r, reference, revision, InMemoryForge.EngineActor);
            return ProviderResult<PushResult>.Success(new PushResult(reference, expectedOldRevision, revision));
        });

    public Task<ProviderResult<CatchUpResult>> CatchUpAsync(RepoRef repo, string parentReference, string workReference, string expectedWorkRevision,
        string message, CancellationToken cancellationToken) =>
        Run("CatchUp", repo, r =>
        {
            if (r.Refs.GetValueOrDefault(parentReference) is not { } parent)
                return ProviderResult<CatchUpResult>.Failure("not_found", $"{parentReference} does not exist");
            if (r.Refs.GetValueOrDefault(workReference) is not { } work)
                return ProviderResult<CatchUpResult>.Failure("not_found", $"{workReference} does not exist");
            if (InMemoryForge.Ancestors(r, work).Contains(parent))
                return ProviderResult<CatchUpResult>.Success(new CatchUpResult(work != expectedWorkRevision, work, parent, []));
            if (work != expectedWorkRevision)
                return ProviderResult<CatchUpResult>.Failure("base_moved", $"{workReference} is not at {expectedWorkRevision}");
            var (files, conflicts) = InMemoryForge.Merge(r, work, parent);
            if (conflicts.Count > 0)
                return ProviderResult<CatchUpResult>.Success(new CatchUpResult(false, work, parent, conflicts));
            var merged = InMemoryForge.Add(r, [work, parent], files, message);
            InMemoryForge.Move(r, workReference, merged, InMemoryForge.EngineActor);
            return ProviderResult<CatchUpResult>.Success(new CatchUpResult(true, merged, parent, []));
        });

    public Task<ProviderResult<MergeResult>> MergeAsync(RepoRef repo, string testedRevision, string targetReference, string expectedTargetRevision,
        string method, string message, string key, CancellationToken cancellationToken) =>
        Run("Merge", repo, r =>
        {
            if (method is not (MergeMethods.Merge or MergeMethods.Squash))
                return ProviderResult<MergeResult>.Failure("validation_failed", $"unknown merge method '{method}'");
            if (!r.Commits.TryGetValue(testedRevision, out var tested))
                return ProviderResult<MergeResult>.Failure("not_found", $"no commit {testedRevision}");
            if (r.Refs.GetValueOrDefault(targetReference) is not { } target)
                return ProviderResult<MergeResult>.Failure("not_found", $"{targetReference} does not exist");
            var history = InMemoryForge.Ancestors(r, target);
            var marker = Trailer + key;
            if (history.Contains(testedRevision) || history.Any(id => r.Commits[id].Message.Split('\n').Contains(marker)))
                return ProviderResult<MergeResult>.Success(new MergeResult(target, null, method, AlreadyMerged: true));
            if (target != expectedTargetRevision)
                return ProviderResult<MergeResult>.Failure("base_moved", $"{targetReference} is not at {expectedTargetRevision}");
            if (!InMemoryForge.Ancestors(r, testedRevision).Contains(target))
                return ProviderResult<MergeResult>.Failure("base_moved", $"{testedRevision} does not contain {targetReference}'s {target}; catch up first");
            string[] parents = method == MergeMethods.Squash ? [target] : [target, testedRevision];
            var landed = InMemoryForge.Add(r, parents, tested.Files, $"{message}\n\n{marker}");
            InMemoryForge.Move(r, targetReference, landed, InMemoryForge.EngineActor);
            return ProviderResult<MergeResult>.Success(new MergeResult(landed, target, method, AlreadyMerged: false));
        });

    static bool SameFiles(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) =>
        a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var v) && v == kv.Value);

    Task<ProviderResult<T>> Run<T>(string operation, RepoRef repo, Func<InMemoryForge.Repo, ProviderResult<T>> work)
    {
        if (forge.TakeFault("vcs." + operation) is { } fault)
            return Done(new ProviderResult<T>(default, fault));
        lock (forge.Gate)
            return Done(forge.TryGet(repo) is { } r ? work(r) : ProviderResult<T>.Failure("not_found", $"no repository {repo.FullName}"));
    }

    Task<ProviderResult<T>> InCopy<T>(string operation, WorkingCopyRef copy, Func<InMemoryForge.Repo, InMemoryForge.WorkingCopy, ProviderResult<T>> work)
    {
        if (forge.TakeFault("vcs." + operation) is { } fault)
            return Done(new ProviderResult<T>(default, fault));
        lock (forge.Gate)
            return Done(forge.WorkingCopies.TryGetValue(copy, out var wc)
                ? work(forge.Get(wc.Repo), wc)
                : ProviderResult<T>.Failure("validation_failed", $"nothing is checked out at {copy.Path}"));
    }

    static Task<T> Done<T>(T value) => Task.FromResult(value);
}

/// <summary>The <c>memory</c> code host provider over an <see cref="InMemoryForge"/>.</summary>
public sealed class InMemoryCodeHost(InMemoryForge forge) : ICodeHostProvider
{
    public InMemoryForge Forge => forge;

    public ProviderDescription Describe() => new("code_host", "memory", "0.1.0", new CodeHostCapabilities(true, false, true, true, false, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken cancellationToken) =>
        Run("GetDefaultBranch", repo, r => ProviderResult<string>.Success(r.DefaultBranch));

    public Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(RepoRef repo, string branch, CancellationToken cancellationToken) =>
        Run("GetBranchProtection", repo, r => r.Refs.ContainsKey(InMemoryForge.Branch(branch))
            ? ProviderResult<BranchProtection>.Success(r.Protection.GetValueOrDefault(branch) ?? new BranchProtection(branch, false, false, 0, [], false))
            : ProviderResult<BranchProtection>.Failure("not_found", $"no branch {branch}"));

    public Task<ProviderResult<RepoFile?>> ReadFileAsync(RepoRef repo, string path, string reference, CancellationToken cancellationToken) =>
        Run("ReadFile", repo, r =>
        {
            if (InMemoryForge.Lookup(r, reference) is not { } revision)
                return ProviderResult<RepoFile?>.Failure("not_found", $"no reference {reference}");
            return ProviderResult<RepoFile?>.Success(r.Commits[revision].Files.TryGetValue(path, out var content)
                ? new RepoFile(path, revision, content, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content))))
                : null);
        });

    public Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(RepoRef repo, PullRequestSpec spec, string key, CancellationToken cancellationToken) =>
        Run("OpenPullRequest", repo, r =>
        {
            if (r.PullRequests.FirstOrDefault(p => p.Key == key && p.State == PullRequestStates.Open) is { } existing)
            {
                existing.Spec = spec;
                return ProviderResult<PullRequestRef>.Success(new PullRequestRef(repo, existing.Number));
            }

            if (InMemoryForge.Lookup(r, InMemoryForge.Branch(spec.Head)) is not { } head || InMemoryForge.Lookup(r, InMemoryForge.Branch(spec.Base)) is not { } target)
                return ProviderResult<PullRequestRef>.Failure("validation_failed", $"{spec.Head} or {spec.Base} does not exist");
            if (InMemoryForge.Ancestors(r, target).Contains(head))
                return ProviderResult<PullRequestRef>.Failure("validation_failed", $"{spec.Head} has nothing {spec.Base} lacks");
            var pr = new InMemoryForge.PullRequest { Number = r.PullRequests.Count + 1, Key = key, Spec = spec, HeadRevision = head };
            r.PullRequests.Add(pr);
            return ProviderResult<PullRequestRef>.Success(new PullRequestRef(repo, pr.Number));
        });

    public Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(PullRequestRef pullRequest, PullRequestSpec spec, CancellationToken cancellationToken) =>
        WithPr("UpdatePullRequest", pullRequest, (_, pr) =>
        {
            pr.Spec = spec with { Head = pr.Spec.Head };
            return ProviderResult<PullRequestRef>.Success(pullRequest);
        });

    public Task<ProviderResult<PullRequestState>> GetPullRequestAsync(PullRequestRef pullRequest, CancellationToken cancellationToken) =>
        WithPr("GetPullRequest", pullRequest, (r, pr) =>
        {
            InMemoryForge.Refresh(r, pr);
            bool? mergeable = pr.State == PullRequestStates.Open && InMemoryForge.Lookup(r, InMemoryForge.Branch(pr.Spec.Base)) is { } target
                ? InMemoryForge.Merge(r, target, pr.HeadRevision).Conflicts.Count == 0
                : null;
            return ProviderResult<PullRequestState>.Success(new PullRequestState(pullRequest, pr.State, pr.Spec.Title, Body(pr), pr.Spec.Head, pr.HeadRevision,
                pr.Spec.Base, pr.Spec.Draft, mergeable,
                pr.Reviews.Where(kv => kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList(),
                pr.Reviews.Where(kv => !kv.Value).Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList(),
                pr.MergedBy, pr.MergeRevision, $"memory://{pullRequest.Repo.FullName}/pull/{pr.Number}"));
        });

    public Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pullRequest, string? comment, CancellationToken cancellationToken) =>
        WithPr("ClosePullRequest", pullRequest, (r, pr) =>
        {
            InMemoryForge.Refresh(r, pr);
            if (comment is not null)
                pr.Comments.Add(comment);
            if (pr.State == PullRequestStates.Open)
                pr.State = PullRequestStates.Closed;
            return ProviderResult<Unit>.Success(default);
        });

    public Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(PullRequestRef pullRequest, string key, CancellationToken cancellationToken) =>
        WithPr("CreateRevertPullRequest", pullRequest, (r, pr) =>
        {
            if (r.PullRequests.FirstOrDefault(p => p.Key == key) is { } existing)
                return ProviderResult<PullRequestRef>.Success(new PullRequestRef(pullRequest.Repo, existing.Number));
            InMemoryForge.Refresh(r, pr);
            if (pr.State != PullRequestStates.Merged || pr.MergeRevision is null)
                return ProviderResult<PullRequestRef>.Failure("validation_failed", $"pull request {pr.Number} is not merged");
            var merge = r.Commits[pr.MergeRevision];
            var before = merge.Parents.Count > 0 ? r.Commits[merge.Parents[0]].Files : new Dictionary<string, string>();
            var target = InMemoryForge.Lookup(r, InMemoryForge.Branch(pr.Spec.Base))!;
            var (files, conflicts) = InMemoryForge.ThreeWay(merge.Files, r.Commits[target].Files, before);
            if (conflicts.Count > 0)
                return ProviderResult<PullRequestRef>.Failure("conflict", $"reverting pull request {pr.Number} conflicts in {string.Join(", ", conflicts)}");
            var branch = $"revert-{pr.Number}";
            InMemoryForge.Move(r, InMemoryForge.Branch(branch), InMemoryForge.Add(r, [target], files, $"Revert pull request #{pr.Number}"), InMemoryForge.EngineActor);
            var revert = new InMemoryForge.PullRequest
            {
                Number = r.PullRequests.Count + 1,
                Key = key,
                Spec = new PullRequestSpec($"Revert \"{pr.Spec.Title}\"", $"Reverts #{pr.Number}", branch, pr.Spec.Base),
            };
            InMemoryForge.Refresh(r, revert);
            r.PullRequests.Add(revert);
            return ProviderResult<PullRequestRef>.Success(new PullRequestRef(pullRequest.Repo, revert.Number));
        });

    public Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(RepoRef repo, string revision, CancellationToken cancellationToken) =>
        Run("GetCheckStatus", repo, r => ProviderResult<CheckStatus>.Success(new CheckStatus(revision, InMemoryForge.Checks(r, revision).ToList())));

    static string Body(InMemoryForge.PullRequest pr) => $"{pr.Spec.Body}\n\n<!-- feature-flow:{pr.Key} -->";

    Task<ProviderResult<T>> Run<T>(string operation, RepoRef repo, Func<InMemoryForge.Repo, ProviderResult<T>> work)
    {
        if (forge.TakeFault("code_host." + operation) is { } fault)
            return Task.FromResult(new ProviderResult<T>(default, fault));
        lock (forge.Gate)
            return Task.FromResult(forge.TryGet(repo) is { } r ? work(r) : ProviderResult<T>.Failure("not_found", $"no repository {repo.FullName}"));
    }

    Task<ProviderResult<T>> WithPr<T>(string operation, PullRequestRef pullRequest, Func<InMemoryForge.Repo, InMemoryForge.PullRequest, ProviderResult<T>> work) =>
        Run(operation, pullRequest.Repo, r => r.PullRequests.FirstOrDefault(p => p.Number == pullRequest.Number) is { } pr
            ? work(r, pr)
            : ProviderResult<T>.Failure("not_found", $"no pull request {pullRequest.Number}"));
}

/// <summary>
/// The <c>memory</c> CI provider over an <see cref="InMemoryForge"/>. A triggered run follows its workflow's
/// <see cref="CiScript"/> and reports a check, under the workflow's name, on the revision it builds.
/// </summary>
public sealed class InMemoryCi(InMemoryForge forge, Func<IArtifactStoreProvider> store) : ICiProvider
{
    public InMemoryForge Forge => forge;

    public ProviderDescription Describe() => new("ci", "memory", "0.1.0", new CiCapabilities(true, true, true, false, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => Task.FromResult(ProviderResult<Unit>.Success(default));

    public Task<ProviderResult<CiRunRef>> TriggerAsync(RepoRef repo, CiJobRef job, string revision, CancellationToken cancellationToken) =>
        Run("Trigger", repo, r =>
        {
            if (!r.Commits.ContainsKey(revision))
                return ProviderResult<CiRunRef>.Failure("validation_failed", $"no commit {revision}");
            var id = forge.NextRun().ToString(System.Globalization.CultureInfo.InvariantCulture);
            var script = forge.ScriptFor(job.Workflow);
            var run = new InMemoryForge.CiRun
            {
                Ref = new CiRunRef(repo, id, revision, $"memory://{repo.FullName}/actions/runs/{id}"),
                Job = job,
                Script = script,
                PollsLeft = script.PollsUntilDone,
            };
            r.Runs[id] = run;
            Report(r, run);
            return ProviderResult<CiRunRef>.Success(run.Ref);
        });

    public Task<ProviderResult<CiRunStatus>> GetRunStatusAsync(CiRunRef run, CancellationToken cancellationToken) =>
        WithRun("GetRunStatus", run, (r, x) =>
        {
            if (x.PollsLeft > 0)
                x.PollsLeft--;
            Report(r, x);
            return ProviderResult<CiRunStatus>.Success(x.PollsLeft > 0 ? new CiRunStatus(CheckStates.InProgress, null) : new CiRunStatus(CheckStates.Completed, x.Script.Conclusion));
        });

    public async Task<ProviderResult<CiRunResult>> ReadResultsAsync(CiRunRef run, CancellationToken cancellationToken)
    {
        var found = await WithRun("ReadResults", run, (_, x) => ProviderResult<InMemoryForge.CiRun>.Success(x));
        if (!found.Ok)
            return new ProviderResult<CiRunResult>(null, found.Error);
        var x = found.Value!;
        if (x.PollsLeft > 0)
            return ProviderResult<CiRunResult>.Failure("validation_failed", $"run {run.Id} has not completed");
        if (x.Result is { } cached)
            return ProviderResult<CiRunResult>.Success(cached);
        var files = new List<CiResultFile>();
        foreach (var file in x.Script.Files)
        {
            var put = await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(file.Content)), cancellationToken);
            if (!put.Ok)
                return new ProviderResult<CiRunResult>(null, put.Error);
            files.Add(new CiResultFile(file.Name, file.Format, put.Value!));
        }

        lock (forge.Gate)
            x.Result ??= new CiRunResult(x.Script.Conclusion, [new CiJobResult(x.Job.Workflow, x.Script.Conclusion)], files);
        return ProviderResult<CiRunResult>.Success(x.Result);
    }

    public async Task<ProviderResult<ArtifactRef>> ReadLogsAsync(CiRunRef run, CancellationToken cancellationToken)
    {
        var found = await WithRun("ReadLogs", run, (_, x) => ProviderResult<string>.Success(x.Script.Log));
        return found.Ok ? await store().PutAsync(new MemoryStream(Encoding.UTF8.GetBytes(found.Value!)), cancellationToken) : new ProviderResult<ArtifactRef>(null, found.Error);
    }

    public Task<ProviderResult<bool>> HasCiAsync(RepoRef repo, string revision, CancellationToken cancellationToken) =>
        Run("HasCi", repo, r => ProviderResult<bool>.Success(r.HasCi));

    static void Report(InMemoryForge.Repo r, InMemoryForge.CiRun run)
    {
        var done = run.PollsLeft == 0;
        var list = InMemoryForge.Checks(r, run.Ref.Revision);
        list.RemoveAll(c => c.Name == run.Job.Workflow);
        list.Add(new CheckRun(run.Job.Workflow, done ? CheckStates.Completed : CheckStates.InProgress, done ? run.Script.Conclusion : null, run.Job.Workflow, run.Ref.Url));
    }

    Task<ProviderResult<T>> Run<T>(string operation, RepoRef repo, Func<InMemoryForge.Repo, ProviderResult<T>> work)
    {
        if (forge.TakeFault("ci." + operation) is { } fault)
            return Task.FromResult(new ProviderResult<T>(default, fault));
        lock (forge.Gate)
            return Task.FromResult(forge.TryGet(repo) is { } r ? work(r) : ProviderResult<T>.Failure("not_found", $"no repository {repo.FullName}"));
    }

    Task<ProviderResult<T>> WithRun<T>(string operation, CiRunRef run, Func<InMemoryForge.Repo, InMemoryForge.CiRun, ProviderResult<T>> work) =>
        Run(operation, run.Repo, r => r.Runs.TryGetValue(run.Id, out var x) ? work(r, x) : ProviderResult<T>.Failure("not_found", $"no run {run.Id}"));
}
