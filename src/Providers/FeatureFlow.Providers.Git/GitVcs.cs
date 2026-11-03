using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Git;

/// <summary>
/// The Git VCS provider (docs/06 section 3.3). The remote is reached from a cache of bare
/// clones on the worker (docs/02 section 6), with a credential from the broker given to <c>git</c> in its
/// environment, never in a command line or a URL. A working copy in a sandbox is reached through the sandbox
/// provider; commits leave it as a bundle (LOG 34). Every push names the revision it expects the reference to
/// have, so the remote refuses it if anything moved (<c>base_moved</c>).
/// </summary>
public sealed class GitVcs(
    string remoteTemplate,
    string cache,
    Func<ISandboxProvider> sandboxes,
    Func<IArtifactStoreProvider> store,
    Func<ICredentialBroker?> broker) : IVcsProvider
{
    readonly ConcurrentDictionary<string, Credential> credentials = new();
    static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public ProviderDescription Describe() => new("vcs", "git", "0.1.0", new VcsCapabilities("git", true, true, false));

    public async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        if (EnsureCache() is { } broken)
            return new ProviderResult<Unit>(default, broken);
        var version = await GitAsync(cache, ["--version"], null, cancellationToken);
        return version.Ok && version.Value!.Exit == 0 ? ProviderResult<Unit>.Success(default) : ProviderResult<Unit>.Failure("unreachable", "git cannot be run on the worker");
    }

    /// <summary>Creates the cache folder; a disk the worker cannot write is infrastructure, not an exception.</summary>
    ProviderError? EnsureCache()
    {
        try
        {
            Directory.CreateDirectory(cache);
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ProviderError("internal_error", $"the clone cache {cache} cannot be created: {e.Message}");
        }
    }

    string UrlOf(RepoRef repo) => remoteTemplate.Replace("{repo}", repo.FullName, StringComparison.Ordinal);

    string CacheOf(RepoRef repo) => Path.Combine(cache, repo.FullName.Replace('/', '_') + ".git");

    public async Task<ProviderResult<string?>> ResolveAsync(RepoRef repo, string reference, CancellationToken cancellationToken)
    {
        OperationTimeout.Value = CommandTimeout;
        var env = await EnvironmentAsync(repo, write: false, cancellationToken);
        if (!env.Ok)
            return new ProviderResult<string?>(null, env.Error);
        if (EnsureCache() is { } broken)
            return new ProviderResult<string?>(null, broken);
        var run = await GitAsync(cache, ["ls-remote", UrlOf(repo), reference], env.Value, cancellationToken);
        if (Failed(run, "read the remote") is { } failed)
            return new ProviderResult<string?>(null, failed);
        var line = run.Value!.Output.Split('\n').FirstOrDefault(l => l.EndsWith("\t" + reference, StringComparison.Ordinal));
        return ProviderResult<string?>.Success(line?.Split('\t')[0]);
    }

    public async Task<ProviderResult<Unit>> CheckOutAsync(RepoRef repo, string sourceReference, string revision, string branch, WorkingCopyRef target,
        CancellationToken cancellationToken)
    {
        var bundled = "";
        var bundle = await WithCacheAsync(repo, async (dir, env, op) =>
        {
            bundled = $"{op}/bundle";
            if (Failed(await GitAsync(dir, ["fetch", "--no-tags", UrlOf(repo), $"+{sourceReference}:{op}/checkout"], env, cancellationToken), "fetch") is { } fetch)
                return new ProviderResult<ArtifactRef>(null, fetch);
            var file = Path.Combine(dir, $"checkout-{Guid.NewGuid():N}.bundle");
            try
            {
                if (Failed(await GitAsync(dir, ["update-ref", $"{op}/bundle", revision], env, cancellationToken), "mark the revision") is { } mark)
                    return new ProviderResult<ArtifactRef>(null, mark);
                if (Failed(await GitAsync(dir, ["bundle", "create", file, $"{op}/bundle"], env, cancellationToken), "bundle the checkout") is { } create)
                    return new ProviderResult<ArtifactRef>(null, create);
                await using var stream = File.OpenRead(file);
                return await store().PutAsync(stream, cancellationToken);
            }
            finally
            {
                File.Delete(file);
            }
        }, write: false, cancellationToken);
        if (!bundle.Ok)
            return new ProviderResult<Unit>(default, bundle.Error);

        var sandbox = sandboxes();
        var inSandbox = "/ff/tmp/checkout.bundle";
        var copied = await sandbox.CopyInAsync(target.Sandbox, bundle.Value!, inSandbox, readOnly: false, cancellationToken);
        if (!copied.Ok)
            return copied;
        var path = target.Path;
        // A repository with no remote: the agent has nothing to push to (F5).
        var script = $"set -e; git init -q {Q(path)}; cd {Q(path)}; git fetch -q {Q(inSandbox)} {Q(bundled)}; " +
                     $"git checkout -q -B {Q(branch)} {Q(revision)}; rm -f {Q(inSandbox)}";
        return await ShAsync(target.Sandbox, script, $"checkout/{Guid.NewGuid():N}", cancellationToken) is { } failed
            ? new ProviderResult<Unit>(default, failed)
            : ProviderResult<Unit>.Success(default);
    }

    public async Task<ProviderResult<string>> GetRevisionAsync(WorkingCopyRef copy, CancellationToken cancellationToken)
    {
        var run = await InCopyAsync(copy, "git rev-parse HEAD", cancellationToken);
        return run.Ok ? ProviderResult<string>.Success(run.Value!.Trim()) : new ProviderResult<string>(null, run.Error);
    }

    public async Task<ProviderResult<bool>> IsDirtyAsync(WorkingCopyRef copy, CancellationToken cancellationToken)
    {
        var run = await InCopyAsync(copy, "git status --porcelain", cancellationToken);
        return run.Ok ? ProviderResult<bool>.Success(run.Value!.Trim().Length > 0) : new ProviderResult<bool>(default, run.Error);
    }

    public async Task<ProviderResult<string?>> CommitAllAsync(WorkingCopyRef copy, string message, CancellationToken cancellationToken)
    {
        var run = await InCopyAsync(copy, $"git add -A && if git diff --cached --quiet; then echo NONE; else git commit -q -m {Q(message)} && git rev-parse HEAD; fi", cancellationToken);
        if (!run.Ok)
            return new ProviderResult<string?>(null, run.Error);
        var text = run.Value!.Trim();
        return ProviderResult<string?>.Success(text == "NONE" ? null : text.Split('\n').Last().Trim());
    }

    public async Task<ProviderResult<CommitBundle>> FetchCommitsAsync(WorkingCopyRef copy, string sinceRevision, CancellationToken cancellationToken)
    {
        var file = $"/ff/tmp/commits-{Guid.NewGuid():N}.bundle";
        var run = await InCopyAsync(copy,
            $"mkdir -p /ff/tmp && git rev-parse HEAD && git rev-list --count {Q(sinceRevision)}..HEAD && git bundle create -q {Q(file)} HEAD ^{Q(sinceRevision)}", cancellationToken);
        if (!run.Ok)
            return new ProviderResult<CommitBundle>(null, run.Error);
        var lines = run.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var bundle = await sandboxes().CopyOutAsync(copy.Sandbox, file, cancellationToken);
        await InCopyAsync(copy, $"rm -f {Q(file)}", cancellationToken);
        if (!bundle.Ok)
            return new ProviderResult<CommitBundle>(null, bundle.Error);
        return ProviderResult<CommitBundle>.Success(new CommitBundle(bundle.Value!, lines[0].Trim(), int.Parse(lines[1].Trim(), System.Globalization.CultureInfo.InvariantCulture)));
    }

    public Task<ProviderResult<bool>> DescendsFromAsync(RepoRef repo, string revision, string ancestor, CancellationToken cancellationToken) =>
        WithCacheAsync(repo, async (dir, env, op) =>
        {
            async Task<bool> Present(string r) => Failed(await GitAsync(dir, ["cat-file", "-e", r + "^{commit}"], env, cancellationToken), "") is null;
            if (!await Present(revision) || !await Present(ancestor))
                await GitAsync(dir, ["fetch", "--no-tags", UrlOf(repo), "+refs/heads/*:refs/ff-cache/heads/*", "+refs/feature-flow/*:refs/ff-cache/ff/*"], env, cancellationToken);
            var run = await GitAsync(dir, ["merge-base", "--is-ancestor", ancestor, revision], env, cancellationToken);
            if (!run.Ok)
                return new ProviderResult<bool>(default, run.Error);
            return run.Value!.Exit switch
            {
                0 => ProviderResult<bool>.Success(true),
                1 => ProviderResult<bool>.Success(false),
                _ => ProviderResult<bool>.Failure("not_found", $"{revision} or {ancestor} is not on the remote: {FirstLine(run.Value.Error)}"),
            };
        }, write: false, cancellationToken);

    public async Task<ProviderResult<int>> CountCommitsAsync(WorkingCopyRef copy, string fromRevision, string toRevision, CancellationToken cancellationToken)
    {
        var run = await InCopyAsync(copy, $"git rev-list --count {Q(fromRevision)}..{Q(toRevision)}", cancellationToken);
        return run.Ok
            ? ProviderResult<int>.Success(int.Parse(run.Value!.Trim(), System.Globalization.CultureInfo.InvariantCulture))
            : new ProviderResult<int>(default, run.Error);
    }

    public async Task<ProviderResult<IReadOnlyList<string>>> ChangedFilesAsync(WorkingCopyRef copy, string fromRevision, string toRevision, CancellationToken cancellationToken)
    {
        var run = await InCopyAsync(copy, $"git diff --name-only {Q(fromRevision)} {Q(toRevision)}", cancellationToken);
        return run.Ok
            ? ProviderResult<IReadOnlyList<string>>.Success(run.Value!.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList())
            : new ProviderResult<IReadOnlyList<string>>(null, run.Error);
    }

    public Task<ProviderResult<PushResult>> PushAsync(RepoRef repo, string reference, string revision, CommitBundle? commits, string? expectedOldRevision,
        CancellationToken cancellationToken) =>
        WithCacheAsync(repo, async (dir, env, op) =>
        {
            async Task<bool> Present() => Failed(await GitAsync(dir, ["cat-file", "-e", revision + "^{commit}"], env, cancellationToken), "") is null;
            if (!await Present())
                // The revision, or the bundle's prerequisite (the reference's old revision or the branch the step
                // started from), may be on the remote only: fetch it, so a fresh cache works too.
                await GitAsync(dir, ["fetch", "--no-tags", UrlOf(repo), "+refs/heads/*:refs/ff-cache/heads/*", "+refs/feature-flow/*:refs/ff-cache/ff/*"], env, cancellationToken);
            if (commits is not null && !await Present())
            {
                var file = Path.Combine(dir, $"push-{Guid.NewGuid():N}.bundle");
                try
                {
                    var got = await store().GetAsync(commits.Bundle, cancellationToken);
                    if (!got.Ok)
                        return new ProviderResult<PushResult>(null, got.Error);
                    await using (var source = got.Value!)
                    await using (var target = File.Create(file))
                        await source.CopyToAsync(target, cancellationToken);
                    if (Failed(await GitAsync(dir, ["fetch", "--no-tags", file, $"+HEAD:{op}/pushed"], env, cancellationToken), "unbundle the commits") is { } unbundle)
                        return new ProviderResult<PushResult>(null, unbundle);
                }
                finally
                {
                    File.Delete(file);
                }
            }

            var lease = $"--force-with-lease={reference}:{expectedOldRevision ?? ""}";
            var push = await GitAsync(dir, ["push", "--porcelain", lease, UrlOf(repo), $"{revision}:{reference}"], env, cancellationToken);
            if (!push.Ok)
                return new ProviderResult<PushResult>(null, push.Error);
            var text = push.Value!.Output + push.Value.Error;
            if (push.Value.Exit != 0)
            {
                if (Moved(text))
                    return ProviderResult<PushResult>.Failure("base_moved", $"{reference} is not at {expectedOldRevision ?? "nothing"}");
                return ProviderResult<PushResult>.Failure(Classify(text, "rejected"), $"the push to {reference} failed: {FirstLine(text)}");
            }

            // Git skips the lease when the reference is already at the revision ("=", up to date): the reference
            // was not where the caller expected unless the caller expected exactly that.
            if (push.Value.Output.Split('\n').Any(l => l.StartsWith("=\t", StringComparison.Ordinal)) && expectedOldRevision != revision)
                return ProviderResult<PushResult>.Failure("base_moved", $"{reference} is not at {expectedOldRevision ?? "nothing"}");
            return ProviderResult<PushResult>.Success(new PushResult(reference, expectedOldRevision, revision));
        }, write: true, cancellationToken);

    /// <summary>
    /// The merge is built in the cache with <c>git merge-tree --write-tree</c> and <c>git commit-tree</c>, so no
    /// working tree is needed, then pushed expecting the work branch's revision.
    /// </summary>
    public Task<ProviderResult<CatchUpResult>> CatchUpAsync(RepoRef repo, string parentReference, string workReference, string expectedWorkRevision,
        string message, CancellationToken cancellationToken) =>
        WithCacheAsync(repo, async (dir, env, op) =>
        {
            var fetched = await FetchAsync(dir, repo, env, cancellationToken, (parentReference, $"{op}/parent"), (workReference, $"{op}/work"));
            if (fetched is not null)
                return new ProviderResult<CatchUpResult>(null, fetched);
            var parent = await RevParseAsync(dir, $"{op}/parent", env, cancellationToken);
            var work = await RevParseAsync(dir, $"{op}/work", env, cancellationToken);
            if (parent is null || work is null)
                return ProviderResult<CatchUpResult>.Failure("internal_error", "the fetched references do not resolve in the cache");

            if (await IsAncestorAsync(dir, parent, work, env, cancellationToken))
                return ProviderResult<CatchUpResult>.Success(new CatchUpResult(work != expectedWorkRevision, work, parent, []));
            if (work != expectedWorkRevision)
                return ProviderResult<CatchUpResult>.Failure("base_moved", $"{workReference} is not at {expectedWorkRevision}");

            var merge = await GitAsync(dir, ["merge-tree", "--write-tree", "--name-only", "--no-messages", work, parent], env, cancellationToken);
            if (!merge.Ok)
                return new ProviderResult<CatchUpResult>(null, merge.Error);
            var lines = merge.Value!.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
            if (merge.Value.Exit == 1)
                return ProviderResult<CatchUpResult>.Success(new CatchUpResult(false, work, parent, lines.Skip(1).Distinct(StringComparer.Ordinal).ToList()));
            if (merge.Value.Exit != 0 || lines.Count == 0)
                return ProviderResult<CatchUpResult>.Failure("validation_failed", $"git could not merge {parentReference} into {workReference}: {FirstLine(merge.Value.Error)}");

            var commit = await CommitTreeAsync(dir, lines[0], [work, parent], message, env, cancellationToken);
            if (!commit.Ok)
                return new ProviderResult<CatchUpResult>(null, commit.Error);
            var pushed = await LeasePushAsync(dir, repo, workReference, commit.Value!, work, env, cancellationToken);
            return pushed is null
                ? ProviderResult<CatchUpResult>.Success(new CatchUpResult(true, commit.Value!, parent, []))
                : new ProviderResult<CatchUpResult>(null, pushed);
        }, write: true, cancellationToken);

    /// <summary>
    /// DR18: the already-landed check runs before anything else, and again when the push is refused or its answer is
    /// lost, so a merge that landed is reported as merged, never as a failure.
    /// </summary>
    public Task<ProviderResult<MergeResult>> MergeAsync(RepoRef repo, string testedRevision, string targetReference, string expectedTargetRevision,
        string method, string message, string key, CancellationToken cancellationToken) =>
        WithCacheAsync(repo, async (dir, env, op) =>
        {
            if (method is not (MergeMethods.Merge or MergeMethods.Squash))
                return ProviderResult<MergeResult>.Failure("validation_failed", $"unknown merge method '{method}'");
            if (await FetchAsync(dir, repo, env, cancellationToken, (targetReference, $"{op}/target")) is { } fetchTarget)
                return new ProviderResult<MergeResult>(null, fetchTarget);
            if (!await PresentAsync(dir, testedRevision, env, cancellationToken))
                await GitAsync(dir, ["fetch", "--no-tags", UrlOf(repo), "+refs/heads/*:refs/ff-cache/heads/*", "+refs/feature-flow/*:refs/ff-cache/ff/*"], env, cancellationToken);
            if (!await PresentAsync(dir, testedRevision, env, cancellationToken))
                return ProviderResult<MergeResult>.Failure("not_found", $"{testedRevision} is not on the remote");
            var target = await RevParseAsync(dir, $"{op}/target", env, cancellationToken);
            if (target is null)
                return ProviderResult<MergeResult>.Failure("internal_error", "the fetched target does not resolve in the cache");

            if (await LandedAsync(dir, testedRevision, target, key, env, cancellationToken))
                return ProviderResult<MergeResult>.Success(new MergeResult(target, null, method, AlreadyMerged: true));
            if (target != expectedTargetRevision && !ControlPlainForcePush)
                return ProviderResult<MergeResult>.Failure("base_moved", $"{targetReference} is not at {expectedTargetRevision}");
            // From here the target is the expected revision (except under the control, which builds on the expected one as SP4's harness did).
            if (!await IsAncestorAsync(dir, expectedTargetRevision, testedRevision, env, cancellationToken))
                return ProviderResult<MergeResult>.Failure("base_moved", $"{testedRevision} does not contain {targetReference}'s {expectedTargetRevision}; catch up first");

            string[] parents = method == MergeMethods.Squash ? [expectedTargetRevision] : [expectedTargetRevision, testedRevision];
            var commit = await CommitTreeAsync(dir, testedRevision + "^{tree}", parents, $"{message.TrimEnd()}\n\n{MergeTrailer}{key}", env, cancellationToken);
            if (!commit.Ok)
                return new ProviderResult<MergeResult>(null, commit.Error);
            var pushed = await LeasePushAsync(dir, repo, targetReference, commit.Value!, ControlPlainForcePush ? null : expectedTargetRevision, env, cancellationToken);
            if (pushed is null)
                return ProviderResult<MergeResult>.Success(new MergeResult(commit.Value!, expectedTargetRevision, method, AlreadyMerged: false));

            // The push was refused or its answer lost: read the target again before calling it a failure.
            if (await FetchAsync(dir, repo, env, cancellationToken, (targetReference, $"{op}/target")) is not null)
                return new ProviderResult<MergeResult>(null, pushed);
            var now = await RevParseAsync(dir, $"{op}/target", env, cancellationToken);
            if (now == commit.Value)
                return ProviderResult<MergeResult>.Success(new MergeResult(commit.Value!, target, method, AlreadyMerged: false));
            if (now is not null && await LandedAsync(dir, testedRevision, now, key, env, cancellationToken))
                return ProviderResult<MergeResult>.Success(new MergeResult(now, null, method, AlreadyMerged: true));
            return new ProviderResult<MergeResult>(null, pushed);
        }, write: true, cancellationToken);

    public const string MergeTrailer = "Feature-Flow-Merge: ";

    /// <summary>
    /// SP4's control N1b only: the merge pushes with a plain <c>--force</c> and skips the expected-revision check, so
    /// the race test can show that without them merges are lost. Never set outside that test.
    /// </summary>
    internal bool ControlPlainForcePush { get; init; }

    /// <summary>Tests only: runs just before a catch-up or merge pushes, with the reference, so a test can move it in between.</summary>
    internal Func<string, Task>? BeforePushForTests { get; init; }

    /// <summary>The author and committer of the commits the engine makes (catch-up and merge commits).</summary>
    public static readonly IReadOnlyDictionary<string, string> EngineIdentity = new Dictionary<string, string>
    {
        ["GIT_AUTHOR_NAME"] = "Feature Flow",
        ["GIT_AUTHOR_EMAIL"] = "feature-flow@users.noreply.github.com",
        ["GIT_COMMITTER_NAME"] = "Feature Flow",
        ["GIT_COMMITTER_EMAIL"] = "feature-flow@users.noreply.github.com",
    };

    /// <summary>
    /// Whether the change landed on <paramref name="target"/>: it contains the tested revision, or a commit carrying the
    /// key's trailer whose tree is exactly the tested revision's. The tree is what makes the trailer evidence: anyone can
    /// write a trailer (an agent's commit merged earlier), but no one can make a commit with the tested tree before the
    /// tested revision exists.
    /// </summary>
    static async Task<bool> LandedAsync(string dir, string tested, string target, string key, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        if (await IsAncestorAsync(dir, tested, target, env, cancellationToken))
            return true;
        var tree = await RevParseTreeAsync(dir, tested, env, cancellationToken);
        if (tree is null)
            return false;
        // --grep narrows the walk cheaply; the trailer is then compared exactly, so key "a-1" never matches "a-10".
        var log = await GitAsync(dir, ["log", "-F", "--grep=" + MergeTrailer + key,
            "--format=%T%x02%(trailers:key=Feature-Flow-Merge,valueonly,separator=%x00)%x01", target], env, cancellationToken);
        return log.Ok && log.Value!.Exit == 0
            && log.Value.Output.Split('\u0001').Select(entry => entry.Trim().Split('\u0002'))
                .Any(parts => parts.Length == 2 && parts[0] == tree && parts[1].Split('\0', '\n').Any(v => v.Trim() == key));
    }

    static async Task<string?> RevParseTreeAsync(string dir, string revision, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var run = await GitAsync(dir, ["rev-parse", "--verify", "-q", revision + "^{tree}"], env, cancellationToken);
        return run.Ok && run.Value!.Exit == 0 ? run.Value.Output.Trim() : null;
    }

    async Task<ProviderError?> FetchAsync(string dir, RepoRef repo, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken,
        params (string Remote, string Local)[] references)
    {
        var arguments = new List<string> { "fetch", "--no-tags", UrlOf(repo) };
        arguments.AddRange(references.Select(r => $"+{r.Remote}:{r.Local}"));
        var run = await GitAsync(dir, arguments, env, cancellationToken);
        if (run.Ok && run.Value!.Exit != 0 && (run.Value.Error + run.Value.Output).Contains("couldn't find remote ref", StringComparison.Ordinal))
            return new ProviderError("not_found", $"{string.Join(" or ", references.Select(r => r.Remote))} does not exist: {FirstLine(run.Value.Error)}");
        return Failed(run, "fetch");
    }

    /// <summary>Pushes expecting <paramref name="expected"/>; null (control N1b only) is a plain forced push.</summary>
    async Task<ProviderError?> LeasePushAsync(string dir, RepoRef repo, string reference, string revision, string? expected, IReadOnlyDictionary<string, string> env,
        CancellationToken cancellationToken)
    {
        if (BeforePushForTests is { } before)
            await before(reference);
        var push = await GitAsync(dir, ["push", "--porcelain", expected is null ? "--force" : $"--force-with-lease={reference}:{expected}", UrlOf(repo), $"{revision}:{reference}"],
            env, cancellationToken);
        if (!push.Ok)
            return push.Error;
        if (push.Value!.Exit == 0)
            return null;
        var text = push.Value.Output + push.Value.Error;
        return Moved(text)
            ? new ProviderError("base_moved", $"{reference} is not at {expected}")
            : new ProviderError(Classify(text, "rejected"), $"the push to {reference} failed: {FirstLine(text)}");
    }

    static async Task<ProviderResult<string>> CommitTreeAsync(string dir, string tree, IReadOnlyList<string> parents, string message,
        IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "commit-tree", tree };
        foreach (var p in parents)
            arguments.AddRange(["-p", p]);
        arguments.AddRange(["-m", message]);
        var withIdentity = new Dictionary<string, string>(env);
        foreach (var (k, v) in EngineIdentity)
            withIdentity[k] = v;
        var run = await GitAsync(dir, arguments, withIdentity, cancellationToken);
        return Failed(run, "make the commit") is { } failed ? new ProviderResult<string>(null, failed) : ProviderResult<string>.Success(run.Value!.Output.Trim());
    }

    static async Task<string?> RevParseAsync(string dir, string reference, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var run = await GitAsync(dir, ["rev-parse", "--verify", "-q", reference + "^{commit}"], env, cancellationToken);
        return run.Ok && run.Value!.Exit == 0 ? run.Value.Output.Trim() : null;
    }

    static async Task<bool> PresentAsync(string dir, string revision, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken) =>
        Failed(await GitAsync(dir, ["cat-file", "-e", revision + "^{commit}"], env, cancellationToken), "") is null;

    static async Task<bool> IsAncestorAsync(string dir, string ancestor, string revision, IReadOnlyDictionary<string, string> env, CancellationToken cancellationToken)
    {
        var run = await GitAsync(dir, ["merge-base", "--is-ancestor", ancestor, revision], env, cancellationToken);
        return run.Ok && run.Value!.Exit == 0;
    }

    static bool Moved(string text) =>
        text.Contains("stale info", StringComparison.Ordinal) || text.Contains("[rejected]", StringComparison.Ordinal)
        || text.Contains("fetch first", StringComparison.Ordinal) || text.Contains("cannot lock ref", StringComparison.Ordinal);

    /// <summary>
    /// Runs <paramref name="work"/> on the repository's cached bare clone, one operation at a time per cache, across
    /// instances (a static gate per path) and processes (an exclusive lock file beside the cache), because the default
    /// cache is shared by every worker on a host. Each operation gets its own scratch references under
    /// <c>refs/ff-cache/op/&lt;guid&gt;</c>, deleted afterwards.
    /// </summary>
    async Task<ProviderResult<T>> WithCacheAsync<T>(RepoRef repo, Func<string, IReadOnlyDictionary<string, string>, string, Task<ProviderResult<T>>> work, bool write,
        CancellationToken cancellationToken)
    {
        OperationTimeout.Value = CommandTimeout;
        var env = await EnvironmentAsync(repo, write, cancellationToken);
        if (!env.Ok)
            return new ProviderResult<T>(default, env.Error);
        var dir = Path.GetFullPath(CacheOf(repo));
        var gate = Gates.GetOrAdd(dir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (EnsureCache() is { } broken)
                return new ProviderResult<T>(default, broken);
            await using var held = await LockFileAsync(dir + ".lock", cancellationToken);
            // GIT_DIR names the bare clone explicitly, so git works where safe.bareRepository is "explicit".
            var withDir = new Dictionary<string, string>(env.Value!) { ["GIT_DIR"] = dir };
            if (!Directory.Exists(Path.Combine(dir, "objects")))
            {
                Directory.CreateDirectory(dir);
                if (Failed(await GitAsync(dir, ["init", "-q", "--bare"], withDir, cancellationToken), "create the cache") is { } init)
                    return new ProviderResult<T>(default, init);
            }

            var op = $"refs/ff-cache/op/{Guid.NewGuid():N}";
            try
            {
                return await work(dir, withDir, op);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException)
            {
                // The worker's disk or a pipe to git failed: infrastructure, never an exception out of the provider.
                return ProviderResult<T>.Failure("internal_error", $"the clone cache failed: {e.Message}");
            }
            finally
            {
                var scratch = await GitAsync(dir, ["for-each-ref", "--format=delete %(refname)", op + "/"], withDir, CancellationToken.None);
                if (scratch.Ok && scratch.Value!.Exit == 0 && scratch.Value.Output.Length > 0)
                    await GitAsync(dir, ["update-ref", "--stdin"], withDir, CancellationToken.None, scratch.Value.Output);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ProviderResult<T>.Failure("internal_error", $"the clone cache's lock failed: {e.Message}");
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>An exclusive lock on <paramref name="path"/>, held until disposed; another process holding it is waited for.</summary>
    static async Task<FileStream> LockFileAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            // Held by another process; a missing folder or bad path is not worth waiting for.
            catch (IOException e) when (e is not (DirectoryNotFoundException or PathTooLongException or FileNotFoundException))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    /// <summary>
    /// The environment that gives <c>git</c> the engine's credential for an HTTPS remote: an authorisation header in
    /// <c>GIT_CONFIG_*</c> variables, so the token is in no command line, URL or file. A local remote needs none.
    /// </summary>
    async Task<ProviderResult<IReadOnlyDictionary<string, string>>> EnvironmentAsync(RepoRef repo, bool write, CancellationToken cancellationToken)
    {
        var config = new List<(string Key, string Value)>(Isolation);
        if (!UrlOf(repo).StartsWith("https://", StringComparison.Ordinal) || broker() is not { } credentialBroker)
            return ProviderResult<IReadOnlyDictionary<string, string>>.Success(IsolatedEnvironment(config));
        var cacheKey = $"{repo.FullName}|{write}";
        if (!credentials.TryGetValue(cacheKey, out var credential) || credential.ExpiresAt < DateTimeOffset.UtcNow.AddMinutes(5))
        {
            var issued = await credentialBroker.IssueAsync(Actor.Engine, "github", write ? CredentialScope.Write(repo.FullName) : CredentialScope.Read(repo.FullName),
                TimeSpan.FromHours(1), cancellationToken);
            if (!issued.Ok)
                return new ProviderResult<IReadOnlyDictionary<string, string>>(null, issued.Error);
            credentials[cacheKey] = credential = issued.Value!;
        }

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + credential.Secret));
        config.Add(("http.extraHeader", "AUTHORIZATION: basic " + basic));
        return ProviderResult<IReadOnlyDictionary<string, string>>.Success(IsolatedEnvironment(config));
    }

    /// <summary>
    /// Review finding 10: the engine's git reads no system or global configuration and no configuration passed down by
    /// the worker's own environment, so no credential helper (which could offer a person's credential after a 401), URL
    /// rewrite or hooks path of the worker's user reaches it.
    /// </summary>
    static readonly (string Key, string Value)[] Isolation = [("credential.helper", ""), ("core.hooksPath", OperatingSystem.IsWindows() ? "NUL" : "/dev/null")];

    static Dictionary<string, string> IsolatedEnvironment(IReadOnlyList<(string Key, string Value)> config)
    {
        var env = new Dictionary<string, string>
        {
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
            ["GIT_CONFIG_PARAMETERS"] = "",
            ["GIT_CONFIG_COUNT"] = config.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        for (var i = 0; i < config.Count; i++)
        {
            env[$"GIT_CONFIG_KEY_{i}"] = config[i].Key;
            env[$"GIT_CONFIG_VALUE_{i}"] = config[i].Value;
        }

        return env;
    }

    async Task<ProviderResult<string>> InCopyAsync(WorkingCopyRef copy, string script, CancellationToken cancellationToken)
    {
        var run = await sandboxes().RunAsync(copy.Sandbox, new SandboxCommand(["sh", "-c", script], null, copy.Path), $"vcs/{Guid.NewGuid():N}", cancellationToken);
        if (!run.Ok)
            return new ProviderResult<string>(null, run.Error);
        return run.Value.Exit == 0
            ? ProviderResult<string>.Success(run.Value.Output)
            : ProviderResult<string>.Failure("validation_failed", $"git in the sandbox failed: {FirstLine(run.Value.Output)}");
    }

    async Task<ProviderError?> ShAsync(SandboxHandle sandbox, string script, string key, CancellationToken cancellationToken)
    {
        var run = await sandboxes().RunAsync(sandbox, new SandboxCommand(["sh", "-c", script]), key, cancellationToken);
        if (!run.Ok)
            return run.Error;
        return run.Value.Exit == 0 ? null : new ProviderError("validation_failed", $"git in the sandbox failed: {FirstLine(run.Value.Output)}");
    }

    public sealed record GitRun(int Exit, string Output, string Error);

    static async Task<ProviderResult<GitRun>> GitAsync(string directory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? env,
        CancellationToken cancellationToken, string? input = null)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = input is not null, UseShellExecute = false,
        };
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
            start.Environment[k] = v;
        var limit = OperationTimeout.Value ?? DefaultCommandTimeout;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        Process? process = null;
        try
        {
            process = Process.Start(start)!;
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return ProviderResult<GitRun>.Success(new GitRun(process.ExitCode, await output, await error));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Review R6: a hung git must not hold the cache's lock for ever.
            Kill(process);
            return ProviderResult<GitRun>.Failure("timeout", $"git {arguments.FirstOrDefault()} did not finish in {limit.TotalSeconds:F0} s");
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return ProviderResult<GitRun>.Failure("unreachable", "git cannot be run: " + e.Message);
        }
        catch (IOException e)
        {
            Kill(process);
            return ProviderResult<GitRun>.Failure("internal_error", "the pipe to git failed: " + e.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    static void Kill(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    /// <summary>
    /// How long one git command may run before it is killed and the operation fails with <c>timeout</c>
    /// (infrastructure), so its cache lock is released. Set it under the activity's timeout.
    /// </summary>
    public TimeSpan CommandTimeout { get; init; } = DefaultCommandTimeout;

    static readonly TimeSpan DefaultCommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>The running operation's <see cref="CommandTimeout"/>, for the static git helpers.</summary>
    static readonly AsyncLocal<TimeSpan?> OperationTimeout = new();

    /// <summary>Null when git ran and exited 0; otherwise the classified error. Git's message is kept to its first line, which never holds the header.</summary>
    static ProviderError? Failed(ProviderResult<GitRun> run, string what)
    {
        if (!run.Ok)
            return run.Error;
        if (run.Value!.Exit == 0)
            return null;
        var text = run.Value.Error + run.Value.Output;
        return new ProviderError(Classify(text, "validation_failed"), $"git could not {what}: {FirstLine(text)}");
    }

    /// <summary>
    /// docs/06 section 1.1 for git's own messages. Credentials and HTTP refusals are checked first, because git reports
    /// them as "unable to access", which alone means the remote could not be reached.
    /// </summary>
    internal static string Classify(string text, string otherwise)
    {
        if (text.Contains("Authentication failed", StringComparison.Ordinal) || text.Contains("error: 401", StringComparison.Ordinal))
            return "unauthenticated";
        if (text.Contains("error: 403", StringComparison.Ordinal))
            return "forbidden";
        if (text.Contains("error: 429", StringComparison.Ordinal))
            return "rate_limited";
        return Unreachable(text) ? "unreachable" : otherwise;
    }

    static bool Unreachable(string text) =>
        text.Contains("Could not resolve host", StringComparison.Ordinal) || text.Contains("Connection refused", StringComparison.Ordinal)
        || text.Contains("Operation timed out", StringComparison.Ordinal) || text.Contains("unable to access", StringComparison.Ordinal)
        || text.Contains("Connection reset", StringComparison.Ordinal) || text.Contains("early EOF", StringComparison.Ordinal)
        || text.Contains("RPC failed", StringComparison.Ordinal);

    static string FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";

    static string Q(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
}

/// <summary>The factories of this package, by type and kind, for a host to register (docs/06 section 4).</summary>
public static class GitProviders
{
    public static IReadOnlyList<ProviderFactory> Factories { get; } =
    [
        new("vcs", "git", ["remote", "cache"], c => new GitVcs(
            c.Setting("remote") ?? "https://github.com/{repo}.git",
            c.PathSetting("cache") ?? Path.Combine(Path.GetTempPath(), "feature-flow-clones"),
            () => c.Provider("sandbox") as ISandboxProvider ?? throw new InvalidOperationException("the Git provider needs a sandbox provider for working copies"),
            () => c.Provider("artifact_store") as IArtifactStoreProvider ?? throw new InvalidOperationException("the Git provider needs an artifact store"),
            () => c.Provider("credential_broker") as ICredentialBroker)),
    ];
}
