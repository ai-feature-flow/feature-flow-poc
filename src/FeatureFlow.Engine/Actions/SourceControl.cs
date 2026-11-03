using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>The source control actions (docs/04 section 7). Every write names the revision it expects (LOG 107).</summary>
static class SourceControl
{
    /// <summary>
    /// <c>scm.catch_up</c> (section 7.1): merges the parent branch into the work branch. <c>updated</c> with <c>changed</c>
    /// false when the work branch already contained the parent (the 204 case); <c>conflict</c> lists the paths and pushes
    /// nothing. A work branch moved under the catch-up is read again, up to three times.
    /// </summary>
    public static async Task<ActionResult> CatchUpAsync(ActionRun run)
    {
        var repo = run.Repository();
        var parent = await run.ParentBranchAsync(repo);
        for (var tries = 1; ; tries++)
        {
            var head = await run.WorkHeadAsync(repo);
            var result = await run.Vcs.CatchUpAsync(repo, "refs/heads/" + parent, run.WorkReference, head,
                $"Merge {parent} into {run.Scope.WorkBranch}\n\nCaught up by Feature Flow for run {run.Call.Step.RunId}.", run.Ct);
            if (result is { Ok: false, Error.Reason: "base_moved" } && tries < 3)
            {
                run.Events.Add($"{run.Scope.WorkBranch} moved during the catch-up; reading it again");
                continue;
            }

            var caught = ActionRun.Must(result, "catch up with the parent branch");
            if (caught.Conflicted)
            {
                run.Events.Add($"catching {run.Scope.WorkBranch} up with {parent} at {EngineActions.Short(caught.ParentRevision)} conflicts in: {string.Join(", ", caught.Conflicts)}");
                return run.Report("conflict");
            }

            run.Events.Add(caught.Changed
                ? $"caught {run.Scope.WorkBranch} up with {parent} at {EngineActions.Short(caught.ParentRevision)}: now {EngineActions.Short(caught.WorkRevision)}"
                : $"{run.Scope.WorkBranch} already contains {parent} at {EngineActions.Short(caught.ParentRevision)}");
            return run.Report("updated", new Dictionary<string, object?> { ["changed"] = caught.Changed });
        }
    }

    /// <summary>
    /// <c>scm.pull_request</c> (section 7.2): opens the work branch's pull request into its parent branch, or updates the
    /// one this run opened. Its key is the run's and the repository's, so a re-entry or retry adopts it.
    /// </summary>
    public static async Task<ActionResult> PullRequestAsync(ActionRun run)
    {
        var repo = run.Repository();
        var parent = await run.ParentBranchAsync(repo);
        await run.WorkHeadAsync(repo);
        var spec = new PullRequestSpec(run.String("title") ?? $"{run.Call.Template()}: {run.Call.Step.RunId}", run.String("body") ?? "", run.Scope.WorkBranch, parent,
            run.Bool("draft", false));
        var pr = ActionRun.Must(await run.CodeHost.OpenPullRequestAsync(repo, spec, $"{run.Call.Step.RunId}/{repo.FullName}/pull_request", run.Ct), "open the pull request");
        run.Events.Add($"pull request {ActionRun.Format(pr)} from {run.Scope.WorkBranch} into {parent}");
        return run.Report("ready", new Dictionary<string, object?> { ["pull_request"] = ActionRun.Format(pr) });
    }

    /// <summary>
    /// <c>scm.merge</c> (section 7.3). Evidence first: with <c>require_tested</c> and <c>require_checks</c> (both default on)
    /// a passing <c>test.run</c> and <c>ci.checks</c> result for the exact head must exist, else <c>checks_failed</c>. Then
    /// F1 again, reading the default branch from the code host at merge time (docs/09 section 3). The engine merges
    /// exactly the tested head through the VCS's expected-revision path, so a parent that moved since the catch-up is
    /// <c>base_moved</c>; a change that already landed (DR18) or a person's merge on the code host is <c>merged</c>.
    /// Where the engine may not merge, or the remote refuses it, the step is an action request: it waits for a person to
    /// merge the pull request (<c>merged</c>) or close it (<c>declined</c>) (docs/04 section 4.5; LOG 123).
    /// </summary>
    public static async Task<ActionResult> MergeAsync(ActionRun run)
    {
        var repo = run.Repository();
        var target = await run.ParentBranchAsync(repo);
        var vcs = run.Vcs;
        var head = ActionRun.Must(await vcs.ResolveAsync(repo, run.WorkReference, run.Ct), "read the work branch")
                   ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no work branch {run.Scope.WorkBranch} to merge");
        await run.EventAsync(ActionRun.StartEvent, $"{repo.FullName} {head}");
        var pr = run.PullRequest(repo);
        var method = run.Bool("squash", false) ? MergeMethods.Squash : MergeMethods.Merge;

        // Idempotency (section 7.3): a person's merge on the code host, or a merge this step already made, is merged.
        if (pr is not null && ActionRun.Must(await run.CodeHost.GetPullRequestAsync(pr, run.Ct), "read the pull request") is { State: PullRequestStates.Merged } merged)
            return await RecordAsync(run, repo, target, method, merged.HeadRevision, null, merged.MergeRevision ?? "",
                merged.MergedBy ?? "unknown", alreadyMerged: true, pr);

        if (await MissingEvidenceAsync(run, repo, head) is { } missing)
        {
            run.Events.Add(missing);
            return run.Report("checks_failed");
        }

        var engineCanMerge = run.Bool("engine_can_merge", true);
        var defaultBranch = ActionRun.Must(await run.CodeHost.GetDefaultBranchAsync(repo, run.Ct), "read the default branch");
        var allowed = target != defaultBranch || Model.RepositoryPatterns.Any(run.Scope.EngineMayMerge, repo.FullName);
        if (engineCanMerge && !allowed)
            run.Events.Add($"F1 at merge time: {target} is {repo.FullName}'s default branch, where the organisation does not let the engine merge; a person merges");

        if (engineCanMerge && allowed)
        {
            var targetRevision = ActionRun.Must(await vcs.ResolveAsync(repo, "refs/heads/" + target, run.Ct), "read the target branch")
                                 ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no branch {target} to merge into");
            var message = $"Merge {run.Scope.WorkBranch} into {target}\n\nMerged by Feature Flow for run {run.Call.Step.RunId}" +
                          (pr is null ? "." : $", pull request {ActionRun.Format(pr)}.");
            var landed = await vcs.MergeAsync(repo, head, "refs/heads/" + target, targetRevision, method, message,
                $"{run.Call.Step.RunId}/{run.Call.Step.StepId}", run.Ct);
            if (landed is { Ok: false, Error.Reason: "base_moved" })
            {
                run.Events.Add($"{target} moved since {run.Scope.WorkBranch} was caught up: {landed.Error.Message}");
                return run.Report("base_moved");
            }

            if (landed.Ok)
            {
                var result = landed.Value!;
                if (method == MergeMethods.Squash && pr is not null)
                    // The code host does not show a squash as merged: close the pull request with a link to the commit (section 7.3).
                    ActionRun.Must(await run.CodeHost.ClosePullRequestAsync(pr, $"Squashed into {target} as {result.Revision} by Feature Flow.", run.Ct), "close the pull request");
                return await RecordAsync(run, repo, target, result.Method, head, result.TargetBefore, result.Revision, EngineActor, result.AlreadyMerged, pr);
            }

            if (landed.Error!.Class == ProviderErrorClass.Infrastructure)
                ActionRun.Must(landed, "merge");
            run.Events.Add($"the engine's merge was refused ({landed.Error.Reason}: {landed.Error.Message}); a person merges");
        }

        return await ActionRequestAsync(run, repo, target, method, head, pr);
    }

    /// <summary>The identity the engine merges as, as the merge record names it.</summary>
    const string EngineActor = "feature-flow";

    /// <summary>Why the head may not merge for want of evidence (require_tested, require_checks), or null.</summary>
    static async Task<string?> MissingEvidenceAsync(ActionRun run, RepoRef repo, string head)
    {
        if (run.Bool("require_tested", true))
        {
            // docs/04 section 7.3: a passing test.run result; test.integration's does not count.
            var tests = await run.EvidenceAsync<TestEvidence>("test_results", "test.run");
            if (!tests.Any(t => t.Value.Repository == repo.FullName && t.Value.Revision == head && t.Value.Passed))
                return $"require_tested: no passing test.run result for {repo.FullName} at {EngineActions.Short(head)}, the head being merged" +
                       (tests.Count == 0 ? "" : $" (results exist for {string.Join(", ", tests.Select(t => $"{t.Entry.StepId} at {EngineActions.Short(t.Value.Revision)}{(t.Value.Passed ? "" : " failed")}"))})");
        }

        if (run.Bool("require_checks", true))
        {
            var checks = await run.EvidenceAsync<CheckEvidence>("check_results", "ci.checks");
            if (!checks.Any(c => c.Value.Repository == repo.FullName && c.Value.Revision == head && c.Value.Passed))
                return $"require_checks: no passing ci.checks result for {repo.FullName} at {EngineActions.Short(head)}, the head being merged";
        }

        return null;
    }

    /// <summary>
    /// The action request (docs/04 section 4.5): a person merges or closes the pull request on the code host; the step
    /// reads it every poll interval, holding no worker slot in between.
    /// </summary>
    static async Task<ActionResult> ActionRequestAsync(ActionRun run, RepoRef repo, string target, string method, string head, PullRequestRef? pr)
    {
        if (pr is null)
            throw ActionStop.CannotAnswer($"a person must merge {run.Scope.WorkBranch} into {target}, and this run has opened no pull request for it: open one with scm.pull_request first");
        await run.RememberAsync("action_request", $"a person merges {ActionRun.Format(pr)} into {target}, or closes it to decline");
        var state = ActionRun.Must(await run.CodeHost.GetPullRequestAsync(pr, run.Ct), "read the pull request");
        switch (state.State)
        {
            case PullRequestStates.Merged:
                run.Events.Add($"{state.MergedBy ?? "someone"} merged {ActionRun.Format(pr)}");
                return await RecordAsync(run, repo, target, method, state.HeadRevision, null, state.MergeRevision ?? "", state.MergedBy ?? "unknown", false, pr);
            case PullRequestStates.Closed:
                run.Events.Add($"{ActionRun.Format(pr)} was closed without merging: declined");
                return run.Report("declined");
            default:
                return ActionResult.Waiting($"waiting for a person to merge {ActionRun.Format(pr)} into {target}", run.Options.PollInterval, run.Events);
        }
    }

    static async Task<ActionResult> RecordAsync(ActionRun run, RepoRef repo, string target, string method, string head, string? before, string revision,
        string mergedBy, bool alreadyMerged, PullRequestRef? pr)
    {
        var record = new MergeEvidence(repo.FullName, target, method, head, before, revision, mergedBy, alreadyMerged, pr is null ? null : ActionRun.Format(pr));
        run.Events.Add($"merged {EngineActions.Short(head)} into {target} as {EngineActions.Short(revision)} ({method}, by {mergedBy}{(alreadyMerged ? ", already merged" : "")})");
        return run.Report("merged", evidence: await run.PutJsonAsync(record));
    }

    /// <summary>
    /// <c>scm.revert</c> (section 7.4): opens a pull request reverting a merge of this run, by default its own pull
    /// request in the step's repository. Merging the revert is a separate <c>scm.merge</c>.
    /// </summary>
    public static async Task<ActionResult> RevertAsync(ActionRun run)
    {
        var repo = run.Repository();
        var pr = (run.String("pull_request") is { } named ? ActionRun.ParsePullRequest(named) : run.PullRequest(repo))
                 ?? throw ActionStop.CannotAnswer($"scm.revert names no pull request, and this run opened none in {repo.FullName}");
        var revert = ActionRun.Must(await run.CodeHost.CreateRevertPullRequestAsync(pr, $"{run.Call.Step.RunId}/{run.Call.Step.StepId}/revert", run.Ct),
            "open the revert pull request");
        run.Events.Add($"opened {ActionRun.Format(revert)}, reverting {ActionRun.Format(pr)}");
        return run.Report("opened");
    }
}

static class StepCallExtensions
{
    /// <summary>The template's name without its library, for titles.</summary>
    public static string Template(this StepCall call) => call.Step.Template;
}
