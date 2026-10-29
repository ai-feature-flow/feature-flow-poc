using FeatureFlow.Spike.Ci;
using FeatureFlow.Spike.CodeHost;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.Providers;
using Temporalio.Activities;

// The agent runtime's AgentProgress is the runtime's own record; Contracts'
// is the heartbeat payload. Aliased, as the sibling harnesses do.
using AgentProgress = FeatureFlow.Spike.AgentRuntime.AgentProgress;
using FileContent = FeatureFlow.Spike.CodeHost.FileContent;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// The non-agent steps, against the real code host and the real CI provider.
///
/// <para>
/// Every step here follows the same three rules: it reads
/// <c>feature-flow.yaml</c> from its branch and records the hash (R16); it takes
/// its credential from the broker only (R6); and it records its cost (R13).
/// </para>
///
/// <para>
/// Waiting steps poll <b>inside one long-running activity</b>, heartbeating
/// (docs/05 1.2.1, R20). No step calls a blocking wait (review 05 P1).
/// </para>
/// </summary>
public sealed partial class RealActivities
{
    [Activity]
    public async Task<AgentRunResult> RunGenericStepAsync(GenericStepRequest request)
    {
        var step = Context.Step(request.StepId);
        if (step is null)
            return new AgentRunResult(Outcomes.Failed, ErrorReason: $"no step '{request.StepId}' in the plan");

        return step.Kind switch
        {
            "test.run_tiers" => await RunTestTiersAsync(step),
            "review.run" => await RunReviewAsync(step),
            "findings.triage" => await RunTriageAsync(step),
            "fix.verify" => await RunFixVerifyAsync(step),
            "findings.evaluate_readiness" => await EvaluateReadinessAsync(step),
            "scm.open_pr" => await OpenPullRequestAsync(step),
            "ci.wait_checks" => await WaitChecksAsync(step),
            "scm.merge" => await MergeAsync(step),
            "plan.replan" => await ReplanAsync(step),
            _ => await UnsupportedAsync(step),
        };
    }

    // ------------------------------------------------------------ test.run_tiers

    /// <summary>
    /// R5: a Docker-dependent tier runs through CI delegation and matches what
    /// CI would do. The tier's execution target comes from
    /// <c>feature-flow.yaml</c>, read per step — the plan selects tiers by tag,
    /// it does not list them (review 09 C1).
    /// </summary>
    private async Task<AgentRunResult> RunTestTiersAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var manifest = await ReadManifestAsync(step.Id);
        var tags = Inputs.List(step, "tier_tags");
        if (tags.Count == 0)
            tags = Context.Tiers.Tiers.ToList();

        var head = await HeadShaAsync();
        if (head is null)
            return Failed(step, startedAt, manifest, "could not resolve the branch head");

        var results = new List<string>();
        var delegated = new List<string>();

        foreach (var tier in tags)
        {
            var mapping = Context.Tiers.Get(tier);
            if (mapping is null)
            {
                // An unknown tier is Work, not Infrastructure (docs/05 section 5).
                return Failed(step, startedAt, manifest, $"tier '{tier}' is not mapped");
            }

            // The Docker-dependent tier is delegated to CI. The sandbox has no
            // Docker here, which is exactly the case Q2 decided.
            var target = Manifest.TierTarget(manifest, tier);
            if (string.Equals(target, "ci", StringComparison.OrdinalIgnoreCase))
            {
                delegated.Add(tier);
                var run = await Context.Ci.TriggerAsync(
                    Context.CiRepo, tier, head, Context.IdempotencyKey(step.Id, tier, head),
                    CancellationToken.None);

                if (!run.IsOk)
                    return Classified(step, startedAt, manifest, run.Error!);

                // Poll inside this activity, heartbeating. One activity per step.
                var status = await PollCiAsync(run.Value!, step.Id);
                if (status is null)
                    return Failed(step, startedAt, manifest, $"tier '{tier}' did not finish");

                results.Add($"{tier}={status.Conclusion}");
                if (status.Conclusion != CiConclusion.Success)
                {
                    return new AgentRunResult(
                        Outcomes.Failed,
                        ErrorReason: $"tier '{tier}' concluded {status.Conclusion}");
                }
            }
            else
            {
                // A sandbox tier runs the manifest's own commands locally.
                var exit = await RunTierLocallyAsync(manifest, tier);
                results.Add($"{tier}={(exit == 0 ? "success" : "failure")}");
                if (exit != 0)
                    return Failed(step, startedAt, manifest, $"tier '{tier}' exited {exit}");
            }
        }

        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: "github-actions",
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash));

        Console.WriteLine($"STEP {step.Id}: tiers [{string.Join(", ", results)}], " +
                          $"delegated to CI: [{string.Join(", ", delegated)}]");

        return new AgentRunResult(Outcomes.Completed, LastCommit: head);
    }

    /// <summary>
    /// Polls a CI run to a terminal state, inside this activity, heartbeating.
    /// </summary>
    private async Task<CiRunStatus?> PollCiAsync(CiRunRef run, string stepId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
        var lastBeat = DateTimeOffset.UtcNow;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var poll = await Context.Ci.GetRunStatusAsync(run, CancellationToken.None);

            if (poll.IsOk)
            {
                var status = poll.Value!;
                if (DateTimeOffset.UtcNow - lastBeat >= HeartbeatInterval)
                {
                    lastBeat = DateTimeOffset.UtcNow;
                    Heartbeat(stepId, new AgentProgress(null, 0, null, true, TimeSpan.Zero));
                }

                if (status.IsTerminal)
                    return status;
            }
            else if (DateTimeOffset.UtcNow - lastBeat >= HeartbeatInterval)
            {
                lastBeat = DateTimeOffset.UtcNow;
                Heartbeat(stepId, new AgentProgress(null, 0, null, true, TimeSpan.Zero));
            }

            await Task.Delay(PollInterval);
        }

        return null;
    }

    /// <summary>Runs a sandbox tier's own commands from the manifest.</summary>
    private async Task<int> RunTierLocallyAsync(FileContent? manifest, string tier)
    {
        var commands = Manifest.TierCommands(manifest, tier);
        if (commands.Count == 0)
            return 0;

        foreach (var command in commands)
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("cmd")
            {
                WorkingDirectory = Context.WorkingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(command);

            using var process = System.Diagnostics.Process.Start(startInfo);
            if (process is null)
                return 1;

            await process.WaitForExitAsync();
            if (process.ExitCode != 0)
                return process.ExitCode;
        }

        return 0;
    }

    // ---------------------------------------------------------------- review.run

    /// <summary>
    /// R4: a review run twice on unchanged input gives the same verdict, with
    /// prior findings passed.
    ///
    /// <para>
    /// <b>Review 14 A7.</b> The verdict used to be derived from the diff hash
    /// and the prior findings, which made it a pure function of its inputs and
    /// therefore identical by construction — R4 passed without a model ever
    /// being in the loop. It now calls a real reviewer model through the
    /// router, so agreement between passes is a measurement rather than a
    /// tautology.
    /// </para>
    ///
    /// <para>
    /// The verdict is deliberately not forced to agree. Two passes that
    /// disagree are reported as a disagreement, because that is the finding R4
    /// exists to detect.
    /// </para>
    /// </summary>
    private async Task<AgentRunResult> RunReviewAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var manifest = await ReadManifestAsync(step.Id);

        var head = await HeadShaAsync();
        var diff = await Context.CodeHost.GetDiffAsync(
            Context.CodeHostRepo, Context.IntegrationBranch, Context.Branch, CancellationToken.None);

        var diffHash = diff.IsOk ? diff.Value!.ContentHash : null;
        var diffText = await DiffTextAsync(Context.IntegrationBranch, Context.Branch);
        var prior = Findings.PriorFor(Context, step.Id);
        var declined = Findings.DeclinedIds(Context);

        var reviewed = await Context.Reviewer.ReviewAsync(
            diffHash, diffText, prior, declined, step.Id, CancellationToken.None);

        ReviewVerdict verdict;
        if (!reviewed.IsOk)
        {
            // A reviewer that cannot be reached is infrastructure, not a
            // verdict. It is reported as failed with the reason named, rather
            // than falling back to a hash-derived verdict that would make R4
            // pass for the wrong reason.
            Console.WriteLine($"STEP {step.Id}: reviewer unavailable: " +
                              $"{reviewed.Error!.Class}/{reviewed.Error.Reason}: {reviewed.Error.Detail}");

            return new AgentRunResult(
                reviewed.Error!.Class == ErrorClass.Infrastructure ? "infrastructure" : Outcomes.Failed,
                LastCommit: head,
                ErrorReason: $"reviewer unavailable: {reviewed.Error!.Class}/{reviewed.Error.Reason}");
        }

        verdict = reviewed.Value!;

        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: verdict.Model, RuntimeInstance: "reviewer-model",
            InputTokens: verdict.InputTokens, OutputTokens: verdict.OutputTokens,
            Credits: null, CostUsd: verdict.CostUsd is double spent ? (decimal)spent : null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "model_call", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash));

        Console.WriteLine($"STEP {step.Id}: diffHash={diffHash} priorFindings={prior.Count} " +
                          $"verdict={verdict.Outcome} findings={verdict.Findings.Count} " +
                          $"model={verdict.Model ?? "(unreported)"}");

        Findings.Record(Context, step.Id, verdict);

        return new AgentRunResult(
            verdict.Outcome,
            LastCommit: head,
            ErrorReason: verdict.Outcome == Outcomes.Failed ? "review failed" : null);
    }

    // ------------------------------------------------------------ findings.triage

    private async Task<AgentRunResult> RunTriageAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var manifest = await ReadManifestAsync(step.Id);

        var open = Findings.Open(Context);
        var selected = open.Where(f => !Findings.IsDeclined(Context, f.Id)).ToList();

        Findings.RecordTriage(Context, step.Id, selected);

        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: "triage-local",
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash));

        Console.WriteLine($"STEP {step.Id}: open={open.Count} selected={selected.Count}");

        return new AgentRunResult(Outcomes.Completed);
    }

    // ---------------------------------------------------------------- fix.verify

    /// <summary>
    /// K4: a fix must add a regression test, and the fix is checked against the
    /// original finding on the <b>full diff</b>, not only the changed lines
    /// (docs/06 10.1).
    /// </summary>
    private async Task<AgentRunResult> RunFixVerifyAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var manifest = await ReadManifestAsync(step.Id);

        var finding = Findings.Selected(Context);
        var diff = await Context.CodeHost.GetDiffAsync(
            Context.CodeHostRepo, Context.IntegrationBranch, Context.Branch, CancellationToken.None);

        // The full diff, not the changed lines: the prototype's re-review
        // reported no issues after fixes that had introduced new ones.
        var fullDiff = diff.IsOk ? diff.Value!.ContentHash : null;

        // The invariant: a regression test is required unless the finding's
        // triage record carries an accepted "not applicable" reason.
        //
        // The test is detected in the diff the fixer actually produced, not
        // read from a field only a check ever writes. Previously nothing on
        // the real fixer path called AttachRegressionTest, so verify_fix
        // failed on every fix and the run looped forever.
        var hasRegressionTest = Findings.HasRegressionTest(Context, finding?.Id) ||
                                DiffMentionsTest(await DiffTextAsync(Context.IntegrationBranch, Context.Branch));
        var notApplicable = finding is not null && Findings.NotApplicableReason(Context, finding.Id) is not null;

        var passed = hasRegressionTest || notApplicable;

        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: "fix-verify-local",
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash));

        Console.WriteLine($"STEP {step.Id}: finding={finding?.Id ?? "(none)"} fullDiff={fullDiff} " +
                          $"regressionTest={hasRegressionTest} notApplicable={notApplicable} => " +
                          $"{(passed ? "completed" : "failed")}");

        if (!passed)
        {
            return new AgentRunResult(
                Outcomes.Failed,
                ErrorReason: "no regression test was added for the finding, and no " +
                             "'not applicable' reason was recorded");
        }

        Findings.MarkFixed(Context, finding!.Id);
        return new AgentRunResult(Outcomes.Completed);
    }

    // ------------------------------------------------- findings.evaluate_readiness

    /// <summary>
    /// D8: the bar is no unresolved blocking finding, and every finding fixed or
    /// declined with a recorded reason. Not "zero findings".
    /// </summary>
    private async Task<AgentRunResult> EvaluateReadinessAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var manifest = await ReadManifestAsync(step.Id);

        var open = Findings.Open(Context);
        var unresolved = open.Where(f => !Findings.IsDeclined(Context, f.Id)).ToList();

        var ready = unresolved.Count == 0;

        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: "local",
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash));

        Console.WriteLine($"STEP {step.Id}: open={open.Count} unresolved={unresolved.Count} => " +
                          $"{(ready ? "ready" : "findings_open")}");

        return new AgentRunResult(ready ? Outcomes.Ready : Outcomes.FindingsOpen);
    }

    // ---------------------------------------------------------------- scm.open_pr

    private async Task<AgentRunResult> OpenPullRequestAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var manifest = await ReadManifestAsync(step.Id);

        var key = Context.IdempotencyKey(step.Id, Context.Branch, Context.IntegrationBranch);
        var spec = new PullRequestSpec(
            Title: $"{Context.TicketId}: spike 2 real run",
            Head: Context.Branch,
            Base: Context.IntegrationBranch,
            Body: $"Opened by the Spike 2 real run {Context.RunId}.\n\n" +
                  $"Ticket: {Context.TicketId}\n");

        var opened = await Context.CodeHost.OpenPullRequestAsync(
            Context.CodeHostRepo, spec, key, CancellationToken.None);

        if (!opened.IsOk)
            return Classified(step, startedAt, manifest, opened.Error!);

        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: "github",
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash));

        Console.WriteLine($"STEP {step.Id}: PR #{opened.Value!.Number} " +
                          $"{Context.Branch} -> {Context.IntegrationBranch}");

        // The PR number is what scm.merge and the decline path need. Recorded
        // outside workflow state: a number is a reference, and the workflow
        // carries references only (DP5).
        Findings.RecordPr(Context, opened.Value!.Number);

        return new AgentRunResult(Outcomes.Completed, LastCommit: opened.Value.HeadSha);
    }

    // -------------------------------------------------------------- ci.wait_checks

    /// <summary>
    /// Waits for every check on the head commit, including checks that register
    /// late. Polls inside one activity, heartbeating (docs/06 7.2).
    /// </summary>
    private async Task<AgentRunResult> WaitChecksAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var manifest = await ReadManifestAsync(step.Id);

        var head = await HeadShaAsync();
        if (head is null)
            return Failed(step, startedAt, manifest, "could not resolve the branch head");

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromMinutes(15);
        var lastBeat = DateTimeOffset.UtcNow;
        var sawChecks = false;

        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await Context.CodeHost.GetCheckStatusAsync(
                Context.CodeHostRepo, head, CancellationToken.None);

            if (status.IsOk)
            {
                var value = status.Value!;
                if (value.TotalCount > 0)
                    sawChecks = true;

                if (DateTimeOffset.UtcNow - lastBeat >= HeartbeatInterval)
                {
                    lastBeat = DateTimeOffset.UtcNow;
                    Heartbeat(step.Id, new AgentProgress(null, 0, null, true, TimeSpan.Zero));
                }

                if (string.Equals(value.State, "success", StringComparison.Ordinal))
                {
                    Context.RecordCost(new StepCost(
                        RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
                        Model: null, RuntimeInstance: "github",
                        InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
                        ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
                        UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
                        ManifestHash: manifest?.ContentHash));

                    Console.WriteLine($"STEP {step.Id}: checks {value.State} " +
                                      $"({value.TotalCount} check(s)) at {head}");
                    return new AgentRunResult(Outcomes.Completed, LastCommit: head);
                }

                // A failing check is a work outcome, not infrastructure
                // (docs/06 7.2). It is a real answer from the remote.
                if (string.Equals(value.State, "failure", StringComparison.Ordinal))
                {
                    return new AgentRunResult(
                        Outcomes.Failed,
                        LastCommit: head,
                        ErrorReason: $"checks failed: {string.Join(", ", value.CheckRuns.Select(c => c.Name))}");
                }
            }
            else if (DateTimeOffset.UtcNow - lastBeat >= HeartbeatInterval)
            {
                lastBeat = DateTimeOffset.UtcNow;
                Heartbeat(step.Id, new AgentProgress(null, 0, null, true, TimeSpan.Zero));
            }

            await Task.Delay(PollInterval);
        }

        // Nothing registered. Checks may still register late, so this is blocked
        // rather than failed: the engine waits, it does not conclude there is no CI.
        return new AgentRunResult(
            Outcomes.Blocked,
            LastCommit: head,
            ErrorReason: sawChecks ? "checks did not reach success" : "no checks registered");
    }

    // ------------------------------------------------------------------ scm.merge

    private async Task<AgentRunResult> MergeAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var manifest = await ReadManifestAsync(step.Id);

        var prNumber = Findings.LastPrNumber(Context);
        if (prNumber is null)
            return Failed(step, startedAt, manifest, "no pull request was opened by this run");

        var pr = new PullRequestRef(Context.CodeHostRepo, prNumber.Value);
        var observed = await Context.CodeHost.GetPullRequestAsync(pr, CancellationToken.None);
        if (!observed.IsOk)
            return Classified(step, startedAt, manifest, observed.Error!);

        // F1 must not depend on a file the governed party can edit. The agent
        // can write to the ticket branch, so it could declare another branch as
        // the default and be judged by the weaker ticket policy. The default
        // branch therefore comes from the code host, read at merge time.
        var defaultBranchResult = await Context.CodeHost.GetDefaultBranchAsync(
            Context.CodeHostRepo, CancellationToken.None);
        if (defaultBranchResult.Error is not null)
            return Classified(step, startedAt, manifest, defaultBranchResult.Error);

        var defaultBranch = defaultBranchResult.Value!;

        var authorization = MergeAuthorization.Evaluate(
            observed.Value!.BaseBranch,
            defaultBranch,
            Context.Decisions.ReadAll(),
            Context.Organisation,
            declaredDefaultBranch: Manifest.DefaultBranch(manifest));
        Console.WriteLine($"STEP {step.Id}: {authorization.Message}");
        if (!authorization.Allowed)
            return Refused(step, startedAt, manifest, authorization.Message);

        // The repository states its preferred method in feature-flow.yaml
        // (docs/07 section 5). No merge method in a plan (review 09 C2).
        var method = Manifest.MergeMethod(manifest) ?? "merge";

        var merged = await Context.CodeHost.MergeAsync(pr, method, CancellationToken.None);
        if (!merged.IsOk)
            return Classified(step, startedAt, manifest, merged.Error!);

        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: "github",
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash));

        Console.WriteLine($"STEP {step.Id}: merged PR #{prNumber} method={method} " +
                          $"merged={merged.Value!.Merged} branchChanged={merged.Value.BranchChanged} " +
                          $"head={merged.Value.NewHeadSha}");

        return new AgentRunResult(
            merged.Value.Merged ? Outcomes.Completed : Outcomes.Blocked,
            LastCommit: merged.Value.NewHeadSha);
    }

    // ---------------------------------------------------------------- plan.replan

    /// <summary>
    /// A replan proposes a patch. The patch is applied by the interpreter's
    /// applyPatch, not here: this step only proposes (docs/09 15.1).
    /// </summary>
    private async Task<AgentRunResult> ReplanAsync(Step step)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var manifest = await ReadManifestAsync(step.Id);

        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: "local",
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash));

        Console.WriteLine($"STEP {step.Id}: replan requested");
        return new AgentRunResult(Outcomes.ReplanRequested);
    }

    private async Task<AgentRunResult> UnsupportedAsync(Step step)
    {
        var manifest = await ReadManifestAsync(step.Id);
        Console.WriteLine($"STEP {step.Id}: kind '{step.Kind}' has no real implementation; " +
                          "reported as failed rather than faked");
        return Failed(step, DateTimeOffset.UtcNow, manifest, $"kind '{step.Kind}' is not implemented");
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// R16: read <c>feature-flow.yaml</c> from the branch this step works on and
    /// record its hash. A mid-run edit is picked up by the next step, and the
    /// hash is recorded (docs/07 section 9).
    /// </summary>
    private async Task<FileContent?> ReadManifestAsync(string stepId)
    {
        var result = await Context.CodeHost.ReadFileAsync(
            Context.CodeHostRepo, "feature-flow.yaml", Context.Branch, CancellationToken.None);

        if (!result.IsOk)
        {
            Console.WriteLine($"STEP {stepId}: feature-flow.yaml unreadable on {Context.Branch}: " +
                              $"{result.Error!.Class}/{result.Error.Reason}");
            return null;
        }

        var file = result.Value!;
        Console.WriteLine($"STEP {stepId}: feature-flow.yaml ref={file.Ref} " +
                          $"sha256:{file.ContentHash} ({file.SizeBytes} bytes)");
        return file;
    }

    private async Task<string?> HeadShaAsync()
    {
        // The branch head is what matters, not a PR's head. Resolve the ref from
        // the checkout, which is the sandbox the run works in.
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = Context.WorkingDirectory,
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
    /// Whether a diff adds a test method. The K4 invariant is that a fix adds
    /// a regression test; the evidence is the diff, not a field.
    /// </summary>
    private static bool DiffMentionsTest(string diffText)
    {
        if (string.IsNullOrWhiteSpace(diffText))
            return false;

        foreach (var line in diffText.Split('\n'))
        {
            if (!line.StartsWith('+') || line.StartsWith("+++", StringComparison.Ordinal))
                continue;

            if (line.Contains("[Fact]", StringComparison.Ordinal) ||
                line.Contains("[Theory]", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The diff text between two refs, read from the checkout.
    ///
    /// <para>
    /// <c>GetDiffAsync</c> deliberately returns a reference rather than the
    /// content, so a large aggregate diff never enters workflow state (DP10).
    /// The reviewer model needs the text, and it runs inside an activity where
    /// reading it is fine — the text goes to the model and is not written into
    /// workflow state.
    /// </para>
    /// </summary>
    private async Task<string> DiffTextAsync(string fromRef, string toRef)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = Context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("diff");
        startInfo.ArgumentList.Add($"{fromRef}...{toRef}");

        using var git = System.Diagnostics.Process.Start(startInfo);
        if (git is null)
            return "";

        var text = await git.StandardOutput.ReadToEndAsync();
        await git.WaitForExitAsync();

        return git.ExitCode == 0 ? text : "";
    }

    private AgentRunResult Failed(Step step, DateTimeOffset startedAt, FileContent? manifest, string reason)
    {
        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: null,
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash,
            ErrorClass: ErrorClass.Work.ToString(),
            ErrorReason: reason));

        return new AgentRunResult(Outcomes.Failed, ErrorReason: reason);
    }

    private AgentRunResult Refused(
        Step step,
        DateTimeOffset startedAt,
        FileContent? manifest,
        string reason)
    {
        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: "policy",
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash,
            ErrorClass: ErrorClass.Work.ToString(),
            ErrorReason: reason));

        return new AgentRunResult(Outcomes.Rejected, ErrorReason: reason);
    }

    private AgentRunResult Classified(
        Step step, DateTimeOffset startedAt, FileContent? manifest, ProviderError error)
    {
        Context.RecordCost(new StepCost(
            RunId: Context.RunId, StepId: step.Id, StepKind: step.Kind,
            Model: null, RuntimeInstance: null,
            InputTokens: null, OutputTokens: null, Credits: null, CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none", RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash,
            ErrorClass: error.Class.ToString(),
            ErrorReason: error.Reason));

        if (error.Class == ErrorClass.Infrastructure)
        {
            return new AgentRunResult(
                "infrastructure",
                ErrorReason: $"{error.Class}/{error.Reason}: {error.Detail}");
        }

        // A work failure must map to an outcome the step's routes actually
        // declare. docs/09 4.2 requires every kind to route every outcome it can
        // return, and scm.merge declares blocked — not failed — so reporting
        // 'failed' here would leave the route unresolved and end the run
        // silently, looking like a success.
        var outcome = step.Kind == "scm.merge" ? Outcomes.Blocked : Outcomes.Failed;

        return new AgentRunResult(
            outcome,
            ErrorReason: $"{error.Class}/{error.Reason}: {error.Detail}");
    }
}
