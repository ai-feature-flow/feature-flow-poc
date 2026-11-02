using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// The GitHub Actions CI provider (docs/06 section 3.5). A run is dispatched on a tag <c>ff-ci/&lt;hash of the key&gt;</c>
/// at the revision, because a dispatch needs a reference, not a SHA: the run then builds that revision with the
/// workflow file as it is there, and its <c>head_branch</c> is the tag, so a retried trigger finds it. Creating the
/// tag is the claim: a retry that finds the tag looks for the run before dispatching. The tag is deleted once the
/// run is seen completed, by a status poll or a read of its results. Runs are polled, never awaited. (Without a tag
/// a retry could not find its run reliably: a run does not expose its inputs, and the run list lags a dispatch by
/// seconds; LOG 112.)
/// </summary>
public sealed class GitHubActionsCi(GitHubApi api, Func<IArtifactStoreProvider> store) : ICiProvider
{
    static readonly IReadOnlyDictionary<string, string> Permissions = GitHubApi.CiPermissions;
    static readonly IReadOnlyDictionary<string, string> ReadPermissions = new Dictionary<string, string> { ["contents"] = "read", ["actions"] = "read" };

    /// <summary>How long a trigger looks for a run GitHub has not indexed yet.</summary>
    public TimeSpan DiscoveryWindow { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan DiscoveryInterval { get; init; } = TimeSpan.FromSeconds(2);

    public const string TagPrefix = "ff-ci/";

    public ProviderDescription Describe() => new("ci", "github_actions", "0.1.0", new CiCapabilities(true, true, true, false, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => api.CheckAsync(cancellationToken);

    public static string TagFor(string key) => TagPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24];

    Task<ProviderResult<GitHubResponse>> Send(RepoRef repo, HttpMethod method, string path, object? body, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? permissions = null) =>
        api.SendAsync(repo, permissions ?? Permissions, method, $"/repos/{repo.FullName}{path}", body, cancellationToken);

    /// <summary>
    /// Inputs the workflow declares as <c>commit_sha</c> and <c>idempotency_key</c> (the fixture's convention, from
    /// Spike 2) are filled with the revision and the key when the job does not set them.
    /// </summary>
    public async Task<ProviderResult<CiRunRef>> TriggerAsync(RepoRef repo, CiJobRef job, string revision, string key, CancellationToken cancellationToken)
    {
        var tag = TagFor(key);
        var workflow = GitHubApi.Escape(job.Workflow);
        var created = await Send(repo, HttpMethod.Post, "/git/refs", new { @ref = "refs/tags/" + tag, sha = revision }, cancellationToken);
        if (!created.Ok && created.Error!.Reason != "validation_failed")
            return new ProviderResult<CiRunRef>(null, created.Error);
        if (!created.Ok)
        {
            // 422: the tag exists (a retry), or GitHub does not know the revision.
            var existing = await Send(repo, HttpMethod.Get, $"/git/ref/tags/{GitHubApi.EscapePath(tag)}", null, cancellationToken);
            if (!existing.Ok)
                return existing.Error!.Reason == "not_found"
                    ? ProviderResult<CiRunRef>.Failure("validation_failed", $"GitHub refused a reference at {revision}: {created.Error!.Message}")
                    : new ProviderResult<CiRunRef>(null, existing.Error);
            if (existing.Value!.Json?["object"]?["sha"]?.GetValue<string>() != revision)
                return ProviderResult<CiRunRef>.Failure("validation_failed", $"key {key} already triggered a run at another revision");
            if (await DiscoverAsync(repo, workflow, tag, revision, DiscoveryWindow, cancellationToken) is { } found)
                return found;
        }
        else if (created.Value!.Code != 201)
            return GitHubApi.Unexpected<CiRunRef>(created.Value, "POST the trigger tag");
        else if (await DiscoverAsync(repo, workflow, tag, revision, TimeSpan.Zero, cancellationToken) is { } earlier)
            // The tag was deleted after an earlier run's results were read: that run is this key's.
            return earlier;

        var inputs = new Dictionary<string, string>(job.Inputs);
        var declared = await DeclaredInputsAsync(repo, job.Workflow, revision, cancellationToken);
        if (!declared.Ok)
            return new ProviderResult<CiRunRef>(null, declared.Error);
        if (declared.Value!.Contains("commit_sha"))
            inputs.TryAdd("commit_sha", revision);
        if (declared.Value.Contains("idempotency_key"))
            inputs.TryAdd("idempotency_key", key);

        var dispatched = await Send(repo, HttpMethod.Post, $"/actions/workflows/{workflow}/dispatches",
            new Dictionary<string, object> { ["ref"] = tag, ["inputs"] = inputs, ["return_run_details"] = true }, cancellationToken);
        if (!dispatched.Ok)
            return new ProviderResult<CiRunRef>(null, dispatched.Error);
        // 200 with the run's id (return_run_details, February 2026); 204 without it from an older API, found by the tag.
        if (dispatched.Value!.Code == 200 && dispatched.Value.Json?["workflow_run_id"]?.GetValue<long>() is { } id)
            return ProviderResult<CiRunRef>.Success(new CiRunRef(repo, id.ToString(System.Globalization.CultureInfo.InvariantCulture), revision,
                dispatched.Value.Json["html_url"]?.GetValue<string>()));
        if (dispatched.Value.Code is not (200 or 204))
            return GitHubApi.Unexpected<CiRunRef>(dispatched.Value, "POST the dispatch");
        return await DiscoverAsync(repo, workflow, tag, revision, DiscoveryWindow, cancellationToken)
               ?? ProviderResult<CiRunRef>.Failure("timeout", $"the run dispatched on {tag} did not appear in {DiscoveryWindow.TotalSeconds:F0} s");
    }

    async Task<ProviderResult<CiRunRef>?> DiscoverAsync(RepoRef repo, string workflow, string tag, string revision, TimeSpan window, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + window;
        while (true)
        {
            var runs = await Send(repo, HttpMethod.Get, $"/actions/workflows/{workflow}/runs?branch={GitHubApi.Escape(tag)}&event=workflow_dispatch&per_page=10", null,
                cancellationToken);
            if (!runs.Ok)
                return new ProviderResult<CiRunRef>(null, runs.Error);
            if (runs.Value!.Code != 200)
                return GitHubApi.Unexpected<CiRunRef>(runs.Value, "GET the workflow's runs");
            var run = (runs.Value.Json?["workflow_runs"] as JsonArray ?? []).Where(r => r?["head_sha"]?.GetValue<string>() == revision)
                .OrderBy(r => r!["id"]!.GetValue<long>()).FirstOrDefault();
            if (run is not null)
                return ProviderResult<CiRunRef>.Success(new CiRunRef(repo, run["id"]!.GetValue<long>().ToString(System.Globalization.CultureInfo.InvariantCulture), revision,
                    run["html_url"]?.GetValue<string>()));
            if (DateTimeOffset.UtcNow >= deadline)
                return null;
            await Task.Delay(DiscoveryInterval, cancellationToken);
        }
    }

    async Task<ProviderResult<IReadOnlySet<string>>> DeclaredInputsAsync(RepoRef repo, string workflow, string revision, CancellationToken cancellationToken)
    {
        var text = await GitHubCodeHost.ReadAtAsync(api, repo, Permissions, ".github/workflows/" + workflow, revision, cancellationToken);
        if (!text.Ok)
            return new ProviderResult<IReadOnlySet<string>>(null, text.Error);
        if (text.Value is null)
            return ProviderResult<IReadOnlySet<string>>.Failure("validation_failed", $"{workflow} does not exist at {revision}");
        return ProviderResult<IReadOnlySet<string>>.Success(Workflow.Parse(Encoding.UTF8.GetString(text.Value))?.DispatchInputs ?? new HashSet<string>());
    }

    async Task<ProviderResult<JsonNode>> RunAsync(CiRunRef run, CancellationToken cancellationToken)
    {
        var response = await Send(run.Repo, HttpMethod.Get, $"/actions/runs/{GitHubApi.Escape(run.Id)}", null, cancellationToken, ReadPermissions);
        if (!response.Ok)
            return new ProviderResult<JsonNode>(null, response.Error);
        return response.Value!.Code == 200 ? ProviderResult<JsonNode>.Success(response.Value.Json!) : GitHubApi.Unexpected<JsonNode>(response.Value, "GET the run");
    }

    /// <summary>GitHub's run states: <c>queued</c>, <c>waiting</c>, <c>requested</c> and <c>pending</c> are queued.</summary>
    public async Task<ProviderResult<CiRunStatus>> GetRunStatusAsync(CiRunRef run, CancellationToken cancellationToken)
    {
        var raw = await RunAsync(run, cancellationToken);
        if (!raw.Ok)
            return new ProviderResult<CiRunStatus>(null, raw.Error);
        var status = Status(raw.Value!);
        if (status.State == CheckStates.Completed && await RemoveTagAsync(run, raw.Value!, cancellationToken) is { } failed)
            return new ProviderResult<CiRunStatus>(null, failed);
        return ProviderResult<CiRunStatus>.Success(status);
    }

    /// <summary>Deletes the trigger tag of a completed run (204); a tag already gone (404, or 422 from GitHub) is fine.</summary>
    async Task<ProviderError?> RemoveTagAsync(CiRunRef run, JsonNode raw, CancellationToken cancellationToken)
    {
        if (raw["head_branch"]?.GetValue<string>() is not { } branch || !branch.StartsWith(TagPrefix, StringComparison.Ordinal))
            return null;
        var deleted = await Send(run.Repo, HttpMethod.Delete, $"/git/refs/tags/{GitHubApi.EscapePath(branch)}", null, cancellationToken);
        return !deleted.Ok && deleted.Error!.Reason is not ("not_found" or "validation_failed") ? deleted.Error : null;
    }

    static CiRunStatus Status(JsonNode run) => run["status"]?.GetValue<string>() switch
    {
        "completed" => new CiRunStatus(CheckStates.Completed, GitHubCodeHost.Conclusion(run["conclusion"]?.GetValue<string>())),
        "in_progress" => new CiRunStatus(CheckStates.InProgress, null),
        _ => new CiRunStatus(CheckStates.Queued, null),
    };

    /// <summary>
    /// The run's jobs, and its artifacts unpacked: each <c>.trx</c>, <c>.sarif</c> and JUnit XML file is copied into the
    /// artifact store unparsed. Expired artifacts are skipped. Reading the results of a run that a tag started deletes
    /// the tag (204; already gone is fine).
    /// </summary>
    public async Task<ProviderResult<CiRunResult>> ReadResultsAsync(CiRunRef run, CancellationToken cancellationToken)
    {
        var raw = await RunAsync(run, cancellationToken);
        if (!raw.Ok)
            return new ProviderResult<CiRunResult>(null, raw.Error);
        var status = Status(raw.Value!);
        if (status.State != CheckStates.Completed)
            return ProviderResult<CiRunResult>.Failure("validation_failed", $"run {run.Id} has not completed");

        var jobs = await api.ListAsync(run.Repo, ReadPermissions, $"/repos/{run.Repo.FullName}/actions/runs/{run.Id}/jobs", j => j["jobs"] as JsonArray, cancellationToken);
        if (!jobs.Ok)
            return new ProviderResult<CiRunResult>(null, jobs.Error);
        var artifacts = await api.ListAsync(run.Repo, ReadPermissions, $"/repos/{run.Repo.FullName}/actions/runs/{run.Id}/artifacts", j => j["artifacts"] as JsonArray,
            cancellationToken);
        if (!artifacts.Ok)
            return new ProviderResult<CiRunResult>(null, artifacts.Error);

        var files = new List<CiResultFile>();
        foreach (var artifact in artifacts.Value!.Where(a => a["expired"]?.GetValue<bool>() != true))
        {
            var zip = await DownloadAsync(run.Repo, $"/actions/artifacts/{artifact["id"]!.GetValue<long>()}/zip", cancellationToken);
            if (!zip.Ok)
                return new ProviderResult<CiRunResult>(null, zip.Error);
            using var archive = new ZipArchive(new MemoryStream(zip.Value!), ZipArchiveMode.Read);
            foreach (var entry in archive.Entries.Where(e => e.Length > 0))
            {
                using var reader = new MemoryStream();
                await using (var source = entry.Open())
                    await source.CopyToAsync(reader, cancellationToken);
                if (FormatOf(entry.FullName, reader.ToArray()) is not { } format)
                    continue;
                var put = await store().PutAsync(new MemoryStream(reader.ToArray()), cancellationToken);
                if (!put.Ok)
                    return new ProviderResult<CiRunResult>(null, put.Error);
                files.Add(new CiResultFile($"{artifact["name"]?.GetValue<string>()}/{entry.FullName}", format, put.Value!));
            }
        }

        if (await RemoveTagAsync(run, raw.Value!, cancellationToken) is { } failed)
            return new ProviderResult<CiRunResult>(null, failed);

        return ProviderResult<CiRunResult>.Success(new CiRunResult(status.Conclusion!,
            jobs.Value!.Select(j => new CiJobResult(j["name"]!.GetValue<string>(), GitHubCodeHost.Conclusion(j["conclusion"]?.GetValue<string>()))).ToList(), files));
    }

    /// <summary>A result file's format, from its name and, for XML, its root element; null for anything else.</summary>
    public static string? FormatOf(string name, byte[] content)
    {
        if (name.EndsWith(".trx", StringComparison.OrdinalIgnoreCase))
            return ResultFormats.Trx;
        if (name.EndsWith(".sarif", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sarif.json", StringComparison.OrdinalIgnoreCase))
            return ResultFormats.Sarif;
        if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            return null;
        var head = Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, 2048));
        return head.Contains("<testsuites", StringComparison.Ordinal) || head.Contains("<testsuite", StringComparison.Ordinal) ? ResultFormats.Junit : null;
    }

    /// <summary>The run's logs: GitHub answers 302 to a short-lived download of a zip, which is copied into the artifact store as it is.</summary>
    public async Task<ProviderResult<ArtifactRef>> ReadLogsAsync(CiRunRef run, CancellationToken cancellationToken)
    {
        var zip = await DownloadAsync(run.Repo, $"/actions/runs/{GitHubApi.Escape(run.Id)}/logs", cancellationToken);
        return zip.Ok ? await store().PutAsync(new MemoryStream(zip.Value!), cancellationToken) : new ProviderResult<ArtifactRef>(null, zip.Error);
    }

    async Task<ProviderResult<byte[]>> DownloadAsync(RepoRef repo, string path, CancellationToken cancellationToken)
    {
        var response = await Send(repo, HttpMethod.Get, path, null, cancellationToken, ReadPermissions);
        if (!response.Ok)
            return new ProviderResult<byte[]>(null, response.Error);
        return response.Value!.Code == 302 && response.Value.Location is { } location
            ? await api.DownloadAsync(location, cancellationToken)
            : GitHubApi.Unexpected<byte[]>(response.Value, $"GET {path}");
    }

    /// <summary>
    /// Read from the workflow files at the revision, as FF's <c>awaitChecks</c> did. A <c>push</c> trigger is evaluated
    /// for the branches whose head the revision is, and, given a pull request, for its head branch; <c>pull_request</c>
    /// and <c>pull_request_target</c> for the pull request's base. A matching trigger with a path filter, a type
    /// filter without the default types, or a file that does not parse, may run. Triggers not set off by a head
    /// (<c>workflow_dispatch</c>, <c>schedule</c>, ...) are ignored.
    /// </summary>
    public async Task<ProviderResult<ExpectedRuns>> GetExpectedRunsAsync(RepoRef repo, string revision, PullRequestRef? pullRequest, CancellationToken cancellationToken)
    {
        var listing = await Send(repo, HttpMethod.Get, $"/contents/.github/workflows?ref={revision}", null, cancellationToken, ReadPermissions);
        if (!listing.Ok)
            return listing.Error!.Reason == "not_found" ? ProviderResult<ExpectedRuns>.Success(ExpectedRuns.None) : new ProviderResult<ExpectedRuns>(null, listing.Error);
        if (listing.Value!.Code != 200)
            return GitHubApi.Unexpected<ExpectedRuns>(listing.Value, "GET the workflow folder");
        var names = (listing.Value.Json as JsonArray ?? []).Where(f => f?["type"]?.GetValue<string>() == "file")
            .Select(f => f!["name"]!.GetValue<string>()).Where(n => n.EndsWith(".yml", StringComparison.Ordinal) || n.EndsWith(".yaml", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToList();
        if (names.Count == 0)
            return ProviderResult<ExpectedRuns>.Success(ExpectedRuns.None);

        var pushBranches = new List<string>();
        string? pullBase = null;
        if (pullRequest is not null)
        {
            var pr = await Send(repo, HttpMethod.Get, $"/pulls/{pullRequest.Number}", null, cancellationToken, ReadPermissions);
            if (!pr.Ok)
                return new ProviderResult<ExpectedRuns>(null, pr.Error);
            if (pr.Value!.Code != 200)
                return GitHubApi.Unexpected<ExpectedRuns>(pr.Value, "GET the pull request");
            pullBase = pr.Value.Json!["base"]!["ref"]!.GetValue<string>();
            pushBranches.Add(pr.Value.Json["head"]!["ref"]!.GetValue<string>());
        }
        else
        {
            var heads = await Send(repo, HttpMethod.Get, $"/commits/{revision}/branches-where-head", null, cancellationToken, ReadPermissions);
            if (!heads.Ok)
                return new ProviderResult<ExpectedRuns>(null, heads.Error);
            if (heads.Value!.Code != 200)
                return GitHubApi.Unexpected<ExpectedRuns>(heads.Value, "GET the branches at the revision");
            pushBranches.AddRange((heads.Value.Json as JsonArray ?? []).Select(b => b!["name"]!.GetValue<string>()));
        }

        var runs = new List<string>();
        var mayRun = new List<string>();
        foreach (var name in names)
        {
            var text = await GitHubCodeHost.ReadAtAsync(api, repo, ReadPermissions, ".github/workflows/" + name, revision, cancellationToken);
            if (!text.Ok)
                return new ProviderResult<ExpectedRuns>(null, text.Error);
            var verdict = Workflow.Parse(Encoding.UTF8.GetString(text.Value ?? [])) is { } workflow
                ? workflow.Expect(pushBranches, pullBase)
                : Expectation.MayRun;
            if (verdict == Expectation.Runs)
                runs.Add(name);
            else if (verdict == Expectation.MayRun)
                mayRun.Add(name);
        }

        return ProviderResult<ExpectedRuns>.Success(new ExpectedRuns(runs, mayRun));
    }

    public enum Expectation
    {
        None,
        MayRun,
        Runs,
    }

    /// <summary>The parts of a workflow file the provider reads: its triggers and their filters, and its dispatch inputs.</summary>
    public sealed class Workflow(IReadOnlyDictionary<string, YMap?> triggers, IReadOnlySet<string> dispatchInputs)
    {
        public IReadOnlySet<string> DispatchInputs { get; } = dispatchInputs;

        /// <summary>The workflow, or null when it does not parse or has no <c>on</c>.</summary>
        public static Workflow? Parse(string text)
        {
            var diagnostics = new DiagnosticBag();
            if (YamlText.Parse(text, "workflow", diagnostics) is not YMap root || diagnostics.HasErrors || root["on"] is not { } on)
                return null;
            var triggers = new Dictionary<string, YMap?>(StringComparer.Ordinal);
            switch (on)
            {
                case YScalar s:
                    triggers[s.Text] = null;
                    break;
                case YSeq seq:
                    foreach (var item in seq.Items.OfType<YScalar>())
                        triggers[item.Text] = null;
                    break;
                case YMap map:
                    foreach (var (key, value) in map.Entries)
                        triggers[key.Text] = value as YMap;
                    break;
                default:
                    return null;
            }

            var inputs = triggers.GetValueOrDefault("workflow_dispatch")?["inputs"] as YMap;
            return new Workflow(triggers, inputs?.Keys.ToHashSet(StringComparer.Ordinal) ?? []);
        }

        public Expectation Expect(IReadOnlyList<string> pushBranches, string? pullBase)
        {
            var verdicts = new List<Expectation>();
            if (triggers.TryGetValue("push", out var push))
                verdicts.AddRange(pushBranches.Select(b => Evaluate(push, b, pull: false)));
            if (pullBase is not null)
                foreach (var e in new[] { "pull_request", "pull_request_target" })
                    if (triggers.TryGetValue(e, out var pull))
                        verdicts.Add(Evaluate(pull, pullBase, pull: true));
            return verdicts.Count == 0 ? Expectation.None : verdicts.Max();
        }

        static Expectation Evaluate(YMap? filter, string branch, bool pull)
        {
            if (filter is null)
                return Expectation.Runs;
            var branches = List(filter, "branches");
            var ignore = List(filter, "branches-ignore");
            // Only tag filters: a branch never sets it off (GitHub's rule for push).
            if (!pull && branches is null && ignore is null && (filter.Has("tags") || filter.Has("tags-ignore")))
                return Expectation.None;
            if (branches is not null && !Matches(branches, branch))
                return Expectation.None;
            if (ignore is not null && ignore.Any(p => Glob(p).IsMatch(branch)))
                return Expectation.None;
            if (filter.Has("paths") || filter.Has("paths-ignore"))
                return Expectation.MayRun;
            if (pull && List(filter, "types") is { } types && !types.Intersect(["opened", "synchronize", "reopened"]).Any())
                return Expectation.MayRun;
            return Expectation.Runs;
        }

        static List<string>? List(YMap map, string key) => map[key] switch
        {
            YSeq seq => seq.Items.Select(i => i.ScalarText() ?? "").ToList(),
            YScalar { IsNull: false } s => [s.Text],
            _ => null,
        };

        /// <summary>GitHub's filter patterns in order: a later <c>!pattern</c> excludes what an earlier one included.</summary>
        static bool Matches(IReadOnlyList<string> patterns, string branch)
        {
            var matched = false;
            foreach (var p in patterns)
                if (p.StartsWith('!'))
                    matched &= !Glob(p[1..]).IsMatch(branch);
                else if (Glob(p).IsMatch(branch))
                    matched = true;
            return matched;
        }

        /// <summary><c>*</c> is anything but <c>/</c>, <c>**</c> is anything; <c>?</c>, <c>+</c> and <c>[...]</c> are as in a regular expression.</summary>
        static Regex Glob(string pattern)
        {
            var regex = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                var c = pattern[i];
                if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
                {
                    regex.Append(".*");
                    i++;
                }
                else if (c == '*')
                    regex.Append("[^/]*");
                else if (c is '?' or '+' or '[' or ']')
                    regex.Append(c);
                else
                    regex.Append(Regex.Escape(c.ToString()));
            }

            return new Regex(regex.Append('$').ToString(), RegexOptions.CultureInvariant);
        }
    }
}
