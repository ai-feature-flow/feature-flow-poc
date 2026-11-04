using FeatureFlow.Interpreter;
using FeatureFlow.Interpreter.Wire;
using FeatureFlow.Model;
using FeatureFlow.Model.Plans;
using FeatureFlow.Model.Yaml;
using FeatureFlow.Providers.Abstractions;
using Temporalio.Exceptions;
using Temporalio.Workflows;

namespace FeatureFlow.Engine;

/// <summary>
/// Run-time overrides (docs/05 section 5): the <c>override</c> update (<c>RunWorkflowNames.Override</c>, <c>OverrideArgs</c>),
/// mid-run library overrides (docs/08 section 5.2) and closing an aborted run's pull requests. M7, W1. The API authenticated
/// the actor (F8), authorised the base key and recorded the override; the workflow, which alone knows what is protected,
/// refuses protected content or a grant beyond the hard ceiling without the variant the caller holds, and anything that
/// relaxes a floor (LOG 203).
/// </summary>
public partial class RunWorkflow
{
    /// <summary>The patch marker of M7's steering: reopening a decided join, and closing an aborted run's pull requests.</summary>
    public const string SteeringPatch = "m7-steering";

    /// <summary>Query: the pull requests a run opened, and the child runs it started (LOG 215), for an aborted ancestor to close.</summary>
    public const string PullRequestsQuery = "pull_requests";

    /// <summary>Signal, from a run to its running children: a library override, which applies to the run tree from that run down (docs/08 section 5.2).</summary>
    public const string ParentLibraryOverride = "parent_library_override";

    /// <summary>The keys the <c>override</c> update carries out: answers keep the <c>answer</c> update (LOG 203).</summary>
    static readonly IReadOnlyList<string> UpdateKeys =
        [Overrides.Breakpoint, Overrides.Skip, Overrides.Amend, Overrides.ChangeRole, Overrides.GrantAttempts, Overrides.OverrideLibrary, Overrides.Abort];

    /// <summary>An abort asked for the run's open pull requests to be closed (docs/05 section 5).</summary>
    bool closePullRequests;

    /// <summary>The run's current lock: its start lock with the run's library overrides applied. Carried by the next run's <c>LockRef</c>.</summary>
    string? currentLock;

    string LockRef => currentLock ?? input.LockRef;

    /// <summary>Lock records written since the last record: the run's lock after each library override (the last write is the run's lock).</summary>
    readonly List<LockRecord> lockRecords = [];

    [WorkflowUpdateValidator(nameof(OverrideAsync))]
    public void ValidateOverride(OverrideArgs o)
    {
        if (core is null)
            return;
        if (core.State.Overrides.FirstOrDefault(a => a.Id == o.OverrideId) is { } previous)
        {
            if (previous.Key != o.Key)
                throw new ApplicationFailureException($"conflict: override {o.OverrideId} was already applied as {previous.Key}");
            return;
        }

        if (OverrideError(o) is { } error)
            throw new ApplicationFailureException(error);
    }

    [WorkflowUpdate(RunWorkflowNames.Override)]
    public async Task<AnswerReply> OverrideAsync(OverrideArgs o)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        if (draining)
            answersWhileDraining++;
        if (core!.State.Overrides.FirstOrDefault(a => a.Id == o.OverrideId) is { } previous)
            return previous.Key == o.Key
                ? new AnswerReply("duplicate: " + previous.Result)
                : throw new ApplicationFailureException($"conflict: override {o.OverrideId} was already applied as {previous.Key}");
        if (OverrideError(o) is { } error)
            throw new ApplicationFailureException(error);

        string result;
        if (o.Key == Overrides.OverrideLibrary)
            result = await OverrideLibraryAsync(o.Change["item"], o.Change["with"], null, o.Actor, o.Reason);
        else if (o.Key == Overrides.Abort)
        {
            closePullRequests = Bool(o.Change, "close_pull_requests") == true;
            core.Apply(new RunAborted($"{o.Reason} (by {o.Actor})"));
            result = closePullRequests ? "aborted; its open pull requests will be closed" : "aborted";
        }
        else
        {
            var applied = core.Apply(SteeringInput(o)!);
            if (!applied.Accepted || applied.Patch is { Status: not PatchStatus.Applied })
                throw new ApplicationFailureException(string.Join("; ", applied.Errors.Concat(applied.Patch?.Diagnostics.Where(d => d.Blocks).Select(d => $"check {d.Check}: {d.Message}") ?? [])));
            result = o.Key switch
            {
                Overrides.Amend or Overrides.ChangeRole => $"applied: plan revision {core.State.Revision}",
                Overrides.Skip when core.State.Skips.ContainsKey(o.StepId!) => "applied: skipped when the run reaches it",
                Overrides.Skip => "applied: skipped",
                _ => "applied",
            };
        }

        if (Protected(o) is { Count: > 0 } guarded)
            result += $" (protected: {string.Join(", ", guarded)})";
        core.State.Overrides.Add(new AppliedOverride(o.OverrideId, o.Key, result));
        core.State.Trace.Add(new TraceEntry("override", o.StepId ?? "", $"{o.Key} by {o.Actor}: {o.Reason} -> {result}"));
        dirty = true;
        return new AnswerReply(result);
    }

    /// <summary>Why an override would be refused now, or null: its key and arguments, then the core's own check.</summary>
    string? OverrideError(OverrideArgs o)
    {
        if (!UpdateKeys.Contains(o.Key))
            return Overrides.IsKey(o.Key)
                ? $"{o.Key} is not delivered as an override: answers, recoveries and answers to agents go through the answer update"
                : $"'{o.Key}' is not a run-time override key";
        if (core!.State.Status != RunStatus.Running)
            return $"the run has ended ({core.State.Status.Name()})";
        string? Missing(params string[] names) => names.FirstOrDefault(n => !o.Change.TryGetValue(n, out var v) || string.IsNullOrWhiteSpace(v)) is { } name
            ? $"{o.Key} needs change.{name}"
            : null;
        switch (o.Key)
        {
            case Overrides.Breakpoint or Overrides.Skip or Overrides.ChangeRole when o.StepId is null:
                return $"{o.Key} needs a step";
            case Overrides.Skip when Missing("outcome") is { } missing:
                return missing;
            case Overrides.ChangeRole when Missing("role") is { } missing:
                return missing;
            case Overrides.Amend when Missing("patch") is { } missing:
                return missing;
            case Overrides.GrantAttempts when !long.TryParse(o.Change.GetValueOrDefault("attempts"), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _):
                return "grant_attempts needs change.attempts, a whole number";
            case Overrides.Breakpoint when o.Change.ContainsKey("set") && Bool(o.Change, "set") is null:
                return "breakpoint's change.set is true or false";
            case Overrides.OverrideLibrary:
                if (Missing("item", "with") is { } absent)
                    return absent;
                if (core.LibraryItemError(o.Change["item"]) is { } itemError)
                    return itemError;
                return Protected(o) is { Count: > 0 } guarded && !Holds(o, Overrides.OverrideLibraryProtected)
                    ? $"{string.Join(", ", guarded)} may resolve {o.Change["item"]}: overriding it needs {Overrides.OverrideLibraryProtected}"
                    : null;
            case Overrides.Abort:
                return null;
        }

        if (o.Key == Overrides.Amend)
            return ReadPatch(o.Change["patch"], out var d) is not { } patch ? "the patch does not read: " + string.Join("; ", d.Where(x => x.Blocks).Select(x => x.Message))
                : patch.ResumeAt is not null ? "an amend changes the plan, not where the run is: omit resume_at, then answer a breakpoint or intervention with go_to (LOG 209)"
                : null;
        return core.Check(SteeringInput(o)!);
    }

    /// <summary>The core input an override stands for; the actor is the authenticated one the API named (F8).</summary>
    RunInput? SteeringInput(OverrideArgs o) => o.Key switch
    {
        Overrides.Breakpoint => new BreakpointSet(o.StepId!, Bool(o.Change, "set") ?? true, o.Actor),
        Overrides.Skip => new StepSkipped(o.StepId!, o.Change["outcome"], o.Actor, o.Reason, Holds(o, Overrides.SkipProtected)),
        Overrides.GrantAttempts => new AttemptsGranted(o.StepId, long.Parse(o.Change["attempts"], System.Globalization.CultureInfo.InvariantCulture),
            Holds(o, Overrides.GrantAttemptsBeyond), o.Actor),
        Overrides.ChangeRole => new RoleChanged(o.StepId!, o.Change["role"], o.Actor, o.Reason),
        Overrides.Amend when ReadPatch(o.Change["patch"], out _) is { } patch =>
            new PatchProposed(patch with { Reason = $"amend by {o.Actor}: {o.Reason}" }, new Proposer("human", null), Holds(o, Overrides.AmendProtected)),
        _ => null,
    };

    /// <summary>The protected content an override touched, for its reply and record, so a reader sees the <c>_protected</c> variant was needed.</summary>
    IReadOnlyList<string> Protected(OverrideArgs o) => o.Key switch
    {
        Overrides.Skip or Overrides.ChangeRole when core!.StepById(o.StepId!) is { Protected: true } => [o.StepId!],
        // A run that starts children, or may once plan.expand adds them, passes the override down, where protected steps it cannot see may resolve it (LOG 212).
        Overrides.OverrideLibrary => core!.ProtectedResolving(o.Change["item"])
            .Concat(core.Plan.AllSteps.Any(x => x.Action is "run.child" or "plan.expand") ? ["its child runs"] : []).ToList(),
        _ => [],
    };

    static bool Holds(OverrideArgs o, string variant) => o.Variants?.Contains(variant) == true;

    static bool? Bool(IReadOnlyDictionary<string, string> change, string name) => change.GetValueOrDefault(name) switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    static PatchDocument? ReadPatch(string text, out DiagnosticBag d)
    {
        d = new DiagnosticBag();
        var node = YamlText.Parse(text, "patch", d);
        var patch = node is null || d.HasErrors ? null : PatchReader.Read(node, d);
        return d.HasErrors ? null : patch;
    }

    /// <summary>
    /// Replaces a library item for this run's future steps and its children's (docs/08 section 5.2): the content is stored,
    /// the run's lock rewritten with the item's new hash, and the core resolves from the new libraries; steps in flight keep
    /// what they resolved. Recorded as a lock entry: actor, reason, old and new hash, and the steps that keep the old.
    /// </summary>
    async Task<string> OverrideLibraryAsync(string item, string? content, string? contentHash, string actor, string reason)
    {
        if (core!.LibraryFile(item) is not { } file)
            throw new ApplicationFailureException($"{item} is not in the run's libraries");
        BeginActivity();
        LibraryOverrideResult overridden;
        try
        {
            overridden = await Workflow.ExecuteActivityAsync((RunActivities a) => a.OverrideLibraryAsync(new LibraryOverrideCall(LockRef, file.Library, file.Path, content, contentHash)),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2), RetryPolicy = new() { MaximumAttempts = 5 } });
        }
        catch (ActivityFailureException e)
        {
            throw new ApplicationFailureException($"the library override could not be stored: {e.InnerException?.Message ?? e.Message}");
        }
        finally
        {
            EndActivity();
        }

        var next = RunActivities.Build(overridden.Libraries, new DiagnosticBag());
        var entry = new LibraryOverrideEntry(item, $"{file.Library}/{file.Path}", overridden.OldHash, overridden.NewHash, actor, reason,
            core.State.InFlight.ToList(), core.ProtectedResolving(item));
        var applied = core.Apply(new LibraryOverridden(entry, next));
        if (!applied.Accepted)
            throw new ApplicationFailureException(string.Join("; ", applied.Errors));
        libraries = next;
        currentLock = overridden.LockRef;
        lockRecords.Add(new LockRecord(input.RunId, currentLock, Workflow.UtcNow));
        foreach (var child in children.ToList())
            await SignalQuietlyAsync(child.WorkflowId, ParentLibraryOverride, [new InheritedLibraryOverride(item, overridden.NewHash, actor, reason)]);
        dirty = true;
        return overridden.OldHash == overridden.NewHash ? "applied: the content was already in use" : $"applied: {overridden.OldHash} -> {overridden.NewHash}";
    }

    /// <summary>A parent's library override reaching a running child, which applies it to its own lock (the run tree from that run down).</summary>
    [WorkflowSignal(ParentLibraryOverride)]
    public async Task ParentLibraryOverrideAsync(InheritedLibraryOverride inherited)
    {
        await Workflow.WaitConditionAsync(() => core is not null);
        if (core!.State.Status != RunStatus.Running || core.LibraryItemError(inherited.Item) is not null)
            return;
        try
        {
            await OverrideLibraryAsync(inherited.Item, null, inherited.ContentHash, inherited.Actor, $"from the parent run: {inherited.Reason}");
        }
        catch (ApplicationFailureException e)
        {
            core.State.Trace.Add(new TraceEntry("library_override_refused", "", $"{inherited.Item} from the parent run: {e.Message}"));
            dirty = true;
        }
    }

    /// <summary>
    /// An agent's library override (docs/05 section 6) becomes a patch approval for a person, whatever it touches; one from a
    /// step without planning authority, or naming content that cannot be overridden, is refused and recorded.
    /// </summary>
    void ProposeLibraryOverride(Model.Plans.Step step, IReadOnlyDictionary<string, string> proposal)
    {
        var item = proposal.GetValueOrDefault("item") ?? "";
        string? refused = step.Agent?.Planning is null
            ? $"{step.Id} has no planning authority, so it may not propose a library override"
            : !proposal.ContainsKey("with") ? "a library override proposal needs with, the new content" : null;
        var result = refused is null ? core!.Apply(new LibraryOverrideProposed(step.Id, item, proposal["with"])) : ApplyResult.Refused(refused);
        if (!result.Accepted)
            core!.State.Trace.Add(new TraceEntry("library_override_rejected", step.Id, $"{item}: {string.Join("; ", result.Errors)}"));
    }

    [WorkflowQuery(PullRequestsQuery)]
    public RunPullRequests PullRequests() => new(core is null ? [] : PullRequestsOpened(), core is null ? [] : ChildrenStarted());

    /// <summary>
    /// The run's pull requests: the <c>pull_request</c> output of its <c>scm.pull_request</c> and <c>artifact.publish</c> steps
    /// (R1 F7), current and previous executions. Each opens its pull request keyed by run and repository, so earlier executions
    /// name the same one.
    /// </summary>
    List<string> PullRequestsOpened() =>
        core!.State.Steps.Where(kv => core.StepById(kv.Key)?.Action is "scm.pull_request" or "artifact.publish").OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => new[] { kv.Value.Previous, kv.Value.Current })
            .Select(e => e?.Outputs.GetValueOrDefault("pull_request") as string)
            .OfType<string>().Where(pr => pr.Length > 0).Distinct().ToList();

    /// <summary>Every child run this run started, running or ended: one per execution of each <c>run.child</c> step.</summary>
    List<string> ChildrenStarted() =>
        core!.State.Steps.Where(kv => core.StepById(kv.Key)?.Action == "run.child").OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => Enumerable.Range(1, kv.Value.Executions).Select(n => $"{Workflow.Info.WorkflowId}.{kv.Key}.{n}")).ToList();

    /// <summary>
    /// Closes an aborted run's open pull requests when the person asked (docs/05 section 5; m6-results follow-up 3). Each is
    /// noted on the timeline, closed or left; a code host that refuses does not undo the abort.
    /// </summary>
    async Task CloseAbortedPullRequestsAsync()
    {
        if (!closePullRequests || core!.State.Status != RunStatus.Aborted || !Workflow.Patched(SteeringPatch))
            return;
        var opened = PullRequestsOpened();
        var started = ChildrenStarted();
        if (opened.Count == 0 && started.Count == 0)
        {
            core.State.Trace.Add(new TraceEntry("pull_request_closed", "", "the run opened no pull requests"));
            return;
        }

        IReadOnlyList<string> events;
        try
        {
            // The children's, ended or running, are read from them by the activity: the run tree's pull requests (R1 F6).
            events = await Workflow.ExecuteActivityAsync((RunActivities a) => a.ClosePullRequestsAsync(new ClosePullRequestsCall(input.RunId, opened, "", input.DryRun, started)),
                new ActivityOptions { StartToCloseTimeout = TimeSpan.FromMinutes(2), ScheduleToCloseTimeout = TimeSpan.FromMinutes(10), RetryPolicy = new() { MaximumInterval = TimeSpan.FromSeconds(30) } });
        }
        catch (ActivityFailureException e)
        {
            events = [$"could not close {string.Join(", ", opened)}: {e.InnerException?.Message ?? e.Message}"];
        }

        foreach (var e in events)
            core.State.Trace.Add(new TraceEntry("pull_request_closed", "", e));
    }
}

/// <summary>What <see cref="RunWorkflow.PullRequestsQuery"/> answers: the run's pull requests and the child runs it started.</summary>
public sealed record RunPullRequests(IReadOnlyList<string> PullRequests, IReadOnlyList<string> Children);

/// <summary>A parent's library override for a running child: the item, the stored content's hash, and who and why.</summary>
public sealed record InheritedLibraryOverride(string Item, string ContentHash, string Actor, string Reason);
