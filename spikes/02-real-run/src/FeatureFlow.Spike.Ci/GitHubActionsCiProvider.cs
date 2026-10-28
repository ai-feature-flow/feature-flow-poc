using System.Diagnostics;
using System.Globalization;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.Ci;

/// <summary>
/// The GitHub Actions CI provider. docs/04 section 5, docs/05 section 5.
///
/// The rules that shape it:
///
/// - **<see cref="TriggerAsync"/> does not wait.** It dispatches and returns a
///   reference. <c>WaitForRunAsync</c> was deliberately removed (review 05 P1);
///   waiting is an engine mechanism.
/// - **<see cref="GetRunStatusAsync"/> is non-blocking** and handles checks that
///   register late: a run exists before its jobs do, so "no jobs yet" is
///   <see cref="CiRunState.Queued"/> with
///   <see cref="CiRunStatus.ChecksRegistered"/> false, not a failure.
/// - **Queue builds are first-class.** <c>merge_group</c> runs are discovered and
///   reported like any other, because the merge queue path depends on it.
/// - **A failing check is <see cref="ErrorClass.Work"/>, not infrastructure**
///   (docs/06 7.2). A test failure is a real outcome, not a transport problem.
/// - **Credentials come from the broker only** (docs/04 section 1). This class
///   never reads a token from configuration or from the environment.
/// </summary>
public sealed class GitHubActionsCiProvider : ICiProvider
{
    /// <summary>The GitHub Actions <c>merge_group</c> event. docs/04 section 5.</summary>
    public const string MergeGroupEvent = GitHubActionsEvent.MergeGroup;

    /// <summary>docs/05 section 5: TriggerAsync 30s, metadata reads 15s, logs and artifacts 5 min.</summary>
    public static readonly TimeSpan TriggerTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan ResultsTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan LogsTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ArtifactsTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long after a dispatch the provider will look for the run it created.
    /// GitHub indexes a dispatched run asynchronously, so discovery retries
    /// briefly rather than reporting <c>not_found</c> on the first miss.
    /// </summary>
    public static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(20);

    private readonly ICredentialBroker broker;
    private readonly GitHubActionsClient client;
    private readonly CiTierMap tiers;
    private readonly Func<DateTimeOffset> clock;
    private readonly string instanceName;
    private readonly string defaultRef;
    private readonly RepoRef defaultRepo;
    private readonly TimeSpan credentialTtl;
    private readonly bool queueBuilds;

    private Credential? cached;
    private int issued;

    /// <param name="broker">The credential broker. The only source of tokens.</param>
    /// <param name="tiers">The tier-to-workflow map, from <c>feature-flow.yaml</c>.</param>
    /// <param name="client">The Actions API client. Injectable so tests can use a stub.</param>
    /// <param name="instanceName">The named provider instance, e.g. <c>github-actions</c>.</param>
    /// <param name="defaultRef">The ref to dispatch on when a tier does not name one.</param>
    /// <param name="defaultRepo">The repository to check against and to scope credentials to.</param>
    /// <param name="queueBuilds">Whether to report <c>merge_group</c> runs (docs/07 5).</param>
    public GitHubActionsCiProvider(
        ICredentialBroker broker,
        CiTierMap tiers,
        GitHubActionsClient? client = null,
        Func<DateTimeOffset>? clock = null,
        string instanceName = "github-actions",
        string defaultRef = "main",
        RepoRef? defaultRepo = null,
        bool queueBuilds = true,
        TimeSpan? credentialTtl = null)
    {
        this.broker = broker ?? throw new ArgumentNullException(nameof(broker));
        this.tiers = tiers ?? throw new ArgumentNullException(nameof(tiers));
        this.client = client ?? new GitHubActionsClient();
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.instanceName = instanceName;
        this.defaultRef = defaultRef;
        this.defaultRepo = defaultRepo ?? new RepoRef("ai-feature-flow", "feature-flow-example");
        this.queueBuilds = queueBuilds;
        this.credentialTtl = credentialTtl ?? TimeSpan.FromMinutes(55);
    }

    /// <summary>How many credentials this provider has taken from the broker. Safe to log.</summary>
    public int CredentialsIssued => issued;

    public ProviderDescriptor Describe() => new(
        Name: instanceName,
        Version: "0.1.0",
        Capabilities: new object[]
        {
            new CiCapabilities(
                Trigger: true,
                ReadLogs: true,
                Artifacts: true,
                InboundEvents: true),
        },
        Requires: Array.Empty<string>(),
        ConfigSchemaRef: "feature-flow/schemas/ci/github-actions/v1");

    /// <summary>
    /// Proves the credential reaches the Actions API and that the mapped
    /// workflows exist. Reports what it found, so a green check means something.
    /// </summary>
    public async Task<ProviderResult<CheckResult>> CheckAsync(CancellationToken ct)
    {
        var started = clock();

        var credential = await GetCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<CheckResult>.Fail(credential.Error!);

        var repo = defaultRepo;

        var workflows = await client.ListWorkflowsAsync(
            credential.Value!.Secret, repo.Owner, repo.Name, ct).ConfigureAwait(false);

        if (!workflows.IsOk)
            return ProviderResult<CheckResult>.Fail(workflows.Error!);

        var known = workflows.Value!;
        var mapped = tiers.Tiers
            .Select(t => tiers.Get(t))
            .Where(m => m is not null)
            .Select(m => m!)
            .ToList();

        var missing = mapped
            .Where(m => !known.Any(w => WorkflowFileMatches(w.Path, m.WorkflowFile)))
            .Select(m => m.WorkflowFile)
            .ToList();

        var detail = string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1} workflows visible, {2} tiers mapped [{3}]{4}",
            repo.FullName,
            known.Count,
            mapped.Count,
            string.Join(", ", mapped.Select(m => m.Tier)),
            missing.Count > 0 ? $", missing: {string.Join(", ", missing)}" : string.Empty);

        return ProviderResult<CheckResult>.Ok(new CheckResult(
            Ok: missing.Count == 0,
            Detail: detail,
            Latency: clock() - started));
    }

    /// <summary>
    /// Starts a run for a tier at a commit and returns a reference. Does not
    /// wait for the run (docs/05 section 5).
    ///
    /// Idempotency is <c>key</c>: the key is passed to the workflow as an input,
    /// so a retry that reaches the workflow twice is visible in the run's own
    /// inputs, and the engine can find the earlier run by the same key.
    /// </summary>
    public async Task<ProviderResult<CiRunRef>> TriggerAsync(
        RepoRef repo, string tier, string commitSha, string idempotencyKey, CancellationToken ct)
    {
        if (repo is null || string.IsNullOrWhiteSpace(repo.Owner) || string.IsNullOrWhiteSpace(repo.Name))
            return ProviderResult<CiRunRef>.Work("validation_failed", "repository is required");

        if (string.IsNullOrWhiteSpace(commitSha))
            return ProviderResult<CiRunRef>.Work("validation_failed", "commitSha is required");

        if (string.IsNullOrWhiteSpace(idempotencyKey))
            return ProviderResult<CiRunRef>.Work("validation_failed", "idempotencyKey is required");

        // The refusal. An unknown tier is Work, not Infrastructure: the caller
        // asked for a tier this repository does not declare (docs/05 section 5).
        var mapping = tiers.Get(tier);
        if (mapping is null)
            return ProviderResult<CiRunRef>.Work(
                "validation_failed",
                $"tier '{tier}' is not mapped; known tiers: {string.Join(", ", tiers.Tiers)}");

        var credential = await GetCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<CiRunRef>.Fail(credential.Error!);

        var token = credential.Value!.Secret;

        // workflow_dispatch needs a ref, and GitHub rejects a bare SHA as the
        // ref with 422. The tier's configured ref is used when it has one;
        // otherwise the commit's own branch is resolved.
        var gitRef = await ResolveDispatchRefAsync(
            token, repo, commitSha, mapping.Ref ?? defaultRef, ct).ConfigureAwait(false);

        if (!gitRef.IsOk)
            return ProviderResult<CiRunRef>.Fail(gitRef.Error!);

        // Recorded before the call, so discovery can ignore runs that predate
        // this dispatch. A second is shaved off to allow for clock skew between
        // this machine and GitHub's index.
        var dispatchedAt = clock() - TimeSpan.FromSeconds(1);

        var dispatch = await client.DispatchWorkflowAsync(
            token,
            repo.Owner,
            repo.Name,
            mapping.WorkflowFile,
            gitRef.Value!,
            WorkflowInputs.For(commitSha, idempotencyKey),
            ct).ConfigureAwait(false);

        if (!dispatch.IsOk)
            return ProviderResult<CiRunRef>.Fail(dispatch.Error!);

        // The dispatch returns 204 with no run id, so the run is discovered by
        // the commit it was started at. Discovery is bounded and non-blocking:
        // if the run has not appeared within the window, the reference is still
        // returned with a null run id and the caller's next GetRunStatusAsync
        // resolves it.
        var discovered = await DiscoverRunAsync(
            token, repo, mapping, commitSha, idempotencyKey, ct, dispatchedAt).ConfigureAwait(false);

        var run = new CiRunRef(
            RunId: discovered?.Id ?? "",
            Repo: repo,
            Tier: tier,
            CommitSha: commitSha,
            IdempotencyKey: idempotencyKey,
            Url: discovered?.HtmlUrl,
            Event: discovered?.Event ?? GitHubActionsEvent.WorkflowDispatch,
            WorkflowFile: mapping.WorkflowFile);

        return ProviderResult<CiRunRef>.Ok(run);
    }

    /// <summary>
    /// Reports where a run is right now. Non-blocking (docs/05 section 5).
    ///
    /// Handles checks that register late: a run exists before its jobs do, so
    /// "no jobs yet" is <see cref="CiRunState.Queued"/> with
    /// <see cref="CiRunStatus.ChecksRegistered"/> false. The caller keeps
    /// polling; it does not conclude there is no CI.
    /// </summary>
    public async Task<ProviderResult<CiRunStatus>> GetRunStatusAsync(CiRunRef run, CancellationToken ct)
    {
        if (run is null)
            return ProviderResult<CiRunStatus>.Work("validation_failed", "run is required");

        var credential = await GetCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<CiRunStatus>.Fail(credential.Error!);

        var token = credential.Value!.Secret;

        var resolved = await ResolveRunAsync(token, run, ct).ConfigureAwait(false);
        if (!resolved.IsOk)
            return ProviderResult<CiRunStatus>.Fail(resolved.Error!);

        var remote = resolved.Value!;

        var jobs = await client.ListRunJobsAsync(
            token, run.Repo.Owner, run.Repo.Name, remote.Id, ct).ConfigureAwait(false);

        // A run with no jobs yet is a real state, not an error. This is the
        // late-registration case the contract calls out.
        var jobList = jobs.IsOk ? jobs.Value! : Array.Empty<WorkflowJob>();
        var checksRegistered = jobList.Count > 0;

        var completed = jobList.Count(j => IsTerminal(j.Status));
        var state = MapState(remote.Status, checksRegistered);

        return ProviderResult<CiRunStatus>.Ok(new CiRunStatus(
            State: state,
            Conclusion: MapConclusion(remote.Conclusion),
            ChecksRegistered: checksRegistered,
            TotalChecks: jobList.Count,
            CompletedChecks: completed,
            Url: remote.HtmlUrl ?? run.Url,
            UpdatedAt: remote.UpdatedAt));
    }

    /// <summary>
    /// Structured results for a run. A run with unfinished jobs is
    /// <c>partial</c> with class <see cref="ErrorClass.Work"/> (docs/05
    /// section 5): some of the work happened, and the step decides.
    /// </summary>
    public async Task<ProviderResult<CiRunResult>> ReadResultsAsync(CiRunRef run, CancellationToken ct)
    {
        if (run is null)
            return ProviderResult<CiRunResult>.Work("validation_failed", "run is required");

        var credential = await GetCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<CiRunResult>.Fail(credential.Error!);

        var token = credential.Value!.Secret;

        var resolved = await ResolveRunAsync(token, run, ct).ConfigureAwait(false);
        if (!resolved.IsOk)
            return ProviderResult<CiRunResult>.Fail(resolved.Error!);

        var remote = resolved.Value!;

        var jobs = await client.ListRunJobsAsync(
            token, run.Repo.Owner, run.Repo.Name, remote.Id, ct).ConfigureAwait(false);

        if (!jobs.IsOk)
            return ProviderResult<CiRunResult>.Fail(jobs.Error!);

        var results = jobs.Value!
            .Select(j => new CiJobResult(
                Id: j.Id,
                Name: j.Name,
                State: MapState(j.Status, true),
                Conclusion: MapConclusion(j.Conclusion),
                StepsTotal: j.StepsTotal,
                StepsCompleted: j.StepsCompleted,
                StartedAt: j.StartedAt,
                CompletedAt: j.CompletedAt))
            .ToList();

        var complete = results.Count > 0 && results.All(j => j.IsTerminal);

        var result = new CiRunResult(
            Run: run,
            State: MapState(remote.Status, results.Count > 0),
            Conclusion: MapConclusion(remote.Conclusion),
            Jobs: results,
            Complete: complete,
            Url: remote.HtmlUrl ?? run.Url);

        // partial: some jobs have not finished. Work, not Infrastructure — the
        // run is real and progressing; the step decides whether to keep waiting.
        if (!complete)
            return ProviderResult<CiRunResult>.Work(
                "partial",
                $"{results.Count(j => j.IsTerminal)} of {results.Count} jobs finished");

        return ProviderResult<CiRunResult>.Ok(result);
    }

    /// <summary>
    /// Returns a log reference, not the log body (docs/05 section 5). GitHub
    /// answers the logs endpoint with a 302 to a short-lived signed URL, so the
    /// reference carries that URL and its expiry when one is given.
    /// </summary>
    public async Task<ProviderResult<LogRef>> ReadLogsAsync(CiRunRef run, CancellationToken ct)
    {
        if (run is null)
            return ProviderResult<LogRef>.Work("validation_failed", "run is required");

        var credential = await GetCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<LogRef>.Fail(credential.Error!);

        var token = credential.Value!.Secret;

        var resolved = await ResolveRunAsync(token, run, ct).ConfigureAwait(false);
        if (!resolved.IsOk)
            return ProviderResult<LogRef>.Fail(resolved.Error!);

        var remote = resolved.Value!;

        var location = await client.GetRunLogsLocationAsync(
            token, run.Repo.Owner, run.Repo.Name, remote.Id, ct).ConfigureAwait(false);

        if (!location.IsOk)
            return ProviderResult<LogRef>.Fail(location.Error!);

        var signed = location.Value;

        // No redirect means GitHub has not packaged the logs yet, which happens
        // for a run that has not finished. That is a real state, reported as a
        // reference to the API endpoint rather than as a failure.
        if (string.IsNullOrWhiteSpace(signed))
            return ProviderResult<LogRef>.Ok(new LogRef(
                Uri: $"https://api.github.com/repos/{run.Repo.FullName}/actions/runs/{remote.Id}/logs",
                Kind: LogRefKind.Api,
                ContentType: "application/zip"));

        return ProviderResult<LogRef>.Ok(new LogRef(
            Uri: signed,
            Kind: LogRefKind.SignedUrl,
            ExpiresAt: ReadSignedExpiry(signed),
            ContentType: "application/zip"));
    }

    /// <summary>
    /// Returns artifact references, not content (docs/05 section 5). Expired
    /// artifacts are reported with their expiry rather than silently dropped, so
    /// a caller can tell "never uploaded" from "uploaded and gone".
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<ArtifactRef>>> GetArtifactsAsync(
        CiRunRef run, CancellationToken ct)
    {
        if (run is null)
            return ProviderResult<IReadOnlyList<ArtifactRef>>.Work("validation_failed", "run is required");

        var credential = await GetCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<IReadOnlyList<ArtifactRef>>.Fail(credential.Error!);

        var token = credential.Value!.Secret;

        var resolved = await ResolveRunAsync(token, run, ct).ConfigureAwait(false);
        if (!resolved.IsOk)
            return ProviderResult<IReadOnlyList<ArtifactRef>>.Fail(resolved.Error!);

        var remote = resolved.Value!;

        var artifacts = await client.ListRunArtifactsAsync(
            token, run.Repo.Owner, run.Repo.Name, remote.Id, ct).ConfigureAwait(false);

        if (!artifacts.IsOk)
            return ProviderResult<IReadOnlyList<ArtifactRef>>.Fail(artifacts.Error!);

        var refs = artifacts.Value!
            .Select(a => new ArtifactRef(
                Id: a.Id,
                Name: a.Name,
                Uri: a.ArchiveDownloadUrl
                     ?? $"https://api.github.com/repos/{run.Repo.FullName}/actions/artifacts/{a.Id}/zip",
                SizeBytes: a.SizeInBytes,
                Digest: a.Digest,
                ExpiresAt: a.ExpiresAt,
                ContentType: "application/zip"))
            .ToList();

        return ProviderResult<IReadOnlyList<ArtifactRef>>.Ok(refs);
    }

    /// <summary>
    /// Finds the runs for a commit, including queue builds. Used by the merge
    /// queue path and by the harness to show <c>merge_group</c> handling.
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<CiRunRef>>> FindRunsAsync(
        RepoRef repo, string tier, string commitSha, CancellationToken ct)
    {
        var mapping = tiers.Get(tier);
        if (mapping is null)
            return ProviderResult<IReadOnlyList<CiRunRef>>.Work(
                "validation_failed",
                $"tier '{tier}' is not mapped; known tiers: {string.Join(", ", tiers.Tiers)}");

        var credential = await GetCredentialAsync(ct).ConfigureAwait(false);
        if (!credential.IsOk)
            return ProviderResult<IReadOnlyList<CiRunRef>>.Fail(credential.Error!);

        var token = credential.Value!.Secret;

        var runs = await client.ListWorkflowRunsAsync(
            token,
            repo.Owner,
            repo.Name,
            mapping.WorkflowFile,
            headSha: commitSha,
            perPage: 30,
            ct: ct).ConfigureAwait(false);

        if (!runs.IsOk)
            return ProviderResult<IReadOnlyList<CiRunRef>>.Fail(runs.Error!);

        var refs = runs.Value!.Runs
            .Where(r => queueBuilds || !string.Equals(r.Event, MergeGroupEvent, StringComparison.OrdinalIgnoreCase))
            .Select(r => new CiRunRef(
                RunId: r.Id,
                Repo: repo,
                Tier: tier,
                CommitSha: commitSha,
                IdempotencyKey: "",
                Url: r.HtmlUrl,
                Event: r.Event,
                WorkflowFile: mapping.WorkflowFile))
            .ToList();

        return ProviderResult<IReadOnlyList<CiRunRef>>.Ok(refs);
    }

    /// <summary>
    /// A credential from the broker, cached until it is close to expiry. The
    /// provider never reads a token from configuration (docs/04 section 1).
    /// </summary>
    private async Task<ProviderResult<Credential>> GetCredentialAsync(CancellationToken ct)
    {
        var now = clock();

        if (cached is not null && now < cached.ExpiresAt - CredentialRefreshMargin)
            return ProviderResult<Credential>.Ok(cached);

        var repo = defaultRepo;
        var scope = CredentialScope.ForRepository(
            repo.FullName,
            "actions:write",
            "contents:read",
            "metadata:read");

        var issuedCredential = await broker.IssueAsync(
            Actor.Engine("ci-provider"),
            GitHubAppCredentialBroker.TargetSystemName,
            scope.Text,
            credentialTtl,
            ct).ConfigureAwait(false);

        if (!issuedCredential.IsOk)
            return issuedCredential;

        Interlocked.Increment(ref this.issued);
        cached = issuedCredential.Value;
        return issuedCredential;
    }

    /// <summary>Refresh this long before expiry, so a slow call cannot outlive its token.</summary>
    private static readonly TimeSpan CredentialRefreshMargin = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Resolves the run a <see cref="CiRunRef"/> points at. A reference whose
    /// run id is empty — a dispatch whose run had not been indexed yet — is
    /// resolved by discovery, so the caller never has to care.
    /// </summary>
    private async Task<ProviderResult<WorkflowRun>> ResolveRunAsync(
        string token, CiRunRef run, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(run.RunId))
        {
            var direct = await client.GetRunAsync(
                token, run.Repo.Owner, run.Repo.Name, run.RunId, ct).ConfigureAwait(false);

            if (direct.IsOk)
                return direct;

            // A 404 on a run id we hold means the run is gone or was never
            // indexed. Fall through to discovery rather than failing outright.
            if (direct.Error!.Reason != "not_found")
                return direct;
        }

        var mapping = tiers.Get(run.Tier);
        if (mapping is null)
            return ProviderResult<WorkflowRun>.Work(
                "not_found",
                $"tier '{run.Tier}' is not mapped, so the run cannot be resolved");

        var discovered = await DiscoverRunAsync(
            token, run.Repo, mapping, run.CommitSha, run.IdempotencyKey, ct).ConfigureAwait(false);

        return discovered is null
            ? ProviderResult<WorkflowRun>.Work("not_found", $"no run for {run.Tier} at {run.CommitSha}")
            : ProviderResult<WorkflowRun>.Ok(discovered);
    }

    /// <summary>
    /// Finds the run a dispatch created. GitHub indexes a dispatched run
    /// asynchronously, so this retries briefly within
    /// <see cref="DiscoveryWindow"/> and then gives up rather than blocking.
    ///
    /// The search is bounded by the moment the dispatch was sent. Without that
    /// bound, discovery adopts an **earlier** run at the same commit — a previous
    /// attempt, or a push run — and the caller would poll a run that is already
    /// finished and report its conclusion as if it were the new one.
    /// </summary>
    private async Task<WorkflowRun?> DiscoverRunAsync(
        string token,
        RepoRef repo,
        CiTierMapping mapping,
        string commitSha,
        string idempotencyKey,
        CancellationToken ct,
        DateTimeOffset? notBefore = null)
    {
        var deadline = clock() + DiscoveryWindow;
        var floor = notBefore;

        while (true)
        {
            var runs = await client.ListWorkflowRunsAsync(
                token,
                repo.Owner,
                repo.Name,
                mapping.WorkflowFile,
                headSha: commitSha,
                eventName: GitHubActionsEvent.WorkflowDispatch,
                createdAtOrAfter: floor,
                perPage: 10,
                ct: ct).ConfigureAwait(false);

            if (runs.IsOk)
            {
                var match = runs.Value!.Runs
                    .OrderByDescending(r => r.CreatedAt ?? DateTimeOffset.MinValue)
                    .FirstOrDefault();

                if (match is not null)
                    return match;
            }

            if (clock() >= deadline)
                return null;

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// The ref to dispatch on. <c>workflow_dispatch</c> requires a ref and
    /// GitHub rejects a bare SHA with 422, so the tier's configured ref is used
    /// when it has one and the commit's own branch otherwise.
    /// </summary>
    private async Task<ProviderResult<string>> ResolveDispatchRefAsync(
        string token, RepoRef repo, string commitSha, string configuredRef, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configuredRef))
            return ProviderResult<string>.Ok(configuredRef);

        var branches = await client.ListBranchesForCommitAsync(
            token, repo.Owner, repo.Name, commitSha, ct).ConfigureAwait(false);

        if (!branches.IsOk)
            return ProviderResult<string>.Fail(branches.Error!);

        var branch = branches.Value!.FirstOrDefault();
        return !string.IsNullOrWhiteSpace(branch)
            ? ProviderResult<string>.Ok(branch)
            : ProviderResult<string>.Work(
                "validation_failed",
                $"commit {commitSha} is not the head of any branch, so it cannot be dispatched");
    }

    private static bool WorkflowFileMatches(string path, string workflowFile) =>
        string.Equals(
            path.Replace('\\', '/').Split('/').LastOrDefault(),
            workflowFile,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsTerminal(string? status) =>
        string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Maps GitHub's run status to a lifecycle state. A run with no jobs yet is
    /// <see cref="CiRunState.Queued"/>: the checks have not registered, which is
    /// the late-registration case, not an error.
    /// </summary>
    private static CiRunState MapState(string? status, bool checksRegistered) =>
        status?.ToLowerInvariant() switch
        {
            "completed" => CiRunState.Completed,
            "in_progress" => CiRunState.InProgress,
            "queued" or "waiting" or "requested" or "pending" => CiRunState.Queued,
            null => checksRegistered ? CiRunState.InProgress : CiRunState.Queued,
            _ => checksRegistered ? CiRunState.InProgress : CiRunState.Queued,
        };

    /// <summary>
    /// Maps GitHub's conclusion. A failing check is <see cref="CiConclusion.Failed"/>,
    /// which the step treats as a work outcome (docs/06 7.2) — never as
    /// infrastructure.
    /// </summary>
    private static CiConclusion MapConclusion(string? conclusion) =>
        conclusion?.ToLowerInvariant() switch
        {
            "success" => CiConclusion.Success,
            "failure" => CiConclusion.Failed,
            "cancelled" => CiConclusion.Cancelled,
            "skipped" => CiConclusion.Skipped,
            "timed_out" => CiConclusion.TimedOut,
            "action_required" => CiConclusion.ActionRequired,
            "neutral" => CiConclusion.Neutral,
            "startup_failure" => CiConclusion.Failed,
            _ => CiConclusion.None,
        };

    /// <summary>
    /// The expiry GitHub encodes in a signed log URL, when it sends one. Best
    /// effort: a URL without a usable expiry is still a valid reference.
    /// </summary>
    private static DateTimeOffset? ReadSignedExpiry(string url)
    {
        try
        {
            var query = new Uri(url).Query;
            foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (parts.Length != 2)
                    continue;

                var name = Uri.UnescapeDataString(parts[0]);
                if (!name.Contains("expire", StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains("expires", StringComparison.OrdinalIgnoreCase))
                    continue;

                var value = Uri.UnescapeDataString(parts[1]);
                if (long.TryParse(value, out var epoch))
                    return DateTimeOffset.FromUnixTimeSeconds(epoch);

                if (DateTimeOffset.TryParse(value, out var parsed))
                    return parsed;
            }
        }
        catch (UriFormatException)
        {
            return null;
        }

        return null;
    }
}
