using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Console.Tests;

/// <summary>
/// The console B1 records the engine writes (step states, pull requests, a wait's cause or patch, a run's level, parent step,
/// tracker issue and start, a child run), written as the engine would write them so a test can show them, and a running agent
/// step's activity. Each helper is one batch, so one change in the feed (the child run, two: its record and the parent's timeline).
/// </summary>
public sealed partial class FakeEngine
{
    /// <summary>Records a run with the fields B1 shows: its plan's level, the parent's step that started it, its tracker issue (with title and link when read) and when it started.</summary>
    public Task RunWithAsync(string runId, string template = "std:templates/ticket_delivery", string status = "running", string requester = "local",
        IReadOnlyList<string>? repositories = null, string? parentRunId = null, string? detail = null, string? level = null, string? parentStepId = null,
        string? trackerIssue = null, string? trackerTitle = null, string? trackerUrl = null, DateTimeOffset? startedAt = null) =>
        AppendAsync(new RecordBatch([new RunRecord(runId, parentRunId, template, status, requester, repositories ?? [], false, Now(), detail,
            level, parentStepId, trackerIssue, trackerTitle, trackerUrl, startedAt ?? Now())]));

    /// <summary>
    /// Starts a child run as a <c>run.child</c> step does: the child's record (with its parent and parent step), then the
    /// parent's <c>child_started</c> entry naming the child, as the core traces it.
    /// </summary>
    public async Task ChildRunAsync(string parentRunId, string parentStepId, string childRunId, string template = "std:templates/ticket_delivery",
        string status = "running", string requester = "local", IReadOnlyList<string>? repositories = null, string? level = "ticket",
        string? trackerIssue = null, string? trackerTitle = null)
    {
        await RunWithAsync(childRunId, template, status, requester, repositories, parentRunId, level: level, parentStepId: parentStepId,
            trackerIssue: trackerIssue, trackerTitle: trackerTitle);
        await TraceAsync(parentRunId, "child_started", parentStepId, childRunId);
    }

    /// <summary>Records a step's counters, as the engine does when they change: how often it ran, attempts used, allowed (null for a decision) and granted.</summary>
    public Task StepStateAsync(string runId, string stepId, int executions = 1, int attemptsUsed = 1, long? attemptsAllowed = 3, long attemptsGranted = 0) =>
        AppendAsync(new RecordBatch(StepStates: [new StepStateRecord(runId, stepId, executions, attemptsUsed, attemptsAllowed, attemptsGranted, Now())]));

    /// <summary>
    /// Records a pull request as a step read it: by default open, mergeable, one file changed and its checks passed. Pass
    /// <paramref name="files"/> or <paramref name="checks"/> as empty to record a read that fetched none.
    /// </summary>
    public Task PullRequestAsync(string runId, string repository = "acme/api", int number = 1, string stepId = "open_pr", string state = PullRequestStates.Open,
        string title = "Add DELETE /widgets/{id} (#200)", bool? mergeable = true, string? mergeableState = "clean", IReadOnlyList<string>? approvedBy = null,
        IReadOnlyList<PullRequestFile>? files = null, IReadOnlyList<CheckRun>? checks = null, string headRevision = "0123456789abcdef0123456789abcdef01234567")
    {
        files ??= [new PullRequestFile("src/Widgets.cs", "modified", 12, 3)];
        checks ??= [new CheckRun("build", CheckStates.Completed, Conclusions.Success, "ci.yml", $"https://github.com/{repository}/actions/runs/1")];
        var pr = new PullRequestState(new PullRequestRef(new RepoRef(repository), number), state, title, "", $"ff/{runId}", headRevision, "main", false, mergeable,
            approvedBy ?? [], [], state == PullRequestStates.Merged ? "a-person" : null, null, $"https://github.com/{repository}/pull/{number}",
            files.Sum(f => f.Additions), files.Sum(f => f.Deletions), files.Count, mergeableState);
        return AppendAsync(new RecordBatch(PullRequests:
            [new PullRequestRecord(runId, repository, number, stepId, pr, files.Count == 0 ? null : files, checks.Count == 0 ? null : new CheckStatus(headRevision, checks), Now())]));
    }

    /// <summary>
    /// Opens an intervention on <paramref name="stepId"/> with why it stopped the step, as the core describes it; returns its id.
    /// The prompt is the cause's reason, as the engine's.
    /// </summary>
    public async Task<string> OpenInterventionAsync(string runId, string stepId, WaitCause cause, string? waitId = null)
    {
        waitId ??= NewWaitId(runId, "intervention");
        await AppendAsync(new RecordBatch(Waits: [new WaitRecord(runId, waitId, "intervention", stepId, "", cause.Reason, DefaultChoices("intervention"), true, Now(),
            ReasonRequired: ReasonRequired("intervention"), Cause: cause)]));
        return waitId;
    }

    /// <summary>Opens a patch approval on <paramref name="stepId"/> with the patch it waits on, as the core describes it; returns its id.</summary>
    public async Task<string> OpenPatchApprovalAsync(string runId, string stepId, WaitPatch patch, string prompt = "Approve the plan patch?", string? waitId = null)
    {
        waitId ??= NewWaitId(runId, "patch_approval");
        await AppendAsync(new RecordBatch(Waits: [new WaitRecord(runId, waitId, "patch_approval", stepId, "", prompt, DefaultChoices("patch_approval"), true, Now(),
            ReasonRequired: ReasonRequired("patch_approval"), Patch: patch)]));
        return waitId;
    }

    /// <summary>
    /// A cause as the core writes one for a step that used its attempts (<c>RunCore</c>, run #198's shape): outcome
    /// <c>attempts_exhausted</c>, the reason with the step's last summary, and each execution sent back by
    /// <paramref name="byStep"/> ending <paramref name="byOutcome"/> with <paramref name="sentBack"/>.
    /// </summary>
    public static WaitCause ExhaustedCause(string stepId, int attempts, string said, string byStep, string byOutcome, string sentBack) =>
        new(stepId, "attempts_exhausted", $"attempts exhausted ({said})", said, attempts, attempts,
            Enumerable.Range(1, attempts).Select(i => new SentBack(i, byStep, byOutcome, sentBack)).ToList());

    /// <summary>The agent step <see cref="AgentStepAsync"/> runs: a ticket's planner.</summary>
    public const string AgentStep = "plan.plan";

    /// <summary>
    /// A standalone ticket run whose agent step <see cref="AgentStep"/> has run <paramref name="executions"/> times, each with
    /// its prompt as the agent activity indexes it before a session (<c>prompt:1</c>); the last still running unless
    /// <paramref name="running"/> is false.
    /// </summary>
    public async Task AgentStepAsync(string runId, int executions = 1, bool running = true)
    {
        await RunWithAsync(runId, "std:templates/ticket_delivery", repositories: [ScenarioRepository], level: "ticket", trackerIssue: "INV-9", trackerTitle: "Ticket INV-9");
        await AppendAsync(new RecordBatch(Locks: [new LockRecord(runId, await LockAsync(), Now())]));
        await PlanAsync(runId, "std:templates/ticket_delivery", [ScenarioRepository], new() { ["ticket_id"] = "INV-9" });
        await DoneAsync(runId, ("baseline.tests", "passed"), ("baseline.analysis", "passed"));
        for (var n = 1; n <= executions; n++)
        {
            await TraceAsync(runId, "started", AgentStep, $"execution {n}");
            await ProduceAsync(runId, AgentStep, "prompt:1", $"Plan ticket INV-9 (execution {n}).", type: "prompt", execution: n);
            if (n < executions || !running)
                await TraceAsync(runId, "completed", AgentStep, "planned");
        }
    }

    /// <summary>An entry of an agent's activity as the runtime records it: its kind (<see cref="ActivityKinds"/>) and the fields that kind uses.</summary>
    public sealed record ActivityEntry(string Kind, string? CallId = null, string? Name = null, string? Text = null, string? Detail = null, bool Failed = false)
    {
        public static ActivityEntry Said(string text) => new(ActivityKinds.Message, Text: text);

        public static ActivityEntry ToolStarted(string callId, string name, string summary, string arguments = "{}") =>
            new(ActivityKinds.ToolStarted, callId, name, summary, arguments);

        public static ActivityEntry ToolFinished(string callId, string output, bool failed = false) =>
            new(ActivityKinds.ToolFinished, callId, Detail: output, Failed: failed);

        public static ActivityEntry Truncated() => new(ActivityKinds.Truncated);
    }

    readonly Dictionary<(string, string, int, int), int> activitySequences = [];

    /// <summary>
    /// Appends <paramref name="entries"/> to the session's activity as one flush of the agent activity does (one batch, so one
    /// <c>step_changed</c>), numbered after the session's earlier entries.
    /// </summary>
    public Task ActivityAsync(string runId, string stepId, int execution, int session, params ActivityEntry[] entries)
    {
        int first;
        lock (activitySequences)
        {
            first = activitySequences.GetValueOrDefault((runId, stepId, execution, session)) + 1;
            activitySequences[(runId, stepId, execution, session)] = first + entries.Length - 1;
        }

        return AppendAsync(new RecordBatch(Activity: entries.Select((e, i) =>
            new ActivityRecord(runId, stepId, execution, session, first + i, e.Kind, e.CallId, e.Name, e.Text, e.Detail, e.Failed, Now())).ToList()));
    }
}
