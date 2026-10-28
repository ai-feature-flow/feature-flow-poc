using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Providers;
using FeatureFlow.Spike.RealWorker;

namespace FeatureFlow.Spike.RealHarness;

/// <summary>
/// Check K4, part two: the decline path. A decline reason is posted on the PR,
/// and the finding is not re-raised on the next review.
///
/// <para>
/// docs/06 9.1: a declined finding stays declined until the code its fingerprint
/// points at changes. It never restarts the review loop.
/// </para>
/// </summary>
public static class CheckK4Decline
{
    public static async Task<int> RunAsync(RunContext context, Action<string> say)
    {
        say("## K4: the decline path (reason posted, finding not re-raised)");
        say("");

        var failures = 0;

        var finding = new Finding(
            Id: "f-decline-1",
            Rule: "merge-bar/magic-number",
            Path: "src/FeatureFlow.Example.Api/WidgetStore.cs",
            Symbol: "WidgetStore.Add",
            Snippet: "_nextId++",
            Blocking: true);

        Findings.Seed(context, finding);
        say($"  seeded finding: {finding.Id} ({finding.Rule})");

        // The finding is open before the decline.
        var openBefore = Findings.Open(context);
        say($"  open before decline: {openBefore.Count} " +
            $"({string.Join(", ", openBefore.Select(f => f.Id))})");
        say("");

        // Post the decline reason on a PR. The check opens its own PR when the
        // run's PR is already merged or closed, because a decline comment cannot
        // be posted on a closed PR — GitHub answers 422. The comment is real
        // either way; only which PR carries it differs.
        var prNumber = await OpenPrForDeclineAsync(context, say);
        if (prNumber is null)
        {
            say("  NOT EVIDENCED: no pull request could be opened for the decline, so the");
            say("  decline reason cannot be posted on a real PR.");
            say("");
            say("  **NOT EVIDENCED**");
            say("");
            return 1;
        }

        var reason = "accepted: the id sequence is internal to the store and is not part of the API contract";

        var comment = await context.CodeHost.AddReviewCommentAsync(
            new FeatureFlow.Spike.CodeHost.PullRequestRef(context.CodeHostRepo, prNumber.Value),
            new FeatureFlow.Spike.CodeHost.ReviewComment(
                Body: $"Declined: {finding.Rule}\n\n{reason}",
                Marker: FeatureFlow.Spike.CodeHost.Marker.Hidden(
                    context.IdempotencyKey("decline", finding.Id))),
            CancellationToken.None);

        if (!comment.IsOk)
        {
            say($"  FAIL: the decline comment was not posted: " +
                $"{comment.Error!.Class}/{comment.Error.Reason} {comment.Error.Detail}");
            return 1;
        }

        say($"  decline posted on PR #{prNumber}: {comment.Value!.Url ?? comment.Value.Id}");
        say($"  reason: {reason}");
        say("");

        // Record the decline.
        Findings.Decline(context, finding.Id, reason);

        var declined = Findings.IsDeclined(context, finding.Id);
        say($"  finding recorded as declined: {declined}");
        if (!declined) failures++;

        // The next review must not re-raise it.
        var prior = Findings.PriorFor(context, "review");
        var reRaised = prior.Any(f => f.Id == finding.Id);

        say($"  next review's prior findings: {prior.Count} " +
            $"({string.Join(", ", prior.Select(f => f.Id))})");
        say($"  the declined finding was re-raised: {reRaised}  (must be false)");
        if (reRaised) failures++;

        // And the verdict reflects it: the declined finding is not counted. The
        // check seeds only one finding of its own, so it declines the others the
        // sibling check left open before asserting on the verdict — otherwise it
        // would be asserting on another check's state.
        foreach (var other in Findings.Open(context).Where(f => f.Id != finding.Id).ToList())
        {
            Findings.Decline(context, other.Id, "declined by the K4 decline-path check");
        }

        var verdict = Findings.Verdict("decline-check", Findings.PriorFor(context, "review"), "review");
        say($"  verdict with the finding declined: {verdict.Outcome} " +
            $"(findings={verdict.Findings.Count})");

        var notOpen = verdict.Outcome != Outcomes.FindingsOpen;
        say($"  verdict is not findings_open: {notOpen}");
        if (!notOpen) failures++;

        say("");
        say($"  **{(failures == 0 ? "PASS" : $"FAIL ({failures})")}**");
        say("");
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Returns a PR the decline can be posted on: the run's own PR when it is
    /// still open, otherwise a fresh one opened for the check.
    /// </summary>
    private static async Task<int?> OpenPrForDeclineAsync(RunContext context, Action<string> say)
    {
        var existing = Findings.LastPrNumber(context);
        if (existing is not null)
        {
            var state = await context.CodeHost.GetPullRequestAsync(
                new FeatureFlow.Spike.CodeHost.PullRequestRef(context.CodeHostRepo, existing.Value),
                CancellationToken.None);

            if (state.IsOk && string.Equals(state.Value!.State, "open", StringComparison.OrdinalIgnoreCase))
            {
                say($"  using the run's own PR #{existing} (still open)");
                return existing;
            }

            say($"  the run's PR #{existing} is " +
                $"{(state.IsOk ? state.Value!.State : "unreadable")}; opening one for the decline");
        }

        var key = context.IdempotencyKey("decline-pr", context.Branch);

        // A pull request needs commits between head and base. The run's branch
        // has already been merged, so it has none — the check makes one small
        // commit on a fresh branch so the decline has a real PR to land on.
        var declineBranch = $"ff/ticket/{context.RunId}-decline-{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        var opened = await OpenWithCommitAsync(context, declineBranch, key, say);

        if (opened is null)
            return null;

        say($"  opened PR #{opened.Value} on {declineBranch} for the decline");
        return opened.Value;
    }

    /// <summary>
    /// Creates a branch with one commit and opens a PR from it, so the decline
    /// has a real pull request to be posted on.
    /// </summary>
    private static async Task<int?> OpenWithCommitAsync(
        RunContext context, string branch, string key, Action<string> say)
    {
        var baseSha = await ResolveRefAsync(context, context.IntegrationBranch);
        if (baseSha is null)
        {
            say("  could not resolve the base branch head");
            return null;
        }

        var created = await context.CodeHost.CreateBranchAsync(
            context.CodeHostRepo, branch, context.IntegrationBranch,
            context.IdempotencyKey("decline-branch", branch), CancellationToken.None);

        if (!created.IsOk)
        {
            say($"  could not create {branch}: {created.Error!.Class}/{created.Error.Reason}");
            return null;
        }

        // One real commit on the branch, through the code host API.
        var committed = await CommitAsync(context, branch, "docs/decline-check.md",
            "# Decline path check\n\nThis commit exists so the K4 decline-path check has a\n" +
            "real pull request to post a decline reason on.\n");

        if (!committed.IsOk)
        {
            say($"  could not commit on {branch}: {committed.Error!.Class}/{committed.Error.Reason}");
            return null;
        }

        var opened = await context.CodeHost.OpenPullRequestAsync(
            context.CodeHostRepo,
            new FeatureFlow.Spike.CodeHost.PullRequestSpec(
                Title: $"{context.TicketId}: decline path check",
                Head: branch,
                Base: context.IntegrationBranch,
                Body: "Opened by the K4 decline-path check, so the decline reason has a real " +
                      "pull request to be posted on.\n"),
            key,
            CancellationToken.None);

        if (!opened.IsOk)
        {
            say($"  could not open a PR: {opened.Error!.Class}/{opened.Error.Reason} " +
                $"{opened.Error.Detail}");
            return null;
        }

        DeclineBranches.Add(branch);
        return opened.Value!.Number;
    }

    /// <summary>Branches this check created, so cleanup can remove them.</summary>
    public static readonly List<string> DeclineBranches = new();

    private static async Task<string?> ResolveRefAsync(RunContext context, string reference)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("rev-parse");
        startInfo.ArgumentList.Add(reference);

        using var git = System.Diagnostics.Process.Start(startInfo);
        if (git is null)
            return null;

        var sha = await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();
        return git.ExitCode == 0 ? sha.Trim() : null;
    }

    /// <summary>
    /// Creates one commit on a branch through the GitHub git API. The code host
    /// provider exposes no commit method — it is not part of
    /// <see cref="FeatureFlow.Spike.CodeHost.ICodeHostProvider"/> — so this uses
    /// the same REST API the provider uses, with a credential from the broker.
    /// </summary>
    private static async Task<ProviderResult<string>> CommitAsync(
        RunContext context, string branch, string path, string content)
    {
        var credential = await context.Broker.IssueAsync(
            Actor.Engine("decline-check"),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(
                $"{RunContext.Owner}/{RunContext.Repository}",
                "contents:write", "contents:read").Text,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        if (!credential.IsOk)
            return ProviderResult<string>.Fail(credential.Error!);

        var token = credential.Value!.Secret;
        using var http = new HttpClient();

        async Task<ProviderResult<string>> Post(string url, object payload)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("feature-flow-spike2");
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(payload),
                System.Text.Encoding.UTF8, "application/json");

            var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                return ProviderResult<string>.Infrastructure("server_error", detail: body);

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            return ProviderResult<string>.Ok(doc.RootElement.GetProperty("sha").GetString()!);
        }

        var root = $"https://api.github.com/repos/{RunContext.Owner}/{RunContext.Repository}";

        var head = await Post($"{root}/git/ref/heads/{Uri.EscapeDataString(branch)}", new { }) switch
        {
            { IsOk: true } ok => ok.Value,
            _ => null,
        };

        // Resolve the branch head properly: the ref endpoint is a GET.
        using (var request = new HttpRequestMessage(HttpMethod.Get,
                   $"{root}/git/ref/heads/{Uri.EscapeDataString(branch)}"))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("feature-flow-spike2");
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

            var response = await http.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                return ProviderResult<string>.Infrastructure("server_error", detail: body);

            using var doc = System.Text.Json.JsonDocument.Parse(body);
            head = doc.RootElement.GetProperty("object").GetProperty("sha").GetString();
        }

        if (head is null)
            return ProviderResult<string>.Infrastructure("server_error", detail: "no branch head");

        var blob = await Post($"{root}/git/blobs", new { content, encoding = "utf-8" });
        if (!blob.IsOk)
            return blob;

        var tree = await Post($"{root}/git/trees", new
        {
            base_tree = head,
            tree = new[] { new { path, mode = "100644", type = "blob", sha = blob.Value! } },
        });
        if (!tree.IsOk)
            return tree;

        var commit = await Post($"{root}/git/commits", new
        {
            message = $"{context.TicketId}: decline path check",
            tree = tree.Value!,
            parents = new[] { head },
        });
        if (!commit.IsOk)
            return commit;

        using (var request = new HttpRequestMessage(HttpMethod.Patch,
                   $"{root}/git/refs/heads/{Uri.EscapeDataString(branch)}"))
        {
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
            request.Headers.UserAgent.ParseAdd("feature-flow-spike2");
            request.Headers.Accept.Add(
                new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { sha = commit.Value!, force = false }),
                System.Text.Encoding.UTF8, "application/json");

            var response = await http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                return ProviderResult<string>.Infrastructure("server_error", detail: body);
            }
        }

        return ProviderResult<string>.Ok(commit.Value!);
    }
}
