using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// The waiting actions (docs/04 section 8). Each activity attempt reads once; while the condition is open it ends with
/// <see cref="ActionResult.Wait"/>, and Temporal retries it after the poll interval, so a wait holds no worker slot. The
/// time a wait began and the head it waits on are kept in the record store across those retries.
/// </summary>
static class Waiting
{
    /// <summary>The conclusions that pass. Anything else fails; a conclusion not known at all raises an intervention.</summary>
    static readonly HashSet<string> Passing = [Conclusions.Success, Conclusions.Neutral, Conclusions.Skipped];

    static readonly HashSet<string> KnownFailing =
        [Conclusions.Failure, Conclusions.Cancelled, Conclusions.TimedOut, Conclusions.ActionRequired, "startup_failure", "stale"];

    /// <summary>The completed checks that did not pass, by name; an unknown conclusion stops the step for a person.</summary>
    static List<string> NotPassing(IEnumerable<CheckRun> checks)
    {
        var list = checks.ToList();
        if (list.FirstOrDefault(c => !Passing.Contains(c.Conclusion ?? "") && !KnownFailing.Contains(c.Conclusion ?? "")) is { } odd)
            throw ActionStop.CannotAnswer($"check {odd.Name} concluded '{odd.Conclusion ?? "(nothing)"}', which the engine does not know how to judge");
        return list.Where(c => !Passing.Contains(c.Conclusion!)).Select(c => $"{c.Name} {c.Conclusion}").ToList();
    }

    /// <summary>
    /// <c>ci.checks</c> (section 8.1): waits for every check on the head, including late ones, and for every CI run the CI
    /// provider says will report (<c>GetExpectedRunsAsync</c>) and every <c>expected</c> name, so an early third-party
    /// check cannot read as "all passed". Runs that only may report are waited for up to <c>grace</c>. With no CI and no
    /// checks, it passes at once. A head that changes while waiting, or the timeout, raises an intervention.
    /// </summary>
    public static async Task<ActionResult> ChecksAsync(ActionRun run)
    {
        var repo = run.Repository();
        var pr = run.PullRequest(repo);
        var head = pr is not null
            ? ActionRun.Must(await run.CodeHost.GetPullRequestAsync(pr, run.Ct), "read the pull request").HeadRevision
            : ActionRun.Must(await run.Vcs.ResolveAsync(repo, run.WorkReference, run.Ct), "read the work branch")
              ?? throw ActionStop.CannotAnswer($"{repo.FullName} has no work branch {run.Scope.WorkBranch} to check");
        var waitedOn = await run.RememberAsync("checks_head", head);
        if (waitedOn != head)
            throw ActionStop.CannotAnswer($"the head of {(pr is null ? run.Scope.WorkBranch : ActionRun.Format(pr))} changed from {EngineActions.Short(waitedOn)} to {EngineActions.Short(head)} while its checks were awaited");
        var since = await run.SinceAsync();
        var elapsed = DateTimeOffset.UtcNow - since;
        var timeout = run.Duration("timeout", TimeSpan.FromHours(2));
        var (pending, status) = await ReadChecksAsync(run, repo, pr, head, run.Strings("expected"), run.Duration("grace", TimeSpan.FromMinutes(2)), elapsed);
        if (pending.Count > 0)
        {
            if (elapsed > timeout)
                throw ActionStop.CannotAnswer($"the checks on {EngineActions.Short(head)} did not finish within {timeout}: {string.Join("; ", pending)}");
            return ActionResult.Waiting(string.Join("; ", pending), run.Duration("poll_every", run.Options.PollInterval), run.Events);
        }

        var failed = NotPassing(status.Checks);
        var skipped = status.Checks.Count(c => c.Conclusion == Conclusions.Skipped);
        run.Events.Add(status.Checks.Count == 0
            ? $"{repo.FullName} has no CI and no checks on {EngineActions.Short(head)}"
            : $"checks on {EngineActions.Short(head)}: {string.Join(", ", status.Checks.Select(c => $"{c.Name} {c.Conclusion}"))}");
        var evidence = await run.PutJsonAsync(new CheckEvidence(repo.FullName, head, pr is null ? null : ActionRun.Format(pr), failed.Count == 0, skipped, status.Checks));
        return run.Report(failed.Count == 0 ? "passed" : "failed", new Dictionary<string, object?> { ["skipped"] = (long)skipped }, evidence);
    }

    /// <summary>What is still awaited on a head, and the checks reported so far.</summary>
    static async Task<(List<string> Pending, CheckStatus Status)> ReadChecksAsync(ActionRun run, RepoRef repo, PullRequestRef? pr, string head,
        IReadOnlyList<string> expected, TimeSpan grace, TimeSpan elapsed)
    {
        var ci = run.Optional<ICiProvider>("ci");
        var runs = ci is null ? ExpectedRuns.None : ActionRun.Must(await ci.GetExpectedRunsAsync(repo, head, pr, run.Ct), "read the expected CI runs");
        var status = ActionRun.Must(await run.CodeHost.GetCheckStatusAsync(repo, head, run.Ct), "read the checks");
        bool Reported(string name) => status.Checks.Any(c => c.Suite == name || c.Name == name);
        var pending = new List<string>();
        pending.AddRange(status.Checks.Where(c => c.State != CheckStates.Completed).Select(c => $"{c.Name} is {c.State}"));
        pending.AddRange(runs.Runs.Where(r => !Reported(r)).Select(r => $"CI run {r} has not reported"));
        pending.AddRange(expected.Where(e => !status.Checks.Any(c => c.Name == e)).Select(e => $"expected check {e} has not appeared"));
        if (elapsed < grace)
            pending.AddRange(runs.MayRun.Where(r => !Reported(r)).Select(r => $"CI run {r} may still report"));
        return (pending, status);
    }

    /// <summary>
    /// <c>wait.condition</c> (section 8.2): one condition on the run's pull request (<c>approved</c>, <c>merged</c>,
    /// <c>mergeable</c>) or its checks. <c>met</c>; <c>unmet</c> when the pull request is closed unmerged, changes are
    /// requested, or the checks fail. The timeout (default 7 days) raises an intervention. Tracker conditions wait for
    /// the tracker provider (M7) and raise an intervention now.
    /// </summary>
    public static async Task<ActionResult> ConditionAsync(ActionRun run)
    {
        var repo = run.Repository();
        if (run.Inputs.GetValueOrDefault("tracker") is not null)
            throw ActionStop.CannotAnswer("wait.condition on a tracker issue needs the tracker provider, which arrives in a later milestone");
        var pr = run.PullRequest(repo) ?? throw ActionStop.CannotAnswer($"wait.condition waits on this run's pull request in {repo.FullName}, and it has opened none");
        var since = await run.SinceAsync();
        var elapsed = DateTimeOffset.UtcNow - since;
        var timeout = run.Duration("timeout", TimeSpan.FromDays(7));
        var state = ActionRun.Must(await run.CodeHost.GetPullRequestAsync(pr, run.Ct), "read the pull request");
        var name = ActionRun.Format(pr);

        string? outcome;
        string waiting;
        if (run.String("pull_request") is { } wanted)
        {
            (outcome, waiting) = (wanted, state) switch
            {
                (_, { State: PullRequestStates.Closed }) => ("unmet", $"{name} was closed without merging"),
                (_, { State: PullRequestStates.Merged }) => ("met", $"{name} is merged"),
                ("approved", { ChangesRequestedBy.Count: > 0 }) => ("unmet", $"{string.Join(", ", state.ChangesRequestedBy)} requested changes on {name}"),
                ("approved", { ApprovedBy.Count: > 0 }) => ("met", $"{string.Join(", ", state.ApprovedBy)} approved {name}"),
                ("mergeable", { Mergeable: true }) => ("met", $"{name} is mergeable"),
                _ => ((string?)null, $"waiting for {name} to be {wanted}"),
            };
        }
        else
        {
            var (pending, status) = await ReadChecksAsync(run, repo, pr, state.HeadRevision, [], TimeSpan.Zero, elapsed);
            var failed = NotPassing(status.Checks);
            (outcome, waiting) = pending.Count > 0 ? (null, string.Join("; ", pending))
                : failed.Count > 0 ? ("unmet", $"checks failed on {name}: {string.Join(", ", failed)}")
                : ("met", $"the checks on {name} passed");
        }

        if (outcome is null)
        {
            if (elapsed > timeout)
                throw ActionStop.CannotAnswer($"wait.condition timed out after {timeout}: {waiting}");
            return ActionResult.Waiting(waiting, run.Duration("poll_every", run.Options.PollInterval), run.Events);
        }

        run.Events.Add(waiting);
        return run.Report(outcome);
    }
}
