using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using FeatureFlow.Model;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Providers.GitHub;

/// <summary>
/// The GitHub Actions CI provider (docs/06 section 3.5). A run is dispatched on the repository's default branch with
/// the revision as the workflow's <c>commit_sha</c> input, so the workflow definition, and what it does with the
/// repository's secrets, always comes from the reviewed default branch, never from a file the agent could edit.
/// The dispatch returns the run's id (<c>return_run_details</c>), which the engine records (LOG 147); a trigger retried
/// after a lost answer dispatches again, and the rare duplicate run is accepted. Runs are polled, never awaited.
/// </summary>
public sealed class GitHubActionsCi(GitHubApi api, Func<IArtifactStoreProvider> store) : ICiProvider
{
    static readonly IReadOnlyDictionary<string, string> Permissions = GitHubApi.CiPermissions;
    static readonly IReadOnlyDictionary<string, string> ReadPermissions = new Dictionary<string, string> { ["contents"] = "read", ["actions"] = "read" };

    /// <summary>
    /// The one cap on downloads: an artifact that says it is larger is skipped, and an artifact or log download, or a
    /// result file decompressed from an artifact, that runs past it is refused. Result files are small, and the workflow
    /// is the repository's to change.
    /// </summary>
    public long MaxDownloadBytes { get; init; } = 100L * 1024 * 1024;

    public ProviderDescription Describe() => new("ci", "github_actions", "0.1.0", new CiCapabilities(true, true, true, false, false));

    public Task<ProviderResult<Unit>> CheckAsync(CancellationToken cancellationToken) => api.CheckAsync(cancellationToken);

    Task<ProviderResult<GitHubResponse>> Send(RepoRef repo, HttpMethod method, string path, object? body, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? permissions = null) =>
        api.SendAsync(repo, permissions ?? (method == HttpMethod.Get ? ReadPermissions : Permissions), method, $"/repos/{repo.FullName}{path}", body, cancellationToken);

    /// <summary>
    /// The workflow must declare a <c>commit_sha</c> input: it runs from the default branch and must check out the
    /// revision. The provider sets <c>commit_sha</c>; the job's other inputs pass as they are.
    /// </summary>
    public async Task<ProviderResult<CiRunRef>> TriggerAsync(RepoRef repo, CiJobRef job, string revision, CancellationToken cancellationToken)
    {
        if (!GitHubApi.IsRevision(revision))
            return ProviderResult<CiRunRef>.Failure("validation_failed", $"{revision} is not a commit SHA");
        var repository = await Send(repo, HttpMethod.Get, "", null, cancellationToken, ReadPermissions);
        if (!repository.Ok)
            return new ProviderResult<CiRunRef>(null, repository.Error);
        if (repository.Value!.Code != 200 || repository.Value.Json?["default_branch"]?.GetValue<string>() is not { } defaultBranch)
            return GitHubApi.Unexpected<CiRunRef>(repository.Value, "GET the repository");
        var declared = await DeclaredInputsAsync(repo, job.Workflow, defaultBranch, cancellationToken);
        if (!declared.Ok)
            return new ProviderResult<CiRunRef>(null, declared.Error);
        if (!declared.Value!.Contains("commit_sha"))
            return ProviderResult<CiRunRef>.Failure("validation_failed",
                $"{job.Workflow} on {defaultBranch} declares no commit_sha input, so it cannot build {revision}: workflows run from the default branch");
        var inputs = new Dictionary<string, string>(job.Inputs) { ["commit_sha"] = revision };

        var dispatched = await Send(repo, HttpMethod.Post, $"/actions/workflows/{GitHubApi.Escape(job.Workflow)}/dispatches",
            new Dictionary<string, object> { ["ref"] = defaultBranch, ["inputs"] = inputs, ["return_run_details"] = true }, cancellationToken);
        if (!dispatched.Ok)
            return new ProviderResult<CiRunRef>(null, dispatched.Error);
        // 200 with the run (return_run_details); the only documented success.
        if (dispatched.Value!.Code != 200 || dispatched.Value.Json?["workflow_run_id"]?.GetValue<long>() is not { } id)
            return GitHubApi.Unexpected<CiRunRef>(dispatched.Value, "POST the dispatch");
        return ProviderResult<CiRunRef>.Success(new CiRunRef(repo, id.ToString(System.Globalization.CultureInfo.InvariantCulture), revision,
            dispatched.Value.Json["html_url"]?.GetValue<string>()));
    }

    async Task<ProviderResult<IReadOnlySet<string>>> DeclaredInputsAsync(RepoRef repo, string workflow, string reference, CancellationToken cancellationToken)
    {
        var text = await GitHubCodeHost.ReadAtAsync(api, repo, ReadPermissions, ".github/workflows/" + workflow, reference, cancellationToken);
        if (!text.Ok)
            return new ProviderResult<IReadOnlySet<string>>(null, text.Error);
        if (text.Value is null)
            return ProviderResult<IReadOnlySet<string>>.Failure("validation_failed", $"{workflow} does not exist on {reference}");
        return ProviderResult<IReadOnlySet<string>>.Success(DispatchInputs(Encoding.UTF8.GetString(text.Value)));
    }

    static bool IsRunId(string id) => id.Length > 0 && id.All(char.IsAsciiDigit);

    async Task<ProviderResult<JsonNode>> RunAsync(CiRunRef run, CancellationToken cancellationToken)
    {
        if (!IsRunId(run.Id))
            return ProviderResult<JsonNode>.Failure("not_found", $"{run.Id} is not a run id");
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
        return ProviderResult<CiRunStatus>.Success(Status(raw.Value!));
    }

    static CiRunStatus Status(JsonNode run) => run["status"]?.GetValue<string>() switch
    {
        "completed" => new CiRunStatus(CheckStates.Completed, GitHubCodeHost.Conclusion(run["conclusion"]?.GetValue<string>())),
        "in_progress" => new CiRunStatus(CheckStates.InProgress, null),
        _ => new CiRunStatus(CheckStates.Queued, null),
    };

    /// <summary>
    /// The run's jobs, and its artifacts unpacked: each <c>.trx</c>, <c>.sarif</c> and JUnit XML file is copied into the
    /// artifact store unparsed. Expired artifacts are skipped.
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
        foreach (var artifact in artifacts.Value!.Where(a => a["expired"]?.GetValue<bool>() != true && (a["size_in_bytes"]?.GetValue<long>() ?? 0) <= MaxDownloadBytes))
        {
            var zip = await DownloadAsync(run.Repo, $"/actions/artifacts/{artifact["id"]!.GetValue<long>()}/zip", MaxDownloadBytes, cancellationToken);
            if (!zip.Ok)
                return new ProviderResult<CiRunResult>(null, zip.Error);
            await using var zipStream = zip.Value!;
            ZipArchive archive;
            try
            {
                archive = new ZipArchive(zipStream, ZipArchiveMode.Read);
            }
            catch (InvalidDataException)
            {
                return ProviderResult<CiRunResult>.Failure("validation_failed", $"artifact {artifact["name"]} is not a zip");
            }

            using var _ = archive;
            // Only entries named like result files are read, each under the same cap as it decompresses (its stated size can lie).
            foreach (var entry in archive.Entries.Where(e => e.Length > 0 && MayBeResult(e.FullName)))
            {
                using var reader = new MemoryStream();
                await using (var source = entry.Open())
                    if (!await GitHubApi.CopyCappedAsync(source, reader, MaxDownloadBytes, cancellationToken))
                        return ProviderResult<CiRunResult>.Failure("validation_failed", $"{entry.FullName} in artifact {artifact["name"]} is larger than {MaxDownloadBytes} bytes");
                if (FormatOf(entry.FullName, reader.ToArray()) is not { } format)
                    continue;
                reader.Position = 0;
                var put = await store().PutAsync(reader, cancellationToken);
                if (!put.Ok)
                    return new ProviderResult<CiRunResult>(null, put.Error);
                files.Add(new CiResultFile($"{artifact["name"]?.GetValue<string>()}/{entry.FullName}", format, put.Value!));
            }
        }

        return ProviderResult<CiRunResult>.Success(new CiRunResult(status.Conclusion!,
            jobs.Value!.Select(j => new CiJobResult(j["name"]!.GetValue<string>(), GitHubCodeHost.Conclusion(j["conclusion"]?.GetValue<string>()))).ToList(), files));
    }

    static bool MayBeResult(string name) =>
        name.EndsWith(".trx", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".sarif", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".sarif.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);

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
        if (!IsRunId(run.Id))
            return ProviderResult<ArtifactRef>.Failure("not_found", $"{run.Id} is not a run id");
        var zip = await DownloadAsync(run.Repo, $"/actions/runs/{GitHubApi.Escape(run.Id)}/logs", MaxDownloadBytes, cancellationToken);
        if (!zip.Ok)
            return new ProviderResult<ArtifactRef>(null, zip.Error);
        await using var logs = zip.Value!;
        return await store().PutAsync(logs, cancellationToken);
    }

    async Task<ProviderResult<Stream>> DownloadAsync(RepoRef repo, string path, long maxBytes, CancellationToken cancellationToken)
    {
        var response = await Send(repo, HttpMethod.Get, path, null, cancellationToken, ReadPermissions);
        if (!response.Ok)
            return new ProviderResult<Stream>(null, response.Error);
        return response.Value!.Code == 302 && response.Value.Location is { } location
            ? await api.DownloadAsync(location, maxBytes, cancellationToken)
            : GitHubApi.Unexpected<Stream>(response.Value, $"GET {path}");
    }

    /// <summary>CI is configured when the revision has a workflow file (<c>.yml</c> or <c>.yaml</c>) in <c>.github/workflows</c>.</summary>
    public async Task<ProviderResult<bool>> HasCiAsync(RepoRef repo, string revision, CancellationToken cancellationToken)
    {
        if (!GitHubApi.IsRevision(revision))
            return ProviderResult<bool>.Failure("validation_failed", $"{revision} is not a commit SHA");
        var listing = await Send(repo, HttpMethod.Get, $"/contents/.github/workflows?ref={GitHubApi.Escape(revision)}", null, cancellationToken, ReadPermissions);
        if (!listing.Ok)
            return listing.Error!.Reason == "not_found" ? ProviderResult<bool>.Success(false) : new ProviderResult<bool>(default, listing.Error);
        if (listing.Value!.Code != 200)
            return GitHubApi.Unexpected<bool>(listing.Value, "GET the workflow folder");
        return ProviderResult<bool>.Success((listing.Value.Json as JsonArray ?? []).Any(f => f?["type"]?.GetValue<string>() == "file"
            && f["name"]?.GetValue<string>() is { } n && (n.EndsWith(".yml", StringComparison.Ordinal) || n.EndsWith(".yaml", StringComparison.Ordinal))));
    }

    /// <summary>The inputs a workflow's <c>workflow_dispatch</c> trigger declares; empty when it declares none or does not parse.</summary>
    public static IReadOnlySet<string> DispatchInputs(string text)
    {
        var diagnostics = new DiagnosticBag();
        if (YamlText.Parse(text, "workflow", diagnostics) is not YMap root || diagnostics.HasErrors)
            return new HashSet<string>();
        var inputs = (root["on"] as YMap)?["workflow_dispatch"] is YMap dispatch ? dispatch["inputs"] as YMap : null;
        return inputs?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
    }
}
