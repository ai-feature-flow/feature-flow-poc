using FeatureFlow.Spike.CodeHost;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;
using FeatureFlow.Spike.RealWorker;

using FileContent = FeatureFlow.Spike.CodeHost.FileContent;
using Actor = FeatureFlow.Spike.Providers.Actor;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check R16: editing <c>feature-flow.yaml</c> mid-run is picked up by the next
/// step, and the hash is recorded.
///
/// <para>
/// The edit is real: a commit to the branch that changes the manifest, pushed
/// through the code host. The next step's read must return the new content and a
/// different hash.
/// </para>
/// </summary>
public static class CheckR16
{
    public static async Task<int> RunAsync(RunContext context, Action<string> say)
    {
        say("## R16: a mid-run edit to feature-flow.yaml is picked up by the next step");
        say("");

        var failures = 0;

        // Step N reads the manifest.
        var before = await context.CodeHost.ReadFileAsync(
            context.CodeHostRepo, "feature-flow.yaml", context.Branch, CancellationToken.None);

        if (!before.IsOk)
        {
            say($"  FAIL: read before the edit: {before.Error!.Class}/{before.Error.Reason}");
            return 1;
        }

        var first = before.Value!;
        say($"  step N read:   ref={first.Ref} sha256:{first.ContentHash} ({first.SizeBytes} bytes)");

        // Edit the manifest mid-run: a real commit on the branch, through the
        // code host API rather than a local file write, so the branch really
        // changed.
        var edited = await EditManifestAsync(context, first);
        if (!edited.IsOk)
        {
            say($"  FAIL: the mid-run edit failed: {edited.Error!.Class}/{edited.Error.Reason} " +
                $"{edited.Error.Detail}");
            say("");
            say("  **FAIL**");
            say("");
            return 1;
        }

        say($"  mid-run edit:  committed {edited.Value} to {context.Branch}");
        say("");

        // Step N+1 reads the manifest again. It must see the new content and a
        // different hash.
        var after = await context.CodeHost.ReadFileAsync(
            context.CodeHostRepo, "feature-flow.yaml", context.Branch, CancellationToken.None);

        if (!after.IsOk)
        {
            say($"  FAIL: read after the edit: {after.Error!.Class}/{after.Error.Reason}");
            return 1;
        }

        var second = after.Value!;
        say($"  step N+1 read: ref={second.Ref} sha256:{second.ContentHash} ({second.SizeBytes} bytes)");
        say("");

        var hashChanged = !string.Equals(first.ContentHash, second.ContentHash, StringComparison.Ordinal);
        say($"  hash changed between the two steps: {hashChanged}");
        say($"    {first.ContentHash} -> {second.ContentHash}");
        if (!hashChanged) failures++;

        // The edit must be visible in the content, not only in the hash.
        var editVisible = second.Content.Contains("# spike2-r16", StringComparison.Ordinal);
        say($"  the edit is visible in the content the next step read: {editVisible}");
        if (!editVisible) failures++;

        // The hash is recorded: that is the second half of R16. Show the record.
        var costs = RunContext.ReadCosts(context.RecordDirectory, context.RunId);
        var withHash = costs.Count(c => c.ManifestHash is not null);
        say($"  steps whose cost record carries the manifest hash: {withHash}/{costs.Count}");

        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Commits a real change to <c>feature-flow.yaml</c> on the branch, through
    /// the code host API. Returns the new commit sha.
    /// </summary>
    private static async Task<ProviderResult<string>> EditManifestAsync(
        RunContext context, FileContent current)
    {
        // Resolve the branch head from the remote, not from the local checkout.
        // The run pushes commits from the worker, so the local checkout can be
        // behind the branch — and a commit whose parent is not the branch head
        // is rejected with "Update is not a fast forward".
        var head = await RemoteHeadAsync(context);
        if (head is null)
            return ProviderResult<string>.Work("not_found", "could not resolve the branch head");

        var newContent = current.Content.TrimEnd() + "\n\n# spike2-r16: edited mid-run\n";

        var blob = await CreateBlobAsync(context, newContent);
        if (!blob.IsOk)
            return ProviderResult<string>.Fail(blob.Error!);

        var tree = await CreateTreeAsync(context, head, "feature-flow.yaml", blob.Value!);
        if (!tree.IsOk)
            return ProviderResult<string>.Fail(tree.Error!);

        var commit = await CreateCommitAsync(
            context, head, tree.Value!, "spike2: edit feature-flow.yaml mid-run (R16)");
        if (!commit.IsOk)
            return ProviderResult<string>.Fail(commit.Error!);

        var moved = await MoveBranchAsync(context, commit.Value!);
        if (!moved.IsOk)
            return ProviderResult<string>.Fail(moved.Error!);

        return ProviderResult<string>.Ok(commit.Value!);
    }

    /// <summary>
    /// The branch head as the remote has it. The local checkout can be behind,
    /// because the run's commits are pushed by the worker.
    /// </summary>
    private static async Task<string?> RemoteHeadAsync(RunContext context)
    {
        var credential = await context.Broker.IssueAsync(
            Actor.Engine("r16-check"),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(
                $"{RunContext.Owner}/{RunContext.Repository}", "contents:read").Text,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        if (!credential.IsOk)
            return null;

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.github.com/repos/{RunContext.Owner}/{RunContext.Repository}" +
            $"/git/ref/heads/{Uri.EscapeDataString(context.Branch)}");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Value!.Secret);
        request.Headers.UserAgent.ParseAdd("feature-flow-spike2");
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var body = await response.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("object").GetProperty("sha").GetString();
    }

    private static async Task<string?> ResolveHeadAsync(RunContext context)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add("HEAD");

        using var git = System.Diagnostics.Process.Start(startInfo);
        if (git is null)
            return null;

        var sha = await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();
        return git.ExitCode == 0 ? sha.Trim() : null;
    }

    /// <summary>
    /// The code host provider exposes no commit-creation method — it is not part
    /// of <see cref="ICodeHostProvider"/> — so the edit goes through the same
    /// GitHub REST API the provider uses, with a credential from the broker.
    /// </summary>
    private static async Task<ProviderResult<string>> CreateBlobAsync(
        RunContext context, string content)
    {
        var credential = await context.Broker.IssueAsync(
            Actor.Engine("r16-check"),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(
                $"{RunContext.Owner}/{RunContext.Repository}", "contents:write").Text,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        if (!credential.IsOk)
            return ProviderResult<string>.Fail(credential.Error!);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{RunContext.Owner}/{RunContext.Repository}/git/blobs");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Value!.Secret);
        request.Headers.UserAgent.ParseAdd("feature-flow-spike2");
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                content,
                encoding = "utf-8",
            }),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return ProviderResult<string>.Infrastructure("server_error", detail: body);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return ProviderResult<string>.Ok(doc.RootElement.GetProperty("sha").GetString()!);
    }

    private static async Task<ProviderResult<string>> CreateTreeAsync(
        RunContext context, string baseSha, string path, string blobSha)
    {
        var credential = await context.Broker.IssueAsync(
            Actor.Engine("r16-check"),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(
                $"{RunContext.Owner}/{RunContext.Repository}", "contents:write").Text,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        if (!credential.IsOk)
            return ProviderResult<string>.Fail(credential.Error!);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{RunContext.Owner}/{RunContext.Repository}/git/trees");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Value!.Secret);
        request.Headers.UserAgent.ParseAdd("feature-flow-spike2");
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                base_tree = baseSha,
                tree = new[] { new { path, mode = "100644", type = "blob", sha = blobSha } },
            }),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return ProviderResult<string>.Infrastructure("server_error", detail: body);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return ProviderResult<string>.Ok(doc.RootElement.GetProperty("sha").GetString()!);
    }

    private static async Task<ProviderResult<string>> CreateCommitAsync(
        RunContext context, string parent, string tree, string message)
    {
        var credential = await context.Broker.IssueAsync(
            Actor.Engine("r16-check"),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(
                $"{RunContext.Owner}/{RunContext.Repository}", "contents:write").Text,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        if (!credential.IsOk)
            return ProviderResult<string>.Fail(credential.Error!);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://api.github.com/repos/{RunContext.Owner}/{RunContext.Repository}/git/commits");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Value!.Secret);
        request.Headers.UserAgent.ParseAdd("feature-flow-spike2");
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new
            {
                message,
                tree,
                parents = new[] { parent },
            }),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return ProviderResult<string>.Infrastructure("server_error", detail: body);

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return ProviderResult<string>.Ok(doc.RootElement.GetProperty("sha").GetString()!);
    }

    private static async Task<ProviderResult<string>> MoveBranchAsync(
        RunContext context, string commitSha)
    {
        var credential = await context.Broker.IssueAsync(
            Actor.Engine("r16-check"),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(
                $"{RunContext.Owner}/{RunContext.Repository}", "contents:write").Text,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        if (!credential.IsOk)
            return ProviderResult<string>.Fail(credential.Error!);

        using var http = new HttpClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Patch,
            $"https://api.github.com/repos/{RunContext.Owner}/{RunContext.Repository}" +
            $"/git/refs/heads/{Uri.EscapeDataString(context.Branch)}");
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", credential.Value!.Secret);
        request.Headers.UserAgent.ParseAdd("feature-flow-spike2");
        request.Headers.Accept.Add(
            new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new { sha = commitSha, force = false }),
            System.Text.Encoding.UTF8,
            "application/json");

        var response = await http.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            return ProviderResult<string>.Infrastructure("server_error", detail: body);

        return ProviderResult<string>.Ok(commitSha);
    }
}
