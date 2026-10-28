using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;

namespace FeatureFlow.Spike.Ci;

/// <summary>
/// The GitHub Actions REST calls the CI provider needs. Plain
/// <see cref="HttpClient"/>, no GitHub SDK, and every result comes back as one of
/// our own records — the transport-neutral rule (docs/04 section 1).
///
/// Every call returns a <see cref="ProviderResult{T}"/> whose error is
/// classified from the **HTTP status code**, never from exception text
/// (docs/05 1.3). The mapping lives in one place, <see cref="Classify"/>, so a
/// new endpoint cannot invent its own.
/// </summary>
public sealed class GitHubActionsClient
{
    public const string DefaultApiBase = "https://api.github.com";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient http;
    private readonly HttpClient noRedirectHttp;
    private readonly string apiBase;

    public GitHubActionsClient(HttpClient? http = null, string apiBase = DefaultApiBase)
    {
        this.http = http ?? new HttpClient();
        this.apiBase = apiBase.TrimEnd('/');

        // The logs endpoint answers 302 to a signed URL. The provider wants the
        // location, not the body, so this client must not follow it.
        this.noRedirectHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    }

    /// <summary>
    /// POST /repos/{owner}/{repo}/actions/workflows/{workflow}/dispatches.
    /// Returns 204 with no body, so the caller discovers the run separately.
    /// </summary>
    public async Task<ProviderResult<Unit>> DispatchWorkflowAsync(
        string token,
        string owner,
        string repo,
        string workflowFile,
        string gitRef,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ref"] = gitRef,
            ["inputs"] = inputs,
        };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{apiBase}/repos/{owner}/{repo}/actions/workflows/{workflowFile}/dispatches");

        AddHeaders(request, token);
        request.Content = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await SendAsync(request, ct).ConfigureAwait(false);
        return response.Error is not null
            ? ProviderResult<Unit>.Fail(response.Error)
            : ProviderResult<Unit>.Ok(Unit.Value);
    }

    /// <summary>GET /repos/{owner}/{repo}/actions/runs/{runId}.</summary>
    public async Task<ProviderResult<WorkflowRun>> GetRunAsync(
        string token, string owner, string repo, string runId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{apiBase}/repos/{owner}/{repo}/actions/runs/{runId}");

        AddHeaders(request, token);

        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<WorkflowRun>.Fail(response.Error);

        return ProviderResult<WorkflowRun>.Ok(ParseRun(JsonDocument.Parse(response.Body).RootElement));
    }

    /// <summary>
    /// GET /repos/{owner}/{repo}/actions/workflows/{workflow}/runs. Used to
    /// discover the run a dispatch created, and to find queue builds.
    /// </summary>
    public async Task<ProviderResult<WorkflowRunPage>> ListWorkflowRunsAsync(
        string token,
        string owner,
        string repo,
        string workflowFile,
        string? headSha = null,
        string? eventName = null,
        DateTimeOffset? createdAtOrAfter = null,
        int perPage = 30,
        CancellationToken ct = default)
    {
        var query = new List<string> { $"per_page={Math.Clamp(perPage, 1, 100)}" };
        if (!string.IsNullOrWhiteSpace(headSha))
            query.Add($"head_sha={Uri.EscapeDataString(headSha)}");
        if (!string.IsNullOrWhiteSpace(eventName))
            query.Add($"event={Uri.EscapeDataString(eventName)}");
        if (createdAtOrAfter is not null)
            query.Add($"created=>={Uri.EscapeDataString(createdAtOrAfter.Value.UtcDateTime.ToString("o"))}");

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{apiBase}/repos/{owner}/{repo}/actions/workflows/{workflowFile}/runs?{string.Join("&", query)}");

        AddHeaders(request, token);

        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<WorkflowRunPage>.Fail(response.Error);

        using var doc = JsonDocument.Parse(response.Body);
        var root = doc.RootElement;

        var runs = new List<WorkflowRun>();
        if (root.TryGetProperty("workflow_runs", out var array))
            foreach (var element in array.EnumerateArray())
                runs.Add(ParseRun(element));

        return ProviderResult<WorkflowRunPage>.Ok(new WorkflowRunPage(
            TotalCount: root.TryGetProperty("total_count", out var total) ? total.GetInt32() : runs.Count,
            Runs: runs));
    }

    /// <summary>GET /repos/{owner}/{repo}/actions/runs/{runId}/jobs.</summary>
    public async Task<ProviderResult<IReadOnlyList<WorkflowJob>>> ListRunJobsAsync(
        string token, string owner, string repo, string runId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{apiBase}/repos/{owner}/{repo}/actions/runs/{runId}/jobs?per_page=100");

        AddHeaders(request, token);

        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<IReadOnlyList<WorkflowJob>>.Fail(response.Error);

        using var doc = JsonDocument.Parse(response.Body);
        var jobs = new List<WorkflowJob>();

        if (doc.RootElement.TryGetProperty("jobs", out var array))
            foreach (var element in array.EnumerateArray())
                jobs.Add(ParseJob(element));

        return ProviderResult<IReadOnlyList<WorkflowJob>>.Ok(jobs);
    }

    /// <summary>GET /repos/{owner}/{repo}/actions/runs/{runId}/artifacts.</summary>
    public async Task<ProviderResult<IReadOnlyList<WorkflowArtifact>>> ListRunArtifactsAsync(
        string token, string owner, string repo, string runId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{apiBase}/repos/{owner}/{repo}/actions/runs/{runId}/artifacts?per_page=100");

        AddHeaders(request, token);

        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<IReadOnlyList<WorkflowArtifact>>.Fail(response.Error);

        using var doc = JsonDocument.Parse(response.Body);
        var artifacts = new List<WorkflowArtifact>();

        if (doc.RootElement.TryGetProperty("artifacts", out var array))
            foreach (var element in array.EnumerateArray())
                artifacts.Add(ParseArtifact(element));

        return ProviderResult<IReadOnlyList<WorkflowArtifact>>.Ok(artifacts);
    }

    /// <summary>
    /// GET /repos/{owner}/{repo}/actions/runs/{runId}/logs. GitHub answers 302
    /// to a short-lived signed URL, so this follows no redirects and returns the
    /// target: the provider hands back a <see cref="LogRef"/>, never the body.
    /// </summary>
    public async Task<ProviderResult<string?>> GetRunLogsLocationAsync(
        string token, string owner, string repo, string runId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{apiBase}/repos/{owner}/{repo}/actions/runs/{runId}/logs");

        AddHeaders(request, token);

        var response = await SendAsync(request, ct, followRedirects: false, http: noRedirectHttp)
            .ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<string?>.Fail(response.Error);

        return ProviderResult<string?>.Ok(response.Location);
    }

    /// <summary>
    /// GET /repos/{owner}/{repo}/actions/workflows. Used by
    /// <see cref="GitHubActionsCiProvider.CheckAsync"/> to prove the credential
    /// reaches the Actions API, and to resolve a tier to a workflow file.
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<WorkflowSummary>>> ListWorkflowsAsync(
        string token, string owner, string repo, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{apiBase}/repos/{owner}/{repo}/actions/workflows?per_page=100");

        AddHeaders(request, token);

        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<IReadOnlyList<WorkflowSummary>>.Fail(response.Error);

        using var doc = JsonDocument.Parse(response.Body);
        var workflows = new List<WorkflowSummary>();

        if (doc.RootElement.TryGetProperty("workflows", out var array))
            foreach (var element in array.EnumerateArray())
                workflows.Add(new WorkflowSummary(
                    Id: element.TryGetProperty("id", out var id) ? id.GetInt64() : 0,
                    Name: element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                    Path: element.TryGetProperty("path", out var path) ? path.GetString() ?? "" : "",
                    State: element.TryGetProperty("state", out var state) ? state.GetString() : null));

        return ProviderResult<IReadOnlyList<WorkflowSummary>>.Ok(workflows);
    }

    /// <summary>
    /// GET /repos/{owner}/{repo}/commits/{ref}. Resolves a branch or tag to the
    /// commit it points at, which is what a caller needs before it can trigger a
    /// run at a real SHA.
    /// </summary>
    public async Task<ProviderResult<string>> GetCommitShaForRefAsync(
        string token, string owner, string repo, string gitRef, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{apiBase}/repos/{owner}/{repo}/commits/{Uri.EscapeDataString(gitRef)}");

        AddHeaders(request, token);

        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<string>.Fail(response.Error);

        using var doc = JsonDocument.Parse(response.Body);

        return doc.RootElement.TryGetProperty("sha", out var sha) && sha.GetString() is { } value
            ? ProviderResult<string>.Ok(value)
            : ProviderResult<string>.Work("not_found", $"no commit for ref '{gitRef}'");
    }

    /// <summary>
    /// GET /repos/{owner}/{repo}/commits/{sha}/branches-where-head. How the
    /// provider turns a commit SHA into a ref, which
    /// <c>workflow_dispatch</c> requires: GitHub rejects a bare SHA as the
    /// dispatch ref with 422.
    /// </summary>
    public async Task<ProviderResult<IReadOnlyList<string>>> ListBranchesForCommitAsync(
        string token, string owner, string repo, string commitSha, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{apiBase}/repos/{owner}/{repo}/commits/{commitSha}/branches-where-head");

        AddHeaders(request, token);

        var response = await SendAsync(request, ct).ConfigureAwait(false);
        if (response.Error is not null)
            return ProviderResult<IReadOnlyList<string>>.Fail(response.Error);

        using var doc = JsonDocument.Parse(response.Body);
        var branches = new List<string>();

        foreach (var element in doc.RootElement.EnumerateArray())
            if (element.TryGetProperty("name", out var name))
                branches.Add(name.GetString() ?? "");

        return ProviderResult<IReadOnlyList<string>>.Ok(
            branches.Where(b => !string.IsNullOrWhiteSpace(b)).ToList());
    }

    private static void AddHeaders(HttpRequestMessage request, string token)
    {
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.UserAgent.ParseAdd("feature-flow-spike2");
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
    }

    private async Task<RawResponse> SendAsync(
        HttpRequestMessage request,
        CancellationToken ct,
        bool followRedirects = true,
        HttpClient? http = null)
    {
        var transport = http ?? this.http;

        try
        {
            using var response = await transport.SendAsync(
                request,
                followRedirects ? HttpCompletionOption.ResponseContentRead : HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var status = (int)response.StatusCode;

            // A redirect is the success shape for the logs endpoint: the body is
            // elsewhere and the provider only wants the location.
            if (followRedirects && status is >= 300 and < 400)
                return RawResponse.Ok("", null);

            if (!followRedirects && status is >= 300 and < 400)
                return RawResponse.Ok("", response.Headers.Location?.ToString());

            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (status is >= 200 and < 300)
                return RawResponse.Ok(body, null);

            return RawResponse.Failed(Classify(status, body, response.Headers));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException)
        {
            // HttpClient raises this for its own timeout as well as for
            // cancellation. Only a cancellation we did not ask for is a timeout.
            return RawResponse.Failed(new ProviderError(ErrorClass.Infrastructure, "timeout"));
        }
        catch (OperationCanceledException)
        {
            return RawResponse.Failed(new ProviderError(ErrorClass.Infrastructure, "timeout"));
        }
        catch (HttpRequestException)
        {
            return RawResponse.Failed(new ProviderError(ErrorClass.Infrastructure, "unreachable"));
        }
    }

    /// <summary>
    /// The one place HTTP status becomes an <see cref="ErrorClass"/>.
    /// Classification is by status code, never by matching exception or body
    /// text (docs/05 1.3).
    /// </summary>
    public static ProviderError Classify(int status, string body, HttpResponseHeaders? headers = null)
    {
        var remoteId = TryReadRemoteId(body);

        return status switch
        {
            401 => new ProviderError(ErrorClass.Infrastructure, "unauthenticated", null, remoteId),
            403 => new ProviderError(ErrorClass.Infrastructure, "forbidden", ReadRetryAfter(headers), remoteId),
            404 => new ProviderError(ErrorClass.Work, "not_found", null, remoteId),
            422 => new ProviderError(ErrorClass.Work, "validation_failed", null, remoteId),
            409 => new ProviderError(ErrorClass.Work, "conflict", null, remoteId),
            429 => new ProviderError(ErrorClass.Infrastructure, "rate_limited", ReadRetryAfter(headers), remoteId),
            >= 500 => new ProviderError(ErrorClass.Infrastructure, "server_error", ReadRetryAfter(headers), remoteId),
            _ => new ProviderError(ErrorClass.Infrastructure, "server_error", null, remoteId),
        };
    }

    /// <summary>
    /// Honours <c>Retry-After</c> into <see cref="ProviderError.RetryAfter"/>
    /// (docs/05 1.3). GitHub sends seconds; an HTTP date is accepted too.
    /// </summary>
    private static TimeSpan? ReadRetryAfter(HttpResponseHeaders? headers)
    {
        if (headers is null || !headers.TryGetValues("Retry-After", out var values))
            return null;

        var text = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(text))
            return null;

        if (double.TryParse(text, out var seconds) && seconds >= 0)
            return TimeSpan.FromSeconds(seconds);

        if (DateTimeOffset.TryParse(text, out var until))
        {
            var remaining = until - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }

        return null;
    }

    /// <summary>
    /// The remote's own identifier for the failure, when it sends one. Carried
    /// on the error so an operator can correlate with GitHub's logs. Never the
    /// message text: that is not stable enough to classify on.
    /// </summary>
    private static string? TryReadRemoteId(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("request_id", out var id))
                return id.GetString();
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static WorkflowRun ParseRun(JsonElement element) => new(
        Id: element.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : "",
        Name: element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
        Status: element.TryGetProperty("status", out var status) ? status.GetString() : null,
        Conclusion: element.TryGetProperty("conclusion", out var conclusion)
            ? conclusion.GetString()
            : null,
        Event: element.TryGetProperty("event", out var ev) ? ev.GetString() : null,
        HeadSha: element.TryGetProperty("head_sha", out var sha) ? sha.GetString() : null,
        HeadBranch: element.TryGetProperty("head_branch", out var branch) ? branch.GetString() : null,
        HtmlUrl: element.TryGetProperty("html_url", out var url) ? url.GetString() : null,
        RunAttempt: element.TryGetProperty("run_attempt", out var attempt) ? attempt.GetInt32() : 1,
        CreatedAt: ParseDate(element, "created_at"),
        UpdatedAt: ParseDate(element, "updated_at"),
        RunStartedAt: ParseDate(element, "run_started_at"));

    private static WorkflowJob ParseJob(JsonElement element)
    {
        var steps = 0;
        var completed = 0;

        if (element.TryGetProperty("steps", out var array))
            foreach (var step in array.EnumerateArray())
            {
                steps++;
                if (step.TryGetProperty("status", out var s) &&
                    string.Equals(s.GetString(), "completed", StringComparison.Ordinal))
                    completed++;
            }

        return new WorkflowJob(
            Id: element.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : "",
            Name: element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Status: element.TryGetProperty("status", out var status) ? status.GetString() : null,
            Conclusion: element.TryGetProperty("conclusion", out var conclusion)
                ? conclusion.GetString()
                : null,
            StepsTotal: steps,
            StepsCompleted: completed,
            StartedAt: ParseDate(element, "started_at"),
            CompletedAt: ParseDate(element, "completed_at"));
    }

    private static WorkflowArtifact ParseArtifact(JsonElement element) => new(
        Id: element.TryGetProperty("id", out var id) ? id.GetInt64().ToString() : "",
        Name: element.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
        SizeInBytes: element.TryGetProperty("size_in_bytes", out var size) ? size.GetInt64() : null,
        Digest: element.TryGetProperty("digest", out var digest) ? digest.GetString() : null,
        ArchiveDownloadUrl: element.TryGetProperty("archive_download_url", out var url)
            ? url.GetString()
            : null,
        ExpiresAt: ParseDate(element, "expires_at"),
        Expired: element.TryGetProperty("expired", out var expired) && expired.GetBoolean());

    private static DateTimeOffset? ParseDate(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) &&
        DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;

    private readonly record struct RawResponse(string Body, string? Location, ProviderError? Error)
    {
        public static RawResponse Ok(string body, string? location) => new(body, location, null);

        public static RawResponse Failed(ProviderError error) => new("", null, error);
    }
}

/// <summary>A workflow run, as the API reports it.</summary>
public sealed record WorkflowRun(
    string Id,
    string Name,
    string? Status,
    string? Conclusion,
    string? Event,
    string? HeadSha,
    string? HeadBranch,
    string? HtmlUrl,
    int RunAttempt = 1,
    DateTimeOffset? CreatedAt = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? RunStartedAt = null);

/// <summary>A page of workflow runs.</summary>
public sealed record WorkflowRunPage(int TotalCount, IReadOnlyList<WorkflowRun> Runs);

/// <summary>A job within a run.</summary>
public sealed record WorkflowJob(
    string Id,
    string Name,
    string? Status,
    string? Conclusion,
    int StepsTotal = 0,
    int StepsCompleted = 0,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null);

/// <summary>An artifact uploaded by a run.</summary>
public sealed record WorkflowArtifact(
    string Id,
    string Name,
    long? SizeInBytes,
    string? Digest,
    string? ArchiveDownloadUrl,
    DateTimeOffset? ExpiresAt = null,
    bool Expired = false);

/// <summary>A workflow in a repository.</summary>
public sealed record WorkflowSummary(long Id, string Name, string Path, string? State);
