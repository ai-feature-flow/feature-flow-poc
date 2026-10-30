using System.Diagnostics;
using System.Text.Json;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// Spike 3 deltas to the real worker (docs/17 7.1):
/// N3 <c>scm.create_branch</c> and <c>scm.merge_base_forward</c> as steps, the
/// latter publishing <c>branch_changed</c>; N4 syncing the checkout to the
/// remote head at every step start, because the forward merge happens on the
/// server (docs/01 5.1.2).
/// </summary>
public sealed partial class RealActivities
{
    /// <summary>N4. Off by default so Spike 2's behaviour is unchanged.</summary>
    public static bool SyncWorktree;

    private static readonly JsonSerializerOptions EvidenceJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private void Evidence(string kind, object record)
    {
        var path = Path.Combine(Context.RecordDirectory, $"{Context.RunId}.{kind}.jsonl");
        lock (EvidenceJson)
        {
            File.AppendAllText(path, JsonSerializer.Serialize(record, EvidenceJson) + Environment.NewLine);
        }
    }

    // ------------------------------------------------------------------ N4

    /// <summary>
    /// Fetch, point the integration branch's local ref at the remote, and put the
    /// working branch at its remote head unless the local branch is strictly
    /// ahead (an agent commit not yet pushed, e.g. after a worker crash).
    /// </summary>
    public async Task SyncWorktreeAsync(string stepId)
    {
        if (!SyncWorktree)
            return;

        await GitAsync("fetch", "--prune", "--quiet", "origin");

        var integrationRemote = await GitOutAsync("rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{Context.IntegrationBranch}");
        var current = (await GitOutAsync("rev-parse", "--abbrev-ref", "HEAD"))?.Trim();

        if (integrationRemote is not null && current != Context.IntegrationBranch)
            await GitAsync("update-ref", $"refs/heads/{Context.IntegrationBranch}", integrationRemote.Trim());

        var remote = (await GitOutAsync("rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{Context.Branch}"))?.Trim();
        if (remote is null)
        {
            Console.WriteLine($"SYNC {stepId}: origin/{Context.Branch} does not exist yet; checkout left as is");
            return;
        }

        var local = (await GitOutAsync("rev-parse", "--verify", "--quiet", $"refs/heads/{Context.Branch}"))?.Trim();
        var keepLocal = local is not null && local != remote &&
                        await GitAsync("merge-base", "--is-ancestor", remote, local) == 0;

        if (keepLocal)
        {
            await GitAsync("checkout", "-f", Context.Branch);
            Console.WriteLine($"SYNC {stepId}: {Context.Branch} local {Short(local)} is ahead of origin {Short(remote)}; kept");
        }
        else
        {
            await GitAsync("checkout", "-f", "-B", Context.Branch, $"refs/remotes/origin/{Context.Branch}");
            Console.WriteLine($"SYNC {stepId}: {Context.Branch} at origin {Short(remote)}");
        }

        await GitAsync("clean", "-fdq");
    }

    private async Task<string?> RemoteHeadAsync() =>
        (await GitOutAsync("rev-parse", "--verify", "--quiet", $"refs/remotes/origin/{Context.Branch}"))?.Trim();

    private static string Short(string? sha) => sha is null ? "(none)" : sha[..Math.Min(7, sha.Length)];

    private async Task<int> GitAsync(params string[] args)
    {
        var (code, _) = await RunGitAsync(args);
        return code;
    }

    private async Task<string?> GitOutAsync(params string[] args)
    {
        var (code, output) = await RunGitAsync(args);
        return code == 0 ? output : null;
    }

    private async Task<(int Code, string Output)> RunGitAsync(string[] args)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            WorkingDirectory = Context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var git = Process.Start(startInfo)!;
        var output = await git.StandardOutput.ReadToEndAsync();
        await git.StandardError.ReadToEndAsync();
        await git.WaitForExitAsync();
        return (git.ExitCode, output);
    }

    // ------------------------------------------------------------------ N3

    private async Task<AgentRunResult> CreateBranchAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var key = Context.IdempotencyKey(step.Id, Context.Branch, Context.IntegrationBranch);
        var created = await Context.CodeHost.CreateBranchAsync(
            Context.CodeHostRepo, Context.Branch, Context.IntegrationBranch, key, CancellationToken.None);

        if (!created.IsOk)
            return Classified(step, startedAt, null, created.Error!);

        Evidence("branches", new
        {
            at = DateTimeOffset.UtcNow, step = step.Id, branch = Context.Branch,
            from = Context.IntegrationBranch, sha = created.Value!.HeadSha,
        });
        Console.WriteLine($"STEP {step.Id}: created {Context.Branch} from {Context.IntegrationBranch} at {created.Value!.HeadSha}");

        // Make the new branch the checkout before the next step reads it.
        await SyncWorktreeAsync(step.Id);
        return new AgentRunResult(Outcomes.Completed, LastCommit: created.Value.HeadSha);
    }

    /// <summary>
    /// docs/06 6.2 and J10: <c>branch</c> is the integration branch (merged
    /// from), <c>into</c> the ticket branch (merged into), as in the provider.
    /// A 409 is <c>Work/conflict</c> and routes as <c>blocked</c>.
    /// </summary>
    private async Task<AgentRunResult> MergeBaseForwardStepAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var integrationHead = (await GitOutAsync("rev-parse", $"refs/remotes/origin/{Context.IntegrationBranch}"))?.Trim();
        var ticketBefore = await RemoteHeadAsync();

        var merged = await Context.CodeHost.MergeBaseForwardAsync(
            Context.CodeHostRepo, Context.IntegrationBranch, Context.Branch, CancellationToken.None);

        if (!merged.IsOk)
        {
            var error = merged.Error!;
            var conflict = error.Class == ErrorClass.Work && error.Reason == "conflict";
            List<string>? files = null;
            if (conflict && integrationHead is not null && ticketBefore is not null)
            {
                // merge-tree exits 1 on conflict, printing the tree id then the conflicted paths.
                var (_, tree) = await RunGitAsync(new[] { "merge-tree", "--write-tree", "--name-only", "--no-messages", ticketBefore, integrationHead });
                files = tree.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList();
            }

            Evidence("forward", new
            {
                at = DateTimeOffset.UtcNow, step = step.Id, from = Context.IntegrationBranch, into = Context.Branch,
                integrationHead, ticketBefore, ticketAfter = ticketBefore, outcome = conflict ? "blocked" : "error",
                reason = $"{error.Class}/{error.Reason}", conflictedFiles = files,
            });
            Console.WriteLine($"STEP {step.Id}: forward merge {Context.IntegrationBranch} -> {Context.Branch}: " +
                              $"{error.Class}/{error.Reason} files=[{string.Join(", ", files ?? new())}]");

            if (conflict)
                return new AgentRunResult(Outcomes.Blocked, LastCommit: ticketBefore, ErrorReason: "Work/conflict");
            return Classified(step, startedAt, null, error);
        }

        var value = merged.Value!;
        Evidence("forward", new
        {
            at = DateTimeOffset.UtcNow, step = step.Id, from = Context.IntegrationBranch, into = Context.Branch,
            integrationHead, ticketBefore, ticketAfter = value.NewHeadSha, outcome = "completed",
            branchChanged = value.BranchChanged,
        });
        Console.WriteLine($"STEP {step.Id}: forward merge {Context.IntegrationBranch} -> {Context.Branch}: " +
                          $"branchChanged={value.BranchChanged} {Short(ticketBefore)} -> {Short(value.NewHeadSha)}");

        return new AgentRunResult(
            Outcomes.Completed,
            LastCommit: value.NewHeadSha,
            Outputs: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["branch_changed"] = value.BranchChanged ? "true" : "false",
            });
    }
}

/// <summary>Everything a review is given, assembled by one code path (docs/17 5.2).</summary>
public sealed record ReviewInputs(
    string BaseRef,
    string? BaseSha,
    string? HeadSha,
    string? DiffHash,
    string DiffText,
    List<Finding> Prior,
    List<string> Declined,
    string? Specification);

public sealed partial class RealActivities
{
    /// <summary>
    /// The review step's inputs. The gated <c>review.run</c> and ff-presubmit both
    /// call this, so the session path reaches the validator with the inputs the
    /// gated step would use. The overrides exist only for C5's sanity check.
    /// </summary>
    public async Task<ReviewInputs> BuildReviewInputsAsync(Step step, string? baseOverride = null, bool omitPrior = false)
    {
        var baseRef = baseOverride ?? Context.IntegrationBranch;
        var head = await HeadShaAsync();
        var baseSha = (await GitOutAsync("rev-parse", baseRef))?.Trim();
        var diff = await Context.CodeHost.GetDiffAsync(
            Context.CodeHostRepo, baseRef, Context.Branch, CancellationToken.None);
        var diffText = await DiffTextAsync(baseRef, Context.Branch);
        var prior = omitPrior ? new List<Finding>() : Findings.PriorFor(Context, step.Id);
        var declined = Findings.DeclinedIds(Context);
        var spec = Prompt.ReviewSpecification?.Invoke(step, Context) ?? Prompt.TicketSummary;
        return new ReviewInputs(baseRef, baseSha, head, diff.IsOk ? diff.Value!.ContentHash : null,
            diffText, prior, declined, spec);
    }

    public string RenderReviewHash(ReviewInputs inputs) =>
        Context.Reviewer.Render(inputs.DiffHash, inputs.DiffText, inputs.Prior, inputs.Declined, inputs.Specification).Hash;

    private void RecordReviewEvidence(Step step, ReviewInputs inputs, ProviderResult<ReviewVerdict> reviewed)
    {
        var n = Directory.GetFiles(Context.RecordDirectory, $"{Context.RunId}.review-*.diff").Length + 1;
        var diffFile = $"{Context.RunId}.review-{n:D2}.diff";
        File.WriteAllText(Path.Combine(Context.RecordDirectory, diffFile), inputs.DiffText);
        var verdict = reviewed.IsOk ? reviewed.Value : null;
        Evidence("reviews", new
        {
            at = DateTimeOffset.UtcNow,
            step = step.Id,
            scope = Inputs.Text(step, "scope"),
            baseRef = inputs.BaseRef,
            baseSha = inputs.BaseSha,
            head = inputs.HeadSha,
            diffHash = inputs.DiffHash,
            diffFile,
            diffChars = inputs.DiffText.Length,
            diffLimit = ReviewerModel.DiffLimit,
            truncated = inputs.DiffText.Length > ReviewerModel.DiffLimit,
            specification = inputs.Specification,
            prior = inputs.Prior.Select(f => f.Id).ToList(),
            inputHash = verdict?.InputHash ?? RenderReviewHash(inputs),
            outcome = verdict?.Outcome ?? $"error:{reviewed.Error?.Reason}",
            model = verdict?.Model,
            costUsd = verdict?.CostUsd,
            findings = verdict?.Findings,
        });
    }
}
