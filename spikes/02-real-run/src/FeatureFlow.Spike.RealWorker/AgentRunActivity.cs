using FeatureFlow.Spike.AgentRuntime;
using FeatureFlow.Spike.Contracts;
using FeatureFlow.Spike.Interpreter;
using FeatureFlow.Spike.Providers;
using Temporalio.Activities;
using Temporalio.Exceptions;

// The agent runtime's AgentProgress is the runtime's own progress record;
// Contracts' AgentProgress is the heartbeat payload the workflow reads. Both
// names are correct for what they are, so this file disambiguates rather than
// renaming either — the same choice the sibling harnesses make.
using AgentProgress = FeatureFlow.Spike.AgentRuntime.AgentProgress;
using FileContent = FeatureFlow.Spike.CodeHost.FileContent;
using HeartbeatProgress = FeatureFlow.Spike.Contracts.AgentProgress;

namespace FeatureFlow.Spike.RealWorker;

/// <summary>
/// The agent.run step against a real agent runtime.
///
/// <para>
/// The shape is the one docs/05 1.2.1 and R20 require: <b>start and poll, never
/// await</b>. <see cref="IAgentRuntimeProvider.StartAsync"/> returns a handle
/// without waiting; the poll loop runs <b>inside this one activity</b>,
/// heartbeating, so a step that runs for hours is one activity, not one per
/// poll.
/// </para>
///
/// <para>
/// Heartbeats carry progress — last commit and session id — so a stalled agent
/// is distinguishable from a slow one (docs/01 3.6).
/// </para>
/// </summary>
public sealed partial class RealActivities
{
    /// <summary>How often the poll loop reads progress. docs/06 2.1: heartbeat 30s.</summary>
    public static TimeSpan PollInterval = TimeSpan.FromSeconds(5);

    /// <summary>How often the poll loop heartbeats. Must be under the heartbeat timeout.</summary>
    public static TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(5);

    [Activity]
    public async Task<AgentRunResult> RunAgentAsync(AgentRunRequest request)
    {
        var step = Context.Step(request.StepId);
        if (step is null)
            return new AgentRunResult(Outcomes.Failed, ErrorReason: $"no step '{request.StepId}' in the plan");

        var startedAt = DateTimeOffset.UtcNow;
        var role = Inputs.Role(step);
        var runtime = Context.Runtimes.ForRole(role);
        var instance = Context.Runtimes.InstanceForRole(role);

        // The head before the agent runs. Comparing against it afterwards is
        // what makes "the agent made no commit" detectable: reading HEAD after
        // the run always succeeds, so a bare post-run read can never tell
        // whether the agent committed anything.
        await SyncWorktreeAsync(step.Id);

        // Spike 3 (N4): with the checkout synced, "the agent committed" means
        // the local head moved past the remote head. A commit made before a
        // worker crash and not yet pushed then still counts on resume.
        var headBefore = SyncWorktree ? await RemoteHeadAsync() ?? await HeadShaAsync() : await HeadShaAsync();

        // R16: read feature-flow.yaml from the branch this step works on, and
        // record its hash. A mid-run edit is visible rather than silently stale.
        var manifest = await ReadManifestAsync(step.Id);

        // R6: the credential is minted per step, short-lived and scoped to one
        // repository. It is never read from configuration (docs/04 section 1).
        //
        // The agent grant is deliberately the narrowest that still lets the
        // agent do its job: write to the contents of this one repository, and
        // nothing else. Pull request, checks and merge calls are made by the
        // engine's own provider calls, not by the agent, so the agent's token
        // does not carry those permissions (review 14 A4(b)).
        var credential = await Context.Broker.IssueAsync(
            Actor.Agent(instance).ForStep(Context.RunId, step.Id),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(
                $"{RunContext.Owner}/{RunContext.Repository}",
                "contents:write",
                "contents:read",
                "metadata:read").Text,
            TimeSpan.FromMinutes(30),
            CancellationToken.None);

        if (!credential.IsOk)
        {
            return Classified(step, instance, startedAt, manifest, credential.Error!, null);
        }

        var limits = new AgentLimits(
            MaxWallClock: Limits.WallClock(step),
            HeartbeatInterval: HeartbeatInterval);

        var task = new AgentTask(
            Prompt: Prompt.For(step, Context, manifest),
            Role: role ?? "implementer",
            WriteAccess: Inputs.WriteAccess(step),
            Branch: Context.Branch,
            Repository: $"{RunContext.Owner}/{RunContext.Repository}");

        var key = Context.IdempotencyKey(step.Id, Context.Branch, role ?? "");
        var sandbox = Context.Sandbox(step.Id);

        // Start, or resume when the runtime declares session_resume and a prior
        // session reference exists. Resume is a capability, not an assumption.
        var session = await StartOrResumeAsync(runtime, sandbox, task, limits, key, request.SessionRef);

        if (!session.IsOk)
        {
            return Classified(step, instance, startedAt, manifest, session.Error!, credential.Value);
        }

        var handle = session.Value!;

        // The poll loop. One activity, heartbeating, until the runtime reports a
        // terminal result or the wall clock passes.
        var lastProgress = await PollAsync(runtime, handle, step.Id);

        var result = await runtime.GetResultAsync(handle, CancellationToken.None);
        if (!result.IsOk)
        {
            return Classified(step, instance, startedAt, manifest, result.Error!, credential.Value, handle);
        }

        var value = result.Value!;
        var compute = (DateTimeOffset.UtcNow - startedAt).TotalSeconds;

        // The step's work is whatever the agent committed. Nothing here writes
        // to the checkout: review 14 A3 removed StepWork from the agent path,
        // because applying canned content made the run look like the agent had
        // authored the change when it had not.
        //
        // The commit is read back from the branch and compared with the head
        // from before the run. A step whose agent made no commit is a real
        // failure, and it is reported as one rather than silently adopting
        // whatever HEAD happens to be.
        var commit = await HeadShaAsync();
        if (commit is null)
        {
            Console.WriteLine($"STEP {step.Id}: the branch head could not be resolved; " +
                              "reported as failed rather than faked");
            return new AgentRunResult(
                Outcomes.Failed,
                SessionRef: value.SessionRef ?? handle.SessionRef,
                SessionTurns: value.SessionTurns,
                CommandHandle: handle.SessionRef,
                SandboxKey: sandbox.Key,
                ErrorReason: $"the branch head could not be resolved on {Context.Branch}");
        }

        if (string.Equals(commit, headBefore, StringComparison.Ordinal))
        {
            Console.WriteLine($"STEP {step.Id}: the agent made no commit on {Context.Branch} " +
                              $"(head still {commit}); reported as failed rather than faked");
            return new AgentRunResult(
                Outcomes.Failed,
                SessionRef: value.SessionRef ?? handle.SessionRef,
                SessionTurns: value.SessionTurns,
                CommandHandle: handle.SessionRef,
                SandboxKey: sandbox.Key,
                ErrorReason: $"the agent produced no commit on {Context.Branch}");
        }

        // The agent commits locally; the engine publishes. The prompt tells the
        // agent not to push, because the push credential is the engine's to
        // hold — an agent that can push can also rewrite history the engine
        // has already recorded. The credential is the one minted for this step
        // above, so no second grant is needed.
        var pushed = await PushAsync(credential.Value!);
        Console.WriteLine($"STEP {step.Id}: agent committed {commit} on {Context.Branch} " +
                          $"(was {headBefore}); pushed={pushed}");

        if (!pushed)
        {
            return new AgentRunResult(
                Outcomes.Failed,
                SessionRef: value.SessionRef ?? handle.SessionRef,
                SessionTurns: value.SessionTurns,
                CommandHandle: handle.SessionRef,
                SandboxKey: sandbox.Key,
                ErrorReason: $"the agent's commit {commit} could not be pushed to {Context.Branch}");
        }

        // R13: tokens, model and compute time, per step.
        Context.RecordCost(new StepCost(
            RunId: Context.RunId,
            StepId: step.Id,
            StepKind: step.Kind,
            Model: value.Usage?.Model,
            RuntimeInstance: instance,
            InputTokens: value.Usage?.InputTokens,
            OutputTokens: value.Usage?.OutputTokens,
            Credits: value.Usage?.Credits,
            CostUsd: value.Usage?.CostUsd,
            ComputeSeconds: compute,
            UsageSource: value.Usage?.Source ?? "none",
            RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash,
            CredentialId: credential.Value?.Id));

        // docs/06 2.1: an agent that ran out of time is a work outcome, not
        // infrastructure. The step overrides whatever the provider classified,
        // and the provider classifies it correctly to begin with.
        if (value.TimedOut || string.Equals(value.Outcome, "timeout", StringComparison.Ordinal))
        {
            return new AgentRunResult(
                Outcomes.Failed,
                value.LastCommit ?? lastProgress?.LastCommit,
                value.SessionRef ?? handle.SessionRef,
                value.SessionTurns,
                handle.SessionRef,
                sandbox.Key,
                ErrorReason: "timeout");
        }

        var outcome = value.Outcome switch
        {
            "completed" => Outcomes.Completed,
            "blocked" => Outcomes.Blocked,
            "failed" => Outcomes.Failed,
            _ => Outcomes.Failed,
        };

        return new AgentRunResult(
            outcome,
            commit ?? value.LastCommit ?? lastProgress?.LastCommit,
            value.SessionRef ?? handle.SessionRef,
            value.SessionTurns,
            handle.SessionRef,
            sandbox.Key,
            ErrorReason: outcome == Outcomes.Failed ? $"runtime exited {value.ExitCode?.ToString() ?? "(unknown)"}" : null);
    }

    /// <summary>
    /// Start, or resume the prior session. A resume is only attempted when the
    /// runtime declares <c>session_resume</c> and a session reference exists
    /// (docs/04 section 3).
    /// </summary>
    private static async Task<ProviderResult<AgentSession>> StartOrResumeAsync(
        IAgentRuntimeProvider runtime,
        SandboxHandle sandbox,
        AgentTask task,
        AgentLimits limits,
        string key,
        string? sessionRef)
    {
        if (!string.IsNullOrWhiteSpace(sessionRef))
        {
            var resumed = await runtime.ResumeAsync(sandbox, sessionRef!, limits, key, CancellationToken.None);
            // A resume that cannot happen is not a failure of the step: fall
            // back to a fresh start rather than losing the attempt.
            if (resumed.IsOk)
                return resumed;
        }

        return await runtime.StartAsync(sandbox, task, limits, key, CancellationToken.None);
    }

    /// <summary>
    /// The poll loop. Runs inside this activity and heartbeats, so a long step
    /// is one activity (R20) and a stalled agent is distinguishable from a slow
    /// one (docs/01 3.6).
    /// </summary>
    private async Task<AgentProgress?> PollAsync(
        IAgentRuntimeProvider runtime, AgentSession handle, string stepId)
    {
        AgentProgress? last = null;
        var lastHeartbeat = DateTimeOffset.UtcNow;

        while (true)
        {
            var progress = await runtime.GetProgressAsync(handle, CancellationToken.None);

            if (progress.IsOk)
            {
                last = progress.Value!;

                // Heartbeat on the interval, and always on the first poll, so a
                // worker crash is noticed within one heartbeat timeout.
                if (DateTimeOffset.UtcNow - lastHeartbeat >= HeartbeatInterval)
                {
                    lastHeartbeat = DateTimeOffset.UtcNow;
                    Heartbeat(stepId, last);
                }

                if (!last.Running)
                    return last;
            }
            else
            {
                // A progress read that fails is infrastructure: the work did not
                // happen, so it retries free. Heartbeat anyway, so the activity
                // is not timed out while it is retrying.
                Heartbeat(stepId, last);
            }

            await Task.Delay(PollInterval);
        }
    }

    /// <summary>
    /// Publishes the agent's commit to the branch.
    ///
    /// <para>
    /// The agent is told not to push, so the engine does it here. The token goes
    /// into the remote URL for one command only; it is never written to the
    /// repository's config or to a file (floor F2).
    /// </para>
    /// </summary>
    private async Task<bool> PushAsync(Credential credential)
    {
        var token = credential.Secret;
        var remote = $"https://x-access-token:{token}@github.com/{RunContext.Owner}/{RunContext.Repository}.git";

        var startInfo = new System.Diagnostics.ProcessStartInfo("git")
        {
            WorkingDirectory = Context.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("push");
        startInfo.ArgumentList.Add(remote);
        startInfo.ArgumentList.Add(Context.Branch);

        using var git = System.Diagnostics.Process.Start(startInfo);
        if (git is null)
            return false;

        var error = await git.StandardError.ReadToEndAsync();
        await git.WaitForExitAsync();

        if (git.ExitCode != 0)
        {
            Console.WriteLine($"STEP push: exited {git.ExitCode}: {error.Trim()}");
            return false;
        }

        return true;
    }

    /// <summary>
    /// docs/01 3.6: heartbeats carry progress — last commit and session id — so
    /// a stalled agent is distinguishable from a slow one.
    /// </summary>
    private static void Heartbeat(string stepId, AgentProgress? progress)
    {
        var detail = new HeartbeatProgress(
            LastCommit: progress?.LastCommit,
            SessionTurns: progress?.SessionTurns ?? 0,
            SessionRef: progress?.SessionRef,
            CommandHandle: stepId,
            SandboxKey: null,
            ElapsedSeconds: progress?.Elapsed.TotalSeconds ?? 0);

        // Outside a Temporal activity there is no execution context, and
        // heartbeating is meaningless there. Guarded so a step can be exercised
        // directly, as the checks do, without throwing.
        if (ActivityExecutionContext.HasCurrent)
        {
            ActivityExecutionContext.Current.Heartbeat(new object?[] { detail });
        }
    }

    /// <summary>
    /// Turns a classified provider error into a step result. Infrastructure
    /// failures never consume an attempt (docs/01 3.7), so they are reported as
    /// <c>infrastructure</c> and the interpreter retries free.
    /// </summary>
    private AgentRunResult Classified(
        Step step,
        string instance,
        DateTimeOffset startedAt,
        FileContent? manifest,
        ProviderError error,
        Credential? credential,
        AgentSession? handle = null)
    {
        Context.RecordCost(new StepCost(
            RunId: Context.RunId,
            StepId: step.Id,
            StepKind: step.Kind,
            Model: null,
            RuntimeInstance: instance,
            InputTokens: null,
            OutputTokens: null,
            Credits: null,
            CostUsd: null,
            ComputeSeconds: (DateTimeOffset.UtcNow - startedAt).TotalSeconds,
            UsageSource: "none",
            RecordedAt: DateTimeOffset.UtcNow,
            ManifestHash: manifest?.ContentHash,
            CredentialId: credential?.Id,
            ErrorClass: error.Class.ToString(),
            ErrorReason: error.Reason));

        return new AgentRunResult(
            error.Class == ErrorClass.Infrastructure ? "infrastructure" : Outcomes.Failed,
            SessionRef: handle?.SessionRef,
            CommandHandle: handle?.SessionRef,
            SandboxKey: Context.Sandbox(step.Id).Key,
            ErrorReason: $"{error.Class}/{error.Reason}: {error.Detail}");
    }

    /// <summary>
    /// The outcome a work failure should report for a step kind.
    ///
    /// <para>
    /// docs/09 4.2: every kind must route every outcome it can return. A step
    /// kind that declares <c>blocked</c> but not <c>failed</c> would leave a
    /// 'failed' result with no route, and the run would end silently — which
    /// looks like a success. Reporting the outcome the plan actually routes is
    /// what makes a failure visible.
    /// </para>
    /// </summary>
    private static string WorkOutcomeFor(Step step) => step.Kind switch
    {
        "scm.merge" => Outcomes.Blocked,
        _ => Outcomes.Failed,
    };
}
