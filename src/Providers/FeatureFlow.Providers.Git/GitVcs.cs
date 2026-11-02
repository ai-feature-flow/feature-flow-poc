using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.Git;

/// <summary>
/// The Git VCS provider (docs/06 section 3.3), as far as M4 needs it. The remote is reached from a cache of bare
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
    readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new();

    public ProviderDescription Describe() => new("vcs", "git", "0.1.0", new VcsCapabilities("git", true, false, false));

    public async Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(cache);
        var version = await GitAsync(cache, ["--version"], null, cancellationToken);
        return version.Ok && version.Value!.Exit == 0 ? ProviderResult<Unit>.Success(default) : ProviderResult<Unit>.Failure("unreachable", "git cannot be run on the worker");
    }

    string UrlOf(RepoRef repo) => remoteTemplate.Replace("{repo}", repo.FullName, StringComparison.Ordinal);

    string CacheOf(RepoRef repo) => Path.Combine(cache, repo.FullName.Replace('/', '_') + ".git");

    public async Task<ProviderResult<string?>> ResolveAsync(RepoRef repo, string reference, CancellationToken cancellationToken)
    {
        var env = await EnvironmentAsync(repo, write: false, cancellationToken);
        if (!env.Ok)
            return new ProviderResult<string?>(null, env.Error);
        var run = await GitAsync(cache, ["ls-remote", UrlOf(repo), reference], env.Value, cancellationToken);
        if (Failed(run, "read the remote") is { } failed)
            return new ProviderResult<string?>(null, failed);
        var line = run.Value!.Output.Split('\n').FirstOrDefault(l => l.EndsWith("\t" + reference, StringComparison.Ordinal));
        return ProviderResult<string?>.Success(line?.Split('\t')[0]);
    }

    public async Task<ProviderResult<Unit>> CheckOutAsync(RepoRef repo, string sourceReference, string revision, string branch, WorkingCopyRef target,
        CancellationToken cancellationToken)
    {
        var bundle = await WithCacheAsync(repo, async (dir, env) =>
        {
            if (Failed(await GitAsync(dir, ["fetch", "--no-tags", UrlOf(repo), $"+{sourceReference}:refs/ff-cache/checkout"], env, cancellationToken), "fetch") is { } fetch)
                return new ProviderResult<ArtifactRef>(null, fetch);
            var file = Path.Combine(dir, $"checkout-{Guid.NewGuid():N}.bundle");
            try
            {
                if (Failed(await GitAsync(dir, ["update-ref", "refs/ff-cache/bundle", revision], env, cancellationToken), "mark the revision") is { } mark)
                    return new ProviderResult<ArtifactRef>(null, mark);
                if (Failed(await GitAsync(dir, ["bundle", "create", file, "refs/ff-cache/bundle"], env, cancellationToken), "bundle the checkout") is { } create)
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
        var script = $"set -e; git init -q {Q(path)}; cd {Q(path)}; git fetch -q {Q(inSandbox)} refs/ff-cache/bundle; " +
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
        WithCacheAsync(repo, async (dir, env) =>
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
                    if (Failed(await GitAsync(dir, ["fetch", "--no-tags", file, "+HEAD:refs/ff-cache/pushed"], env, cancellationToken), "unbundle the commits") is { } unbundle)
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
                if (text.Contains("stale info", StringComparison.Ordinal) || text.Contains("[rejected]", StringComparison.Ordinal) || text.Contains("fetch first", StringComparison.Ordinal))
                    return ProviderResult<PushResult>.Failure("base_moved", $"{reference} is not at {expectedOldRevision ?? "nothing"}");
                return ProviderResult<PushResult>.Failure(Unreachable(text) ? "unreachable" : "rejected", $"the push to {reference} failed: {FirstLine(text)}");
            }

            return ProviderResult<PushResult>.Success(new PushResult(reference, expectedOldRevision, revision));
        }, write: true, cancellationToken);

    /// <summary>Runs <paramref name="work"/> on the repository's cached bare clone, one operation at a time per repository.</summary>
    async Task<ProviderResult<T>> WithCacheAsync<T>(RepoRef repo, Func<string, IReadOnlyDictionary<string, string>, Task<ProviderResult<T>>> work, bool write,
        CancellationToken cancellationToken)
    {
        var env = await EnvironmentAsync(repo, write, cancellationToken);
        if (!env.Ok)
            return new ProviderResult<T>(default, env.Error);
        var dir = CacheOf(repo);
        var gate = locks.GetOrAdd(dir, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                if (Failed(await GitAsync(dir, ["init", "-q", "--bare"], env.Value, cancellationToken), "create the cache") is { } init)
                    return new ProviderResult<T>(default, init);
            }

            return await work(dir, env.Value!);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The environment that gives <c>git</c> the engine's credential for an HTTPS remote: an authorisation header in
    /// <c>GIT_CONFIG_*</c> variables, so the token is in no command line, URL or file. A local remote needs none.
    /// </summary>
    async Task<ProviderResult<IReadOnlyDictionary<string, string>>> EnvironmentAsync(RepoRef repo, bool write, CancellationToken cancellationToken)
    {
        var env = new Dictionary<string, string> { ["GIT_TERMINAL_PROMPT"] = "0" };
        if (!UrlOf(repo).StartsWith("https://", StringComparison.Ordinal) || broker() is not { } credentialBroker)
            return ProviderResult<IReadOnlyDictionary<string, string>>.Success(env);
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
        env["GIT_CONFIG_COUNT"] = "1";
        env["GIT_CONFIG_KEY_0"] = "http.extraHeader";
        env["GIT_CONFIG_VALUE_0"] = "AUTHORIZATION: basic " + basic;
        return ProviderResult<IReadOnlyDictionary<string, string>>.Success(env);
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
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in arguments)
            start.ArgumentList.Add(a);
        foreach (var (k, v) in env ?? new Dictionary<string, string>())
            start.Environment[k] = v;
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return ProviderResult<GitRun>.Success(new GitRun(process.ExitCode, await output, await error));
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            return ProviderResult<GitRun>.Failure("unreachable", "git cannot be run: " + e.Message);
        }
    }

    /// <summary>Null when git ran and exited 0; otherwise the classified error. Git's message is kept to its first line, which never holds the header.</summary>
    static ProviderError? Failed(ProviderResult<GitRun> run, string what)
    {
        if (!run.Ok)
            return run.Error;
        if (run.Value!.Exit == 0)
            return null;
        var text = run.Value.Error + run.Value.Output;
        return new ProviderError(Unreachable(text) ? "unreachable" : "validation_failed", $"git could not {what}: {FirstLine(text)}");
    }

    static bool Unreachable(string text) =>
        text.Contains("Could not resolve host", StringComparison.Ordinal) || text.Contains("Connection refused", StringComparison.Ordinal)
        || text.Contains("Operation timed out", StringComparison.Ordinal) || text.Contains("unable to access", StringComparison.Ordinal);

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
