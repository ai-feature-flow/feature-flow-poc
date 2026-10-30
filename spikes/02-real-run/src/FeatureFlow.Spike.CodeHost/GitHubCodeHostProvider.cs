using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.CodeHost;

/// <summary>
/// The GitHub implementation of <see cref="ICodeHostProvider"/>, against the
/// real REST API (and one GraphQL mutation, noted where it is used).
///
/// <para>Rules this implementation holds to:</para>
/// <list type="bullet">
/// <item>Every method returns <see cref="ProviderResult{T}"/>. Exceptions are
/// only for programming errors, and are classified by <b>type</b>, never by
/// string-matching a message (docs/05 section 1.3).</item>
/// <item>No method waits on human or external completion. Every call is a
/// short, bounded HTTP request (docs/04 section 1.3).</item>
/// <item>Credentials come from <see cref="ITokenFactory"/> only. This class
/// never reads a token from configuration (docs/04 section 4.6).</item>
/// <item>Every object created carries a hidden marker, and create methods
/// adopt an existing object with the same marker (S4).</item>
/// </list>
/// </summary>
public sealed class GitHubCodeHostProvider : ICodeHostProvider
{
    public const string ProviderName = "github";
    public const string ProviderVersion = "0.1.0-spike2";

    private static readonly CodeHostCapabilities Capabilities = new(
        PullRequest: true,
        ReviewComments: true,
        CheckStatus: true,
        MergeMethods: new[] { "merge", "squash", "rebase" },
        BranchProtection: true,
        MergeQueue: true,
        InboundEvents: true);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The GitHub REST API root.</summary>
    public const string ApiRoot = "https://api.github.com";

    private readonly HttpClient http;
    private readonly ITokenFactory tokens;
    private readonly IArtifactStore? artifacts;
    private readonly TimeSpan timeout;

    /// <summary>
    /// Builds the provider.
    /// </summary>
    /// <param name="tokens">
    /// The credential source. In production this is a
    /// <see cref="BrokerTokenFactory"/> over the sibling's
    /// <c>ICredentialBroker</c>; the provider cannot tell the difference.
    /// </param>
    /// <param name="artifacts">
    /// Where <see cref="GetDiffAsync"/> stores the diff. Optional: without it
    /// the provider returns a content-addressed reference with no store URI,
    /// which keeps the method usable in tests without a store.
    /// </param>
    /// <param name="http">
    /// The transport. Injected so the harness can supply a handler that fails
    /// on demand, which is how the infrastructure classification is verified
    /// against a real code path rather than a stub.
    /// </param>
    /// <param name="timeout">Per-call timeout. docs/05 section 1.2.</param>
    public GitHubCodeHostProvider(
        ITokenFactory tokens,
        IArtifactStore? artifacts = null,
        HttpClient? http = null,
        TimeSpan? timeout = null)
    {
        this.tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        this.artifacts = artifacts;
        this.timeout = timeout ?? TimeSpan.FromSeconds(30);

        if (http is not null)
        {
            this.http = http;
        }
        else
        {
            this.http = new HttpClient { Timeout = this.timeout };
        }

        // Relative request URIs resolve against this, so call sites can stay
        // short. Set only when the caller has not already set one, so an
        // injected client pointed at a proxy or a recording transport keeps it.
        this.http.BaseAddress ??= new Uri($"{ApiRoot}/");
    }

    // ---------------------------------------------------------------- IProvider

    public ProviderDescriptor Describe() => new(
        Name: ProviderName,
        Version: ProviderVersion,
        Capabilities: new object[] { Capabilities },
        Requires: Array.Empty<string>(),
        ConfigSchemaRef: "schemas/providers/github-codehost-v1.json");

    /// <summary>
    /// Verifies the credential and the API are usable. A metadata read, so it
    /// is bounded and cheap (docs/05 section 1.2: 15s). It does not touch a
    /// repository, because a health check must not depend on one being
    /// configured.
    /// </summary>
    public async Task<ProviderResult<CheckResult>> CheckAsync(CancellationToken ct)
    {
        var started = DateTimeOffset.UtcNow;

        var credential = await tokens.GetTokenAsync(new RepoRef("_", "_"), ct).ConfigureAwait(false);
        if (credential.Error is not null)
            return ProviderResult<CheckResult>.Fail(credential.Error);

        var result = await SendAsync<JsonElement>(
            new HttpRequestMessage(HttpMethod.Get, $"{ApiRoot}/rate_limit"),
            credential.Value!.Token, "check", ct).ConfigureAwait(false);

        if (result.Error is not null)
            return ProviderResult<CheckResult>.Fail(result.Error);

        return ProviderResult<CheckResult>.Ok(new CheckResult(
            Ok: true,
            Detail: $"github reachable, credential valid until {credential.Value.ExpiresAt:O}",
            Latency: DateTimeOffset.UtcNow - started));
    }

    // ---------------------------------------------------------------- Branches

    public async Task<ProviderResult<BranchRef>> CreateBranchAsync(
        RepoRef repo, string name, string fromRef, string idempotencyKey, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("branch name is required", nameof(name));
        if (string.IsNullOrWhiteSpace(fromRef)) throw new ArgumentException("fromRef is required", nameof(fromRef));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<BranchRef>.Fail(credential.Error);

        var token = credential.Value!.Token;

        var sourceSha = await ResolveRefAsync(repo, fromRef, token, ct).ConfigureAwait(false);
        if (sourceSha.Error is not null) return ProviderResult<BranchRef>.Fail(sourceSha.Error);

        // Idempotency: adopt the branch if it already exists at the same SHA.
        // A branch that exists at a *different* SHA is a conflict, not an
        // adoption — the engine must reconcile, not silently move it
        // (docs/05 section 4.1).
        var existing = await TryGetRefAsync(repo, name, token, ct).ConfigureAwait(false);
        if (existing.Error is not null && existing.Error.Reason != "not_found")
            return ProviderResult<BranchRef>.Fail(existing.Error);

        if (existing.Value is not null)
        {
            // The single-ref endpoint carries the SHA under `object`, not at the
            // top level, so read it the same way ResolveRefAsync does.
            var existingSha = existing.Value.Object?.Sha ?? existing.Value.Sha;

            if (!string.Equals(existingSha, sourceSha.Value, StringComparison.OrdinalIgnoreCase))
                return ProviderResult<BranchRef>.Work("conflict");

            return ProviderResult<BranchRef>.Ok(new BranchRef(repo, name, existingSha));
        }

        var created = await SendAsync<Dto.Ref>(
            new HttpRequestMessage(HttpMethod.Post, $"repos/{repo.FullName}/git/refs")
            {
                Content = JsonBody(new { @ref = $"refs/heads/{name}", sha = sourceSha.Value }),
            },
            token, "create_branch", ct).ConfigureAwait(false);

        if (created.Error is not null) return ProviderResult<BranchRef>.Fail(created.Error);

        var sha = created.Value!.Object?.Sha ?? created.Value!.Sha;
        return ProviderResult<BranchRef>.Ok(new BranchRef(repo, name, sha));
    }

    public async Task<ProviderResult<Unit>> DeleteBranchAsync(RepoRef repo, string name, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("branch name is required", nameof(name));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<Unit>.Fail(credential.Error);

        var result = await SendRawAsync(
            new HttpRequestMessage(HttpMethod.Delete,
                $"repos/{repo.FullName}/git/refs/heads/{Uri.EscapeDataString(name)}"),
            credential.Value!.Token, "delete_branch", ct).ConfigureAwait(false);

        // Idempotent: deleting a branch that is already gone returns 404, which
        // is success for this operation.
        if (result.Error is not null && result.Error.Reason != "not_found")
            return ProviderResult<Unit>.Fail(result.Error);

        return ProviderResult<Unit>.Ok(Unit.Value);
    }

    // ------------------------------------------------------------ Pull requests

    public async Task<ProviderResult<PullRequestRef>> OpenPullRequestAsync(
        RepoRef repo, PullRequestSpec spec, string idempotencyKey, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (spec is null) throw new ArgumentNullException(nameof(spec));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<PullRequestRef>.Fail(credential.Error);

        // Adopt first: an existing PR carrying this marker is the same work,
        // whether or not this attempt created it (S4). This is also the
        // timeout-recovery path from docs/05 section 1.4.
        var found = await FindByMarkerAsync(repo, Marker.For(idempotencyKey), ct).ConfigureAwait(false);
        if (found.Error is not null) return ProviderResult<PullRequestRef>.Fail(found.Error);

        var adopted = found.Value!.PullRequests.FirstOrDefault();
        if (adopted is not null)
            return ProviderResult<PullRequestRef>.Ok(adopted);

        var marker = Marker.Hidden(idempotencyKey);
        var body = spec.Body.Contains(marker, StringComparison.Ordinal)
            ? spec.Body
            : spec.Body + $"\n\n{marker}\n";

        var created = await SendAsync<Dto.PullRequest>(
            new HttpRequestMessage(HttpMethod.Post, $"repos/{repo.FullName}/pulls")
            {
                Content = JsonBody(new
                {
                    title = spec.Title,
                    head = spec.Head,
                    @base = spec.Base,
                    body,
                    draft = spec.Draft,
                }),
            },
            credential.Value!.Token, "open_pr", ct).ConfigureAwait(false);

        if (created.Error is not null) return ProviderResult<PullRequestRef>.Fail(created.Error);

        return ProviderResult<PullRequestRef>.Ok(
            new PullRequestRef(repo, created.Value!.Number, created.Value.Head?.Sha));
    }

    public async Task<ProviderResult<PullRequestRef>> UpdatePullRequestAsync(
        PullRequestRef pr, PullRequestSpec spec, CancellationToken ct)
    {
        if (pr is null) throw new ArgumentNullException(nameof(pr));
        if (spec is null) throw new ArgumentNullException(nameof(spec));

        var credential = await GetCredentialAsync(pr.Repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<PullRequestRef>.Fail(credential.Error);

        var updated = await SendAsync<Dto.PullRequest>(
            new HttpRequestMessage(HttpMethod.Patch, $"repos/{pr.Repo.FullName}/pulls/{pr.Number}")
            {
                Content = JsonBody(new
                {
                    title = spec.Title,
                    body = spec.Body,
                    @base = spec.Base,
                    state = "open",
                }),
            },
            credential.Value!.Token, "update_pr", ct).ConfigureAwait(false);

        if (updated.Error is not null) return ProviderResult<PullRequestRef>.Fail(updated.Error);

        return ProviderResult<PullRequestRef>.Ok(
            new PullRequestRef(pr.Repo, updated.Value!.Number, updated.Value.Head?.Sha));
    }

    public async Task<ProviderResult<PullRequestState>> GetPullRequestAsync(
        PullRequestRef pr, CancellationToken ct)
    {
        if (pr is null) throw new ArgumentNullException(nameof(pr));

        var credential = await GetCredentialAsync(pr.Repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<PullRequestState>.Fail(credential.Error);

        var token = credential.Value!.Token;

        var prResult = await SendAsync<Dto.PullRequest>(
            new HttpRequestMessage(HttpMethod.Get, $"repos/{pr.Repo.FullName}/pulls/{pr.Number}"),
            token, "get_pr", ct).ConfigureAwait(false);
        if (prResult.Error is not null) return ProviderResult<PullRequestState>.Fail(prResult.Error);

        var dto = prResult.Value!;

        // Approvals come from reviews. GitHub's review list is chronological and
        // a reviewer may change their mind, so the latest review per user wins.
        var reviewsResult = await SendAsync<List<Dto.Review>>(
            new HttpRequestMessage(HttpMethod.Get,
                $"repos/{pr.Repo.FullName}/pulls/{pr.Number}/reviews?per_page=100"),
            token, "get_pr_reviews", ct).ConfigureAwait(false);

        var approvedBy = new List<string>();
        if (reviewsResult.Value is not null)
        {
            approvedBy = reviewsResult.Value
                .GroupBy(r => r.User?.Login ?? "")
                .Select(g => g.OrderBy(r => r.Id).Last())
                .Where(r => string.Equals(r.State, "APPROVED", StringComparison.OrdinalIgnoreCase))
                .Select(r => r.User?.Login ?? "")
                .Where(login => login.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        return ProviderResult<PullRequestState>.Ok(new PullRequestState(
            Pr: new PullRequestRef(pr.Repo, dto.Number, dto.Head?.Sha),
            State: dto.State,
            Merged: dto.Merged,
            Draft: dto.Draft,
            HeadSha: dto.Head?.Sha ?? "",
            HeadBranch: dto.Head?.Ref ?? "",
            BaseBranch: dto.Base?.Ref ?? "",
            Mergeable: dto.Mergeable,
            MergeableState: dto.MergeableState,
            Approvals: approvedBy.Count,
            ApprovedBy: approvedBy,
            Url: dto.HtmlUrl));
    }

    public async Task<ProviderResult<string>> GetDefaultBranchAsync(RepoRef repo, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<string>.Fail(credential.Error);

        var result = await SendAsync<Dto.Repository>(
            new HttpRequestMessage(HttpMethod.Get, $"repos/{repo.FullName}"),
            credential.Value!.Token, "get_default_branch", ct).ConfigureAwait(false);
        if (result.Error is not null) return ProviderResult<string>.Fail(result.Error);

        var branch = result.Value!.DefaultBranch;
        if (string.IsNullOrWhiteSpace(branch))
        {
            // Fails closed: an unknown default branch must never be treated as
            // "not the default branch", which would apply the weaker policy.
            return ProviderResult<string>.Work(
                "not_found", "the code host did not report a default branch");
        }

        return ProviderResult<string>.Ok(branch);
    }

    public async Task<ProviderResult<Unit>> ClosePullRequestAsync(PullRequestRef pr, CancellationToken ct)
    {
        if (pr is null) throw new ArgumentNullException(nameof(pr));

        var credential = await GetCredentialAsync(pr.Repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<Unit>.Fail(credential.Error);

        var result = await SendAsync<Dto.PullRequest>(
            new HttpRequestMessage(HttpMethod.Patch, $"repos/{pr.Repo.FullName}/pulls/{pr.Number}")
            {
                Content = JsonBody(new { state = "closed" }),
            },
            credential.Value!.Token, "close_pr", ct).ConfigureAwait(false);

        // Closing an already-closed PR is idempotent: GitHub returns 200 with
        // state "closed", so there is nothing to distinguish.
        return result.Error is not null
            ? ProviderResult<Unit>.Fail(result.Error)
            : ProviderResult<Unit>.Ok(Unit.Value);
    }

    public async Task<ProviderResult<PullRequestRef>> CreateRevertPullRequestAsync(
        PullRequestRef pr, string idempotencyKey, CancellationToken ct)
    {
        if (pr is null) throw new ArgumentNullException(nameof(pr));

        var credential = await GetCredentialAsync(pr.Repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<PullRequestRef>.Fail(credential.Error);

        var token = credential.Value!.Token;

        var current = await SendAsync<Dto.PullRequest>(
            new HttpRequestMessage(HttpMethod.Get, $"repos/{pr.Repo.FullName}/pulls/{pr.Number}"),
            token, "revert_read_pr", ct).ConfigureAwait(false);
        if (current.Error is not null) return ProviderResult<PullRequestRef>.Fail(current.Error);

        var headBranch = current.Value!.Head?.Ref ?? "";
        var baseBranch = current.Value!.Base?.Ref ?? "";
        var revertBranch = $"revert-{headBranch}-{Marker.BranchSuffix(idempotencyKey)}";

        // Adopt: if the revert branch already exists, an earlier attempt already
        // opened the revert PR (S4).
        var existing = await TryGetRefAsync(pr.Repo, revertBranch, token, ct).ConfigureAwait(false);
        if (existing.Error is not null && existing.Error.Reason != "not_found")
            return ProviderResult<PullRequestRef>.Fail(existing.Error);

        if (existing.Value is null)
        {
            var baseSha = await ResolveRefAsync(pr.Repo, baseBranch, token, ct).ConfigureAwait(false);
            if (baseSha.Error is not null) return ProviderResult<PullRequestRef>.Fail(baseSha.Error);

            var created = await SendAsync<Dto.Ref>(
                new HttpRequestMessage(HttpMethod.Post, $"repos/{pr.Repo.FullName}/git/refs")
                {
                    Content = JsonBody(new { @ref = $"refs/heads/{revertBranch}", sha = baseSha.Value }),
                },
                token, "revert_create_branch", ct).ConfigureAwait(false);
            if (created.Error is not null) return ProviderResult<PullRequestRef>.Fail(created.Error);

            // `git revert -m 1 M` yields the tree of M's first parent, so the
            // revert commit carries that tree with the current base head as its
            // parent.
            var mergeSha = current.Value.MergeCommitSha;
            if (!string.IsNullOrEmpty(mergeSha))
            {
                var mergeCommit = await SendAsync<Dto.Commit>(
                    new HttpRequestMessage(HttpMethod.Get, $"repos/{pr.Repo.FullName}/git/commits/{mergeSha}"),
                    token, "revert_read_merge", ct).ConfigureAwait(false);
                if (mergeCommit.Error is not null)
                    return ProviderResult<PullRequestRef>.Fail(mergeCommit.Error);

                var firstParent = mergeCommit.Value!.Parents?.FirstOrDefault();
                if (firstParent is not null)
                {
                    var parentCommit = await SendAsync<Dto.Commit>(
                        new HttpRequestMessage(HttpMethod.Get,
                            $"repos/{pr.Repo.FullName}/git/commits/{firstParent}"),
                        token, "revert_read_parent", ct).ConfigureAwait(false);
                    if (parentCommit.Error is not null)
                        return ProviderResult<PullRequestRef>.Fail(parentCommit.Error);

                    var revertCommit = await SendAsync<Dto.Commit>(
                        new HttpRequestMessage(HttpMethod.Post, $"repos/{pr.Repo.FullName}/git/commits")
                        {
                            Content = JsonBody(new
                            {
                                message = $"Revert \"{current.Value.Title}\"\n\n{Marker.Trailer(idempotencyKey)}",
                                tree = parentCommit.Value!.Tree,
                                parents = new[] { baseSha.Value },
                            }),
                        },
                        token, "revert_commit", ct).ConfigureAwait(false);
                    if (revertCommit.Error is not null)
                        return ProviderResult<PullRequestRef>.Fail(revertCommit.Error);

                    var moved = await SendAsync<Dto.Ref>(
                        new HttpRequestMessage(HttpMethod.Patch,
                            $"repos/{pr.Repo.FullName}/git/refs/heads/{Uri.EscapeDataString(revertBranch)}")
                        {
                            Content = JsonBody(new { sha = revertCommit.Value!.Sha, force = false }),
                        },
                        token, "revert_move_branch", ct).ConfigureAwait(false);
                    if (moved.Error is not null)
                        return ProviderResult<PullRequestRef>.Fail(moved.Error);
                }
            }
        }

        var opened = await SendAsync<Dto.PullRequest>(
            new HttpRequestMessage(HttpMethod.Post, $"repos/{pr.Repo.FullName}/pulls")
            {
                Content = JsonBody(new
                {
                    title = $"Revert \"{current.Value.Title}\"",
                head = revertBranch,
                @base = baseBranch,
                body = $"Reverts #{pr.Number}.\n\n{Marker.Hidden(idempotencyKey)}\n",
            }),
            },
            token, "revert_open_pr", ct).ConfigureAwait(false);

        if (opened.Error is not null) return ProviderResult<PullRequestRef>.Fail(opened.Error);

        return ProviderResult<PullRequestRef>.Ok(
            new PullRequestRef(pr.Repo, opened.Value!.Number, opened.Value.Head?.Sha));
    }

    // ---------------------------------------------------------------- Merging

    public async Task<ProviderResult<MergeResult>> MergeBaseForwardAsync(
        RepoRef repo, string branch, string into, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("branch is required", nameof(branch));
        if (string.IsNullOrWhiteSpace(into)) throw new ArgumentException("into is required", nameof(into));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<MergeResult>.Fail(credential.Error);

        var token = credential.Value!.Token;

        var before = await ResolveRefAsync(repo, into, token, ct).ConfigureAwait(false);
        if (before.Error is not null) return ProviderResult<MergeResult>.Fail(before.Error);

        // Spike 3: SendRawAsync, not SendAsync. GitHub answers 204 with no body
        // when `into` already contains `branch` ("nothing to merge"); SendAsync
        // read the empty body as Infrastructure/server_error, which the engine
        // retries free, forever. A no-op is a completed merge with no change.
        var raw = await SendRawAsync(
            new HttpRequestMessage(HttpMethod.Post, $"repos/{repo.FullName}/merges")
            {
                Content = JsonBody(new
                {
                    @base = into,
                    head = branch,
                    commit_message = $"Merge {branch} into {into}",
                }),
            },
            token, "merge_base_forward", ct).ConfigureAwait(false);

        // GitHub reports a merge conflict as 409, which FromStatus already maps
        // to Work/conflict. Pass it through unchanged.
        if (raw.Error is not null) return ProviderResult<MergeResult>.Fail(raw.Error);

        Dto.Commit? mergedCommit = null;
        if (!string.IsNullOrWhiteSpace(raw.Value))
        {
            try { mergedCommit = JsonSerializer.Deserialize<Dto.Commit>(raw.Value!, Json); }
            catch (JsonException) { }
        }
        var merged = ProviderResult<Dto.Commit?>.Ok(mergedCommit);

        var after = await ResolveRefAsync(repo, into, token, ct).ConfigureAwait(false);
        if (after.Error is not null) return ProviderResult<MergeResult>.Fail(after.Error);

        var changed = !string.Equals(before.Value, after.Value, StringComparison.OrdinalIgnoreCase);

        return ProviderResult<MergeResult>.Ok(new MergeResult(
            Merged: true,
            BranchChanged: changed,
            NewHeadSha: after.Value,
            Sha: merged.Value?.Sha ?? after.Value));
    }

    public async Task<ProviderResult<MergeResult>> MergeAsync(
        PullRequestRef pr, string method, CancellationToken ct)
    {
        if (pr is null) throw new ArgumentNullException(nameof(pr));
        if (!Capabilities.MergeMethods.Contains(method))
            return ProviderResult<MergeResult>.Work("capability_missing");

        var credential = await GetCredentialAsync(pr.Repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<MergeResult>.Fail(credential.Error);

        var token = credential.Value!.Token;

        var before = await SendAsync<Dto.PullRequest>(
            new HttpRequestMessage(HttpMethod.Get, $"repos/{pr.Repo.FullName}/pulls/{pr.Number}"),
            token, "merge_read_pr", ct).ConfigureAwait(false);
        if (before.Error is not null) return ProviderResult<MergeResult>.Fail(before.Error);

        // Already merged: idempotent success, and the branch did not change as a
        // result of this call (review 06 C3).
        if (before.Value!.Merged)
            return ProviderResult<MergeResult>.Ok(new MergeResult(
                Merged: true, BranchChanged: false, NewHeadSha: before.Value.Head?.Sha));

        var result = await SendAsync<Dto.MergeResponse>(
            new HttpRequestMessage(HttpMethod.Put, $"repos/{pr.Repo.FullName}/pulls/{pr.Number}/merge")
            {
                Content = JsonBody(new { merge_method = method }),
            },
            token, "merge", ct).ConfigureAwait(false);

        if (result.Error is not null) return ProviderResult<MergeResult>.Fail(result.Error);

        var after = await SendAsync<Dto.PullRequest>(
            new HttpRequestMessage(HttpMethod.Get, $"repos/{pr.Repo.FullName}/pulls/{pr.Number}"),
            token, "merge_read_pr_after", ct).ConfigureAwait(false);

        var newHead = after.Value?.Head?.Sha ?? result.Value?.Sha;
        var changed = !string.Equals(before.Value.Head?.Sha, newHead, StringComparison.OrdinalIgnoreCase);

        return ProviderResult<MergeResult>.Ok(new MergeResult(
            Merged: result.Value?.Merged ?? true,
            BranchChanged: changed,
            NewHeadSha: newHead,
            Sha: result.Value?.Sha,
            Message: result.Value?.Message));
    }

    public async Task<ProviderResult<MergeResult>> EnqueueMergeAsync(PullRequestRef pr, CancellationToken ct)
    {
        if (pr is null) throw new ArgumentNullException(nameof(pr));
        if (!Capabilities.MergeQueue)
            return ProviderResult<MergeResult>.Work("capability_missing");

        var credential = await GetCredentialAsync(pr.Repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<MergeResult>.Fail(credential.Error);

        // Enqueue and return. It does not wait — the engine polls
        // GetPullRequestAsync or receives an inbound event (docs/05 section 4.3).
        var result = await SendAsync<JsonElement>(
            new HttpRequestMessage(HttpMethod.Post,
                $"repos/{pr.Repo.FullName}/pulls/{pr.Number}/merge_queue"),
            credential.Value!.Token, "enqueue_merge", ct).ConfigureAwait(false);

        if (result.Error is not null) return ProviderResult<MergeResult>.Fail(result.Error);

        return ProviderResult<MergeResult>.Ok(new MergeResult(
            Merged: false,
            BranchChanged: false,
            NewHeadSha: pr.HeadSha,
            Message: "enqueued"));
    }

    public async Task<ProviderResult<BranchProtection>> GetBranchProtectionAsync(
        RepoRef repo, string branch, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (string.IsNullOrWhiteSpace(branch)) throw new ArgumentException("branch is required", nameof(branch));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<BranchProtection>.Fail(credential.Error);

        var result = await SendAsync<Dto.ProtectionResponse>(
            new HttpRequestMessage(HttpMethod.Get,
                $"repos/{repo.FullName}/branches/{Uri.EscapeDataString(branch)}/protection"),
            credential.Value!.Token, "get_branch_protection", ct).ConfigureAwait(false);

        // An unprotected branch returns 404, which is a normal answer here, not
        // an error: "no protection" is a fact the caller needs.
        if (result.Error is not null)
        {
            if (result.Error.Reason == "not_found")
                return ProviderResult<BranchProtection>.Ok(new BranchProtection(
                    Branch: branch,
                    Enabled: false,
                    RequiredReviews: false,
                    RequiredApprovingReviews: 0,
                    RequiresQueue: false,
                    RequiredChecks: Array.Empty<string>(),
                    EnforceAdmins: false));

            return ProviderResult<BranchProtection>.Fail(result.Error);
        }

        var dto = result.Value!;
        var reviews = dto.RequiredReviews;

        return ProviderResult<BranchProtection>.Ok(new BranchProtection(
            Branch: branch,
            Enabled: dto.Enabled ?? reviews is not null || dto.RequiredChecks is not null,
            RequiredReviews: reviews is not null,
            RequiredApprovingReviews: reviews?.RequiredApprovingReviews ?? 0,
            RequiresQueue: false,
            RequiredChecks: (IReadOnlyList<string>?)dto.RequiredChecks?.Contexts ?? Array.Empty<string>(),
            EnforceAdmins: dto.EnforceAdmins?.Enabled ?? false));
    }

    // ------------------------------------------------------------------ Checks

    public async Task<ProviderResult<CheckStatus>> GetCheckStatusAsync(
        RepoRef repo, string commitSha, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (string.IsNullOrWhiteSpace(commitSha)) throw new ArgumentException("commitSha is required", nameof(commitSha));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<CheckStatus>.Fail(credential.Error);

        var token = credential.Value!.Token;

        var runsResult = await SendAsync<Dto.CheckRunsResponse>(
            new HttpRequestMessage(HttpMethod.Get,
                $"repos/{repo.FullName}/commits/{commitSha}/check-runs?per_page=100"),
            token, "get_check_runs", ct).ConfigureAwait(false);
        if (runsResult.Error is not null) return ProviderResult<CheckStatus>.Fail(runsResult.Error);

        var statusResult = await SendAsync<Dto.CombinedStatusResponse>(
            new HttpRequestMessage(HttpMethod.Get, $"repos/{repo.FullName}/commits/{commitSha}/status"),
            token, "get_commit_status", ct).ConfigureAwait(false);
        if (statusResult.Error is not null) return ProviderResult<CheckStatus>.Fail(statusResult.Error);

        var runs = runsResult.Value!;
        var statuses = statusResult.Value!;

        var checkRuns = runs.CheckRuns.Select(r => new CheckRun(
            Id: r.Id,
            Name: r.Name,
            Status: r.Status,
            Conclusion: r.Conclusion,
            DetailsUrl: r.DetailsUrl,
            StartedAt: r.StartedAt,
            CompletedAt: r.CompletedAt)).ToList();

        var commitStatuses = statuses.Statuses.Select(s => new CommitStatus(
            Id: s.Id,
            Context: s.Context,
            State: s.State,
            Description: s.Description,
            TargetUrl: s.TargetUrl)).ToList();

        return ProviderResult<CheckStatus>.Ok(new CheckStatus(
            Repo: repo,
            CommitSha: commitSha,
            State: Combine(checkRuns, commitStatuses),
            TotalCount: runs.TotalCount + statuses.TotalCount,
            CheckRuns: checkRuns,
            Statuses: commitStatuses));
    }

    /// <summary>
    /// Combines check runs and commit statuses into one state. A check that has
    /// not finished counts as pending, and a commit with nothing registered yet
            /// is <c>none</c> rather than <c>success</c> — checks may register late, and
    /// treating "nothing yet" as success would let a merge through before CI
    /// starts (docs/05 section 4.4).
    /// </summary>
    private static string Combine(IReadOnlyList<CheckRun> runs, IReadOnlyList<CommitStatus> statuses)
    {
        if (runs.Count == 0 && statuses.Count == 0)
            return "none";

        var pending = runs.Any(r => !string.Equals(r.Status, "completed", StringComparison.OrdinalIgnoreCase)) ||
                      statuses.Any(s => string.Equals(s.State, "pending", StringComparison.OrdinalIgnoreCase));

        var failed = runs.Any(r => r.Conclusion is not null &&
                                   !string.Equals(r.Conclusion, "success", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(r.Conclusion, "neutral", StringComparison.OrdinalIgnoreCase) &&
                                   !string.Equals(r.Conclusion, "skipped", StringComparison.OrdinalIgnoreCase)) ||
                     statuses.Any(s => string.Equals(s.State, "failure", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(s.State, "error", StringComparison.OrdinalIgnoreCase));

        if (failed) return "failure";
        if (pending) return "pending";
        return "success";
    }

    // --------------------------------------------------------- Review comments

    public async Task<ProviderResult<IReadOnlyList<ReviewThread>>> ListReviewCommentsAsync(
        PullRequestRef pr, CancellationToken ct)
    {
        if (pr is null) throw new ArgumentNullException(nameof(pr));

        var credential = await GetCredentialAsync(pr.Repo, ct).ConfigureAwait(false);
        if (credential.Error is not null)
            return ProviderResult<IReadOnlyList<ReviewThread>>.Fail(credential.Error);

        var result = await SendAsync<List<Dto.ReviewCommentDto>>(
            new HttpRequestMessage(HttpMethod.Get,
                $"repos/{pr.Repo.FullName}/pulls/{pr.Number}/comments?per_page=100"),
            credential.Value!.Token, "list_review_comments", ct).ConfigureAwait(false);

        if (result.Error is not null)
            return ProviderResult<IReadOnlyList<ReviewThread>>.Fail(result.Error);

        var comments = result.Value!;

        // GitHub returns flat comments. Group into threads: a comment with
        // in_reply_to_id belongs to the thread of its parent; anything else
        // starts a thread. Threads, not flat comments, is what the contract
        // asks for, because a thread is what can be resolved
        // (docs/05 section 4.5).
        var ids = comments.Select(c => c.Id).ToHashSet();
        var roots = comments
            .Where(c => c.InReplyToId is null || !ids.Contains(c.InReplyToId.Value))
            .ToList();

        var threads = roots.Select(root =>
        {
            var members = comments
                .Where(c => c.Id == root.Id || c.InReplyToId == root.Id)
                .OrderBy(c => c.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenBy(c => c.Id)
                .Select(c => new ReviewComment(
                    Body: c.Body,
                    Path: c.Path,
                    Line: c.Line ?? c.OriginalLine,
                    CommitSha: c.CommitId ?? c.OriginalCommitId))
                .ToList();

            var head = members.FirstOrDefault();
            var outdated = root.CommitId is not null &&
                           root.OriginalCommitId is not null &&
                           !string.Equals(root.CommitId, root.OriginalCommitId, StringComparison.OrdinalIgnoreCase);

            return new ReviewThread(
                Id: root.Id.ToString(),
                Path: head?.Path,
                Line: head?.Line,
                Resolved: false,
                Outdated: outdated,
                Comments: members);
        }).ToList();

        return ProviderResult<IReadOnlyList<ReviewThread>>.Ok(threads);
    }

    public async Task<ProviderResult<CommentRef>> AddReviewCommentAsync(
        PullRequestRef pr, ReviewComment comment, CancellationToken ct)
    {
        if (pr is null) throw new ArgumentNullException(nameof(pr));
        if (comment is null) throw new ArgumentNullException(nameof(comment));

        var credential = await GetCredentialAsync(pr.Repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<CommentRef>.Fail(credential.Error);

        var body = comment.Body;
        if (comment.Marker is not null && !body.Contains(comment.Marker, StringComparison.Ordinal))
            body += $"\n\n{comment.Marker}\n";

        // A pull-request-level comment goes to the issue comments endpoint.
        // GitHub's review-comment endpoint requires positioning — commit_id,
        // path and line — and answers 422 without it, so a decline reason that
        // is not anchored to a line cannot be posted there.
        if (comment.Path is null)
        {
            var issueResult = await SendAsync<Dto.ReviewCommentDto>(
                new HttpRequestMessage(HttpMethod.Post, $"repos/{pr.Repo.FullName}/issues/{pr.Number}/comments")
                {
                    Content = JsonBody(new Dictionary<string, object?> { ["body"] = body }),
                },
                credential.Value!.Token, "add_pr_comment", ct).ConfigureAwait(false);

            if (issueResult.Error is not null)
                return ProviderResult<CommentRef>.Fail(issueResult.Error);

            return ProviderResult<CommentRef>.Ok(new CommentRef(
                $"{issueResult.Value!.Id}#{pr.Repo.FullName}#{pr.Number}", issueResult.Value.HtmlUrl));
        }

        var payload = new Dictionary<string, object?>
        {
            ["body"] = body,
            ["path"] = comment.Path,
            ["line"] = comment.Line,
            ["side"] = comment.Side ?? "RIGHT",
            ["commit_id"] = comment.CommitSha,
        };

        var result = await SendAsync<Dto.ReviewCommentDto>(
            new HttpRequestMessage(HttpMethod.Post, $"repos/{pr.Repo.FullName}/pulls/{pr.Number}/comments")
            {
                Content = JsonBody(payload),
            },
            credential.Value!.Token, "add_review_comment", ct).ConfigureAwait(false);

        if (result.Error is not null) return ProviderResult<CommentRef>.Fail(result.Error);

        // The reply endpoint is scoped to the pull request, so the reference
        // carries the repository and number with it.
        return ProviderResult<CommentRef>.Ok(new CommentRef(
            $"{result.Value!.Id}#{pr.Repo.FullName}#{pr.Number}", result.Value.HtmlUrl));
    }

    public async Task<ProviderResult<CommentRef>> ReplyToReviewCommentAsync(
        CommentRef comment, string body, CancellationToken ct)
    {
        if (comment is null) throw new ArgumentNullException(nameof(comment));

        var (repo, number, commentId) = ParsePrScopedCommentRef(comment.Id);

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<CommentRef>.Fail(credential.Error);

        var result = await SendAsync<Dto.ReviewCommentDto>(
            new HttpRequestMessage(HttpMethod.Post, $"repos/{repo.FullName}/pulls/{number}/comments")
            {
                Content = JsonBody(new { body, in_reply_to = commentId }),
            },
            credential.Value!.Token, "reply_review_comment", ct).ConfigureAwait(false);

        if (result.Error is not null) return ProviderResult<CommentRef>.Fail(result.Error);

        return ProviderResult<CommentRef>.Ok(new CommentRef(
            $"{result.Value!.Id}#{repo.FullName}#{number}", result.Value.HtmlUrl));
    }

    public async Task<ProviderResult<Unit>> ResolveReviewThreadAsync(ReviewThread thread, CancellationToken ct)
    {
        if (thread is null) throw new ArgumentNullException(nameof(thread));

        var (repo, number, commentId) = ParsePrScopedCommentRef(thread.Id);

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<Unit>.Fail(credential.Error);

        // GitHub's REST API has no resolve-thread endpoint; resolution is a
        // GraphQL mutation on the review thread node. The thread id we hold is
        // a REST comment id, so resolve it to a GraphQL node id first.
        var query = """
            query($owner: String!, $name: String!, $number: Int!) {
              repository(owner: $owner, name: $name) {
                pullRequest(number: $number) {
                  reviewThreads(first: 100) {
                    nodes { id isResolved comments(first: 1) { nodes { databaseId } } }
                  }
                }
              }
            }
            """;

        var queryResult = await SendGraphQLAsync(
            query,
            new { owner = repo.Owner, name = repo.Name, number },
            credential.Value!.Token, "resolve_thread_query", ct).ConfigureAwait(false);

        if (queryResult.Error is not null) return ProviderResult<Unit>.Fail(queryResult.Error);

        var threads = queryResult.Value!
            .GetProperty("data")
            .GetProperty("repository")
            .GetProperty("pullRequest")
            .GetProperty("reviewThreads")
            .GetProperty("nodes");

        string? threadNodeId = null;
        foreach (var node in threads.EnumerateArray())
        {
            var comments = node.GetProperty("comments").GetProperty("nodes");
            foreach (var comment in comments.EnumerateArray())
            {
                if (comment.TryGetProperty("databaseId", out var id) && id.GetInt64() == commentId)
                {
                    threadNodeId = node.GetProperty("id").GetString();
                    break;
                }
            }

            if (threadNodeId is not null) break;
        }

        if (threadNodeId is null)
            return ProviderResult<Unit>.Work("not_found");

        var mutation = """
            mutation($threadId: ID!) {
              resolveReviewThread(input: { threadId: $threadId }) {
                thread { id isResolved }
              }
            }
            """;

        var mutationResult = await SendGraphQLAsync(
            mutation,
            new { threadId = threadNodeId },
            credential.Value!.Token, "resolve_thread", ct).ConfigureAwait(false);

        if (mutationResult.Error is not null) return ProviderResult<Unit>.Fail(mutationResult.Error);

        // Idempotent: resolving an already-resolved thread succeeds.
        return ProviderResult<Unit>.Ok(Unit.Value);
    }

    // ------------------------------------------------------------------ Diffs

    public async Task<ProviderResult<ArtifactRef>> GetDiffAsync(
        RepoRef repo, string fromRef, string toRef, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (string.IsNullOrWhiteSpace(fromRef)) throw new ArgumentException("fromRef is required", nameof(fromRef));
        if (string.IsNullOrWhiteSpace(toRef)) throw new ArgumentException("toRef is required", nameof(toRef));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<ArtifactRef>.Fail(credential.Error);

        var url = $"{ApiRoot}/repos/{repo.FullName}/compare/" +
                  $"{Uri.EscapeDataString(fromRef)}...{Uri.EscapeDataString(toRef)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.diff"));

        var response = await SendRawAsync(request, credential.Value!.Token, "get_diff", ct).ConfigureAwait(false);
        if (response.Error is not null) return ProviderResult<ArtifactRef>.Fail(response.Error);

        var bytes = Encoding.UTF8.GetBytes(response.Value!);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // The diff goes to the artifact store, never into workflow state (DP10).
        if (artifacts is null)
            return ProviderResult<ArtifactRef>.Ok(new ArtifactRef(
                Uri: $"artifact://sha256/{hash}",
                ContentHash: hash,
                SizeBytes: bytes.LongLength));

        using var stream = new MemoryStream(bytes);
        var stored = await artifacts.PutAsync(stream, ct).ConfigureAwait(false);
        if (stored.Error is not null) return ProviderResult<ArtifactRef>.Fail(stored.Error);

        return ProviderResult<ArtifactRef>.Ok(new ArtifactRef(
            Uri: stored.Value!.Uri,
            ContentHash: hash,
            SizeBytes: bytes.LongLength));
    }

    // -------------------------------------------------------------- File reads

    public async Task<ProviderResult<FileContent>> ReadFileAsync(
        RepoRef repo, string path, string reference, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("path is required", nameof(path));
        if (string.IsNullOrWhiteSpace(reference)) throw new ArgumentException("reference is required", nameof(reference));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<FileContent>.Fail(credential.Error);

        var url = $"{ApiRoot}/repos/{repo.FullName}/contents/{EscapePath(path)}" +
                  $"?ref={Uri.EscapeDataString(reference)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        var result = await SendAsync<Dto.ContentResponse>(
            request, credential.Value!.Token, "read_file", ct).ConfigureAwait(false);

        if (result.Error is not null) return ProviderResult<FileContent>.Fail(result.Error);

        var dto = result.Value!;
        if (string.IsNullOrEmpty(dto.Content))
            return ProviderResult<FileContent>.Fail(
                new ProviderError(ErrorClass.Infrastructure, "server_error"));

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(dto.Content.Replace("\n", "", StringComparison.Ordinal));
        }
        catch (FormatException)
        {
            return ProviderResult<FileContent>.Fail(
                new ProviderError(ErrorClass.Infrastructure, "server_error"));
        }

        var content = Encoding.UTF8.GetString(bytes);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        // R16: the hash of the manifest as read from the branch. This is what
        // the engine records per step so a mid-run edit is visible
        // (docs/07 section 9).
        return ProviderResult<FileContent>.Ok(new FileContent(
            Path: path,
            Ref: reference,
            Content: content,
            ContentHash: hash,
            SizeBytes: bytes.LongLength,
            BlobSha: dto.Sha));
    }

    // ------------------------------------------------------------ Idempotency

    public async Task<ProviderResult<FoundObjects>> FindByMarkerAsync(
        RepoRef repo, string marker, CancellationToken ct)
    {
        if (repo is null) throw new ArgumentNullException(nameof(repo));
        if (string.IsNullOrWhiteSpace(marker)) throw new ArgumentException("marker is required", nameof(marker));

        var credential = await GetCredentialAsync(repo, ct).ConfigureAwait(false);
        if (credential.Error is not null) return ProviderResult<FoundObjects>.Fail(credential.Error);

        var token = credential.Value!.Token;

        // Branches: the marker is a suffix of the branch name.
        var branches = new List<BranchRef>();
        var refsResult = await SendAsync<List<Dto.Ref>>(
            new HttpRequestMessage(HttpMethod.Get, $"repos/{repo.FullName}/git/refs/heads?per_page=100"),
            token, "find_branches", ct).ConfigureAwait(false);
        if (refsResult.Error is not null) return ProviderResult<FoundObjects>.Fail(refsResult.Error);

        foreach (var reference in refsResult.Value!)
        {
            var name = reference.Name.StartsWith("refs/heads/", StringComparison.Ordinal)
                ? reference.Name["refs/heads/".Length..]
                : reference.Name;

            if (name.EndsWith(marker, StringComparison.OrdinalIgnoreCase))
                branches.Add(new BranchRef(repo, name, reference.Object?.Sha ?? reference.Sha));
        }

        // Pull requests: the marker is hidden in the body.
        var prs = new List<PullRequestRef>();
        var prsResult = await SendAsync<List<Dto.PullRequest>>(
            new HttpRequestMessage(HttpMethod.Get, $"repos/{repo.FullName}/pulls?state=all&per_page=100"),
            token, "find_prs", ct).ConfigureAwait(false);
        if (prsResult.Error is not null) return ProviderResult<FoundObjects>.Fail(prsResult.Error);

        foreach (var pr in prsResult.Value!)
        {
            if (Marker.Contains(pr.Body, marker))
                prs.Add(new PullRequestRef(repo, pr.Number, pr.Head?.Sha));
        }

        // Empty is a normal answer, not an error (docs/05 section 4.6).
        return ProviderResult<FoundObjects>.Ok(new FoundObjects(branches, prs));
    }

    // ------------------------------------------------------------- Transport

    private Task<ProviderResult<Credential>> GetCredentialAsync(RepoRef repo, CancellationToken ct) =>
        tokens.GetTokenAsync(repo, ct);

    private async Task<ProviderResult<T>> SendAsync<T>(
        HttpRequestMessage request, string token, string operation, CancellationToken ct)
    {
        var response = await SendRawAsync(request, token, operation, ct).ConfigureAwait(false);
        if (response.Error is not null) return ProviderResult<T>.Fail(response.Error);

        var body = response.Value!;
        if (string.IsNullOrWhiteSpace(body))
            return ProviderResult<T>.Fail(new ProviderError(ErrorClass.Infrastructure, "server_error"));

        try
        {
            var value = JsonSerializer.Deserialize<T>(body, Json);
            if (value is null)
                return ProviderResult<T>.Fail(new ProviderError(ErrorClass.Infrastructure, "server_error"));

            return ProviderResult<T>.Ok(value);
        }
        catch (JsonException)
        {
            return ProviderResult<T>.Fail(new ProviderError(ErrorClass.Infrastructure, "server_error"));
        }
    }

    private async Task<ProviderResult<string>> SendRawAsync(
        HttpRequestMessage request, string token, string operation, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("feature-flow-spike2", ProviderVersion));
        if (!request.Headers.Accept.Any())
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        try
        {
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var error = ErrorClassification.FromStatus(response.StatusCode, operation, response.Headers);
                return ProviderResult<string>.Fail(error with { RemoteId = TryReadRemoteId(body) });
            }

            return ProviderResult<string>.Ok(body);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The caller cancelled. That is not a provider error and must not be
            // swallowed into a classified result.
            throw;
        }
        catch (Exception ex)
        {
            // Classified by exception type, never by message text.
            return ProviderResult<string>.Fail(ErrorClassification.FromException(ex));
        }
    }

    private async Task<ProviderResult<JsonElement>> SendGraphQLAsync(
        string query, object variables, string token, string operation, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}/graphql")
        {
            Content = JsonBody(new { query, variables }),
        };

        var result = await SendAsync<JsonElement>(request, token, operation, ct).ConfigureAwait(false);
        if (result.Error is not null) return result;

        if (result.Value!.TryGetProperty("errors", out var errors) &&
            errors.ValueKind == JsonValueKind.Array &&
            errors.GetArrayLength() > 0)
        {
            var message = errors[0].TryGetProperty("message", out var text)
                ? text.GetString()
                : "graphql_error";

            // A GraphQL error carries its classification in `type`: NOT_FOUND
            // and FORBIDDEN are work outcomes, anything else is a fault.
            var type = errors[0].TryGetProperty("type", out var kind) ? kind.GetString() : null;
            var reason = type switch
            {
                "NOT_FOUND" => "not_found",
                "FORBIDDEN" => "rejected",
                _ => "server_error",
            };

            var errorClass = reason is "not_found" or "rejected"
                ? ErrorClass.Work
                : ErrorClass.Infrastructure;

            return ProviderResult<JsonElement>.Fail(
                new ProviderError(errorClass, reason, RemoteId: message));
        }

        return result;
    }

    private async Task<ProviderResult<Dto.Ref?>> TryGetRefAsync(
        RepoRef repo, string branch, string token, CancellationToken ct)
    {
        var result = await SendAsync<Dto.Ref>(
            new HttpRequestMessage(HttpMethod.Get,
                $"repos/{repo.FullName}/git/ref/heads/{Uri.EscapeDataString(branch)}"),
            token, "get_ref", ct).ConfigureAwait(false);

        if (result.Error is not null)
        {
            if (result.Error.Reason == "not_found")
                return ProviderResult<Dto.Ref?>.Ok(null);
            return ProviderResult<Dto.Ref?>.Fail(result.Error);
        }

        return ProviderResult<Dto.Ref?>.Ok(result.Value);
    }

    private async Task<ProviderResult<string>> ResolveRefAsync(
        RepoRef repo, string reference, string token, CancellationToken ct)
    {
        var result = await SendAsync<Dto.Ref>(
            new HttpRequestMessage(HttpMethod.Get,
                $"repos/{repo.FullName}/git/ref/heads/{Uri.EscapeDataString(reference)}"),
            token, "resolve_ref", ct).ConfigureAwait(false);

        if (result.Error is not null) return ProviderResult<string>.Fail(result.Error);

        return ProviderResult<string>.Ok(result.Value!.Object?.Sha ?? result.Value!.Sha);
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload, Json), Encoding.UTF8, "application/json");

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string? TryReadRemoteId(string body)
    {
        try
        {
            var error = JsonSerializer.Deserialize<Dto.ErrorResponse>(body, Json);
            return error?.Message;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A review comment reference is scoped to its pull request, because the
    /// reply endpoint is <c>/pulls/{number}/comments</c>. The id is
    /// <c>{commentId}#{owner}/{repo}#{number}</c>.
    /// </summary>
    private static (RepoRef Repo, int Number, long CommentId) ParsePrScopedCommentRef(string id)
    {
        var parts = id.Split('#');
        if (parts.Length < 3)
            throw new ArgumentException(
                $"comment reference '{id}' must be of the form <commentId>#<owner>/<repo>#<number>", nameof(id));

        var commentId = long.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture);
        var repoParts = parts[1].Split('/');
        if (repoParts.Length != 2)
            throw new ArgumentException($"repository '{parts[1]}' must be of the form <owner>/<repo>", nameof(id));

        var number = int.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        return (new RepoRef(repoParts[0], repoParts[1]), number, commentId);
    }
}
