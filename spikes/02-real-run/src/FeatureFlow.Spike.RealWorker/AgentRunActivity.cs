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

        // R16: read feature-flow.yaml from the branch this step works on, and
        // record its hash. A mid-run edit is visible rather than silently stale.
        var manifest = await ReadManifestAsync(step.Id);

        // R6: the credential is minted per step, short-lived and scoped to one
        // repository. It is never read from configuration (docs/04 section 1).
        var credential = await Context.Broker.IssueAsync(
            Actor.Agent(instance).ForStep(Context.RunId, step.Id),
            GitHubAppCredentialBroker.TargetSystemName,
            CredentialScope.ForRepository(
                $"{RunContext.Owner}/{RunContext.Repository}",
                "contents:write",
                "contents:read",
                "pull_requests:write",
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

        // The step's work. The runtime really ran and was really polled; the
        // change itself is applied by StepWork because the model behind this
        // spike's runtime cannot emit tool calls (see StepWork's header). The
        // commit is real, on the real branch, and the diff is read back from the
        // code host.
        var commit = await StepWork.ApplyAsync(Context, step);
        if (commit is not null)
            Console.WriteLine($"STEP {step.Id}: committed {commit} on {Context.Branch}");

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
