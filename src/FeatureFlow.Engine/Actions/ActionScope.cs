using FeatureFlow.Providers.Abstractions;

namespace FeatureFlow.Engine.ActionSteps;

/// <summary>
/// An artifact the run holds now: the step and name it was produced under, its declared type, its reference, the
/// engine action that produced it (null for an agent step or decision), and whether its type is marked
/// <c>evidence: true</c> (F7: only engine actions may produce such types, check 12).
/// </summary>
public sealed record ArtifactEntry(string StepId, string Name, string? Type, string Ref, string? Action = null, bool Evidence = false);

/// <summary>
/// What an engine action knows of its run beyond its inputs, as the workflow computed it when the step started: the
/// run's repositories and the step's own, the work branch, a child run's parent branch (null: the repository's
/// default branch, read from the code host), the artifacts the run holds (evidence for <c>scm.merge</c>, the baseline
/// for <c>analysis.run</c>), the pull requests its <c>scm.pull_request</c> steps opened, F1's patterns as fixed at
/// run start (docs/05 section 8), and the run's tracker issue (<c>run.tracker_issue</c>), if it has one.
/// </summary>
public sealed record ActionScope(
    IReadOnlyList<string> Repositories,
    string? Repository,
    string WorkBranch,
    string? ParentBranch,
    IReadOnlyList<ArtifactEntry> Artifacts,
    IReadOnlyList<string> PullRequests,
    IReadOnlyList<string> EngineMayMerge,
    string? TrackerIssue = null)
{
    public static readonly ActionScope None = new([], null, "", null, [], [], []);
}

/// <summary>
/// How an action ended. <see cref="Wait"/> means its condition is not yet met: the activity fails retryably with
/// <see cref="Delay"/> as the next retry's delay, so a wait holds no worker slot between reads (docs/04 section 8.2).
/// </summary>
public sealed record ActionResult(string Kind, StepReport? Report, string? Reason, TimeSpan Delay, IReadOnlyList<string> Events)
{
    public const string Wait = "wait";

    public static ActionResult Reported(StepReport report, IReadOnlyList<string> events) => new(StepOutcome.Reported, report, null, TimeSpan.Zero, events);

    public static ActionResult Failed(string reason, IReadOnlyList<string> events) => new(StepOutcome.WorkFailed, null, reason, TimeSpan.Zero, events);

    public static ActionResult CannotAnswer(string reason, IReadOnlyList<string> events) => new(StepOutcome.CannotAnswer, null, reason, TimeSpan.Zero, events);

    public static ActionResult Waiting(string reason, TimeSpan delay, IReadOnlyList<string> events) => new(Wait, null, reason, delay, events);

    /// <summary>The <c>findings</c> namespace after a findings action (<see cref="StepOutcome.Findings"/>).</summary>
    public IReadOnlyDictionary<string, long>? Findings { get; init; }

    /// <summary>The ledger version the counts were read at (<see cref="StepOutcome.FindingsVersion"/>).</summary>
    public long FindingsVersion { get; init; }

    public StepOutcome ToOutcome() => new(Kind, Report, Reason, Events) { Findings = Findings, FindingsVersion = FindingsVersion };
}

/// <summary>An infrastructure failure inside an action: the activity is retried and no attempt is used (docs/01 section 4).</summary>
public sealed class ActionInfrastructureException(string message) : Exception(message);
